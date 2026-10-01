using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;

namespace TinyTracker.Core.SelfUpdate;

// Tiny Tracker's releases, from GitHub's API. Throws when GitHub doesn't answer.
public interface ISelfReleases
{
    // The latest published release; null before the first.
    Task<SelfRelease?> LatestAsync(CancellationToken ct);
}

// Looks for Tiny Tracker's own update (spec §6.5) with the first check at start, Check now, and the first check a day after the
// last answer; one with no answer is tried at the next check, with no notice. offer hears each answer, on a worker thread.
public sealed class SelfUpdateCheck : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly CheckScheduler _scheduler;
    private readonly ISelfReleases _releases;
    private readonly Action<SelfRelease?> _offer;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset? _answeredAt;
    private Task _looking = Task.CompletedTask;
    private bool _disposed;

    public SelfUpdateCheck(CheckScheduler scheduler, ISelfReleases releases, Action<SelfRelease?> offer, TimeProvider time, FileLog log)
    {
        _scheduler = scheduler;
        _releases = releases;
        _offer = offer;
        _time = time;
        _log = log;
        scheduler.CheckDue += OnCheckDue;
    }

    // Completes once a look that ran at Dispose has stopped.
    public Task Stopped
    {
        get { lock (_gate) return _looking; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _scheduler.CheckDue -= OnCheckDue;
        _stop.Cancel();
    }

    private void OnCheckDue(object? sender, CheckTicket ticket)
    {
        lock (_gate)
        {
            if (_disposed || !_looking.IsCompleted) return;
            var now = _time.GetUtcNow();
            // An answer after now means the clock went back.
            var fresh = _answeredAt is { } at && at <= now && now - at < Every;
            if (fresh && ticket.Trigger is not (CheckTrigger.Startup or CheckTrigger.Manual)) return;
            _looking = Task.Run(LookAsync);
        }
    }

    private async Task LookAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(Timeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stop.Token);
            var release = await _releases.LatestAsync(linked.Token);
            lock (_gate) _answeredAt = _time.GetUtcNow();
            _offer(release);
        }
        catch (Exception e)
        {
            if (!_stop.IsCancellationRequested) _log.Info($"Tiny Tracker's releases weren't read: {e.Message}");
        }
    }
}
