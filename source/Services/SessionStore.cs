using System.Collections.Concurrent;

namespace WebTest.Services;

public class UserSession
{
    public string Id { get; init; } = "";
    public string Display { get; init; } = "";
    public bool IsAdmin { get; init; }
    public List<string> Groups { get; init; } = new();
    public WinToken Token { get; init; } = null!;
    public string Ip { get; init; } = "";
    public string Agent { get; init; } = "";
    public DateTime Created { get; init; } = DateTime.UtcNow;
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public string CurrentLoc { get; set; } = "";
    public string CurrentPath { get; set; } = "";
}

/// <summary>Server-side sessions. Only a Windows token is held in memory - never a password.</summary>
public class SessionStore
{
    private readonly ConcurrentDictionary<string, UserSession> _s = new();
    private readonly LocationStore _cfg;
    private readonly AuditLog _audit;

    public SessionStore(LocationStore cfg, AuditLog audit) { _cfg = cfg; _audit = audit; }

    public string Add(UserInfo info, WinToken token, string ip, string agent)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        _s[id] = new UserSession
        {
            Id = id, Display = info.Name, IsAdmin = info.IsAdmin, Groups = info.Groups,
            Token = token, Ip = ip, Agent = agent
        };
        return id;
    }

    public UserSession? Get(string? id, bool touch = true)
    {
        if (string.IsNullOrEmpty(id) || !_s.TryGetValue(id, out var s)) return null;
        var set = _cfg.Settings;
        var now = DateTime.UtcNow;
        if (now - s.LastUsed > TimeSpan.FromMinutes(Math.Max(1, set.IdleMinutes)))
        { Remove(id, "timeout-idle"); return null; }
        if (now - s.Created > TimeSpan.FromHours(Math.Max(1, set.AbsoluteHours)))
        { Remove(id, "timeout-absolute"); return null; }
        if (touch) s.LastUsed = now;
        return s;
    }

    public void Remove(string id, string reason = "logout")
    {
        if (_s.TryRemove(id, out var s))
        {
            s.Token.Dispose();
            _audit.Write(reason, s.Display, s.Ip, "", "", true);
        }
    }

    public List<UserSession> All() => _s.Values.OrderByDescending(x => x.LastUsed).ToList();

    public int KickAll(string exceptId)
    {
        int n = 0;
        foreach (var k in _s.Keys.ToList()) if (k != exceptId) { Remove(k, "kicked"); n++; }
        return n;
    }

    public int KickNonAdmins()
    {
        int n = 0;
        foreach (var s in _s.Values.Where(x => !x.IsAdmin).ToList()) { Remove(s.Id, "kicked-maintenance"); n++; }
        return n;
    }

    public void Sweep() { foreach (var k in _s.Keys.ToList()) Get(k, touch: false); }
}
