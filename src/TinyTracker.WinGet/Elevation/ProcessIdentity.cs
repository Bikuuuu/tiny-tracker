using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Elevation;

// Who a process is: its program's full path, and whether it runs elevated.
internal static class ProcessIdentity
{
    private const int TokenElevation = 20;
    private const int TokenElevationType = 18;
    private const int ElevationTypeLimited = 3;

    // The process serving a pipe. Null when Windows won't say.
    public static (string Path, bool Elevated)? ServerOf(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var id) ? Of(id) : null;

    public static (string Path, bool Elevated)? Of(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || PathOf(process) is not { } path) return null;
        return (path, IsElevated(process));
    }

    // An administrator's account: unelevated behind UAC (a split token), elevated, or with UAC off.
    public static bool IsAdminAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        return GetTokenInformation(identity.AccessToken, TokenElevationType, out var type, sizeof(int), out _) && type == ElevationTypeLimited;
    }

    private static bool IsElevated(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        using (token) return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
    }
}
