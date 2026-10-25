namespace WebTest.Services;

public static class UiHelper
{
    public static string Size(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1048576) return (b / 1024.0).ToString("0.#") + " KB";
        if (b < 1073741824) return (b / 1048576.0).ToString("0.#") + " MB";
        return (b / 1073741824.0).ToString("0.##") + " GB";
    }

    public static string Kind(string name)
    {
        var e = Path.GetExtension(name).ToLowerInvariant();
        if (e == ".pdf") return "t-pdf";
        if (e == ".doc" || e == ".docx" || e == ".txt" || e == ".rtf" || e == ".odt") return "t-doc";
        if (e == ".xls" || e == ".xlsx" || e == ".csv" || e == ".ods") return "t-xls";
        if (e == ".ppt" || e == ".pptx" || e == ".odp") return "t-ppt";
        if (e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".gif" || e == ".bmp" || e == ".webp" || e == ".svg") return "t-img";
        if (e == ".zip" || e == ".rar" || e == ".7z" || e == ".gz") return "t-zip";
        if (e == ".exe" || e == ".msi") return "t-exe";
        return "";
    }

    /// <summary>pdf | img | office | "" (no preview)</summary>
    public static string PreviewKind(string name)
    {
        var e = Path.GetExtension(name).ToLowerInvariant();
        if (e == ".pdf") return "pdf";
        if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".bmp" || e == ".webp") return "img";
        if (e == ".doc" || e == ".docx" || e == ".xls" || e == ".xlsx" || e == ".ppt" || e == ".pptx"
            || e == ".odt" || e == ".ods" || e == ".odp" || e == ".rtf") return "office";
        return "";
    }

    public static bool InGroups(IEnumerable<string> userGroups, string list)
    {
        var wanted = list.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (wanted.Count == 0) return true;
        foreach (var g in userGroups)
        {
            var shortName = g.Contains('\\') ? g[(g.IndexOf('\\') + 1)..] : g;
            if (wanted.Any(w => w.Equals(g, StringComparison.OrdinalIgnoreCase) ||
                                w.Equals(shortName, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    public static bool CanUse(UserSession s, Location l)
        => l.Enabled && (s.IsAdmin || InGroups(s.Groups, l.AllowedGroups));
}
