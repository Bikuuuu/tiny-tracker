using System.ComponentModel;
using System.Runtime.InteropServices;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Elevation;

// Time the PC was awake: Windows' unbiased interrupt time, which leaves out sleep and hibernation, in 100 ns ticks. The helper's
// idle wait counts only that (spec §5.1); its timers still run on real time, so one due during a sleep looks again at resume.
public sealed class AwakeTime : TimeProvider
{
    public static AwakeTime Instance { get; } = new();

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => QueryUnbiasedInterruptTime(out var time) ? (long)time : throw new Win32Exception(Marshal.GetLastPInvokeError());
}
