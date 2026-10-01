using TinyTracker.Core.Launch;

namespace TinyTracker.App.Interop;

internal static class ProcessInfo
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationTypeClass = 18;

    public static ElevationType GetElevationType()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TOKEN_QUERY, out var token)) return ElevationType.Default;
        try
        {
            return NativeMethods.GetTokenInformation(token, TokenElevationTypeClass, out var type, sizeof(int), out _)
                ? (ElevationType)type
                : ElevationType.Default;
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }
}
