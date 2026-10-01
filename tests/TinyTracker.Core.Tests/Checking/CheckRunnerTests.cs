using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Checking;

public sealed class CheckRunnerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TrackedApp Firefox = new() { Id = "Mozilla.Firefox", Source = "winget", Name = "Firefox" };
    private static readonly TrackedApp Vlc = new() { Id = "VideoLAN.VLC", Source = "winget", Name = "VLC" };

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeSource _source = new();
    private readonly FakeDates _dates = new();
    private readonly Channel<CheckCompleted> _completed = Channel.CreateUnbounded<CheckCompleted>();
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _store;
    private readonly FileLog _log;
    private readonly CheckRunner _runner;

    public CheckRunnerTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _store = new SettingsStore(SettingsPath);
        _store.Update(f => f with { Apps = [Firefox] });
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _runner = new CheckRunner(_scheduler, _store, _source, _dates, _time, _log);
        _runner.Completed += (_, e) => _completed.Writer.TryWrite(e);
    }

    // A check the test left running may still log; that line must not bring the folder back.
    public void Dispose()
    {
        _runner.Dispose();
        _scheduler.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private string SettingsPath => _folder.PathOf("settings.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CatalogRead Offering(string installed, string? available) =>
        new([new PackageSnapshot("Mozilla.Firefox", "winget", "Mozilla Firefox", installed, available)], []);

    private async Task<CheckCompleted> NextCompleted() => await _completed.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);

    private Task<CheckCompleted> StartupCheck()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        return NextCompleted();
    }

    // Starts the startup check and holds it inside the source until the test releases it.
    private async Task<TaskCompletionSource<CatalogRead>> StartBlockedCheck()
    {
        var release = new TaskCompletionSource<CatalogRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        _time.Advance(CheckScheduler.StartupDelay);
        await entered.Task.WaitAsync(Wait, Ct);
        return release;
    }

    // A run Dispose cancelled may still be saving; Stopped waits for it, so the demo can delete its folder after it.
    [Fact]
    public async Task Stopped_WaitsForTheRunDisposeCancelled()
    {
        var release = await StartBlockedCheck();
        _runner.Dispose();
        var stopped = _runner.Stopped;
        Assert.False(stopped.IsCompleted);
        release.SetResult(Offering("130.0", "131.0"));
        await stopped.WaitAsync(Wait, Ct);
    }

    [Fact]
    public async Task Stopped_IsDone_WhenNoRunIsGoing()
    {
        Assert.True(_runner.Stopped.IsCompleted);
        _source.Default = Offering("130.0", "131.0");
        await StartupCheck();
        await _runner.Stopped.WaitAsync(Wait, Ct);
    }

    // A listener that throws can't fault the run: it's logged, and Stopped still ends.
    [Fact]
    public async Task ListenerThatThrows_IsLogged_AndTheRunStillEnds()
    {
        _runner.Completed += (_, _) => throw new InvalidOperationException("The listener failed.");
        _source.Default = Offering("130.0", "131.0");
        await StartupCheck();
        _runner.Dispose();
        await _runner.Stopped.WaitAsync(Wait, Ct);
        Assert.Contains("ERROR Check not delivered", File.ReadAllText(_folder.PathOf("app.log")));
    }

    [Fact]
    public async Task CheckDue_IsAnsweredWithFinished()
    {
        _source.Default = Offering("130.0", "131.0");
        var completed = await StartupCheck();
        Assert.Equal(CheckTrigger.Startup, completed.Ticket.Trigger);
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Equal(AppStatus.Available, Assert.Single(completed.Apps).Status);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(6), _scheduler.NextCheck);
    }

    [Fact]
    public async Task Check_SavesTheMerge()
    {
        _source.Default = Offering("130.0", "131.0");
        await StartupCheck();
        var app = Assert.Single(_store.Current.Apps);
        Assert.Equal("Mozilla Firefox", app.Name);
        Assert.Equal(new Offer { Version = "131.0", FirstSeen = _time.GetUtcNow() }, app.Offer);
        var reloaded = new SettingsStore(SettingsPath);
        reloaded.Load();
        Assert.Equal(app, Assert.Single(reloaded.Current.Apps));
    }

    private static CatalogRead Listing(params string[] ids) =>
        Offering("130.0", "131.0") with { Listed = [.. ids.Select(id => new ListedApp(id, id.Split('.')[^1], @"ARP\Machine\X64\" + id))] };

    // New apps (spec §4.3): the first check that lists apps only notes them.
    [Fact]
    public async Task FirstList_IsNoted_AndOffersNothing()
    {
        _source.Default = Listing("Mozilla.Firefox", "Contoso.Editor");
        var completed = await StartupCheck();
        Assert.Equal([], completed.NewApps);
        Assert.Equal(["Mozilla.Firefox", "Contoso.Editor"], _store.Current.KnownApps);
    }

    [Fact]
    public async Task FirstList_IsNotedOnce_WithEveryTrackedApp()
    {
        _store.Update(f => f with { Apps = [Firefox, Vlc] });
        _source.Default = Listing("Mozilla.Firefox", "mozilla.firefox", "Contoso.Editor");
        await StartupCheck();
        Assert.Equal(["Mozilla.Firefox", "Contoso.Editor", "VideoLAN.VLC"], _store.Current.KnownApps);
    }

    [Fact]
    public async Task AppInstalledSince_IsOfferedAtEachCheck()
    {
        _store.Update(f => f with { KnownApps = ["Mozilla.Firefox"] });
        _source.Default = Listing("Mozilla.Firefox", "Contoso.Editor");
        Assert.Equal(["Contoso.Editor"], (await StartupCheck()).NewApps!.Select(a => a.Id));
        _scheduler.CheckNow();
        Assert.Equal(["Contoso.Editor"], (await NextCompleted()).NewApps!.Select(a => a.Id));
        Assert.Equal(["Mozilla.Firefox"], _store.Current.KnownApps);
    }

    [Fact]
    public async Task CheckWithoutAList_LeavesNewAppsAlone()
    {
        _source.Default = Offering("130.0", "131.0");
        Assert.Null((await StartupCheck()).NewApps);
        Assert.Null(_store.Current.KnownApps);
        _store.Update(f => f with { KnownApps = ["Mozilla.Firefox"] });
        _scheduler.CheckNow();
        Assert.Null((await NextCompleted()).NewApps);
        Assert.Equal(["Mozilla.Firefox"], _store.Current.KnownApps);
    }

    [Fact]
    public async Task FailedCheck_LeavesNewAppsAlone()
    {
        _source.Then(_ => throw new PackageSourceException(CheckProblem.WinGetUnreachable, "RPC server unavailable"));
        Assert.Null((await StartupCheck()).NewApps);
        Assert.Null(_store.Current.KnownApps);
    }

    [Fact]
    public async Task ToggleDuringTheCheck_IsKept()
    {
        var release = await StartBlockedCheck();
        _store.Update(f => f with { Apps = [f.Apps[0] with { AutoChoice = true }] });
        release.SetResult(Offering("130.0", "131.0"));
        await NextCompleted();
        var app = Assert.Single(_store.Current.Apps);
        Assert.True(app.AutoChoice);
        Assert.Equal("131.0", app.Offer!.Version);
    }

    [Fact]
    public async Task AppAddedDuringTheCheck_WaitsForTheNextCheck()
    {
        var release = await StartBlockedCheck();
        _store.Update(f => f with { Apps = [.. f.Apps, Vlc] });
        release.SetResult(Offering("130.0", "131.0"));
        var completed = await NextCompleted();
        Assert.Equal(["Mozilla.Firefox"], completed.Apps.Select(c => c.App.Id));
        Assert.Equal(Vlc, _store.Current.Apps.Single(a => a.Id == Vlc.Id));
    }

    [Fact]
    public async Task AppRemovedDuringTheCheck_StaysRemoved()
    {
        var release = await StartBlockedCheck();
        _store.Update(f => f with { Apps = [] });
        release.SetResult(Offering("130.0", "131.0"));
        var completed = await NextCompleted();
        Assert.Empty(completed.Apps);
        Assert.Empty(_store.Current.Apps);
    }

    [Fact]
    public async Task NoTrackedApps_SkipsTheSource()
    {
        _store.Update(f => f with { Apps = [] });
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Empty(completed.Apps);
        Assert.Empty(_source.Requests);
    }

    [Fact]
    public async Task SourceProblem_IsReportedAndRetried()
    {
        _source.Then(_ => throw new PackageSourceException(CheckProblem.WinGetUnreachable, "RPC server unavailable"));
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.WinGetUnreachable, completed.Problem);
        Assert.Equal("RPC server unavailable", completed.Detail);
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
    }

    [Fact]
    public async Task SourceProblemCode_IsKeptInTheDetail()
    {
        _source.Then(_ => throw new PackageSourceException(
            CheckProblem.WinGetUnreachable, "winget stopped", new System.Runtime.InteropServices.COMException("RPC", unchecked((int)0x800706BA))));
        var completed = await StartupCheck();
        Assert.Equal("winget stopped (0x800706BA)", completed.Detail);
    }

    [Fact]
    public async Task UnexpectedError_IsLoggedAndRetried()
    {
        _source.Then(_ => Task.FromException<CatalogRead>(new InvalidOperationException("boom")));
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.Failed, completed.Problem);
        Assert.Contains("boom", File.ReadAllText(_folder.PathOf("app.log")));
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
    }

    [Fact]
    public async Task HungCheck_EndsAtTheDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CatalogRead([], []);
        });
        _time.Advance(CheckScheduler.StartupDelay);
        await entered.Task.WaitAsync(Wait, Ct);
        _time.Advance(CheckRunner.Deadline);
        var completed = await NextCompleted();
        Assert.Equal(CheckProblem.TimedOut, completed.Problem);
        Assert.Equal("No answer within 9 minutes.", completed.Detail);
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
    }

    [Fact]
    public async Task NewTicket_ReplacesACheckThatIgnoredItsDeadline()
    {
        var stuck = new TaskCompletionSource<CatalogRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(_ =>
        {
            entered.TrySetResult();
            return stuck.Task;
        });
        _source.Then(_ => Task.FromResult(Offering("130.0", "131.0")));
        _time.Advance(CheckScheduler.StartupDelay);
        await entered.Task.WaitAsync(Wait, Ct);
        // The runner's deadline passes, then the scheduler's watchdog gives up and plans a retry.
        _time.Advance(CheckScheduler.CheckTimeout);
        _time.Advance(CheckScheduler.RetryDelays[0]);
        var completed = await NextCompleted();
        Assert.Equal(CheckTrigger.Retry, completed.Ticket.Trigger);

        stuck.SetResult(Offering("130.0", "999.0"));
        await Task.Delay(200, Ct);
        Assert.False(_completed.Reader.TryRead(out _));
        Assert.Equal("131.0", Assert.Single(_store.Current.Apps).Offer!.Version);
    }

    [Fact]
    public async Task ReleaseDate_IsFetchedOnceAndKeptWithTheOffer()
    {
        _source.Default = Offering("130.0", "131.0");
        _dates.Known["Mozilla.Firefox 131.0"] = new DateOnly(2026, 9, 20);
        var first = await StartupCheck();
        Assert.Equal(new DateOnly(2026, 9, 20), Assert.Single(first.Apps).App.Offer!.ReleaseDate);
        Assert.Equal(new DateOnly(2026, 9, 20), Assert.Single(_store.Current.Apps).Offer!.ReleaseDate);

        _scheduler.CheckNow();
        await NextCompleted();
        Assert.Equal(["Mozilla.Firefox 131.0"], _dates.Asked);
    }

    [Fact]
    public async Task MissingReleaseDate_StaysUnknown()
    {
        _source.Default = Offering("130.0", "131.0");
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Null(Assert.Single(_store.Current.Apps).Offer!.ReleaseDate);
    }

    [Fact]
    public async Task ReleaseDateError_DoesNotFailTheCheck()
    {
        _source.Default = Offering("130.0", "131.0");
        _dates.Error = new HttpRequestException("offline");
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Null(Assert.Single(completed.Apps).App.Offer!.ReleaseDate);
    }

    [Fact]
    public async Task SettingsThatCantBeSaved_AreReportedAndRetried()
    {
        var release = await StartBlockedCheck();
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            release.SetResult(Offering("130.0", "131.0"));
            Assert.Equal(CheckProblem.SettingsNotSaved, (await NextCompleted()).Problem);
        }
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
        Assert.Null(Assert.Single(_store.Current.Apps).Offer);
    }

    [Fact]
    public async Task UnreadableSettingsAtStartup_AreReadAgainOnTheNextCheck()
    {
        _source.Default = Offering("130.0", "131.0");
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _store.Load();
            Assert.Equal(CheckProblem.SettingsNotSaved, (await StartupCheck()).Problem);
        }
        _time.Advance(CheckScheduler.RetryDelays[0]);
        var retry = await NextCompleted();
        Assert.Equal(CheckProblem.None, retry.Problem);
        Assert.Equal(AppStatus.Available, Assert.Single(retry.Apps).Status);
    }

    [Fact]
    public async Task IntervalOfAFileReadLate_TimesTheNextCheck()
    {
        _source.Default = Offering("130.0", "131.0");
        _store.Update(f => f with { Settings = f.Settings with { CheckIntervalHours = 1 } });
        var store = new SettingsStore(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Load();
        using var runner = new CheckRunner(_scheduler, store, _source, _dates, _time, new FileLog(_folder.PathOf("late.log"), _time));
        var completed = Channel.CreateUnbounded<CheckCompleted>();
        runner.Completed += (_, e) => completed.Writer.TryWrite(e);
        _runner.Dispose();
        _time.Advance(CheckScheduler.StartupDelay);
        Assert.Equal(CheckProblem.None, (await completed.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct)).Problem);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(1), _scheduler.NextCheck);
    }

    [Fact]
    public async Task IntervalOfAFileASaveReadLate_TimesTheNextCheck()
    {
        _source.Default = Offering("130.0", "131.0");
        _store.Update(f => f with { Settings = f.Settings with { CheckIntervalHours = 1 } });
        var store = new SettingsStore(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Load();
        using var runner = new CheckRunner(_scheduler, store, _source, _dates, _time, _log);
        var completed = Channel.CreateUnbounded<CheckCompleted>();
        runner.Completed += (_, e) => completed.Writer.TryWrite(e);
        _runner.Dispose();
        // A save, such as a tick in Choose apps, reads the file before the check does.
        store.Update(f => f with { Apps = [.. f.Apps, Vlc] });
        _time.Advance(CheckScheduler.StartupDelay);
        Assert.Equal(CheckProblem.None, (await completed.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct)).Problem);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(1), _scheduler.NextCheck);
    }

    [Fact]
    public async Task DateSaveFailure_KeepsTheMergedRows()
    {
        _source.Default = Offering("130.0", "131.0");
        FileStream? held = null;
        _dates.Reply = _ =>
        {
            // Locks settings.json between the merge's save and the date's save.
            held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.FromResult<DateOnly?>(new DateOnly(2026, 9, 20));
        };
        var completed = await StartupCheck();
        held?.Dispose();
        Assert.Equal(CheckProblem.None, completed.Problem);
        var check = Assert.Single(completed.Apps);
        Assert.True(check.NewVersion);
        Assert.Null(check.App.Offer!.ReleaseDate);
        Assert.Equal("131.0", Assert.Single(_store.Current.Apps).Offer!.Version);
    }

    [Fact]
    public async Task DeadlineWhileFetchingDates_KeepsTheMergedRows()
    {
        _source.Default = Offering("130.0", "131.0");
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dates.Reply = async ct =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        _time.Advance(CheckScheduler.StartupDelay);
        await asked.Task.WaitAsync(Wait, Ct);
        _time.Advance(CheckRunner.Deadline);
        var completed = await NextCompleted();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.True(Assert.Single(completed.Apps).NewVersion);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(6), _scheduler.NextCheck);
    }

    [Fact]
    public async Task AppMissingForADay_StopsBeingTracked()
    {
        Assert.Equal(AppStatus.NotFound, Assert.Single((await StartupCheck()).Apps).Status);
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromHours(6));
            Assert.Single((await NextCompleted()).Apps);
        }
        _time.Advance(TimeSpan.FromHours(6));
        var gone = await NextCompleted();
        Assert.Empty(gone.Apps);
        Assert.Equal([new PackageKey("Mozilla.Firefox", "winget")], gone.Untracked);
        Assert.Empty(_store.Current.Apps);
        Assert.Contains("INFO Stopped tracking Mozilla.Firefox: not installed for a day", File.ReadAllText(_folder.PathOf("app.log")));
    }

    [Fact]
    public async Task AppFoundAgain_StartsTheDayOver()
    {
        await StartupCheck();
        _time.Advance(TimeSpan.FromHours(6));
        await NextCompleted();
        _source.Then(_ => Task.FromResult(Offering("131.0", null)));
        _time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(AppStatus.UpToDate, Assert.Single((await NextCompleted()).Apps).Status);
        _time.Advance(TimeSpan.FromHours(6));
        await NextCompleted();
        _time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(AppStatus.NotFound, Assert.Single((await NextCompleted()).Apps).Status);
        Assert.Single(_store.Current.Apps);
    }

    [Fact]
    public async Task PackageGoneFromTheCatalog_IsNotInCatalog()
    {
        _source.Default = new CatalogRead([], [new PackageKey("Mozilla.Firefox", "winget")]);
        Assert.Equal(AppStatus.NotInCatalog, Assert.Single((await StartupCheck()).Apps).Status);
    }

    [Fact]
    public async Task Dispose_StopsTheRunner()
    {
        _runner.Dispose();
        _time.Advance(CheckScheduler.StartupDelay);
        await Task.Delay(100, Ct);
        Assert.Empty(_source.Requests);
        Assert.False(_completed.Reader.TryRead(out _));
    }

    // Replies to checks in order, then with Default.
    private sealed class FakeSource : IPackageSource
    {
        private readonly Queue<Func<CancellationToken, Task<CatalogRead>>> _replies = new();
        private readonly List<IReadOnlyList<TrackedApp>> _requests = [];

        public CatalogRead Default { get; set; } = new([], []);

        public IReadOnlyList<IReadOnlyList<TrackedApp>> Requests
        {
            get { lock (_replies) return [.. _requests]; }
        }

        public void Then(Func<CancellationToken, Task<CatalogRead>> reply)
        {
            lock (_replies) _replies.Enqueue(reply);
        }

        public Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
        {
            Func<CancellationToken, Task<CatalogRead>>? reply;
            lock (_replies)
            {
                _requests.Add(apps);
                reply = _replies.TryDequeue(out var next) ? next : null;
            }
            return reply is null ? Task.FromResult(Default) : reply(ct);
        }
    }

    private sealed class FakeDates : IReleaseDates
    {
        private readonly List<string> _asked = [];

        public Dictionary<string, DateOnly> Known { get; } = [];
        public Exception? Error { get; set; }
        // Used instead of Known and Error when set.
        public Func<CancellationToken, Task<DateOnly?>>? Reply { get; set; }

        public IReadOnlyList<string> Asked
        {
            get { lock (_asked) return [.. _asked]; }
        }

        public Task<DateOnly?> GetAsync(string id, string version, CancellationToken ct)
        {
            lock (_asked) _asked.Add($"{id} {version}");
            if (Reply is not null) return Reply(ct);
            if (Error is not null) return Task.FromException<DateOnly?>(Error);
            return Task.FromResult<DateOnly?>(Known.TryGetValue($"{id} {version}", out var date) ? date : null);
        }
    }
}
