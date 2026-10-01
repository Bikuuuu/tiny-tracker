using System.Diagnostics;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Closing;

// The uninstaller closing Tiny Tracker (spec §10): every process of the app's exe in this session, whoever runs it, asked to
// close the way Quit does, and ended if it still runs after its grace. Then the helpers, which end once their app hangs up.
public static class RunningCopies
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);
    // Asked again now and then: a copy that has just started may not have its window yet.
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan EndedWithin = TimeSpan.FromSeconds(5);

    public static void Close(string exe, string helper, string windowClass, TimeSpan grace, TimeSpan helperGrace)
    {
        Close(exe, grace, id => Ask(id, windowClass));
        Close(helper, helperGrace, null);
    }

    private static void Close(string program, TimeSpan grace, Action<int>? ask)
    {
        var copies = RunningApps.OfProgram(program);
        try
        {
            var watch = Stopwatch.StartNew();
            var asked = -AskEvery;
            while (copies.Any(c => !c.Exited) && watch.Elapsed < grace)
            {
                if (ask is not null && watch.Elapsed - asked >= AskEvery)
                {
                    foreach (var copy in copies.Where(c => !c.Exited)) ask(copy.App.Id);
                    asked = watch.Elapsed;
                }
                Thread.Sleep(Every);
            }
            foreach (var copy in copies.Where(c => !c.Exited)) copy.End();
            var ending = Stopwatch.StartNew();
            while (copies.Any(c => !c.Exited) && ending.Elapsed < EndedWithin) Thread.Sleep(Every);
        }
        finally
        {
            foreach (var copy in copies) copy.Dispose();
        }
    }

    // Its windows of that class get the close request: Tiny Tracker's tray window quits on it.
    private static void Ask(int id, string windowClass)
    {
        var name = new char[256];
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == id && new string(name, 0, GetClassName(window, name, name.Length)) == windowClass)
                PostMessage(window, WmClose, 0, 0);
            return true;
        }, 0);
    }
}
