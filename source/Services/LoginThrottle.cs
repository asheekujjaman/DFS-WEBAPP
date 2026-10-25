using System.Collections.Concurrent;

namespace WebTest.Services;

/// <summary>Blocks repeated failed sign-ins BEFORE Active Directory is contacted (protects accounts from lockout abuse).</summary>
public class LoginThrottle
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _fails = new();
    private readonly LocationStore _cfg;
    public LoginThrottle(LocationStore cfg) => _cfg = cfg;

    private static string K(string kind, string v) => kind + ":" + v.Trim().ToLowerInvariant();

    private bool Blocked(string key)
    {
        var s = _cfg.Settings;
        // one address may be many users (NAT / terminal server): allow it 4x more failures than a single account
        var limit = key.StartsWith("ip:") ? s.MaxFailedLogins * 4 : s.MaxFailedLogins;
        if (!_fails.TryGetValue(key, out var l)) return false;
        lock (l)
        {
            l.RemoveAll(t => t < DateTime.UtcNow.AddMinutes(-s.LockoutMinutes));
            return l.Count >= limit;
        }
    }

    public bool IsBlocked(string ip, string user) => Blocked(K("ip", ip)) || Blocked(K("user", user));

    public void Fail(string ip, string user)
    {
        foreach (var k in new[] { K("ip", ip), K("user", user) })
        {
            var l = _fails.GetOrAdd(k, _ => new List<DateTime>());
            lock (l) l.Add(DateTime.UtcNow);
        }
    }

    public void Reset(string user) => _fails.TryRemove(K("user", user), out _);

    public List<string> BlockedKeys() => _fails.Keys.Where(Blocked).ToList();
    public void Clear() => _fails.Clear();
}
