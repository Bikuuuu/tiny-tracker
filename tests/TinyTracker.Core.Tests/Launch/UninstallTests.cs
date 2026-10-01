using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

// The uninstaller's --uninstall (spec §10): what takes an administrator here, the signed-in account's own things through --cleanup.
public sealed class UninstallTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Settings(bool turnedOn)
    {
        var path = _folder.PathOf("settings.json");
        File.WriteAllText(path, $$"""{"turnedOnProxyOption": {{(turnedOn ? "true" : "false")}}}""");
        return path;
    }

    [Fact]
    public void Steps_CloseFirst_ThenWhatNeedsAnAdministrator_ThenTheSignedInUsersCleanup()
    {
        var system = new FakeSystem();
        Assert.Empty(Uninstall.Run(system, Settings(turnedOn: true), removeData: true).Failed);
        Assert.Equal(["close", "tasks", "proxy off", "cleanup as the signed-in user, data too"], system.Calls);
    }

    [Fact]
    public void ProxyOption_IsLeftAsItIs_UnlessTinyTrackerTurnedItOn()
    {
        var off = new FakeSystem();
        Uninstall.Run(off, Settings(turnedOn: false), removeData: false);
        var missing = new FakeSystem();
        Uninstall.Run(missing, _folder.PathOf("none.json"), removeData: false);
        Assert.Equal(["close", "tasks", "cleanup as the signed-in user"], off.Calls);
        Assert.Equal(off.Calls, missing.Calls);
    }

    // A remote or scripted uninstall has no desktop.
    [Fact]
    public void WithoutAShell_ItCleansUpHere()
    {
        var system = new FakeSystem { Shell = false };
        Assert.Empty(Uninstall.Run(system, Settings(turnedOn: false), removeData: true).Failed);
        Assert.Equal(["close", "tasks", "cleanup as the signed-in user, data too", "cleanup here, data too"], system.Calls);
    }

    [Fact]
    public void FailedSteps_DontStopTheOthers_AndAreReported()
    {
        var system = new FakeSystem { CloseThrows = true, TasksThrow = true, ProxyStaysOn = true };
        Assert.Equal(["close", "tasks", "proxy option"], Uninstall.Run(system, Settings(turnedOn: true), removeData: false).Failed);
        Assert.Equal(["close", "tasks", "proxy off", "cleanup as the signed-in user"], system.Calls);
    }

    // A path it can't even open fails only its own step.
    [Fact]
    public void SettingsItCantRead_FailOnlyTheirStep()
    {
        var system = new FakeSystem();
        Assert.Equal(["proxy option"], Uninstall.Run(system, "settings\0.json", removeData: false).Failed);
        Assert.Equal(["close", "tasks", "cleanup as the signed-in user"], system.Calls);
    }

    [Fact]
    public void CleanupHereThatFails_IsReported()
    {
        var system = new FakeSystem { Shell = false, HereFails = true };
        Assert.Equal(["cleanup"], Uninstall.Run(system, Settings(turnedOn: false), removeData: false).Failed);
    }

    // The exit code tells the uninstaller's log which cleanup ran, and the install test checks it's the shell's (spec §10, §12).
    [Theory]
    [InlineData(true, false, CleanupRoute.Shell, 0)]
    [InlineData(false, false, CleanupRoute.Here, 2)]
    [InlineData(false, true, CleanupRoute.Here, 1)]
    public void Outcome_SaysWhichCleanupRan(bool shell, bool hereFails, CleanupRoute route, int exitCode)
    {
        var outcome = Uninstall.Run(new FakeSystem { Shell = shell, HereFails = hereFails }, Settings(turnedOn: false), removeData: false);
        Assert.Equal((route, exitCode), (outcome.Cleanup, outcome.ExitCode));
    }

    [Fact]
    public void AnyFailedStep_ExitsWith1()
    {
        var outcome = Uninstall.Run(new FakeSystem { TasksThrow = true }, Settings(turnedOn: false), removeData: false);
        Assert.Equal((CleanupRoute.Shell, 1), (outcome.Cleanup, outcome.ExitCode));
    }

    // Reading it must not set a damaged file aside, as a load would.
    [Fact]
    public void TurnedOnProxyOption_IsReadWithoutChangingTheFile()
    {
        var damaged = _folder.PathOf("damaged.json");
        File.WriteAllText(damaged, "{ not json");
        Assert.False(TinyTracker.Core.Storage.SettingsFile.TurnedOnProxyOptionIn(damaged));
        Assert.Equal(["damaged.json"], Directory.GetFiles(_folder.Root).Select(Path.GetFileName));
        Assert.True(TinyTracker.Core.Storage.SettingsFile.TurnedOnProxyOptionIn(Settings(turnedOn: true)));
    }

    private sealed class FakeSystem : IUninstallSystem
    {
        public List<string> Calls { get; } = [];
        public bool Shell { get; init; } = true;
        public bool CloseThrows { get; init; }
        public bool TasksThrow { get; init; }
        public bool ProxyStaysOn { get; init; }
        public bool HereFails { get; init; }

        public void CloseRunningCopies()
        {
            Calls.Add("close");
            if (CloseThrows) throw new InvalidOperationException("close");
        }

        public void RemoveEveryTask()
        {
            Calls.Add("tasks");
            if (TasksThrow) throw new UnauthorizedAccessException("tasks");
        }

        public bool TurnProxyOptionOff()
        {
            Calls.Add("proxy off");
            return !ProxyStaysOn;
        }

        public bool CleanUpAsSignedInUser(bool removeData)
        {
            Calls.Add(removeData ? "cleanup as the signed-in user, data too" : "cleanup as the signed-in user");
            return Shell;
        }

        public bool CleanUpHere(bool removeData)
        {
            Calls.Add(removeData ? "cleanup here, data too" : "cleanup here");
            return !HereFails;
        }
    }
}
