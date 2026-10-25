using Microsoft.AspNetCore.Mvc;
using WebTest.Models;
using WebTest.Services;

namespace WebTest.Controllers;

/// <summary>First-run wizard: a domain admin signs in and registers the first domain + DFS path.</summary>
public class SetupController : Controller
{
    private readonly LocationStore _locs;
    private readonly LoginService _login;
    private readonly AuditLog _audit;

    public SetupController(LocationStore locs, LoginService login, AuditLog audit) { _locs = locs; _login = login; _audit = audit; }

    [HttpGet]
    public IActionResult Index()
    {
        if (_locs.IsConfigured) return RedirectToAction("Index", "Home");
        return View(new SetupViewModel());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SetupViewModel m)
    {
        if (_locs.IsConfigured) return RedirectToAction("Index", "Home");
        if (!ModelState.IsValid) { m.AdminPassword = ""; return View(m); }

        var r = await _login.LoginAsync(HttpContext, m.AdminUser, m.AdminPassword, m.Domain.Trim(), requireAdmin: true);
        m.AdminPassword = "";
        if (r.Result != LogonResult.Success)
        {
            ModelState.Remove(nameof(m.AdminPassword));
            ModelState.AddModelError("", LoginService.Message(r.Result));
            return View(m);
        }

        var web = (m.WebUrl ?? "").Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/');
        _locs.Save(new Location { Name = m.Name.Trim(), Domain = m.Domain.Trim(), Path = m.Path.Trim().TrimEnd('\\'), WebUrl = web });
        _locs.SetCompanyName(m.CompanyName);
        _locs.AddAdminUser(r.Display);
        _audit.Write("admin-setup", r.Display, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", m.Name, "First-time setup", true);
        return RedirectToAction("Index", "Admin");
    }
}
