using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebTest.Models;
using WebTest.Services;

namespace WebTest.Controllers;

[Authorize]
public class FilesController : Controller
{
    private static readonly SemaphoreSlim ZipGate = new(3, 3);
    private readonly FileBrowser _fb;
    private readonly SessionStore _store;
    private readonly LocationStore _locs;
    private readonly AuditLog _audit;
    private readonly PreviewService _preview;

    public FilesController(FileBrowser fb, SessionStore store, LocationStore locs, AuditLog audit, PreviewService preview)
    { _fb = fb; _store = store; _locs = locs; _audit = audit; _preview = preview; }

    private (UserSession? S, Location? L) Ctx(string? loc)
    {
        var s = _store.Get(User.FindFirst("sid")?.Value);
        var l = _locs.Get(loc);
        if (s != null && l != null && !UiHelper.CanUse(s, l)) l = null;
        return (s, l);
    }

    private static string Parent(string p) { var i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }
    private static bool Expected(Exception ex) => ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException;

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied: you do not have permission for this action.",
        FileNotFoundException or DirectoryNotFoundException => "Not found.",
        ArgumentException or InvalidOperationException => ex.Message,
        _ => "The operation could not be completed. Try again or contact your administrator."
    };

    private IActionResult Back(string loc, string path, string msg)
    {
        TempData["Error"] = msg;
        return RedirectToAction(nameof(Index), new { loc, path });
    }

    private void Log(UserSession s, Location l, string type, string detail, bool ok = true)
        => _audit.Write(type, s.Display, s.Ip, l.Name, detail, ok);

    public IActionResult Index(string? loc, string? path)
    {
        var (s, l) = Ctx(loc);
        if (s == null) return RedirectToAction("Login", "Account");
        if (l == null) return RedirectToAction("Index", "Home");

        var set = _locs.Settings;
        var vm = new BrowseViewModel
        {
            Loc = l, Path = _fb.Normalize(path), Error = TempData["Error"] as string,
            ZipEnabled = set.ZipEnabled, PreviewEnabled = set.PreviewEnabled
        };
        s.CurrentLoc = l.Name; s.CurrentPath = vm.Path;
        try
        {
            vm.CanWrite = _fb.GetAccess(s, l, vm.Path).Write;
            vm.CanDelete = vm.CanWrite && l.DeleteMode != "Disabled";
            vm.Entries = _fb.List(s, l, vm.Path);
        }
        catch (Exception ex) when (Expected(ex)) { vm.Error = Describe(ex); }
        return View(vm);
    }

    public IActionResult Access(string? loc, string? path)
    {
        var (s, l) = Ctx(loc);
        if (s == null) return RedirectToAction("Login", "Account");
        if (l == null) return RedirectToAction("Index", "Home");
        path = _fb.Normalize(path);
        try { return View(_fb.Explain(s, l, path)); }
        catch (Exception ex) when (Expected(ex)) { return Back(l.Id, path, Describe(ex)); }
    }

    public IActionResult Download(string? loc, string path)
    {
        var (s, l) = Ctx(loc);
        if (s == null) return RedirectToAction("Login", "Account");
        if (l == null) return RedirectToAction("Index", "Home");
        path = _fb.Normalize(path);
        try
        {
            var fs = _fb.OpenRead(s, l, path);
            Log(s, l, "download", path);
            return File(fs, "application/octet-stream", System.IO.Path.GetFileName(path), enableRangeProcessing: true);
        }
        catch (Exception ex) when (Expected(ex)) { Log(s, l, "download", path, false); return Back(l.Id, Parent(path), Describe(ex)); }
    }

    /// <summary>Inline preview: PDF and images directly, Word/Excel/PowerPoint through a cached PDF.</summary>
    public async Task<IActionResult> Preview(string? loc, string path)
    {
        var (s, l) = Ctx(loc);
        if (s == null) return Unauthorized();
        if (l == null) return NotFound();
        if (!_locs.Settings.PreviewEnabled) return StatusCode(403, "Preview is turned off.");
        path = _fb.Normalize(path);
        try
        {
            var kind = UiHelper.PreviewKind(path);
            if (kind == "pdf")
            {
                var fs = _fb.OpenRead(s, l, path);
                Log(s, l, "preview", path);
                return File(fs, "application/pdf", enableRangeProcessing: true);
            }
            if (kind == "img")
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                var type = ext == ".png" ? "image/png" : ext == ".gif" ? "image/gif" : ext == ".bmp" ? "image/bmp" : ext == ".webp" ? "image/webp" : "image/jpeg";
                Log(s, l, "preview", path);
                return File(_fb.OpenRead(s, l, path), type, enableRangeProcessing: true);
            }
            if (kind == "office")
            {
                var pdf = await _preview.GetPdfAsync(s, l, path);
                Log(s, l, "preview", path);
                return PhysicalFile(pdf, "application/pdf", enableRangeProcessing: true);
            }
            return StatusCode(415, "This file type cannot be previewed.");
        }
        catch (Exception ex) when (Expected(ex)) { return StatusCode(409, Describe(ex)); }
    }

    public IActionResult DownloadFolder(string? loc, string? path)
    {
        var (s, l) = Ctx(loc);
        if (s == null) return RedirectToAction("Login", "Account");
        if (l == null) return RedirectToAction("Index", "Home");
        path = _fb.Normalize(path);
        var set = _locs.Settings;
        if (!set.ZipEnabled) return Back(l.Id, path, "Folder download is turned off by the administrator.");
        if (!ZipGate.Wait(0)) return Back(l.Id, path, "The server is busy preparing other downloads. Please try again in a minute.");
        try
        {
            var (bytes, exceeded) = _fb.FolderSize(s, l, path, set.ZipMaxMb * 1024L * 1024L);
            if (exceeded) return Back(l.Id, path, $"This folder is larger than the {set.ZipMaxMb} MB limit for zip downloads. Download files one by one.");
            var fs = _fb.ZipFolder(s, l, path);
            Log(s, l, "download-zip", path);
            var name = path.Length == 0 ? l.Name : System.IO.Path.GetFileName(path);
            return File(fs, "application/zip", name + ".zip");
        }
        catch (Exception ex) when (Expected(ex)) { return Back(l.Id, path, Describe(ex)); }
        finally { ZipGate.Release(); }
    }

    [HttpPost, ValidateAntiForgeryToken, DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Upload(string? loc, string? path, string? relativePath, IFormFile file)
    {
        var (s, l) = Ctx(loc);
        if (s == null || l == null) return Unauthorized();
        var set = _locs.Settings;
        var rel = string.IsNullOrWhiteSpace(relativePath) ? file.FileName : relativePath;
        var ext = System.IO.Path.GetExtension(rel).ToLowerInvariant();
        if (set.BlockedExtensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(b => b.Trim().Equals(ext, StringComparison.OrdinalIgnoreCase) || b.Trim().Equals(ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase)))
            return Json(new { ok = false, error = $"Files of type {ext} are not allowed." });
        if (set.MaxUploadMb > 0 && file.Length > set.MaxUploadMb * 1024L * 1024L)
            return Json(new { ok = false, error = $"The file is larger than the {set.MaxUploadMb} MB limit." });
        if (!_fb.GetAccess(s, l, path ?? "").Write)
            return Json(new { ok = false, error = "Read-only folder: you do not have write permission here." });
        try
        {
            await using var dst = _fb.OpenWrite(s, l, path ?? "", rel);
            await file.CopyToAsync(dst);
            Log(s, l, "upload", (path ?? "") + "/" + rel);
            return Json(new { ok = true });
        }
        catch (Exception ex) when (Expected(ex)) { Log(s, l, "upload", rel, false); return Json(new { ok = false, error = Describe(ex) }); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult NewFolder(string? loc, string? path, string name)
    {
        var (s, l) = Ctx(loc);
        if (s == null) return RedirectToAction("Login", "Account");
        if (l == null) return RedirectToAction("Index", "Home");
        path = _fb.Normalize(path);
        if (!_fb.GetAccess(s, l, path).Write) return Back(l.Id, path, "Read-only folder: you do not have write permission here.");
        try { _fb.CreateFolder(s, l, path, name ?? ""); Log(s, l, "folder-create", path + "/" + name); }
        catch (Exception ex) when (Expected(ex)) { return Back(l.Id, path, Describe(ex)); }
        return RedirectToAction(nameof(Index), new { loc = l.Id, path });
    }

    /// <summary>For the delete dialog: how much is inside a folder.</summary>
    public IActionResult Count(string? loc, string path)
    {
        var (s, l) = Ctx(loc);
        if (s == null || l == null) return Unauthorized();
        try { var (f, d, t) = _fb.Count(s, l, _fb.Normalize(path)); return Json(new { files = f, folders = d, truncated = t }); }
        catch (Exception ex) when (Expected(ex)) { return Json(new { files = 0, folders = 0, truncated = false, error = Describe(ex) }); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Delete(string? loc, string path)
    {
        var (s, l) = Ctx(loc);
        if (s == null || l == null) return Unauthorized();
        path = _fb.Normalize(path);
        if (l.DeleteMode == "Disabled") return Json(new { ok = false, error = "Deleting is turned off for this location." });
        if (!_fb.GetAccess(s, l, Parent(path)).Write) return Json(new { ok = false, error = "You do not have permission to delete here." });
        try
        {
            if (l.DeleteMode == "Permanent") { _fb.DeletePermanent(s, l, path); Log(s, l, "delete-permanent", path); return Json(new { ok = true }); }
            var id = _fb.Recycle(s, l, path);
            Log(s, l, "delete-recycle", path);
            return Json(new { ok = true, undoId = id });
        }
        catch (Exception ex) when (Expected(ex) || ex is FileNotFoundException)
        {
            Log(s, l, "delete", path, false);
            var msg = ex is IOException && l.DeleteMode == "Recycle" && ex is not FileNotFoundException and not DirectoryNotFoundException
                ? "This item could not be moved to the recycle bin (it may be on a different disk). Ask an administrator."
                : Describe(ex);
            return Json(new { ok = false, error = msg });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Undo(string? loc, string id)
    {
        var (s, l) = Ctx(loc);
        if (s == null || l == null) return Unauthorized();
        try { _fb.Undo(s, l, id); Log(s, l, "delete-undo", id); return Json(new { ok = true }); }
        catch (Exception ex) when (Expected(ex)) { return Json(new { ok = false, error = Describe(ex) }); }
    }
}
