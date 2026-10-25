using System.Text;
using System.Text.Json;

namespace WebTest.Services;

public record AuditEvent(DateTime Time, string Type, string User, string Ip, string Loc, string Detail, bool Ok);

/// <summary>Append-only daily files (App_Data/audit/audit-yyyy-MM-dd.jsonl). Never contains passwords.</summary>
public class AuditLog
{
    private readonly string _dir;
    private readonly object _lock = new();

    public AuditLog(LocationStore cfg)
    {
        _dir = System.IO.Path.Combine(cfg.DataDir, "audit");
        Directory.CreateDirectory(_dir);
    }

    public void Write(string type, string user, string ip, string loc, string detail, bool ok)
    {
        var e = new AuditEvent(DateTime.UtcNow, type, user, ip, loc, Clean(detail), ok);
        try
        {
            var f = System.IO.Path.Combine(_dir, $"audit-{e.Time:yyyy-MM-dd}.jsonl");
            lock (_lock) File.AppendAllText(f, JsonSerializer.Serialize(e) + "\n", Encoding.UTF8);
        }
        catch { /* logging must never break the site */ }
    }

    private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ");

    public List<AuditEvent> Query(string? type = null, string? user = null, int days = 7, int take = 500)
    {
        var res = new List<AuditEvent>();
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        string[] files;
        try { files = Directory.GetFiles(_dir, "audit-*.jsonl").OrderByDescending(x => x).ToArray(); }
        catch { return res; }
        foreach (var f in files)
        {
            string[] lines;
            try { lock (_lock) lines = File.ReadAllLines(f); } catch { continue; }
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                AuditEvent? e;
                try { e = JsonSerializer.Deserialize<AuditEvent>(lines[i]); } catch { continue; }
                if (e == null || e.Time < since) continue;
                if (!string.IsNullOrEmpty(type) && !e.Type.StartsWith(type, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(user) && !e.User.Contains(user, StringComparison.OrdinalIgnoreCase)) continue;
                res.Add(e);
                if (res.Count >= take) return res;
            }
        }
        return res;
    }

    public string ToCsv(IEnumerable<AuditEvent> events)
    {
        var sb = new StringBuilder("Time (UTC),Type,User,IP,Location,Detail,Result\n");
        foreach (var e in events)
            sb.AppendLine(string.Join(",", Csv(e.Time.ToString("yyyy-MM-dd HH:mm:ss")), Csv(e.Type), Csv(e.User),
                Csv(e.Ip), Csv(e.Loc), Csv(e.Detail), e.Ok ? "ok" : "fail"));
        return sb.ToString();
    }

    private static string Csv(string v)
    {
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;      // block CSV formula injection
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public void Purge(int days)
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "audit-*.jsonl"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-Math.Max(7, days))) File.Delete(f);
        }
        catch { }
    }
}
