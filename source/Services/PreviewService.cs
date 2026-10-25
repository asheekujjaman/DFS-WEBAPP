using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WebTest.Services;

/// <summary>Office documents are converted to PDF with LibreOffice (headless) and cached; PDF/images need nothing.</summary>
public class PreviewService
{
    private readonly SemaphoreSlim _gate = new(2);
    private readonly string _cache;
    private readonly LocationStore _cfg;
    private readonly FileBrowser _fb;

    public PreviewService(LocationStore cfg, FileBrowser fb)
    {
        _cfg = cfg; _fb = fb;
        _cache = System.IO.Path.Combine(cfg.DataDir, "preview-cache");
        Directory.CreateDirectory(_cache);
    }

    private string? FindOffice()
    {
        var set = _cfg.Settings.LibreOfficePath;
        var c = new List<string>();
        if (!string.IsNullOrWhiteSpace(set)) c.Add(set);
        c.Add(@"C:\Program Files\LibreOffice\program\soffice.exe");
        c.Add(@"C:\Program Files (x86)\LibreOffice\program\soffice.exe");
        c.Add("/usr/bin/soffice");
        return c.FirstOrDefault(File.Exists);
    }

    public bool OfficeAvailable => FindOffice() != null;

    /// <summary>Returns the path of a cached PDF for an Office file (permission is checked as the user first).</summary>
    public async Task<string> GetPdfAsync(UserSession s, Location l, string rel)
    {
        var (len, mod) = _fb.Info(s, l, rel);                                  // throws if the user cannot access
        if (len > _cfg.Settings.PreviewMaxMb * 1024L * 1024L) throw new InvalidOperationException("This file is too large to preview.");
        using (var probe = _fb.OpenRead(s, l, rel)) { }                          // must be readable by the user

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{l.Id}|{rel.ToLowerInvariant()}|{len}|{mod.Ticks}")))[..32];
        var pdf = System.IO.Path.Combine(_cache, key + ".pdf");
        if (File.Exists(pdf)) return pdf;

        var soffice = FindOffice() ?? throw new InvalidOperationException("Preview for Office files needs LibreOffice installed on the server.");
        await _gate.WaitAsync();
        var work = System.IO.Path.Combine(_cache, "w-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(pdf)) return pdf;
            Directory.CreateDirectory(work);
            var input = System.IO.Path.Combine(work, "in" + System.IO.Path.GetExtension(rel).ToLowerInvariant());
            using (var src = _fb.OpenRead(s, l, rel))
            using (var dst = new FileStream(input, FileMode.Create)) await src.CopyToAsync(dst);

            var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--headless", "--norestore", "--nolockcheck", "--nodefault",
                "-env:UserInstallation=file:///" + System.IO.Path.Combine(work, "profile").Replace('\\', '/'),
                "--convert-to", "pdf", "--outdir", work, input }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } throw new InvalidOperationException("Preview timed out."); }

            var made = System.IO.Path.Combine(work, "in.pdf");
            if (!File.Exists(made)) throw new InvalidOperationException("This file could not be converted for preview.");
            File.Move(made, pdf, true);
            return pdf;
        }
        finally
        {
            _gate.Release();
            try { Directory.Delete(work, true); } catch { }
        }
    }

    public void CleanCache(int days = 3)
    {
        try
        {
            foreach (var f in Directory.GetFiles(_cache, "*.pdf"))
                if (File.GetLastAccessTimeUtc(f) < DateTime.UtcNow.AddDays(-days)) File.Delete(f);
        }
        catch { }
    }
}
