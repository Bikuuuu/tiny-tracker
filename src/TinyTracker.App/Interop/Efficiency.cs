using System.Runtime.InteropServices;

namespace TinyTracker.App.Interop;

internal static class Efficiency
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;

    public static void EnterIdle()
    {
        SetEcoQoS(true);
        NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(), NativeMethods.BELOW_NORMAL_PRIORITY_CLASS);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        NativeMethods.SetProcessWorkingSetSizeEx(NativeMethods.GetCurrentProcess(), -1, -1, 0);
    }

    public static void ExitIdle()
    {
        SetEcoQoS(false);
        NativeMethods.SetPriorityClass(NativeMethods.GetCurrentProcess(), NativeMethods.NORMAL_PRIORITY_CLASS);
    }

    private static void SetEcoQoS(bool on)
    {
        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
        NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
    }
}
