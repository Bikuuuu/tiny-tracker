using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Cli;
using TinyTracker.WinGet.Elevation;
using TinyTracker.WinGet.Tests.Cli;
using Xunit;
using static TinyTracker.WinGet.Tests.Cli.FakeWinGet;

namespace TinyTracker.WinGet.Tests.Elevation;

// winget's proxy option, as the speed limit's switch turns it on (spec §6.4).
public sealed class ProxyOptionTests
{
    private const string On = """{"adminSettings":{"ProxyCommandLineOptions":true}}""";
    private const string Off = """{"adminSettings":{"ProxyCommandLineOptions":false}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A policy's 1 turns a setting on for everyone, and its 0 off.
    [Theory]
    [InlineData(null, null, null, null, true, SpeedLimitAvailability.Available)]
    [InlineData(1, 1, 1, null, true, SpeedLimitAvailability.Available)]
    [InlineData(null, null, null, null, false, SpeedLimitAvailability.NotAdmin)]
    [InlineData(null, null, null, 1, false, SpeedLimitAvailability.Available)]
    [InlineData(null, null, 0, 1, false, SpeedLimitAvailability.Available)]
    [InlineData(null, null, null, 0, true, SpeedLimitAvailability.Blocked)]
    [InlineData(null, 0, null, null, true, SpeedLimitAvailability.Blocked)]
    [InlineData(null, 0, null, 1, true, SpeedLimitAvailability.Blocked)]
    [InlineData(0, null, null, null, true, SpeedLimitAvailability.Blocked)]
    [InlineData(null, null, 0, null, true, SpeedLimitAvailability.Blocked)]
    public void Availability_FollowsWinGetsPoliciesAndTheAccount(int? winGet, int? commandLine, int? settings, int? proxyOption, bool admin, SpeedLimitAvailability availability) =>
        Assert.Equal(availability, ProxyOption.AvailabilityOf(winGet, commandLine, settings, proxyOption, admin));

    [Fact]
    public void ThisPc_HasAnAvailability() => Assert.True(Enum.IsDefined(new ProxyOption(WinGetCli.Real, new FakeHelper(HelperStartResult.Declined)).Availability));

    [Fact]
    public async Task TurningOn_GoesThroughTheHelper_AndIsDoneOnceItReadsOnForThisUser()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        var helper = new FakeHelper(HelperStartResult.Started);
        Assert.Equal((SwitchResult.Done, (string?)null), await new ProxyOption(fake.Cli, helper).TurnOnAsync(Ct));
        Assert.Equal((1, true, true), (helper.Enables, helper.Disposed, helper.MayPrompt));
        Assert.Equal(["settings", "export"], fake.Arguments);
    }

    [Fact]
    public async Task DeclinedPrompt_IsDeclined()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        Assert.Equal((SwitchResult.Declined, (string?)null), await new ProxyOption(fake.Cli, new FakeHelper(HelperStartResult.Declined)).TurnOnAsync(Ct));
        Assert.False(fake.Ran);
    }

    [Fact]
    public async Task HelperThatDidntStart_Fails_WithItsCode()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        Assert.Equal((SwitchResult.Failed, "0x800705B4"), await new ProxyOption(fake.Cli, new FakeHelper(HelperStartResult.Failed) { Code = "0x800705B4" }).TurnOnAsync(Ct));
    }

    [Fact]
    public async Task HelperThatCouldntTurnItOn_Fails_WithWhy()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(Off)));
        Assert.Equal((SwitchResult.Failed, "still off"), await new ProxyOption(fake.Cli, new FakeHelper(HelperStartResult.Started) { Answer = "still off" }).TurnOnAsync(Ct));
    }

    // The helper ran as another administrator, who got the option instead.
    [Fact]
    public async Task OptionStillOffForThisUser_Fails()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(Off)));
        Assert.Equal((SwitchResult.Failed, "still off for this user"), await new ProxyOption(fake.Cli, new FakeHelper(HelperStartResult.Started)).TurnOnAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prompts_AsTheLauncherSays(bool prompts) =>
        Assert.Equal(prompts, new ProxyOption(WinGetCli.Real, new FakeHelper(HelperStartResult.Started) { Prompts = prompts }).Prompts);

    [Fact]
    public async Task IsOn_ReadsTheExport()
    {
        using var fake = new FakeWinGet(Scenario(0, Say(On)));
        Assert.True(await new ProxyOption(fake.Cli, new FakeHelper(HelperStartResult.Declined)).IsOnAsync(Ct));
    }

    // Starts, or doesn't, as the test says; its answer to turning the option on is Answer.
    private sealed class FakeHelper(HelperStartResult result) : IElevation, IHelperSession
    {
        public bool Prompts { get; init; } = true;
        public string? Code { get; init; }
        public string? Answer { get; init; }
        public int Enables { get; private set; }
        public bool Disposed { get; private set; }
        public bool MayPrompt { get; private set; }
        public bool WinGetAvailable => true;

        public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct)
        {
            MayPrompt = mayPrompt;
            return Task.FromResult(result == HelperStartResult.Started ? new HelperStart(result, this) : new HelperStart(result, Code: Code));
        }

        public Task<string?> EnableProxyOptionAsync(CancellationToken ct)
        {
            Enables++;
            return Task.FromResult(Answer);
        }

        public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Not an upgrade test.");

        public void Dispose() => Disposed = true;
    }
}
