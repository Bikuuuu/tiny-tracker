using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

// Tiny Tracker's own update runs alone: it waits for the queue to be idle, admin helper included, then holds it (spec §6.5).
public sealed class InstallQueueHoldTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    // Long enough for a wrongly started update to show.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly TrackedApp Firefox = new() { Id = "Mozilla.Firefox", Source = "winget", Name = "Firefox", Offer = new Offer { Version = "131.0", FirstSeen = DateTimeOffset.UnixEpoch } };

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeUpgrader _upgrader = new();
    private readonly FakeSource _source = new();
    private readonly FakeElevation _elevation = new();
    private readonly Channel<InstallItem> _items = Channel.CreateUnbounded<InstallItem>();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly InstallQueue _queue;
    private int _idle;

    public InstallQueueHoldTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [Firefox] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _source.Reply = apps => new([new PackageSnapshot(Firefox.Id, Firefox.Source, Firefox.Name, "131.0", null)], []);
        _queue = new InstallQueue(_upgrader, _source, _settings, _history, _time, _log, elevation: _elevation);
        _queue.Changed += (_, item) => _items.Writer.TryWrite(item);
        _queue.BecameIdle += (_, _) => Interlocked.Increment(ref _idle);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Dispose();
        await _queue.Stopped.WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InstallRequest Request(InstallRoute route = InstallRoute.App) => new(new PackageKey(Firefox.Id, Firefox.Source), Firefox.Name, "130.0", "131.0") { Route = route };

    private async Task<InstallItem> Next(Func<InstallItem, bool> match)
    {
        while (true)
        {
            var item = await _items.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);
            if (match(item)) return item;
        }
    }

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
    public void IdleQueue_IsHeld_OneHoldAtATime()
    {
        using var hold = _queue.TryHold();
        Assert.NotNull(hold);
        Assert.Null(_queue.TryHold());
    }

    [Fact]
    public async Task QueueWithAnUpdate_IsntHeld_UntilItSaysItsIdle()
    {
        _queue.Enqueue([Request()]);
        var call = await _upgrader.NextCall(Ct);
        Assert.Null(_queue.TryHold());
        call.Finish(UpgradeResult.Updated);
        await Next(i => i.Stage == InstallStage.Done);
        await Until(() => Volatile.Read(ref _idle) > 0);
        using var hold = _queue.TryHold();
        Assert.NotNull(hold);
    }

    // Setup can't replace the helper while the queue's runs.
    [Fact]
    public async Task QueueThatHoldsItsHelper_IsntIdle_UntilTheHelperGoes()
    {
        _queue.Enqueue([Request(InstallRoute.Helper)]);
        var session = (await _elevation.NextCall(Ct)).Approve();
        var call = await session.Upgrades.NextCall(Ct);
        Assert.Null(_queue.TryHold());
        call.Finish(UpgradeResult.Updated);
        await session.Gone.WaitAsync(Wait, Ct);
        await Until(() => Volatile.Read(ref _idle) > 0);
        using var hold = _queue.TryHold();
        Assert.NotNull(hold);
    }

    [Fact]
    public async Task HeldQueue_StartsNothing_UntilItsLetGo()
    {
        var hold = _queue.TryHold()!;
        _queue.Enqueue([Request()]);
        Assert.Equal(InstallStage.Waiting, (await Next(_ => true)).Stage);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _upgrader.Count);
        hold.Dispose();
        (await _upgrader.NextCall(Ct)).Finish(UpgradeResult.Updated);
        await Next(i => i.Stage == InstallStage.Done);
    }

    // No prompt shows until then either: an admin update's prompt comes once the queue goes on.
    [Fact]
    public async Task HeldQueue_StartsNoHelper()
    {
        var hold = _queue.TryHold()!;
        _queue.Enqueue([Request(InstallRoute.Helper)]);
        Assert.False((await Next(_ => true)).AwaitingPermission);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _elevation.Count);
        hold.Dispose();
        Assert.True((await _elevation.NextCall(Ct)).MayPrompt);
        Assert.True((await Next(i => i.AwaitingPermission)).AwaitingPermission);
    }

    // One that started by itself waits too, and then starts the helper without a prompt.
    [Fact]
    public async Task HeldQueue_StartsNoHelper_ForAnUpdateThatStartedByItself_ThenOneWithoutAPrompt()
    {
        var hold = _queue.TryHold()!;
        _queue.Enqueue([Request(InstallRoute.Helper) with { ByItself = true }]);
        Assert.False((await Next(_ => true)).AwaitingPermission);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _elevation.Count);
        hold.Dispose();
        Assert.False((await _elevation.NextCall(Ct)).MayPrompt);
    }

    [Fact]
    public void LettingGoTwice_ChangesNothing()
    {
        var hold = _queue.TryHold()!;
        hold.Dispose();
        using var again = _queue.TryHold();
        Assert.NotNull(again);
        hold.Dispose();
        Assert.Null(_queue.TryHold());
    }
}
