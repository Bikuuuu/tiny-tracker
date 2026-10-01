using TinyTracker.Core.Checking;
using TinyTracker.WinGet.Cli;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// Real winget, read-only. Skips when winget is missing, too old or doesn't answer, unless TINYTRACKER_WINGET_REQUIRED=1.
internal static class RealWinGet
{
    // A listing that reads no update's notes or elevation.
    public static IReadOnlySet<string> NoDetails { get; } = new HashSet<string>();

    public static async Task<WinGetSession> OpenAsync()
    {
        try
        {
            return await WinGetSession.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (PackageSourceException e) when (Environment.GetEnvironmentVariable("TINYTRACKER_WINGET_REQUIRED") != "1")
        {
            Assert.Skip($"winget isn't usable here: {e.Problem}");
            throw;
        }
    }

    // Its command line, as a runner's own winget, not yet updated, can fail even its version.
    public static async Task RequireCliAsync()
    {
        if (Environment.GetEnvironmentVariable("TINYTRACKER_WINGET_REQUIRED") == "1") return;
        if (!File.Exists(WinGetCli.AliasPath)) Assert.Skip("winget isn't installed here.");
        if (await WinGetCli.Real.RunAsync(["--version"], TestContext.Current.CancellationToken) is not { ExitCode: 0 })
            Assert.Skip("winget's command line doesn't answer here.");
    }
}
