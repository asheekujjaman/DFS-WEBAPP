using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WebTest.Services;

public class Location
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Path { get; set; } = "";
    public string WebUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Recycle (default, safe) | Permanent | Disabled</summary>
    public string DeleteMode { get; set; } = "Recycle";
    /// <summary>Comma separated AD groups allowed to open this location (empty = everyone who can sign in).</summary>
    public string AllowedGroups { get; set; } = "";
}

public class PortalSettings
{
    public bool ZipEnabled { get; set; } = true;
    public int ZipMaxMb { get; set; } = 2048;
    public int MaxUploadMb { get; set; } = 0;                // 0 = no limit
    public string BlockedExtensions { get; set; } = ".bat,.cmd,.ps1,.vbs,.scr,.com";
    public bool PreviewEnabled { get; set; } = true;
    public int PreviewMaxMb { get; set; } = 50;
    public string LibreOfficePath { get; set; } = "";
    public int RecycleDays { get; set; } = 30;
    public int IdleMinutes { get; set; } = 60;
    public int AbsoluteHours { get; set; } = 12;
    public bool Maintenance { get; set; } = false;
    public string Banner { get; set; } = "";
    public string AllowedGroups { get; set; } = "";           // global sign-in groups (empty = all domain users)
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int AuditDays { get; set; } = 90;
}

public class AppConfig
{
    public List<Location> Locations { get; set; } = new();
    public List<string> AdminUsers { get; set; } = new();
    public string CompanyName { get; set; } = "";
    public PortalSettings Settings { get; set; } = new();
}

/// <summary>Stores domains / DFS paths / portal settings (JSON file in App_Data). No secrets are stored.</summary>
public class LocationStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private AppConfig _cfg = new();
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public LocationStore(IOptions<AppSettings> o, IWebHostEnvironment env)
    {
        _file = System.IO.Path.Combine(env.ContentRootPath, o.Value.DataFile);
        try
        {
            if (File.Exists(_file))
                _cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_file)) ?? new AppConfig();
        }
        catch { _cfg = new AppConfig(); }
    }

    public string DataDir => System.IO.Path.GetDirectoryName(_file)!;
    public bool IsConfigured { get { lock (_lock) return _cfg.Locations.Count > 0; } }
    public string CompanyName { get { lock (_lock) return _cfg.CompanyName ?? ""; } }
    public PortalSettings Settings { get { lock (_lock) return _cfg.Settings; } }
    public List<Location> All() { lock (_lock) return _cfg.Locations.ToList(); }
    public Location? Get(string? id) { lock (_lock) return _cfg.Locations.FirstOrDefault(x => x.Id == id); }
    public List<string> AdminUsers() { lock (_lock) return _cfg.AdminUsers.ToList(); }

    public Location? ByHost(string host)
    {
        lock (_lock)
            return _cfg.Locations.FirstOrDefault(x => x.Enabled && x.WebUrl.Length > 0 &&
                x.WebUrl.Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(Location l)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(l.Id)) l.Id = Guid.NewGuid().ToString("N")[..8];
            var i = _cfg.Locations.FindIndex(x => x.Id == l.Id);
            if (i >= 0) _cfg.Locations[i] = l; else _cfg.Locations.Add(l);
            Persist();
        }
    }

    public void Delete(string id) { lock (_lock) { _cfg.Locations.RemoveAll(x => x.Id == id); Persist(); } }
    public void SetCompanyName(string? n) { lock (_lock) { _cfg.CompanyName = (n ?? "").Trim(); Persist(); } }
    public void SaveSettings() { lock (_lock) Persist(); }

    public void SetAdminUsers(IEnumerable<string> users)
    {
        lock (_lock)
        {
            _cfg.AdminUsers = users.Select(u => u.Trim()).Where(u => u.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Persist();
        }
    }

    public void AddAdminUser(string user)
    {
        lock (_lock)
        {
            if (!_cfg.AdminUsers.Contains(user, StringComparer.OrdinalIgnoreCase)) _cfg.AdminUsers.Add(user);
            Persist();
        }
    }

    public string ExportJson() { lock (_lock) return JsonSerializer.Serialize(_cfg, Opt); }

    public bool Import(string json, out string error)
    {
        error = "";
        try
        {
            var c = JsonSerializer.Deserialize<AppConfig>(json);
            if (c == null || c.Locations.Count == 0) { error = "The file has no locations."; return false; }
            lock (_lock) { _cfg = c; Persist(); }
            return true;
        }
        catch (Exception ex) { error = "Invalid file: " + ex.Message; return false; }
    }

    private void Persist()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_cfg, Opt));
        File.Move(tmp, _file, true);
    }
}
