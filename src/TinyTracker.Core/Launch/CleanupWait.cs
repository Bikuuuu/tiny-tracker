namespace TinyTracker.Core.Launch;

// How the uninstaller waits for the --cleanup the shell started, which gives back no process to wait on, so it's looked for in
// the process list (spec §10). The uninstall goes on whatever the outcome.
public static class CleanupWait
{
    public enum Outcome { Ended, NeverSeen, StillRunning }

    public static readonly TimeSpan Appears = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Runs = TimeSpan.FromSeconds(60);
    // Often enough that a quick cleanup can't come and go unseen.
    public static readonly TimeSpan Every = TimeSpan.FromMilliseconds(25);

    public static Outcome For(Func<bool> running, Func<TimeSpan> elapsed, Action pause)
    {
        var seen = false;
        while (true)
        {
            if (running()) seen = true;
            else if (seen) return Outcome.Ended;
            else if (elapsed() >= Appears) return Outcome.NeverSeen;
            if (elapsed() >= Appears + Runs) return Outcome.StillRunning;
            pause();
        }
    }
}
