namespace WebTest.Services;

/// <summary>Status polling, session cleanup and daily clean-ups (audit, recycle bin, preview cache).</summary>
public class BackgroundJobs : BackgroundService
{
    private readonly StatusService _status;
    private readonly SessionStore _sessions;
    private readonly LocationStore _locs;
    private readonly AuditLog _audit;
    private readonly FileBrowser _fb;
    private readonly PreviewService _preview;
    private DateTime _lastDaily = DateTime.MinValue;

    public BackgroundJobs(StatusService status, SessionStore sessions, LocationStore locs, AuditLog audit,
        FileBrowser fb, PreviewService preview)
    { _status = status; _sessions = sessions; _locs = locs; _audit = audit; _fb = fb; _preview = preview; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        while (!ct.IsCancellationRequested)
        {
            try { if (_locs.IsConfigured) await _status.PollAsync(); } catch { }
            try { _sessions.Sweep(); } catch { }
            if (DateTime.UtcNow - _lastDaily > TimeSpan.FromHours(24)) { _lastDaily = DateTime.UtcNow; Daily(); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { }
        }
    }

    private void Daily()
    {
        try { _audit.Purge(_locs.Settings.AuditDays); } catch { }
        try { _preview.CleanCache(); } catch { }
        foreach (var l in _locs.All())
            try { var n = _fb.PurgeRecycle(l, _locs.Settings.RecycleDays); if (n > 0) _audit.Write("recycle-purge", "system", "", l.Name, n + " item(s)", true); } catch { }
    }
}
