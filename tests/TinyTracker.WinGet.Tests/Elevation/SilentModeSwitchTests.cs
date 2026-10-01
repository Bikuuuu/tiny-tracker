using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// Turning silent mode on takes the helper and one prompt; turning it off with no task takes nothing (spec §6.6).
public sealed class SilentModeSwitchTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly string Here = Environment.ProcessPath!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The account running the tests has no task: an installed copy's silent mode would have made one.
    [Fact]
    public async Task TurningOff_WithNoTask_NeedsNoHelper()
    {
        Assert.SkipWhen(SilentTask.Registered(Me), "This account's silent mode is on here, so its task is there.");
        var elevation = new CannedStart(new HelperStart(HelperStartResult.Failed));
        Assert.Equal((SwitchResult.Done, (string?)null), await new SilentModeSwitch(elevation, elevation, Here).TurnOffAsync(Ct));
        Assert.Equal(0, elevation.Starts);
    }

    [Fact]
    public async Task TurningOn_TakesAPrompt_ThatTheUserMayDecline()
    {
        var task = new CannedStart(new HelperStart(HelperStartResult.Failed));
        var prompt = new CannedStart(new HelperStart(HelperStartResult.Declined));
        Assert.Equal((SwitchResult.Declined, (string?)null), await new SilentModeSwitch(task, prompt, Here).TurnOnAsync(Ct));
        Assert.Equal((0, 1), (task.Starts, prompt.Starts));
    }

    [Fact]
    public async Task TurningOn_WhenTheHelperDidntStart_SaysWhy()
    {
        var failed = new CannedStart(new HelperStart(HelperStartResult.Failed, Code: "0x80070002"));
        Assert.Equal((SwitchResult.Failed, "0x80070002"), await new SilentModeSwitch(failed, failed, Here).TurnOnAsync(Ct));
    }

    [Theory]
    [InlineData(null, SwitchResult.Done)]
    [InlineData("not in Program Files", SwitchResult.Failed)]
    public async Task TurningOn_AsksTheHelperToRegisterTheTask(string? answer, SwitchResult result)
    {
        var work = new FakeWork { TaskAnswer = answer };
        var name = HelperRules.NewPipeName();
        using var pipe = HelperPipe.Create(name, Me);
        var server = new HelperServer(work, TimeProvider.System).RunAsync(pipe, Ct);
        var client = await HelperClient.ConnectAsync(name, Here, false, Wait, Ct);
        var started = new CannedStart(new HelperStart(HelperStartResult.Started, client));
        Assert.Equal((result, answer), await new SilentModeSwitch(started, started, Here).TurnOnAsync(Ct));
        // The switch hangs up once it's done.
        await server.WaitAsync(Wait, Ct);
        Assert.Equal(["register"], work.TaskChanges);
    }

    [Fact]
    public void SilentMode_IsForAnInstalledCopyOnly()
    {
        var none = new CannedStart(new HelperStart(HelperStartResult.Failed));
        Assert.True(new SilentModeSwitch(none, none, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tiny Tracker", "TinyTracker.Helper.exe")).InstalledHere);
        var here = new SilentModeSwitch(none, none, Here);
        Assert.False(here.InstalledHere);
        Assert.Equal(SilentModeSwitch.AdminAccount ? SilentModeAvailability.NotInstalled : SilentModeAvailability.NotAdmin, here.Availability);
    }

    private sealed class CannedStart(HelperStart start) : IElevation
    {
        public int Starts { get; private set; }

        public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct)
        {
            Starts++;
            return Task.FromResult(start);
        }
    }
}
