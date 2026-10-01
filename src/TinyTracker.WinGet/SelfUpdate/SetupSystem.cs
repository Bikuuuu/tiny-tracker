using System.Diagnostics;
using TinyTracker.WinGet.Closing;

namespace TinyTracker.WinGet.SelfUpdate;

// What the elevated helper's self-update sees of Windows.
public sealed class SetupSystem : ISetupSystem
{
    public bool RunsInAnotherSession(string exe) => RunningApps.SessionsOf(exe).Any(s => s != RunningApps.OwnSession);

    public bool OtherCopiesRun(string exe) => RunningApps.Of(exe).Count > 0;

    public void Start(string setup, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)! };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Setup didn't start.");
    }
}
