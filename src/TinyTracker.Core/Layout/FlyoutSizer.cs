namespace TinyTracker.Core.Layout;

// When the flyout takes a new height (spec §4.2): at once, except a shrink while something leaves a list, which waits until
// the rest has moved into place, so nothing is cut off. Used on the UI thread; the timer posts there.
public sealed class FlyoutSizer : IDisposable
{
    // WinUI's delete transition moves the rest of a list into place about 0.4 s after the item goes.
    public static readonly TimeSpan ShrinkWait = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action<int> _resize;
    private readonly ITimer _timer;
    private int _height;
    private int _waiting = -1;
    private long _since;

    public FlyoutSizer(TimeProvider time, Action<Action> post, Action<int> resize)
    {
        _time = time;
        _post = post;
        _resize = resize;
        _timer = time.CreateTimer(_ => _post(Due), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // The height the window has now, taken without a resize, as when it opens.
    public void Reset(int height)
    {
        Drop();
        _height = height;
    }

    public void Request(int height, bool atOnce)
    {
        if (height < _height && !atOnce)
        {
            _waiting = height;
            _since = _time.GetTimestamp();
            _timer.Change(ShrinkWait, Timeout.InfiniteTimeSpan);
            return;
        }
        Drop();
        if (height == _height) return;
        _height = height;
        _resize(height);
    }

    // A shrink that came since the timer fired waits the rest of its own time.
    private void Due()
    {
        if (_waiting < 0) return;
        var left = ShrinkWait - _time.GetElapsedTime(_since);
        if (left > TimeSpan.Zero)
        {
            _timer.Change(left, Timeout.InfiniteTimeSpan);
            return;
        }
        _height = _waiting;
        _waiting = -1;
        _resize(_height);
    }

    private void Drop()
    {
        _waiting = -1;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _timer.Dispose();
}
