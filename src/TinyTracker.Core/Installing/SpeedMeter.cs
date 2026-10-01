namespace TinyTracker.Core.Installing;

// Download speed as a rolling average over the last three seconds. Callers serialize access.
public sealed class SpeedMeter(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly List<(long At, ulong Bytes)> _samples = [];

    // Measured from the newest sample a window old, so the speed falls to zero when bytes stop coming.
    public double BytesPerSecond
    {
        get
        {
            if (_samples.Count == 0) return 0;
            var now = time.GetTimestamp();
            var from = _samples[0];
            foreach (var sample in _samples)
                if (time.GetElapsedTime(sample.At, now) >= Window) from = sample;
            var seconds = time.GetElapsedTime(from.At, now).TotalSeconds;
            return seconds <= 0 ? 0 : (_samples[^1].Bytes - from.Bytes) / seconds;
        }
    }

    public void Add(ulong bytes)
    {
        // Fewer bytes than before means the download started over.
        if (_samples.Count > 0 && bytes < _samples[^1].Bytes) _samples.Clear();
        var now = time.GetTimestamp();
        _samples.Add((now, bytes));
        while (_samples.Count > 2 && time.GetElapsedTime(_samples[1].At, now) >= Window) _samples.RemoveAt(0);
    }
}
