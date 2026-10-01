using System.Runtime.InteropServices;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.SelfUpdate;

// Whether Tiny Tracker's Setup runs: it holds the mutex TinyTracker.iss names, which Inno Setup lets every account open.
public static class SetupMutex
{
    public const string Name = @"Global\TinyTrackerSetup";
    private const int AccessDenied = 5;

    // A mutex it may not open is held too.
    public static bool Held(string name = Name)
    {
        var handle = OpenMutex(Synchronize, false, name);
        if (handle != 0)
        {
            CloseHandle(handle);
            return true;
        }
        return Marshal.GetLastPInvokeError() == AccessDenied;
    }
}
