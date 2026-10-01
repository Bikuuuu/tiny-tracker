namespace TinyTracker.Core.Scheduling;

public enum CheckTrigger
{
    Startup,
    Scheduled,
    Retry,
    Resumed,
    Manual,
    FlyoutOpened,
}

// Hand it back to Finished; a check that times out gets a new ticket for its retry.
public readonly record struct CheckTicket(long Id, CheckTrigger Trigger);

// Why timed checks wait.
public enum CheckHold
{
    None,
    Offline,
    BatterySaver,
}

// One timer for the next check, no polling. Answer every CheckDue with Finished.
public sealed class CheckScheduler : IDisposable
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(10);
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private TimeSpan _interval;
    private DateTimeOffset _dueAt;
    private TimeSpan _dueDelay;
    private CheckTrigger _dueReason;
    private DateTimeOffset? _lastAttempt;
    private int _failures;
    private long _ticket;
    private long _startedAt;
    private bool _running;
    private bool _online = true;
    private bool _batterySaver;
    private bool _disposed;

    public CheckScheduler(TimeProvider time, TimeSpan interval)
    {
        _time = time;
        _interval = Checked(interval);
        SetDue(time.GetUtcNow(), StartupDelay, CheckTrigger.Startup);
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate) Plan();
    }

    public event EventHandler<CheckTicket>? CheckDue;

    public TimeSpan Interval
    {
        get { lock (_gate) return _interval; }
    }

    // Null while a check runs, and while offline or Energy saver holds checks back.
    public DateTimeOffset? NextCheck
    {
        get { lock (_gate) return _running || !_online || _batterySaver ? null : _dueAt; }
    }

    // From CheckDue until Finished, or until the watchdog gives up on the check.
    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    // Offline wins, because a check can't run without the network either way.
    public CheckHold Hold
    {
        get { lock (_gate) return !_online ? CheckHold.Offline : _batterySaver ? CheckHold.BatterySaver : CheckHold.None; }
    }

    public void CheckNow()
    {
        CheckTicket? due;
        lock (_gate) due = Begin(CheckTrigger.Manual);
        Raise(due);
    }

    // Refreshes data older than StaleAfter; skipped quietly while offline.
    public void FlyoutOpened()
    {
        CheckTicket? due = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            // A last attempt after now means the clock went back, so the data counts as stale.
            var fresh = _lastAttempt is { } last && last <= now && now - last <= StaleAfter;
            if (_online && !fresh) due = Begin(CheckTrigger.FlyoutOpened);
        }
        Raise(due);
    }

    // Timed checks wait while offline or in Battery saver, then run once both clear.
    public void SetConditions(bool online, bool batterySaver)
    {
        CheckTicket? due;
        lock (_gate)
        {
            _online = online;
            _batterySaver = batterySaver;
            due = Plan();
        }
        Raise(due);
    }

    public void SetInterval(TimeSpan interval)
    {
        Checked(interval);
        CheckTicket? due;
        lock (_gate)
        {
            _interval = interval;
            if (_dueReason == CheckTrigger.Scheduled && _lastAttempt is { } last) SetDue(last, interval, CheckTrigger.Scheduled);
            due = Plan();
        }
        Raise(due);
    }

    // After sleep or a clock change: a check that came due runs a minute later.
    public void Resumed()
    {
        CheckTicket? due;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_running && _dueAt <= now) SetDue(now, StartupDelay, CheckTrigger.Resumed);
            due = Plan();
        }
        Raise(due);
    }

    public void Finished(CheckTicket ticket, bool succeeded)
    {
        CheckTicket? due;
        lock (_gate)
        {
            // A check that already timed out answers too late.
            if (!_running || ticket.Id != _ticket) return;
            Complete(succeeded);
            due = Plan();
        }
        Raise(due);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    private void OnTimer()
    {
        CheckTicket? due;
        lock (_gate)
        {
            if (_running && !_disposed)
            {
                // A callback queued before this check began is not its watchdog.
                var elapsed = _time.GetElapsedTime(_startedAt);
                if (elapsed < CheckTimeout)
                {
                    _timer.Change(elapsed < TimeSpan.Zero ? CheckTimeout : CheckTimeout - elapsed, Timeout.InfiniteTimeSpan);
                    return;
                }
                // The check never finished: count it as failed.
                Complete(succeeded: false);
            }
            due = Plan();
        }
        Raise(due);
    }

    private void Complete(bool succeeded)
    {
        _running = false;
        var now = _time.GetUtcNow();
        _lastAttempt = now;
        if (!succeeded && _failures < RetryDelays.Count)
        {
            SetDue(now, RetryDelays[_failures++], CheckTrigger.Retry);
        }
        else
        {
            _failures = 0;
            SetDue(now, _interval, CheckTrigger.Scheduled);
        }
    }

    // Arms the timer, or starts the check when it's already due. Never arms a zero delay.
    private CheckTicket? Plan()
    {
        if (_disposed || _running) return null;
        if (!_online || _batterySaver)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return null;
        }
        var now = _time.GetUtcNow();
        // After the clock goes back, keep the intended delay instead of waiting out the jump.
        if (_dueAt - now > _dueDelay) _dueAt = now + _dueDelay;
        var wait = _dueAt - now;
        if (wait <= TimeSpan.Zero) return Begin(_dueReason);
        _timer.Change(wait, Timeout.InfiniteTimeSpan);
        return null;
    }

    // Timers can't wait longer than about 49 days; the settings offer at most a day.
    private static TimeSpan Checked(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, TimeSpan.FromDays(30));
        return interval;
    }

    private void SetDue(DateTimeOffset from, TimeSpan delay, CheckTrigger reason)
    {
        _dueAt = from + delay;
        _dueDelay = delay;
        _dueReason = reason;
    }

    private CheckTicket? Begin(CheckTrigger trigger)
    {
        if (_disposed || _running) return null;
        _running = true;
        _startedAt = _time.GetTimestamp();
        // While a check runs, the timer is its watchdog.
        _timer.Change(CheckTimeout, Timeout.InfiniteTimeSpan);
        return new CheckTicket(++_ticket, trigger);
    }

    private void Raise(CheckTicket? ticket)
    {
        if (ticket is { } t) CheckDue?.Invoke(this, t);
    }
}
