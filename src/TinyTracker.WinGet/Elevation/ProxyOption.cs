using Microsoft.Win32;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.WinGet.Cli;

namespace TinyTracker.WinGet.Elevation;

// winget's proxy option for the speed limit's switch (spec §6.4): read as this user, and turned on through the admin helper, with
// a prompt or through silent mode's task.
public sealed class ProxyOption(WinGetCli cli, IElevation elevation) : IProxyOption
{
    private const string Policies = @"SOFTWARE\Policies\Microsoft\Windows\AppInstaller";

    public SpeedLimitAvailability Availability
    {
        get
        {
            using var policies = Registry.LocalMachine.OpenSubKey(Policies);
            int? Policy(string name) => policies?.GetValue(name) as int?;
            return AvailabilityOf(Policy("EnableAppInstaller"), Policy("EnableWindowsPackageManagerCommandLineInterfaces"), Policy("EnableSettings"),
                Policy("EnableWindowsPackageManagerProxyCommandLineOptions"), ProcessIdentity.IsAdminAccount());
        }
    }

    public bool Prompts => elevation.Prompts;

    // From winget's policies, where 1 turns a setting on for everyone and 0 off, and the account.
    public static SpeedLimitAvailability AvailabilityOf(int? winGet, int? commandLine, int? settings, int? proxyOption, bool adminAccount) =>
        winGet == 0 || commandLine == 0 || proxyOption == 0 ? SpeedLimitAvailability.Blocked
        : proxyOption == 1 ? SpeedLimitAvailability.Available
        : settings == 0 ? SpeedLimitAvailability.Blocked
        : adminAccount ? SpeedLimitAvailability.Available
        : SpeedLimitAvailability.NotAdmin;

    public Task<bool?> IsOnAsync(CancellationToken ct) => WinGetSettings.ProxyOptionAsync(cli, ct);

    public async Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct)
    {
        var start = await elevation.StartAsync(mayPrompt: true, ct);
        if (start.Result == HelperStartResult.Declined) return (SwitchResult.Declined, null);
        if (start.Session is not { } session) return (SwitchResult.Failed, start.Code);
        string? error;
        using (session) error = await session.EnableProxyOptionAsync(ct);
        if (error is not null) return (SwitchResult.Failed, error);
        // The helper turned it on for the user it ran as, who may be another administrator.
        return await IsOnAsync(ct) == true ? (SwitchResult.Done, null) : (SwitchResult.Failed, "still off for this user");
    }
}
