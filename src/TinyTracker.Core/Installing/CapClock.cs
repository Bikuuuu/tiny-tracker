namespace TinyTracker.Core.Installing;

// The 30-minute cap on one update (spec §7). Time spent downloading doesn't count: under the speed limit a big download can
// take hours, and one that stops moving is caught as stalled. Callers serialize access.
internal sealed class CapClock : IDisposable
{
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _reached = new();
    private TimeSpan _left;
    private long _since;
    private bool _paused;
    private bool _disposed;

    public CapClock(TimeSpan cap, TimeProvider time)
    {
        _time = time;
        _left = cap;
        _since = time.GetTimestamp();
        _timer = time.CreateTimer(_ => Reach(), null, cap, Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token => _reached.Token;

    public bool Reached => _reached.IsCancellationRequested;

    // Stops counting while a download runs, and counts on once it doesn't.
    public void Downloading(bool downloading)
    {
        if (_disposed || downloading == _paused) return;
        _paused = downloading;
        if (downloading)
        {
            _left -= _time.GetElapsedTime(_since);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _since = _time.GetTimestamp();
            _timer.Change(_left > TimeSpan.Zero ? _left : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _reached.Dispose();
    }

    private void Reach()
    {
        try
        {
            _reached.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
