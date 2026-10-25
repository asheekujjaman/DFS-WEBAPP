using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebTest.Models;
using WebTest.Services;

namespace WebTest.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly LocationStore _locs;
    private readonly SessionStore _store;
    private readonly AuditLog _audit;
    private readonly StatusService _status;
    private readonly FileBrowser _fb;
    private readonly LoginThrottle _throttle;
    private readonly PreviewService _preview;

    public AdminController(LocationStore locs, SessionStore store, AuditLog audit, StatusService status,
        FileBrowser fb, LoginThrottle throttle, PreviewService preview)
    { _locs = locs; _store = store; _audit = audit; _status = status; _fb = fb; _throttle = throttle; _preview = preview; }

    private UserSession? Me() => _store.Get(User.FindFirst("sid")?.Value);
    private void Log(string type, string detail, bool ok = true)
        => _audit.Write(type, User.Identity?.Name ?? "", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "", "", detail, ok);

    // ---------- dashboard ----------
    public async Task<IActionResult> Index()
    {
        await _status.GetAllAsync();
        var today = _audit.Query(null, null, 1, 5000).Where(e => e.Time.Date == DateTime.UtcNow.Date).ToList();
        var vm = new DashboardViewModel
        {
            ActiveUsers = _store.All().Where(x => x.LastUsed > DateTime.UtcNow.AddMinutes(-15)).Select(x => x.Display).Distinct().Count(),
            Locs = _locs.All().Select(l => (l, _status.Get(l.Id))).ToList(),
            Failed24h = _audit.Query("signin-fail", null, 1, 5000).Count + _audit.Query("signin-blocked", null, 1, 5000).Count,
            UploadsToday = today.Count(e => e.Type == "upload" && e.Ok),
            DownloadsToday = today.Count(e => e.Type.StartsWith("download") && e.Ok),
            Recent = _audit.Query(null, null, 7, 12)
        };
        return View(vm);
    }

    // ---------- who is signed in ----------
    public IActionResult Sessions() => View(_store.All());

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Kick(string id)
    {
        if (id != Me()?.Id) { _store.Remove(id, "kicked"); Log("admin-kick", id[..8]); }
        return RedirectToAction(nameof(Sessions));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult KickAll()
    {
        var n = _store.KickAll(Me()?.Id ?? "");
        Log("admin-kick-all", n + " session(s)");
        return RedirectToAction(nameof(Sessions));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult ClearBlocks()
    {
        _throttle.Clear();
        Log("admin-unblock", "Cleared sign-in blocks");
        return RedirectToAction(nameof(Sessions));
    }

    // ---------- audit ----------
    public IActionResult Audit(string? type, string? user, int days = 7)
    {
        ViewBag.Type = type; ViewBag.User = user; ViewBag.Days = days;
        return View(_audit.Query(type, user, days, 500));
    }

    public IActionResult AuditCsv(string? type, string? user, int days = 7)
    {
        Log("admin-export", "Audit CSV");
        var csv = _audit.ToCsv(_audit.Query(type, user, days, 5000));
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", $"audit-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // ---------- locations ----------
    public async Task<IActionResult> Locations()
    {
        await _status.GetAllAsync();
        ViewBag.Status = _locs.All().ToDictionary(l => l.Id, l => _status.Get(l.Id));
        return View(_locs.All());
    }

    [HttpGet]
    public IActionResult Edit(string? id)
    {
        var l = id == null ? null : _locs.Get(id);
        return View(l == null ? new LocationEditModel() : new LocationEditModel
        {
            Id = l.Id, Name = l.Name, Domain = l.Domain, Path = l.Path, WebUrl = l.WebUrl,
            Enabled = l.Enabled, DeleteMode = l.DeleteMode, AllowedGroups = l.AllowedGroups
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Edit(LocationEditModel m)
    {
        if (!ModelState.IsValid) return View(m);
        var web = (m.WebUrl ?? "").Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var mode = m.DeleteMode is "Recycle" or "Permanent" or "Disabled" ? m.DeleteMode : "Recycle";
        _locs.Save(new Location
        {
            Id = m.Id ?? "", Name = m.Name.Trim(), Domain = m.Domain.Trim(), Path = m.Path.Trim().TrimEnd('\\'),
            WebUrl = web, Enabled = m.Enabled, DeleteMode = mode, AllowedGroups = (m.AllowedGroups ?? "").Trim()
        });
        Log("admin-location-save", m.Name);
        return RedirectToAction(nameof(Locations));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Delete(string id)
    {
        var l = _locs.Get(id);
        if (_locs.All().Count > 1 && l != null) { _locs.Delete(id); Log("admin-location-delete", l.Name); }
        else TempData["Error"] = "You cannot remove the last location.";
        return RedirectToAction(nameof(Locations));
    }

    public async Task<IActionResult> Test(string id)
    {
        var l = _locs.Get(id);
        if (l == null) return RedirectToAction(nameof(Locations));
        ViewBag.Loc = l;
        return View(await _status.CheckNowAsync(l));
    }

    // ---------- recycle bin ----------
    public IActionResult Recycle(string id)
    {
        var l = _locs.Get(id); var me = Me();
        if (l == null || me == null) return RedirectToAction(nameof(Locations));
        ViewBag.Loc = l;
        try { return View(_fb.ListRecycle(me, l)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { ViewBag.Error = "Cannot open the recycle bin: " + ex.Message; return View(new List<RecycleMeta>()); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Restore(string id, string item)
    {
        var l = _locs.Get(id); var me = Me();
        if (l == null || me == null) return RedirectToAction(nameof(Locations));
        try { _fb.Restore(me, l, item); Log("admin-restore", l.Name + ": " + item); TempData["Ok"] = "Item restored."; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        { TempData["Error"] = "Restore failed: " + ex.Message; }
        return RedirectToAction(nameof(Recycle), new { id });
    }

    // ---------- settings ----------
    [HttpGet]
    public IActionResult Settings()
    {
        var s = _locs.Settings;
        return View(new SettingsViewModel
        {
            CompanyName = _locs.CompanyName, Banner = s.Banner, ZipEnabled = s.ZipEnabled, ZipMaxMb = s.ZipMaxMb,
            MaxUploadMb = s.MaxUploadMb, BlockedExtensions = s.BlockedExtensions, PreviewEnabled = s.PreviewEnabled,
            PreviewMaxMb = s.PreviewMaxMb, LibreOfficePath = s.LibreOfficePath, RecycleDays = s.RecycleDays,
            IdleMinutes = s.IdleMinutes, AbsoluteHours = s.AbsoluteHours, Maintenance = s.Maintenance,
            AllowedGroups = s.AllowedGroups, MaxFailedLogins = s.MaxFailedLogins, LockoutMinutes = s.LockoutMinutes,
            AuditDays = s.AuditDays, AdminUsers = string.Join("\n", _locs.AdminUsers())
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Settings(SettingsViewModel m)
    {
        if (!ModelState.IsValid) return View(m);
        var s = _locs.Settings;
        s.Banner = (m.Banner ?? "").Trim(); s.ZipEnabled = m.ZipEnabled; s.ZipMaxMb = m.ZipMaxMb; s.MaxUploadMb = m.MaxUploadMb;
        s.BlockedExtensions = (m.BlockedExtensions ?? "").Trim(); s.PreviewEnabled = m.PreviewEnabled; s.PreviewMaxMb = m.PreviewMaxMb;
        s.LibreOfficePath = (m.LibreOfficePath ?? "").Trim(); s.RecycleDays = m.RecycleDays; s.IdleMinutes = m.IdleMinutes;
        s.AbsoluteHours = m.AbsoluteHours; s.Maintenance = m.Maintenance; s.AllowedGroups = (m.AllowedGroups ?? "").Trim();
        s.MaxFailedLogins = m.MaxFailedLogins; s.LockoutMinutes = m.LockoutMinutes; s.AuditDays = m.AuditDays;
        _locs.SetCompanyName(m.CompanyName);
        _locs.SetAdminUsers((m.AdminUsers ?? "").Split('\n', '\r', ';', ','));
        _locs.AddAdminUser(User.Identity?.Name ?? "");                     // never lock yourself out
        _locs.SaveSettings();
        if (m.Maintenance && m.KickUsers) _store.KickNonAdmins();
        Log("admin-settings", m.Maintenance ? "maintenance ON" : "saved");
        TempData["Ok"] = "Settings saved.";
        return RedirectToAction(nameof(Settings));
    }

    // ---------- health / backup ----------
    public IActionResult Health()
    {
        var p = Process.GetCurrentProcess();
        string tmp = Path.GetTempPath();
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(tmp)!).AvailableFreeSpace; } catch { }
        ViewBag.Info = new Dictionary<string, string>
        {
            ["Version"] = typeof(AdminController).Assembly.GetName().Version?.ToString() ?? "",
            ["Started (UTC)"] = p.StartTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm"),
            ["Memory used"] = UiHelper.Size(p.WorkingSet64),
            ["Free space on temp drive"] = UiHelper.Size(free) + "  (" + tmp + ")",
            ["Running as"] = Environment.UserDomainName + "\\" + Environment.UserName,
            ["Operating system"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["Active sessions"] = _store.All().Count.ToString(),
            ["Blocked sign-in keys"] = _throttle.BlockedKeys().Count.ToString(),
            ["LibreOffice (Office preview)"] = _preview.OfficeAvailable ? "found" : "not found",
            ["Data folder"] = _locs.DataDir,
            ["HTTPS on this request"] = Request.IsHttps ? "yes" : "NO - use HTTPS in production"
        };
        return View();
    }

    public IActionResult Backup()
    {
        Log("admin-backup", "Config downloaded");
        return File(Encoding.UTF8.GetBytes(_locs.ExportJson()), "application/json", $"fileshare-config-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreConfig(IFormFile? file)
    {
        if (file == null || file.Length == 0 || file.Length > 2_000_000) { TempData["Error"] = "Choose a config backup file."; return RedirectToAction(nameof(Health)); }
        using var r = new StreamReader(file.OpenReadStream());
        if (_locs.Import(await r.ReadToEndAsync(), out var err)) { Log("admin-restore-config", file.FileName); TempData["Ok"] = "Configuration restored."; }
        else TempData["Error"] = err;
        _locs.AddAdminUser(User.Identity?.Name ?? "");
        return RedirectToAction(nameof(Health));
    }
}
