using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation.Demo;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Demo;

// The demo's pretend update of Tiny Tracker itself: it walks the row's states, installs nothing, and pretends the restart (spec §12).
public sealed class DemoSelfUpdateTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly FileLog _log;
    private readonly InstallQueue _queue;
    private readonly SelfUpdater _updater;
    private readonly DemoSelfUpdate _pretend;

    public DemoSelfUpdateTests()
    {
        var demo = new DemoWinGet(_time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(_ => DemoWinGet.Settings(_time.GetUtcNow(), _time.LocalTimeZone));
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        var helper = new DemoHelper(demo, _time);
        _queue = new InstallQueue(demo, demo, _settings, _history, _time, _log, DemoWinGet.Timings, helper, demo);
        _updater = new SelfUpdater(new SelfVersion(0, 1, 0), helper, _queue, _settings, _history, _time, _log, () => false);
        _pretend = new DemoSelfUpdate(_time);
        _pretend.Follow(_updater);
    }

    public async ValueTask DisposeAsync()
    {
        _updater.Dispose();
        _queue.Dispose();
        await Task.WhenAll(_updater.Stopped, _queue.Stopped).WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Moves the clock on until it holds.
    private async Task Pass(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out at {_updater.State}.");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(5, Ct);
        }
    }

    // Old enough for the demo's 3-day wait.
    [Fact]
    public async Task LatestRelease_IsAPretendOne()
    {
        var release = await _pretend.LatestAsync(Ct);
        Assert.Equal((new SelfVersion(9, 9, 9), Now.AddDays(-5)), (release!.Version, release.PublishedAt));
    }

    [Fact]
    public async Task PretendUpdate_EndsWithAPretendRestart()
    {
        _updater.Offer(await _pretend.LatestAsync(Ct));
        _updater.Update(automatic: false);
        await Pass(() => _updater.State.Stage == SelfUpdateStage.Installing);
        await Pass(() => _updater.State.Stage == SelfUpdateStage.None);
        Assert.Equal(new SelfVersion(9, 9, 9), _updater.Running);
        var entry = Assert.Single(_history.Entries);
        Assert.Equal(("Tiny Tracker", HistoryResult.Updated, "0.1.0", "9.9.9"), (entry.Name, entry.Result, entry.FromVersion, entry.ToVersion));
        Assert.Null(_settings.Current.SelfUpdate.Note);
    }
}
