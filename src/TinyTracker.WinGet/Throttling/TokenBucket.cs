namespace TinyTracker.WinGet.Throttling;

public sealed class TokenBucket(TimeProvider time)
{
    private readonly Lock _gate = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _last = time.GetTimestamp();

    public long BytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond; }
        set
        {
            lock (_gate)
            {
                Refill();
                _bytesPerSecond = Math.Max(0, value);
                _tokens = _bytesPerSecond == 0 ? 0 : Math.Min(_tokens, _bytesPerSecond);
            }
        }
    }

    public TimeSpan Take(int bytes)
    {
        lock (_gate)
        {
            if (_bytesPerSecond == 0) return TimeSpan.Zero;
            Refill();
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _bytesPerSecond);
        }
    }

    // Remaining wait for debt already taken; reflects limit changes immediately.
    public TimeSpan PendingDelay()
    {
        lock (_gate)
        {
            if (_bytesPerSecond == 0) return TimeSpan.Zero;
            Refill();
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _bytesPerSecond);
        }
    }

    // Read size so one grant never exceeds a tenth of a second of data.
    public static int ChunkSize(long bytesPerSecond) =>
        bytesPerSecond == 0 ? MaxChunk : (int)Math.Clamp(bytesPerSecond / 10, 1, MaxChunk);

    private const int MaxChunk = 16 * 1024;

    private void Refill()
    {
        var now = time.GetTimestamp();
        var elapsed = time.GetElapsedTime(_last, now).TotalSeconds;
        _last = now;
        // Burst is capped at one second of data.
        _tokens = Math.Min(_bytesPerSecond, _tokens + elapsed * _bytesPerSecond);
    }
}
