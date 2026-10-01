namespace TinyTracker.Core.Checking;

// Why a check didn't finish. WinGetMissing and WinGetTooOld get the "winget needs an update" banner.
public enum CheckProblem
{
    None,
    WinGetMissing,
    WinGetTooOld,
    // "Can't reach winget right now, retrying"
    WinGetUnreachable,
    TimedOut,
    SettingsNotSaved,
    Failed,
}

// A package source's way to say winget couldn't answer.
public sealed class PackageSourceException(CheckProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public CheckProblem Problem { get; } = problem;

    // The HRESULT behind Details, when a call failed.
    public string? Code => InnerException is { } e ? $"0x{e.HResult:X8}" : null;

    // What Details shows: the message, and the code when there is one.
    public string Detail => Code is null ? Message : $"{Message} ({Code})";
}
