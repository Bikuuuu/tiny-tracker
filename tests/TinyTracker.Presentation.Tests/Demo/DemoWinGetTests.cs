using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Demo;
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Updates;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Demo;

// The demo runs through the real check runner, install queue and page, so it shows what the app would.
public sealed class DemoWinGetTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly DemoWinGet _demo;
    private readonly SettingsStore _settings;
    private readonly FileLog _log;
    private readonly CheckScheduler _scheduler;
    private readonly CheckRunner _runner;
    private readonly InstallQueue _queue;
    private readonly SettingsWriter _writer;
    private readonly HistoryWriter _historyWriter;
    private readonly UiInbox _inbox;
    private readonly UpdatesViewModel _vm;

    public DemoWinGetTests()
    {
        _demo = new DemoWinGet(_time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(_ => DemoWinGet.Settings(_time.GetUtcNow(), _time.LocalTimeZone));
        var history = new HistoryStore(_folder.PathOf("history.json"), _time);
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _runner = new CheckRunner(_scheduler, _settings, _demo, _demo, _time, _log);
        _queue = new InstallQueue(_demo, _demo, _settings, history, _time, _log, DemoWinGet.Timings, new DemoHelper(_demo, _time), _demo);
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _historyWriter = new HistoryWriter(history, _log, _ui.Post);
        _inbox = new UiInbox(_ui.Post, _log);
        _vm = new UpdatesViewModel(_scheduler, _queue, new FakeConditions(), _settings, _writer, history, _historyWriter, _time, _ui.Post, _ => { },
            culture: () => CultureInfo.InvariantCulture);
        _scheduler.CheckDue += _inbox.For<CheckTicket>(_ => _vm.CheckStarted());
        _runner.Completed += _inbox.For<CheckCompleted>(_vm.CheckFinished);
        _queue.Changed += _inbox.For<InstallItem>(_vm.InstallChanged);
    }

    // The queue's last install and every save land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        _inbox.Dispose();
        _vm.Dispose();
        _queue.Dispose();
        _runner.Dispose();
        _scheduler.Dispose();
        await Task.WhenAll(_queue.Stopped, _writer.Idle, _historyWriter.Idle).WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEnumerable<UpdateRow> Rows => _vm.Updates.Concat(_vm.UpToDate);

    // Moves the clock in small steps and lets the workers run, until done says so.
    private async Task Run(Func<bool> done, Action? look = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            _ui.Pump();
            look?.Invoke();
            if (done()) return;
            Assert.True(DateTime.UtcNow < deadline, "The demo didn't get there in time.");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2, Ct);
        }
    }

    [Fact]
    public async Task UpdateAll_WalksThroughEveryRowState()
    {
        var seen = new HashSet<RowState>();
        var statuses = new HashSet<string>();
        void Look()
        {
            foreach (var row in Rows)
            {
                seen.Add(row.View.State);
                statuses.Add(row.View.Status);
            }
        }

        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 19, Look);
        _vm.UpdateAllCommand.Execute(null);
        await Run(() => seen.Contains(RowState.Waiting) && !_vm.IsWorking && Rows.All(r => r.View.State != RowState.Updated), Look);
        // The apps in use close, update and open again; one of them only after the row has asked about Force close.
        foreach (var row in Rows.Where(r => r.View.Action == RowAction.CloseAndUpdate).ToList()) row.PrimaryCommand.Execute(null);
        await Run(() => seen.Contains(RowState.NotClosed) && !_vm.IsWorking && Rows.All(r => r.View.State != RowState.Updated), Look);

        Assert.Equal(Enum.GetValues<RowState>().Order(), seen.Order());
        Assert.Contains("Waiting for another install to finish…", statuses);
        Assert.Contains("Waiting for permission…", statuses);
        Assert.Contains("Permission was declined", statuses);
        Assert.Contains("Download stalled", statuses);
        Assert.Contains("The admin helper stopped", statuses);
        Assert.Contains("Close Woodgrove Mail, then try again", statuses);
        Assert.All(new[] { "Contoso Chat", "Northwind Notes" }, name => Assert.Equal(RowState.UpToDate, Rows.Single(r => r.Name == name).View.State));
    }

    // Long notes with a link, a security fix, and notes with no web page (spec §12).
    [Fact]
    public async Task ItsNotes_ShowEveryKind()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 19);
        Rows.Single(r => r.Name == "Contoso Editor").OpenNotesCommand.Execute(null);
        Assert.True(_vm.WhatsNew.Lines.Count > 10 && _vm.WhatsNew.HasLink);
        Assert.True(Rows.Single(r => r.Name == "Adatum Photos").View.Security);
        Rows.Single(r => r.Name == "Northwind Notes").OpenNotesCommand.Execute(null);
        Assert.False(_vm.WhatsNew.HasLink);
    }

    // Proseware Draw is new since the demo's first check (spec §4.3).
    [Fact]
    public async Task ItsFirstCheck_OffersOneNewApp()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 19);
        Assert.Equal(["New app: Proseware Draw"], _vm.Notices.Where(n => n.Kind == NoticeKind.NewApp).Select(n => n.Title));
    }

    // The install window opens two hours after the demo starts, at 08:00 here.
    [Fact]
    public async Task AnAutoApp_WaitsForTheInstallWindow()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 19);
        Assert.Equal("Installs automatically at 10:00", Rows.Single(r => r.Name == "Tailspin Player").View.Status);
    }

    // Restore's read (spec §4.5) finds the demo's apps, and the checks' banners still come on the second and third.
    [Fact]
    public async Task RestoresRead_IsntACheck()
    {
        var read = ((IPackageSource)_demo).ReadInstalledAsync([new TrackedApp { Id = "Contoso.Editor", Source = TrackedApp.WinGet }, new TrackedApp { Id = "Proseware.Draw", Source = TrackedApp.WinGet }], Ct);
        await Run(() => read.IsCompleted);
        Assert.Equal(["Contoso.Editor", "Proseware.Draw"], (await read).Installed.Select(p => p.Id));
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Any());
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem is not null);
        Assert.Equal("Can't reach winget right now, retrying", _vm.Problem!.Title);
    }

    [Fact]
    public async Task SecondAndThirdChecks_ShowTheBanners()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Any());
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem is not null);
        Assert.Equal("Can't reach winget right now, retrying", _vm.Problem!.Title);
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem?.OffersStore == true);
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem is null && !_vm.IsChecking);
        Assert.Equal(19, Rows.Count());
        Assert.Equal(RowState.Available, Rows.Single(r => r.Name == "Fabrikam Viewer").View.State);
        var maps = Rows.Single(r => r.Name == "Proseware Maps");
        Assert.Equal(("2025.3", RowState.NeedsPermission), (maps.View.To.Unchanged + maps.View.To.Changed, maps.View.State));
    }

    [Fact]
    public async Task Inventory_MovesTheMatchedAppIn()
    {
        var first = new List<string>();
        var task = _demo.ReadAsync(new Reported(list => first.AddRange(list.Trackable.Select(a => a.Name))), Ct);
        await Run(() => task.IsCompleted);
        var final = await task;
        Assert.DoesNotContain("Northwind Budget", first);
        Assert.Contains(final.Trackable, a => a.Name == "Northwind Budget");
        Assert.DoesNotContain(final.Elsewhere, a => a.Name == "Northwind Budget");
        Assert.Contains(final.Elsewhere, a => a.UpdatedBy == Core.Inventory.UpdatedBy.Steam);
    }

    [Fact]
    public async Task History_ShowsEveryKindOfEntry_AndRetriesOnlyTheOneThatFits()
    {
        var history = new HistoryStore(_folder.PathOf("demo-history.json"), _time);
        foreach (var entry in DemoWinGet.History(_time.GetUtcNow())) history.Add(entry);
        var page = new HistoryViewModel(history, new HistoryWriter(history, _log, _ui.Post), _vm, _time, () => System.Globalization.CultureInfo.InvariantCulture);
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 19);
        page.Shown();
        var rows = page.Groups.SelectMany(g => g.Rows).ToList();
        Assert.Equal(Enum.GetValues<HistoryIcon>().Order(), rows.Select(r => r.Icon).Distinct().Order());
        Assert.Equal(["Fabrikam Chat", "Proseware Maps"], rows.Where(r => r.CanRetry).Select(r => r.Name));
        Assert.Contains(rows, r => r.IsFailed && !r.HasDetails);
        Assert.Equal(["Today", "Yesterday", "Sep 23", "Sep 22"], page.Groups.Select(g => g.Title));
    }

    // Each quarter second brings a quarter of the limit's worth, from the next step on after a change (spec §12).
    [Fact]
    public async Task Downloads_RunAtTheSpeedLimit_AndFollowItsChanges()
    {
        var limit = new SpeedLimit(8192);
        var demo = new DemoWinGet(_time, limit);
        var steps = new List<ulong>();
        var upgrade = demo.UpgradeAsync(new PackageKey("Contoso.Editor", TrackedApp.WinGet), "2.5.0", new Progress(p =>
        {
            if (p.Stage == UpgradeStage.Downloading) lock (steps) steps.Add(p.BytesDownloaded);
        }), Ct);
        await Run(() => Count(steps) >= 3);
        limit.Set(0);
        await Run(() => upgrade.IsCompleted);
        lock (steps)
        {
            Assert.Equal([0, 2 * MB, 4 * MB], steps.Take(3));
            Assert.Equal(8 * MB, steps[^1] - steps[^2]);
        }
        Assert.Equal(UpgradeResult.Updated, (await upgrade).Result);
    }

    // As when winget's proxy option went off behind the app's back.
    [Fact]
    public async Task WoodgroveRadio_HasItsProxyRefused_UnderTheLimitOnly()
    {
        var limit = new SpeedLimit(17500);
        var demo = new DemoWinGet(_time, limit);
        var refused = demo.UpgradeAsync(new PackageKey("Woodgrove.Radio", TrackedApp.WinGet), "1.5", null, Ct);
        await Run(() => refused.IsCompleted);
        Assert.Equal(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.ProxyRefused, "0x8A150002"), await refused);
        limit.Set(0);
        var updated = demo.UpgradeAsync(new PackageKey("Woodgrove.Radio", TrackedApp.WinGet), "1.5", null, Ct);
        await Run(() => updated.IsCompleted);
        Assert.Equal(UpgradeResult.Updated, (await updated).Result);
    }

    private static int Count(List<ulong> steps)
    {
        lock (steps) return steps.Count;
    }

    private sealed class Reported(Action<Core.Inventory.AppInventory> report) : IProgress<Core.Inventory.AppInventory>
    {
        public void Report(Core.Inventory.AppInventory value) => report(value);
    }

    private sealed class Progress(Action<UpgradeProgress> report) : IProgress<UpgradeProgress>
    {
        public void Report(UpgradeProgress value) => report(value);
    }
}
