using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebTest.Models;
using WebTest.Services;

namespace WebTest.Controllers;

public class HomeController : Controller
{
    private readonly LocationStore _locs;
    private readonly StatusService _status;
    private readonly SessionStore _store;
    private readonly AuditLog _audit;

    public HomeController(LocationStore locs, StatusService status, SessionStore store, AuditLog audit)
    { _locs = locs; _status = status; _store = store; _audit = audit; }

    private UserSession? Sess() => _store.Get(User.FindFirst("sid")?.Value, touch: false);

    [Authorize]
    public IActionResult Index()
    {
        var s = Sess();
        if (s == null) return RedirectToAction("Login", "Account");
        return View(new HomeViewModel
        {
            Locations = _locs.All().Where(l => UiHelper.CanUse(s, l)).ToList(),
            Banner = _locs.Settings.Banner
        });
    }

    /// <summary>Users get only up/degraded/down + ms. No server names or paths.</summary>
    [Authorize]
    public async Task<IActionResult> Status()
    {
        var s = Sess();
        if (s == null) return Unauthorized();
        var allowed = _locs.All().Where(l => UiHelper.CanUse(s, l)).Select(l => l.Id).ToHashSet();
        var list = (await _status.GetAllAsync()).Where(x => allowed.Contains(x.Id));
        return Json(list.Select(x => new { id = x.Id, state = x.State, ms = x.Ms }));
    }

    public IActionResult Error()
    {
        var ex = HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        if (ex != null) _audit.Write("error", User.Identity?.Name ?? "", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", "", ex.GetType().Name + ": " + ex.Message, false);
        return Content("Something went wrong. Please go back, or sign out and sign in again. If it keeps happening, contact your administrator.", "text/plain");
    }
}
