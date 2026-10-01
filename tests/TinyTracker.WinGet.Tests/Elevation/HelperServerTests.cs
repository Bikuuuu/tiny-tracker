using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// The helper's end of the pipe, as this user, with a made-up winget (spec §5.1, §8).
public sealed class HelperServerTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    // Long enough for a helper that wrongly left to show.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    // The helper's own version, which a self-update must be newer than.
    private static readonly SelfVersion Own = new(0, 1, 0);

    private readonly SleepingTime _time = new();
    private readonly FakeWork _work = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _name = HelperRules.NewPipeName();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _server;

    public HelperServerTests()
    {
        _pipe = HelperPipe.Create(_name, Me);
        _server = new HelperServer(_work, _time, Own).RunAsync(_pipe, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server.WaitAsync(Wait);
        await _pipe.DisposeAsync();
        _stop.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NamedPipeClientStream> Connect(string? name = null)
    {
        var client = new NamedPipeClientStream(".", name ?? _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, Ct);
        return client;
    }

    private static async Task<HelperMessage?> Read(Stream client) => await HelperWire.ReadAsync(client, Ct).WaitAsync(Wait, Ct);

    private static Task Send(Stream client, HelperMessage message) => HelperWire.WriteAsync(client, message, Ct);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }

    // Connects and reads the hello.
    private async Task<NamedPipeClientStream> Greeted()
    {
        var client = await Connect();
        Assert.IsType<HelloMessage>(await Read(client));
        return client;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0x80040154")]
    public async Task Hello_ComesFirst_AndSaysWhetherWinGetAnswers(string? problem)
    {
        var work = new FakeWork { Problem = problem };
        var name = HelperRules.NewPipeName();
        using var pipe = HelperPipe.Create(name, Me);
        var server = new HelperServer(work, _time).RunAsync(pipe, Ct);
        using var client = await Connect(name);
        Assert.Equal(new HelloMessage(problem), await Read(client));
        client.Dispose();
        await server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task Upgrade_SendsItsProgress_ThenItsOutcome()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0));
        var call = await _work.NextCall(Ct);
        Assert.Equal((new PackageKey("Mozilla.Firefox", "winget"), "131.0"), (call.Package, call.Version));
        call.Progress.Report(new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0));
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new ProgressMessage(1, UpgradeStage.Downloading, 10, 100, 0.1, 0), await Read(client));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
    }

    [Theory]
    [InlineData("Firefox", "winget", "131.0", 0)]
    [InlineData("Mozilla.Firefox", "msstore", "131.0", 0)]
    [InlineData("Mozilla.Firefox", "winget", "131.0|calc", 0)]
    [InlineData("Mozilla.Firefox", "winget", "131.0", 50)]
    [InlineData("Mozilla.Firefox", "winget", "131.0", 2_000_000)]
    public async Task RequestThatBreaksTheRules_IsRefused_WithoutRunningIt(string id, string source, string version, int limit)
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(1, id, source, version, limit));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Failed, UpgradeFailure.Other, "refused"), await Read(client));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task LimitedUpgrade_RunsUnderItsLimit_AndLimitRequestsChangeIt()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(4, "Mozilla.Firefox", "winget", "131.0", 17500));
        var call = await _work.NextCall(Ct);
        var changes = new List<int>();
        call.Limit.Changed += (_, _) =>
        {
            lock (changes) changes.Add(call.Limit.KBps);
        };
        Assert.Equal(17500, call.Limit.KBps);
        await Send(client, new LimitRequest(4, 2000));
        // Another upgrade's, or one Settings couldn't have: nothing changes.
        await Send(client, new LimitRequest(5, 500));
        await Send(client, new LimitRequest(4, 50));
        await Send(client, new LimitRequest(4, 0));
        await Until(() => call.Limit.KBps == 0);
        lock (changes) Assert.Equal([2000, 0], changes);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new DoneMessage(4, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
    }

    [Fact]
    public async Task LimitWithNothingRunning_ChangesNothing_AndTheSessionGoesOn()
    {
        using var client = await Greeted();
        await Send(client, new LimitRequest(1, 2000));
        await Send(client, new UpgradeRequest(2, "Mozilla.Firefox", "winget", "131.0", 0));
        Assert.Equal(0, (await _work.NextCall(Ct)).Limit.KBps);
    }

    [Fact]
    public async Task Cancel_StopsTheRunningUpgrade()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(3, "Mozilla.Firefox", "winget", "131.0", 0));
        var call = await _work.NextCall(Ct);
        await Send(client, new CancelRequest(3));
        await call.Token.WhenCancelled().WaitAsync(Wait, Ct);
        Assert.Equal(new DoneMessage(3, UpgradeResult.Cancelled, UpgradeFailure.None, null), await Read(client));
    }

    // The app sends its next upgrade the moment it reads a result.
    [Fact]
    public async Task UpgradeRightAfterAResult_IsNeverBusy()
    {
        using var client = await Greeted();
        for (var number = 1; number <= 50; number++)
        {
            await Send(client, new UpgradeRequest(number, "Mozilla.Firefox", "winget", "131.0", 0));
            (await _work.NextCall(Ct)).Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
            Assert.Equal(new DoneMessage(number, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        }
    }

    [Fact]
    public async Task SecondUpgradeWhileOneRuns_IsBusy()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0));
        var first = await _work.NextCall(Ct);
        await Send(client, new UpgradeRequest(2, "VideoLAN.VLC", "winget", "3.0.21", 0));
        Assert.Equal(new DoneMessage(2, UpgradeResult.Busy, UpgradeFailure.None, null), await Read(client));
        first.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        Assert.Equal(1, _work.Count);
    }

    // Setup replaces the helper's files, so once it has started the helper goes, though the app still listens (spec §6.5).
    [Fact]
    public async Task SelfUpdate_SendsItsProgress_ThenThatSetupStarted_AndTheHelperEnds()
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(1, "0.2.0", 0));
        var call = await _work.NextCall(Ct);
        Assert.Equal((default(PackageKey), "0.2.0", 0), (call.Package, call.Version, call.Limit.KBps));
        call.Progress.Report(new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0));
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new ProgressMessage(1, UpgradeStage.Downloading, 10, 100, 0.1, 0), await Read(client));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        await _server.WaitAsync(Wait, Ct);
        Assert.Null(await Read(client));
    }

    // The app must hear that Setup started, however much is still on its way to it.
    [Fact]
    public async Task SetupStarted_ReachesTheApp_BehindMuchProgress()
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(1, "0.2.0", 0));
        var call = await _work.NextCall(Ct);
        for (var i = 0; i < 20_000; i++) call.Progress.Report(new UpgradeProgress(UpgradeStage.Downloading, (ulong)i, 20_000, i / 20_000.0, 0));
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        HelperMessage? last = null;
        while (await Read(client) is { } message) last = message;
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), last);
    }

    // A disconnect drops what the app hasn't read yet, so the helper waits for it to read, as long as it reads at all.
    [Fact]
    public async Task SetupStarted_ReachesAnAppThatReadsLate()
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(1, "0.2.0", 0));
        var call = await _work.NextCall(Ct);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        await Task.Delay(500, Ct);
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        await _server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task SetupStarted_WithAnAppThatStoppedReading_TheHelperStillEnds()
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(1, "0.2.0", 0));
        var call = await _work.NextCall(Ct);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        await Task.Delay(500, Ct);
        Assert.False(_server.IsCompleted);
        var deadline = DateTime.UtcNow + Wait;
        while (!_server.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "The helper waited on.");
            _time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task SelfUpdateThatFailed_KeepsTheSession()
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(1, "0.2.0", 0));
        (await _work.NextCall(Ct)).Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DigestMismatch));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Failed, UpgradeFailure.DigestMismatch, null), await Read(client));
        await Send(client, new UpgradeRequest(2, "Mozilla.Firefox", "winget", "131.0", 0));
        (await _work.NextCall(Ct)).Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new DoneMessage(2, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        Assert.False(_server.IsCompleted);
    }

    [Theory]
    [InlineData("0.1.0", 0)]
    [InlineData("0.0.9", 0)]
    [InlineData("0.2", 0)]
    [InlineData("https://example.com/setup.exe", 0)]
    [InlineData("0.2.0", 50)]
    public async Task SelfUpdateThatBreaksTheRules_IsRefused_WithoutRunningIt(string version, int limit)
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(1, version, limit));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Failed, UpgradeFailure.Other, "refused"), await Read(client));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task HelperThatDoesntKnowItsVersion_RefusesEverySelfUpdate()
    {
        var name = HelperRules.NewPipeName();
        using var pipe = HelperPipe.Create(name, Me);
        var server = new HelperServer(_work, _time).RunAsync(pipe, Ct);
        using (var client = await Connect(name))
        {
            await Read(client);
            await Send(client, new SelfUpdateRequest(1, "9.9.9", 0));
            Assert.Equal(new DoneMessage(1, UpgradeResult.Failed, UpgradeFailure.Other, "refused"), await Read(client));
        }
        await server.WaitAsync(Wait, Ct);
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task SelfUpdateAndUpgrades_RunOneAtATime()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0));
        var upgrade = await _work.NextCall(Ct);
        await Send(client, new SelfUpdateRequest(2, "0.2.0", 0));
        Assert.Equal(new DoneMessage(2, UpgradeResult.Busy, UpgradeFailure.None, null), await Read(client));
        upgrade.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        await Send(client, new SelfUpdateRequest(3, "0.2.0", 0));
        var self = await _work.NextCall(Ct);
        await Send(client, new UpgradeRequest(4, "Mozilla.Firefox", "winget", "131.0", 0));
        Assert.Equal(new DoneMessage(4, UpgradeResult.Busy, UpgradeFailure.None, null), await Read(client));
        self.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Cancelled));
        Assert.Equal(new DoneMessage(3, UpgradeResult.Cancelled, UpgradeFailure.None, null), await Read(client));
    }

    [Fact]
    public async Task CancelAndLimit_ReachTheSelfUpdate()
    {
        using var client = await Greeted();
        await Send(client, new SelfUpdateRequest(5, "0.2.0", 17500));
        var call = await _work.NextCall(Ct);
        await Send(client, new LimitRequest(5, 2000));
        await Until(() => call.Limit.KBps == 2000);
        await Send(client, new CancelRequest(5));
        await call.Token.WhenCancelled().WaitAsync(Wait, Ct);
        Assert.Equal(new DoneMessage(5, UpgradeResult.Cancelled, UpgradeFailure.None, null), await Read(client));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0x80070005")]
    public async Task TaskChanges_AskTheWork_AndSayHowItWent(string? answer)
    {
        var work = new FakeWork { TaskAnswer = answer };
        var name = HelperRules.NewPipeName();
        using var pipe = HelperPipe.Create(name, Me);
        var server = new HelperServer(work, _time).RunAsync(pipe, Ct);
        using (var client = await Connect(name))
        {
            await Read(client);
            await Send(client, new RegisterTaskRequest());
            Assert.Equal(new TaskDoneMessage(answer), await Read(client));
            await Send(client, new RemoveTaskRequest());
            Assert.Equal(new TaskDoneMessage(answer), await Read(client));
            await Send(client, new EnableProxyOptionRequest());
            Assert.Equal(new TaskDoneMessage(answer), await Read(client));
        }
        await server.WaitAsync(Wait, Ct);
        Assert.Equal(["register", "remove", "enableProxyOption"], work.TaskChanges);
    }

    [Fact]
    public async Task MalformedMessage_EndsTheSession()
    {
        using var client = await Greeted();
        var body = Encoding.UTF8.GetBytes("""{"type":"upgrade","number":1,"id":"Mozilla.Firefox","source":"winget","version":"131.0","override":"/S"}""");
        await client.WriteAsync(BitConverter.GetBytes(body.Length), Ct);
        await client.WriteAsync(body, Ct);
        await _server.WaitAsync(Wait, Ct);
        Assert.Null(await Read(client));
        Assert.Equal(0, _work.Count);
    }

    [Fact]
    public async Task MessageTheAppNeverSends_EndsTheSession()
    {
        using var client = await Greeted();
        await Send(client, new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null));
        await _server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task AppThatHangsUp_StopsItsUpgrade_AndTheHelperEnds()
    {
        var client = await Greeted();
        await Send(client, new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0));
        var call = await _work.NextCall(Ct);
        client.Dispose();
        await call.Token.WhenCancelled().WaitAsync(Wait, Ct);
        await _server.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task NoAppWithin30Seconds_EndsTheHelper()
    {
        _time.Advance(HelperServer.ConnectTimeout);
        await _server.WaitAsync(Wait, Ct);
    }

    // With nothing asked and nothing running for 10 minutes, the app is gone or stuck, and the helper leaves (spec §5.1).
    [Fact]
    public async Task IdleHelper_LeavesAfterTenMinutes()
    {
        using var client = await Greeted();
        _time.Advance(HelperRules.IdleTimeout - TimeSpan.FromSeconds(1));
        await Task.Delay(Settle, Ct);
        Assert.False(_server.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
        await _server.WaitAsync(Wait, Ct);
        Assert.Null(await Read(client));
    }

    [Fact]
    public async Task EachRequest_StartsTheTenMinutesAgain()
    {
        using var client = await Greeted();
        _time.Advance(TimeSpan.FromMinutes(9));
        await Send(client, new RegisterTaskRequest());
        Assert.IsType<TaskDoneMessage>(await Read(client));
        _time.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(Settle, Ct);
        Assert.False(_server.IsCompleted);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _server.WaitAsync(Wait, Ct);
    }

    // Windows' timers run on through a sleep, but the helper's clock counts only time awake: a sleep isn't idle time (spec §5.1).
    [Fact]
    public async Task SleepLongerThanTheIdleWait_IsntIdle()
    {
        using var client = await Greeted();
        _time.Advance(TimeSpan.FromMinutes(3));
        _time.Sleep(TimeSpan.FromMinutes(60));
        await Task.Delay(Settle, Ct);
        Assert.False(_server.IsCompleted);
        _time.Advance(TimeSpan.FromMinutes(7));
        await _server.WaitAsync(Wait, Ct);
    }

    // The app holds the helper for admin updates that wait their turn, and says so: that isn't idle either.
    [Fact]
    public async Task StayMessage_StartsTheTenMinutesAgain()
    {
        using var client = await Greeted();
        _time.Advance(TimeSpan.FromMinutes(9));
        await Send(client, new StayRequest());
        // The helper reads a message only once it has heard the one before, so this send ends only then.
        await Send(client, new StayRequest());
        _time.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(Settle, Ct);
        Assert.False(_server.IsCompleted);
        // Hearing the second may come a moment later, and the wait counts from then.
        for (var minute = 0; minute < 11 && !_server.IsCompleted; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(Settle, Ct);
        }
        await _server.WaitAsync(Wait, Ct);
    }

    // An upgrade isn't idle however long it takes; the 10 minutes start once it ends.
    [Fact]
    public async Task RunningUpgrade_KeepsTheHelper()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0));
        var call = await _work.NextCall(Ct);
        _time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(Settle, Ct);
        Assert.False(_server.IsCompleted);
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        Assert.Equal(new DoneMessage(1, UpgradeResult.Updated, UpgradeFailure.None, null), await Read(client));
        _time.Advance(HelperRules.IdleTimeout);
        await _server.WaitAsync(Wait, Ct);
    }

    // A client that stops reading can't make the helper hold messages without end (spec §8).
    [Fact]
    public async Task ClientThatStopsReading_IsHungUpOn()
    {
        using var client = await Greeted();
        // Refused requests, whose answers it never reads. Once the helper stops reading, a send can't end, so they go on a worker.
        var sending = Task.Run(async () =>
        {
            for (var number = 1; number <= HelperServer.MaxUnread + 8; number++) await Send(client, new UpgradeRequest(number, "Firefox", "winget", "131.0", 0));
        }, Ct);
        // What's left gets a few seconds to go out, then the helper leaves. A second a step stays well short of the idle wait.
        var deadline = DateTime.UtcNow + Wait;
        while (!_server.IsCompleted && DateTime.UtcNow < deadline)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, Ct);
        }
        await _server.WaitAsync(Wait, Ct);
        await Assert.ThrowsAsync<IOException>(() => sending.WaitAsync(Wait, Ct));
        Assert.Equal(0, _work.Count);
    }

    // Progress the app hasn't read yet gives way to the newest, so a slow reader never fills the helper.
    [Fact]
    public async Task ProgressWaitingUnread_IsReplacedByTheNewest()
    {
        using var client = await Greeted();
        await Send(client, new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0));
        var call = await _work.NextCall(Ct);
        for (ulong bytes = 1; bytes <= 100; bytes++) call.Progress.Report(new UpgradeProgress(UpgradeStage.Downloading, bytes, 100, bytes / 100.0, 0));
        call.Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Updated));
        var progress = new List<ProgressMessage>();
        while (await Read(client) is ProgressMessage message) progress.Add(message);
        Assert.InRange(progress.Count, 1, 2);
        Assert.Equal(100UL, progress[^1].BytesDownloaded);
        Assert.False(_server.IsCompleted);
    }

    [Fact]
    public void Pipe_OnlyTheUserAndAdministratorsMayOpenIt_AndNeverOverTheNetwork()
    {
        var rules = _pipe.GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()
            .Select(r => ((SecurityIdentifier)r.IdentityReference, r.AccessControlType, r.PipeAccessRights))
            .ToHashSet();
        Assert.True(rules.SetEquals(
            [
                (new SecurityIdentifier(WellKnownSidType.NetworkSid, null), AccessControlType.Deny, PipeAccessRights.FullControl),
                (Me, AccessControlType.Allow, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize),
                (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), AccessControlType.Allow, PipeAccessRights.FullControl),
            ]), "The pipe's permissions aren't just this user, Administrators and no network.");
    }

    [Fact]
    public async Task Pipe_TakesOneAppOnly()
    {
        using var client = await Greeted();
        using var second = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAsync<TimeoutException>(() => second.ConnectAsync(300, Ct));
    }

    [Fact]
    public void NameAnotherProgramTookFirst_IsRefused()
    {
        var name = HelperRules.NewPipeName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4);
        Assert.Throws<UnauthorizedAccessException>(() => HelperPipe.Create(name, Me));
    }

    [Fact]
    public void OtherNames_AreRefused() =>
        Assert.Throws<ArgumentException>(() => HelperPipe.Create("Example.Pipe", Me));

    // A fake clock that can sleep: its timers go on, as Windows' do, and its timestamps don't, as the helper's awake time doesn't.
    private sealed class SleepingTime : FakeTimeProvider
    {
        private long _asleep;

        public override long GetTimestamp() => base.GetTimestamp() - Volatile.Read(ref _asleep);

        public void Sleep(TimeSpan span)
        {
            Interlocked.Add(ref _asleep, (long)(span.TotalSeconds * TimestampFrequency));
            Advance(span);
        }
    }
}
