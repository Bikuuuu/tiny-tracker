using TinyTracker.Core.Settings;

namespace TinyTracker.Core.Installing;

// The download speed limit in KB/s, or 0 while it's off (spec §6.4). An update reads it as it starts, and a limited download
// follows its changes.
public sealed class SpeedLimit
{
    private int _kbps;

    public SpeedLimit(int kbps = 0) => _kbps = Checked(kbps);

    public int KBps => Volatile.Read(ref _kbps);

    public long BytesPerSecond => KBps * 1024L;

    // Raised on the thread that changed it.
    public event EventHandler? Changed;

    public void Set(int kbps)
    {
        if (Interlocked.Exchange(ref _kbps, Checked(kbps)) != kbps) Changed?.Invoke(this, EventArgs.Empty);
    }

    // As Settings has it.
    public static int Of(AppSettings settings) => settings.SpeedLimitEnabled ? settings.SpeedLimitKBps : 0;

    // None, or a limit the Settings box takes.
    public static bool IsAllowed(int kbps) => kbps == 0 || kbps is >= AppSettings.MinSpeedLimitKBps and <= AppSettings.MaxSpeedLimitKBps;

    private static int Checked(int kbps) => IsAllowed(kbps) ? kbps : throw new ArgumentOutOfRangeException(nameof(kbps), kbps, "Not a speed limit.");
}
