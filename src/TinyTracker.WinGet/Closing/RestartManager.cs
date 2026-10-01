using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Closing;

// Windows' Restart Manager, which installers use to ask apps to close: windowed apps get their normal close, with the chance to
// save, and apps with only hidden windows, such as tray apps, the request Windows sends at sign-out. It never ends an app.
internal sealed class RestartManager : IDisposable
{
    private const int SessionKeyLength = 33;
    private readonly uint _session;

    private RestartManager(uint session) => _session = session;

    // A session for these very processes; null when Windows won't start one, and error says why.
    public static RestartManager? For(IReadOnlyList<RunningApp> apps, out int error)
    {
        var key = new char[SessionKeyLength];
        if ((error = RmStartSession(out var session, 0, key)) != 0) return null;
        var processes = apps.Select(a => new UniqueProcess { ProcessId = a.Id, StartTime = a.StartTime }).ToArray();
        if ((error = RmRegisterResources(session, 0, null, (uint)processes.Length, processes, 0, null)) == 0) return new RestartManager(session);
        RmEndSession(session);
        return null;
    }

    // Asks, and waits for the apps that answer; it gives up on the others. It blocks, so callers run it on a worker.
    public void AskToClose() => RmShutdown(_session, 0, 0);

    // Stops the asking, from another thread.
    public void Cancel() => RmCancelCurrentTask(_session);

    public void Dispose() => RmEndSession(_session);
}
