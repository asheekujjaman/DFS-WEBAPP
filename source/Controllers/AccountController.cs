using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.Options;
using WebTest.Models;
using WebTest.Services;

namespace WebTest.Controllers;

public class AccountController : Controller
{
    private readonly SessionStore _store;
    private readonly LocationStore _locs;
    private readonly LoginService _login;
    private readonly IWinSecurity _win;
    private readonly AppSettings _app;

    public AccountController(SessionStore store, LocationStore locs, LoginService login, IWinSecurity win, IOptions<AppSettings> app)
    { _store = store; _locs = locs; _login = login; _win = win; _app = app.Value; }

    private void Fill(LoginViewModel m)
    {
        var all = _locs.All().Where(l => l.Enabled).ToList();
        ViewBag.Locations = all;
        ViewBag.Sso = _app.WindowsSso;
        var byHost = _locs.ByHost(Request.Host.Host);
        if (byHost != null) { m.LocationId = byHost.Id; ViewBag.Locked = byHost; }
        else if (string.IsNullOrEmpty(m.LocationId)) m.LocationId = all.FirstOrDefault()?.Id ?? "";
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        var m = new LoginViewModel { ReturnUrl = returnUrl };
        Fill(m);
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel m)
    {
        Fill(m);
        if (!ModelState.IsValid) { m.Password = ""; ModelState.Remove(nameof(m.Password)); return View(m); }

        var loc = _locs.Get(m.LocationId);
        if (loc == null || !loc.Enabled) { ModelState.AddModelError("", "Unknown domain."); return View(m); }

        var r = await _login.LoginAsync(HttpContext, m.Username, m.Password, loc.Domain);
        m.Password = "";                                   // never send a password back to the browser
        if (r.Result != LogonResult.Success)
        {
            ModelState.Remove(nameof(m.Password));
            ModelState.AddModelError("", LoginService.Message(r.Result));
            return View(m);
        }
        if (!string.IsNullOrEmpty(m.ReturnUrl) && Url.IsLocalUrl(m.ReturnUrl)) return Redirect(m.ReturnUrl);
        return RedirectToAction("Index", "Home");
    }

    /// <summary>Windows single sign-on (Kerberos/Negotiate through IIS). No password reaches this site.</summary>
    [HttpGet]
    public async Task<IActionResult> Windows()
    {
        if (!_app.WindowsSso) return NotFound();
        var r = await HttpContext.AuthenticateAsync(IISServerDefaults.AuthenticationScheme);
        if (!r.Succeeded) return Challenge(IISServerDefaults.AuthenticationScheme);
        if (r.Principal?.Identity is not WindowsIdentity wi) return Challenge(IISServerDefaults.AuthenticationScheme);
        var token = _win.FromIdentity(wi);
        if (token == null) return RedirectToAction(nameof(Login));
        var o = await _login.FinishAsync(HttpContext, token, wi.Name, false);
        if (o.Result != LogonResult.Success) { TempData["Error"] = LoginService.Message(o.Result); return RedirectToAction(nameof(Login)); }
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var sid = User.FindFirst("sid")?.Value;
        if (sid != null) _store.Remove(sid);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
