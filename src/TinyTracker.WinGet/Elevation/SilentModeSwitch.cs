using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;

namespace TinyTracker.WinGet.Elevation;

// Turns silent mode's task on and off through the helper (spec §6.6): one prompt to turn it on, and none to turn it off, because
// the task itself starts the helper that removes it. prompt starts the helper with a UAC prompt, whatever silent mode says.
public sealed class SilentModeSwitch(IElevation elevation, IElevation prompt, string helperPath) : ISilentMode
{
    // Silent mode works from Program Files only, and on an administrator's account.
    public bool InstalledHere => AppFolders.Holds(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), helperPath);

    public static bool AdminAccount => ProcessIdentity.IsAdminAccount();

    public SilentModeAvailability Availability =>
        !AdminAccount ? SilentModeAvailability.NotAdmin : !InstalledHere ? SilentModeAvailability.NotInstalled : SilentModeAvailability.Available;

    public bool TaskExists => SilentTask.Exists(helperPath, WindowsIdentity.GetCurrent().User!);

    public Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct) => ChangeAsync(prompt, client => client.RegisterTaskAsync(ct), ct);

    // No task needs nothing. The task starts the helper that removes it; when that fails, or the task runs another copy's helper,
    // one prompt does it.
    public async Task<(SwitchResult Result, string? Code)> TurnOffAsync(CancellationToken ct)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        if (!SilentTask.Registered(user)) return (SwitchResult.Done, null);
        if (SilentTask.Exists(helperPath, user))
        {
            var viaTask = await ChangeAsync(elevation, client => client.RemoveTaskAsync(ct), ct);
            if (viaTask.Result != SwitchResult.Failed) return viaTask;
        }
        return await ChangeAsync(prompt, client => client.RemoveTaskAsync(ct), ct);
    }

    private static async Task<(SwitchResult Result, string? Code)> ChangeAsync(IElevation starter, Func<HelperClient, Task<string?>> change, CancellationToken ct)
    {
        var start = await starter.StartAsync(mayPrompt: true, ct);
        if (start.Result == HelperStartResult.Declined) return (SwitchResult.Declined, null);
        if (start.Session is not HelperClient client)
        {
            start.Session?.Dispose();
            return (SwitchResult.Failed, start.Code);
        }
        using (client)
        {
            var error = await change(client);
            return error is null ? (SwitchResult.Done, null) : (SwitchResult.Failed, error);
        }
    }
}
