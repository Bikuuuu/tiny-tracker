using System.IO.Pipes;
using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// The app's end of the pipe, against a helper served by this test process (spec §8).
public sealed class HelperClientTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    // The pipe's server is this very process.
    private static readonly string Here = Environment.ProcessPath!;
    private static readonly PackageKey Firefox = new("Mozilla.Firefox", "winget");

    private readonly FakeWork _work = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _name = HelperRules.NewPipeName();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _server;

    public HelperClientTests()
    {
        _pipe = HelperPipe.Create(_name, Me);
        _server = new HelperServer(_work, TimeProvider.System, new SelfVersion(0, 1, 0)).RunAsync(_pipe, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server.WaitAsync(Wait);
        await _pipe.DisposeAsync();
        _stop.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HelperClient> Connect(string? helper = null, bool elevated = false, SpeedLimit? limit = null) =>
        HelperClient.ConnectAsync(_name, helper ?? Here, elevated, Wait, Ct, limit);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Connect_ChecksTheServer_AndReadsItsHello()
    {
        using var client = await Connect();
        Assert.True(client.WinGetAvailable);
    }

    [Fact]
    public async Task WinGetThatDoesntAnswerTheHelper_IsSaid()
    {
        var name = HelperRules.NewPipeName();
        using var pipe = HelperPipe.Create(name, Me);
        var server = new HelperServer(new FakeWork { Problem = "0x80040154" }, TimeProvider.System).RunAsync(pipe, Ct);
        using (var client = await HelperClient.ConnectAsync(name, Here, false, Wait, Ct))
        {
            Assert.Equal((false, "0x80040154"), (client.WinGetAvailable, client.Problem));
        }
        await server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task PipeServedByAnotherProgram_IsRefused()
    {
        await Assert.ThrowsAsync<IOException>(() => Connect(@"C:\Program Files\Tiny Tracker\TinyTracker.Helper.exe"));
        await _server.WaitAsync(Wait, Ct);
        Assert.Equal(0, _work.Count);
    }

    // This test process serves the pipe, so whether it passes follows its own elevation: runners are elevated, most dev PCs aren't.
    [Fact]
    public async Task RequiringElevation_TakesOnlyAnElevatedServer()
    {
        if (Environment.IsPrivilegedProcess)
        {
            using var client = await Connect(elevated: true);
        }
        else await Assert.ThrowsAsync<IOException>(() => Connect(elevated: true));
    }

    [Fact]
    public async Task Upgrade_ReportsItsProgress_AndReturnsItsOutcome()
    {
        using var client = await Connect();
        var seen = new TaskCompletionSource<UpgradeProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var upgrade = client.UpgradeAsync(Firefox, "131.0", new Reported<UpgradeProgress>(p => seen.TrySetResult(p)), Ct);
        var call = await _work.NextCall(Ct);
        Assert.Equal((Firefox, "131.0"), (call.Package, call.Version));
        call.Progress.Report(new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0));
        Assert.Equal(new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0), await seen.Task.WaitAsync(Wait, Ct));
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.RestartNeeded, Code: "installer 3010"));
        Assert.Equal(new UpgradeOutcome(UpgradeResult.RestartNeeded, Code: "installer 3010"), await upgrade.WaitAsync(Wait, Ct));
    }

    // The limit goes with the upgrade, and while it runs, so does each change (spec §6.4).
    [Fact]
    public async Task LimitedUpgrade_TakesTheLimit_AndItsChanges()
    {
        var limit = new SpeedLimit(1000);
        using var client = await Connect(limit: limit);
        var upgrade = client.UpgradeAsync(Firefox, "131.0", null, Ct);
        var call = await _work.NextCall(Ct);
        Assert.Equal(1000, call.Limit.KBps);
        limit.Set(3000);
        await Until(() => call.Limit.KBps == 3000);
        limit.Set(0);
        await Until(() => call.Limit.KBps == 0);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(UpgradeResult.Updated, (await upgrade.WaitAsync(Wait, Ct)).Result);
    }

    // It downloads through COM, which can't be slowed: a limit turned on meanwhile waits for the next upgrade.
    [Fact]
    public async Task UpgradeStartedWithoutALimit_KeepsNone()
    {
        var limit = new SpeedLimit();
        using var client = await Connect(limit: limit);
        var upgrade = client.UpgradeAsync(Firefox, "131.0", null, Ct);
        var call = await _work.NextCall(Ct);
        limit.Set(2000);
        await Task.Delay(300, Ct);
        Assert.Equal(0, call.Limit.KBps);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        await upgrade.WaitAsync(Wait, Ct);
        var next = client.UpgradeAsync(Firefox, "132.0", null, Ct);
        var second = await _work.NextCall(Ct);
        Assert.Equal(2000, second.Limit.KBps);
        second.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        await next.WaitAsync(Wait, Ct);
    }

    // Tiny Tracker's own update: the version alone, under the limit and its changes (spec §6.5).
    [Fact]
    public async Task SelfUpdate_TakesTheVersionAndTheLimit_AndReturnsItsOutcome()
    {
        var limit = new SpeedLimit(1000);
        using var client = await Connect(limit: limit);
        var seen = new TaskCompletionSource<UpgradeProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = client.SelfUpdateAsync(new SelfVersion(0, 2, 0), new Reported<UpgradeProgress>(p => seen.TrySetResult(p)), Ct);
        var call = await _work.NextCall(Ct);
        Assert.Equal(("0.2.0", 1000), (call.Version, call.Limit.KBps));
        call.Progress.Report(new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0));
        Assert.Equal(new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0), await seen.Task.WaitAsync(Wait, Ct));
        limit.Set(3000);
        await Until(() => call.Limit.KBps == 3000);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), await update.WaitAsync(Wait, Ct));
    }

    // The helper downloads Setup itself, so a limit turned on during a self-update applies at once, unlike COM's (spec §6.4, §6.5).
    [Fact]
    public async Task SelfUpdateStartedWithoutALimit_TakesOneTurnedOn()
    {
        var limit = new SpeedLimit();
        using var client = await Connect(limit: limit);
        var update = client.SelfUpdateAsync(new SelfVersion(0, 2, 0), null, Ct);
        var call = await _work.NextCall(Ct);
        Assert.Equal(0, call.Limit.KBps);
        limit.Set(2000);
        await Until(() => call.Limit.KBps == 2000);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), await update.WaitAsync(Wait, Ct));
    }

    [Fact]
    public async Task SelfUpdateCancel_ReachesTheHelper()
    {
        using var client = await Connect();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var update = client.SelfUpdateAsync(new SelfVersion(0, 2, 0), null, cancel.Token);
        var call = await _work.NextCall(Ct);
        await cancel.CancelAsync();
        await call.Token.WhenCancelled().WaitAsync(Wait, Ct);
        Assert.Equal(UpgradeResult.Cancelled, (await update.WaitAsync(Wait, Ct)).Result);
    }

    [Fact]
    public async Task Cancel_ReachesTheHelper()
    {
        using var client = await Connect();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var upgrade = client.UpgradeAsync(Firefox, "131.0", null, cancel.Token);
        var call = await _work.NextCall(Ct);
        await cancel.CancelAsync();
        await call.Token.WhenCancelled().WaitAsync(Wait, Ct);
        Assert.Equal(UpgradeResult.Cancelled, (await upgrade.WaitAsync(Wait, Ct)).Result);
    }

    [Fact]
    public async Task UpgradeCancelledBeforeItGoes_StartsNothingInTheHelper()
    {
        using var client = await Connect();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Equal(UpgradeResult.Cancelled, (await client.UpgradeAsync(Firefox, "131.0", null, cancelled.Token).WaitAsync(Wait, Ct)).Result);
        // The helper saw nothing of it: the next upgrade is its first.
        var next = client.UpgradeAsync(Firefox, "132.0", null, Ct);
        var call = await _work.NextCall(Ct);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(UpgradeResult.Updated, (await next.WaitAsync(Wait, Ct)).Result);
        Assert.Equal(("132.0", 1), (call.Version, _work.Count));
    }

    [Fact]
    public async Task UpgradesInARow_GoThroughOneHelper()
    {
        using var client = await Connect();
        foreach (var version in new[] { "131.0", "132.0" })
        {
            var upgrade = client.UpgradeAsync(Firefox, version, null, Ct);
            (await _work.NextCall(Ct)).Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
            Assert.Equal(UpgradeResult.Updated, (await upgrade.WaitAsync(Wait, Ct)).Result);
        }
        Assert.Equal(2, _work.Count);
    }

    [Fact]
    public async Task HelperThatStops_FailsTheUpgradeItRan_AndTheNextOnes()
    {
        using var client = await Connect();
        var upgrade = client.UpgradeAsync(Firefox, "131.0", null, Ct);
        await _work.NextCall(Ct);
        await _stop.CancelAsync();
        var stopped = new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperStopped);
        Assert.Equal(stopped, await upgrade.WaitAsync(Wait, Ct));
        Assert.Equal(stopped, await client.UpgradeAsync(Firefox, "131.0", null, Ct).WaitAsync(Wait, Ct));
    }

    [Fact]
    public async Task TaskChanges_SayWhatTheHelperAnswered()
    {
        var name = HelperRules.NewPipeName();
        using var pipe = HelperPipe.Create(name, Me);
        var server = new HelperServer(new FakeWork { TaskAnswer = "not in Program Files" }, TimeProvider.System).RunAsync(pipe, Ct);
        using (var client = await HelperClient.ConnectAsync(name, Here, false, Wait, Ct))
        {
            Assert.Equal("not in Program Files", await client.RegisterTaskAsync(Ct));
            Assert.Equal("not in Program Files", await client.RemoveTaskAsync(Ct));
            Assert.Equal("not in Program Files", await client.EnableProxyOptionAsync(Ct));
        }
        await server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task TaskChange_OnceTheHelperStopped_SaysSo()
    {
        using var client = await Connect();
        await _stop.CancelAsync();
        await _server.WaitAsync(Wait, Ct);
        Assert.Equal("stopped", await client.RegisterTaskAsync(Ct).WaitAsync(Wait, Ct));
    }

    [Fact]
    public async Task Dispose_HangsUp_AndTheHelperEnds()
    {
        var client = await Connect();
        client.Dispose();
        await _server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task NoHelper_TimesOut() =>
        await Assert.ThrowsAsync<TimeoutException>(() => HelperClient.ConnectAsync(HelperRules.NewPipeName(), Here, false, TimeSpan.FromMilliseconds(300), Ct));
}
