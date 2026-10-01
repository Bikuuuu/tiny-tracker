using TinyTracker.Core.Storage;

namespace TinyTracker.Core.Launch;

// What --uninstall does on Windows. It runs elevated, as the account that approved the uninstaller's prompt.
public interface IUninstallSystem
{
    // This copy wherever it runs in this session: closed the way Quit does, ended if it still runs 10 s later, and its helper
    // waited for.
    void CloseRunningCopies();

    // Every account's silent-mode task, and the folder they're in.
    void RemoveEveryTask();

    // winget's proxy option, for the account this runs as. False when it still reads on.
    bool TurnProxyOptionOff();

    // Runs --cleanup as the account signed in at the PC, unelevated, through the Windows shell, and waits for it. False when the
    // shell can't.
    bool CleanUpAsSignedInUser(bool removeData);

    // With no shell: --cleanup's work here, for the account this runs as. False when part of it failed.
    bool CleanUpHere(bool removeData);
}

// Which cleanup ran: the signed-in account's through the Windows shell, or with no shell, the one here. None when neither got to.
public enum CleanupRoute
{
    None,
    Shell,
    Here,
}

// The steps that failed, and which cleanup ran.
public sealed record UninstallOutcome(IReadOnlyList<string> Failed, CleanupRoute Cleanup)
{
    // --uninstall's, which the uninstaller logs: 0 once all is done through the shell, 2 with no shell, 1 when a step failed.
    public int ExitCode => Failed.Count > 0 ? 1 : Cleanup == CleanupRoute.Shell ? 0 : 2;
}

// Uninstalling (spec §10): what takes an administrator happens here; the signed-in account's own things go through --cleanup,
// with that account's own rights. Every step runs even when another fails.
public static class Uninstall
{
    // ownSettings: the settings of the account this runs as, which say whether Tiny Tracker turned its proxy option on.
    public static UninstallOutcome Run(IUninstallSystem system, string ownSettings, bool removeData)
    {
        var failed = new List<string>();
        void Step(string name, Func<bool> step)
        {
            try
            {
                if (!step()) failed.Add(name);
            }
            catch (Exception)
            {
                failed.Add(name);
            }
        }
        Step("close", () =>
        {
            system.CloseRunningCopies();
            return true;
        });
        Step("tasks", () =>
        {
            system.RemoveEveryTask();
            return true;
        });
        // Read before the cleanup can delete it.
        Step("proxy option", () => !SettingsFile.TurnedOnProxyOptionIn(ownSettings) || system.TurnProxyOptionOff());
        var route = CleanupRoute.None;
        Step("cleanup", () =>
        {
            if (system.CleanUpAsSignedInUser(removeData))
            {
                route = CleanupRoute.Shell;
                return true;
            }
            route = CleanupRoute.Here;
            return system.CleanUpHere(removeData);
        });
        return new UninstallOutcome(failed, route);
    }
}
