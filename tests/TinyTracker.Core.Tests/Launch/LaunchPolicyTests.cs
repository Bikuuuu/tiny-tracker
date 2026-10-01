using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

public class LaunchPolicyTests
{
    [Fact]
    public void SplitTokenElevated_Relaunches() =>
        Assert.True(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Full, ["--startup"]));

    [Fact]
    public void UacOffOrBuiltInAdmin_RunsInPlaceInsteadOfLooping() =>
        Assert.False(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Default, []));

    [Fact]
    public void NormalUser_RunsInPlace() =>
        Assert.False(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Limited, []));

    // An elevated shell would start it elevated again, so the copy it starts runs as it is.
    [Fact]
    public void CopyTheShellStarted_RunsAsItIs_EvenElevated() =>
        Assert.False(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Full, ["--startup", LaunchPolicy.RelaunchedFlag]));

    [Fact]
    public void Relaunch_KeepsTheArguments_AndMarksTheCopy()
    {
        Assert.Equal(["--self-check", @"D:\check.json", LaunchPolicy.RelaunchedFlag], LaunchPolicy.RelaunchArguments(["--self-check", @"D:\check.json"]));
        Assert.True(LaunchPolicy.OpenFlyoutOnLaunch(LaunchPolicy.RelaunchArguments([])));
        Assert.Equal(MaintenanceVerb.None, LaunchPolicy.MaintenanceVerbOf(LaunchPolicy.RelaunchArguments([])));
    }

    // A bare name is looked for in the current folder first, which whoever started the elevated copy chose; another shell can own
    // the taskbar; and an Explorer the elevated copy starts would be an elevated shell.
    [Fact]
    public void Fallback_IsTheExplorerThatOwnsTheTaskbar_ByItsFullPath()
    {
        Assert.Equal(@"C:\Windows\explorer.exe", LaunchPolicy.ExplorerFallback(@"C:\Windows", program => program == @"C:\Windows\explorer.exe"));
        Assert.Null(LaunchPolicy.ExplorerFallback(@"C:\Windows", _ => false));
    }

    [Theory]
    [InlineData("--cleanup", MaintenanceVerb.Cleanup)]
    [InlineData("--cleanup-notifications", MaintenanceVerb.Cleanup)]
    [InlineData("--start-with-windows", MaintenanceVerb.StartWithWindows)]
    [InlineData("--uninstall", MaintenanceVerb.Uninstall)]
    [InlineData("--startup", MaintenanceVerb.None)]
    public void MaintenanceVerbs_RunBeforeElevationCheck(string arg, MaintenanceVerb expected) =>
        Assert.Equal(expected, LaunchPolicy.MaintenanceVerbOf([arg]));

    // The answer to "Also remove your settings and history?".
    [Theory]
    [InlineData(new[] { "--uninstall", "--remove-data" }, true)]
    [InlineData(new[] { "--cleanup", "--remove-data" }, true)]
    [InlineData(new[] { "--uninstall" }, false)]
    public void RemovingTheData_TakesItsOwnSwitch(string[] args, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.RemovesData(args));

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--startup" }, false)]
    public void FlyoutOpensOnLaunch_UnlessStartup(string[] args, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.OpenFlyoutOnLaunch(args));

    // The Run entry and Windows' restart can both start the app at sign-in (spec §6.7).
    [Theory]
    [InlineData(@"""C:\Program Files\Tiny Tracker\TinyTracker.exe"" --startup", false)]
    [InlineData(@"""C:\Program Files\Tiny Tracker\TinyTracker.exe""", true)]
    [InlineData(@"C:\Tools\TinyTracker.exe --startup", false)]
    [InlineData(@"""C:\Tools\TinyTracker.exe"" --self-check out.json", true)]
    public void SecondStart_OpensTheFlyout_UnlessStartup(string commandLine, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.OpenFlyoutOnSecondStart(commandLine));

    [Theory]
    [InlineData(new[] { "--demo" }, true)]
    [InlineData(new[] { "--startup", "--demo" }, true)]
    [InlineData(new[] { "--startup" }, false)]
    [InlineData(new[] { "demo" }, false)]
    public void Demo_NeedsTheDemoFlag(string[] args, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.IsDemo(args));
}
