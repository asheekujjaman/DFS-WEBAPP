using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace WebTest.Services;

public record TargetStatus(string Server, string Share, bool Up, long Ms);
public record LocStatus(string Id, string State, long Ms, int UpCount, int Total, double UptimePct, List<TargetStatus> Targets);

/// <summary>
/// Up / Degraded / Down based on the REAL DFS targets of each namespace (SMB port + share probe),
/// not just the domain name (which any domain controller answers).
/// </summary>
public class StatusService
{
    private readonly ConcurrentDictionary<string, LocStatus> _latest = new();
    private readonly ConcurrentDictionary<string, Queue<(DateTime T, bool Up)>> _hist = new();
    private readonly IWinSecurity _win;
    private readonly LocationStore _locs;
    private readonly int _port;

    public StatusService(IWinSecurity win, LocationStore locs, IOptions<AppSettings> o)
    { _win = win; _locs = locs; _port = o.Value.StatusPort; }

    public static string? HostOf(string path)
    {
        if (!path.StartsWith("\\\\")) return null;
        var rest = path[2..];
        var i = rest.IndexOfAny(new[] { '\\', '/' });
        return i < 0 ? rest : rest[..i];
    }

    public LocStatus? Get(string id) => _latest.TryGetValue(id, out var s) ? s : null;

    public async Task<List<LocStatus>> GetAllAsync()
    {
        var locs = _locs.All().Where(l => l.Enabled).ToList();
        if (locs.Any(l => !_latest.ContainsKey(l.Id))) await PollAsync();
        return locs.Select(l => _latest.TryGetValue(l.Id, out var s) ? s : Unknown(l.Id)).ToList();
    }

    private static LocStatus Unknown(string id) => new(id, "down", 0, 0, 0, 0, new());

    public async Task PollAsync()
    {
        var locs = _locs.All().Where(l => l.Enabled).ToList();
        var res = await Task.WhenAll(locs.Select(CheckAsync));
        foreach (var r in res) _latest[r.Id] = r;
        foreach (var k in _latest.Keys.Where(k => locs.All(l => l.Id != k)).ToList()) _latest.TryRemove(k, out _);
    }

    public async Task<LocStatus> CheckNowAsync(Location l) => await CheckAsync(l);

    private async Task<LocStatus> CheckAsync(Location l)
    {
        var targets = new List<DfsTarget>();
        try { targets = _win.DfsTargets(l.Path); } catch { }
        if (targets.Count == 0)
        {
            var host = HostOf(l.Path);
            targets.Add(host == null ? new DfsTarget("", l.Path) : new DfsTarget(host, ""));
        }

        var results = await Task.WhenAll(targets.Select(t => CheckTargetAsync(t, l.Path)));
        int up = results.Count(r => r.Up);
        var state = up == results.Length ? "up" : up == 0 ? "down" : "degraded";
        var ms = up == 0 ? 0 : (long)results.Where(r => r.Up).Average(r => r.Ms);

        var q = _hist.GetOrAdd(l.Id, _ => new Queue<(DateTime, bool)>());
        double pct;
        lock (q)
        {
            q.Enqueue((DateTime.UtcNow, state != "down"));
            while (q.Count > 0 && q.Peek().T < DateTime.UtcNow.AddHours(-24)) q.Dequeue();
            pct = q.Count == 0 ? 0 : 100.0 * q.Count(x => x.Up) / q.Count;
        }
        return new LocStatus(l.Id, state, ms, up, results.Length, Math.Round(pct, 1), results.ToList());
    }

    private async Task<TargetStatus> CheckTargetAsync(DfsTarget t, string rootPath)
    {
        var sw = Stopwatch.StartNew();
        bool ok;
        try
        {
            if (t.Server == "")                                   // local folder
                ok = await ProbeAsync(t.Share);
            else
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(2500);
                await tcp.ConnectAsync(t.Server, _port, cts.Token);
                ok = true;
                if (OperatingSystem.IsWindows() && t.Share != "")
                    ok = await ProbeAsync($"\\\\{t.Server}\\{t.Share}");
            }
        }
        catch { ok = false; }
        return new TargetStatus(t.Server == "" ? "(local)" : t.Server, t.Share == rootPath ? "" : t.Share, ok, ok ? sw.ElapsedMilliseconds : 0);
    }

    // reachable = no error, or "access denied" (the server answered)
    private static async Task<bool> ProbeAsync(string path)
    {
        var t = Task.Run(() =>
        {
            try { using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); e.MoveNext(); return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        });
        return await Task.WhenAny(t, Task.Delay(3000)) == t && t.Result;
    }
}
