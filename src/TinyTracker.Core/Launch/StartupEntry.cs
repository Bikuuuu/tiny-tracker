namespace TinyTracker.Core.Launch;

// Start with Windows (spec §6.7). On means the Run value starts this exe with --startup and Task Manager hasn't turned it off.
public sealed class StartupEntry(IStartupValues values, string exe)
{
    public bool IsOn =>
        string.Equals(values.ReadRun()?.Trim(), StartupCommand.Format(exe), StringComparison.OrdinalIgnoreCase)
        && IsApproved(values.ReadApproved());

    // Turning it on here also clears Task Manager's off mark: the user just asked for it.
    public void Set(bool on)
    {
        if (!on)
        {
            Remove();
            return;
        }
        values.WriteRun(StartupCommand.Format(exe));
        values.DeleteApproved();
    }

    // Only values that start this exe go, so one copy never removes another's.
    public void Remove()
    {
        var run = values.ReadRun();
        if (run is not null)
        {
            if (StartupCommand.Target(run) is not { } target || !string.Equals(Path.GetFullPath(target), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return;
            values.DeleteRun();
        }
        values.DeleteApproved();
    }

    // Windows writes 02 or 06 first while the entry may run, and an odd value such as 03 once it's turned off.
    public static bool IsApproved(byte[]? mark) => mark is not { Length: > 0 } || (mark[0] & 1) == 0;
}
