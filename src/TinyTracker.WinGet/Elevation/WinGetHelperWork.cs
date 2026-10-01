using System.Net.Http.Headers;
using System.Security.Principal;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.SelfUpdate;

namespace TinyTracker.WinGet.Elevation;

// The elevated helper's winget: the app's own upgrade, which re-queries the package and checks the version (spec §8), under the
// limit the app gave it. user is the app's, from the helper's command line.
public sealed class WinGetHelperWork(SecurityIdentifier user) : IHelperWork
{
    public async Task<string?> OpenAsync(CancellationToken ct)
    {
        try
        {
            await WinGetSession.OpenAsync(ct);
            return null;
        }
        catch (PackageSourceException e)
        {
            return e.Code ?? e.Problem.ToString();
        }
    }

    public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct) =>
        new WinGetUpgrader(limit).UpgradeAsync(package, version, progress, ct);

    public string? RegisterTask()
    {
        var path = Environment.ProcessPath ?? "";
        return SilentTask.WhyNotRegister(path, WindowsIdentity.GetCurrent().User!, user) ?? SilentTask.Register(path, user);
    }

    public string? RemoveTask() => SilentTask.Remove(user);

    public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => WinGetSettings.EnableProxyOptionAsync(WinGetCli.Real, ct);

    // Only from Tiny Tracker's own folder in Program Files, which only administrators can write and Setup replaces, as the app
    // checks (spec §6.5, §8). Redirects are followed by hand, to GitHub's hosts only.
    public async Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        var helper = Environment.ProcessPath ?? "";
        if (!AppFolders.DirectlyIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.Name), helper))
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "not in Tiny Tracker's folder");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TinyTracker", typeof(WinGetHelperWork).Assembly.GetName().Version?.ToString(3)));
        var handOff = new SetupHandOff(new GitHubReleases(http, TimeProvider.System), http, Path.GetDirectoryName(helper)!, new SetupSystem(), TimeProvider.System);
        return await handOff.RunAsync(version, limit, progress, ct);
    }
}
