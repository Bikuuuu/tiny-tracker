using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.History;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.History;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.History;

public sealed class HistoryViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly FakeRows _rows = new();
    private readonly FileLog _log;
    private FakeTimeProvider _time = null!;
    private HistoryStore _store = null!;
    private HistoryWriter _writer = null!;
    private HistoryViewModel _vm = null!;
    private CultureInfo _culture = CultureInfo.InvariantCulture;

    public HistoryViewModelTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), new FakeTimeProvider(Now));
        Start(Now, TimeZoneInfo.Utc);
    }

    // Writes a test left queued land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string HistoryPath => _folder.PathOf("history.json");

    private void Start(DateTimeOffset now, TimeZoneInfo zone)
    {
        _time = new FakeTimeProvider(now);
        _time.SetLocalTimeZone(zone);
        _store = new HistoryStore(HistoryPath, _time);
        _store.Load();
        _writer = new HistoryWriter(_store, _log, _ui.Post);
        _vm = new HistoryViewModel(_store, _writer, _rows, _time, () => _culture);
    }

    private static HistoryEntry Entry(string id, DateTimeOffset time, HistoryResult result = HistoryResult.Updated, string? from = "2.4.1", string? to = "2.5.0", string? reason = null, string? code = null) => new()
    {
        Time = time,
        Id = id,
        Source = "winget",
        Name = Name(id),
        Result = result,
        FromVersion = from,
        ToVersion = to,
        Reason = reason,
        Code = code,
    };

    private HistoryRow Row(string name) => _vm.Groups.SelectMany(g => g.Rows).Single(r => r.Name == name);

    private async Task Written()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    // Central European time: summer time from the last Sunday of March to the last Sunday of October.
    private static TimeZoneInfo Europe()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test Europe", TimeSpan.FromHours(1), "Test Europe", "Test Europe", "Test Europe Summer", [rule]);
    }

    private static TimeZoneInfo Fixed(int hours) => TimeZoneInfo.CreateCustomTimeZone($"Test {hours}", TimeSpan.FromHours(hours), "Test", "Test");

    [Fact]
    public void Groups_FollowTheLocalDay_NotTheUtcDay()
    {
        Start(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero), Fixed(-7));
        _store.Add(Entry("Example.Editor", new DateTimeOffset(2026, 9, 25, 7, 30, 0, TimeSpan.Zero)));
        _store.Add(Entry("Example.Paint", new DateTimeOffset(2026, 9, 25, 6, 30, 0, TimeSpan.Zero)));
        _vm.Shown();
        Assert.Equal([("Today", "Example Editor"), ("Yesterday", "Example Paint")], _vm.Groups.SelectMany(g => g.Rows.Select(r => (g.Title, r.Name))));
    }

    [Fact]
    public void LongDayWhenTheClocksGoBack_IsStillToday()
    {
        // Oct 25, 2026 has 25 hours: 00:30 and 23:50 local are more than a day apart.
        Start(new DateTimeOffset(2026, 10, 25, 23, 50, 0, TimeSpan.FromHours(1)), Europe());
        _store.Add(Entry("Example.Editor", new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.FromHours(2))));
        _vm.Shown();
        Assert.Equal("Today", Assert.Single(_vm.Groups).Title);
    }

    [Fact]
    public void ShortDayWhenTheClocksGoForward_EndsAtLocalMidnight()
    {
        // Less than a day apart, but on either side of local midnight.
        Start(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.FromHours(2)), Europe());
        _store.Add(Entry("Example.Editor", new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.FromHours(1))));
        _vm.Shown();
        Assert.Equal("Yesterday", Assert.Single(_vm.Groups).Title);
    }

    [Fact]
    public void Titles_MoveOn_AfterMidnight()
    {
        _store.Add(Entry("Example.Editor", Now));
        _vm.Shown();
        _time.Advance(TimeSpan.FromDays(1));
        _vm.Changed();
        Assert.Equal("Yesterday", Assert.Single(_vm.Groups).Title);
        _time.Advance(TimeSpan.FromDays(1));
        _vm.Changed();
        Assert.Equal("Sep 25", Assert.Single(_vm.Groups).Title);
    }

    [Fact]
    public void Rows_SayWhatHappened()
    {
        _store.Add(Entry("Example.Editor", Now));
        _store.Add(Entry("Example.Player", Now, reason: "RestartNeeded"));
        _store.Add(Entry("Example.Sync", Now, reason: "Phantom"));
        _store.Add(Entry("Example.Chat", Now, HistoryResult.Failed, reason: "DiskFull", code: "0x8A150105"));
        _store.Add(Entry("Example.Odd", Now, HistoryResult.Failed, reason: "Other"));
        _store.Add(Entry("Example.Clock", Now, HistoryResult.Skipped, to: "1.1"));
        _store.Add(Entry("Example.Photos", Now, HistoryResult.Cancelled, to: "12.1"));
        _store.Add(Entry("Example.Bare", Now, from: null, to: "3.0"));
        _store.Add(Entry("Example.Blank", Now, HistoryResult.Cancelled, from: null, to: null) with { Name = "" });
        _vm.Shown();
        Assert.Equal(
            [
                ("Example Editor", HistoryIcon.Updated, "2.4.1 → 2.5.0", null),
                ("Example Player", HistoryIcon.Updated, "2.4.1 → 2.5.0 · Needed a restart", null),
                ("Example Sync", HistoryIcon.Warning, "2.4.1 → 2.5.0 · Windows still showed the old version", null),
                ("Example Chat", HistoryIcon.Failed, "Failed · Not enough disk space", "Code: 0x8A150105"),
                ("Example Odd", HistoryIcon.Failed, "Failed", null),
                ("Example Clock", HistoryIcon.Skipped, "Skipped version 1.1", null),
                ("Example Photos", HistoryIcon.Cancelled, "Cancelled update to 12.1", null),
                ("Example Bare", HistoryIcon.Updated, "Updated to 3.0", null),
                ("Example.Blank", HistoryIcon.Cancelled, "Cancelled", (string?)null),
            ],
            new[] { "Example Editor", "Example Player", "Example Sync", "Example Chat", "Example Odd", "Example Clock", "Example Photos", "Example Bare", "Example.Blank" }
                .Select(Row).Select(r => (r.Name, r.Icon, r.Status, r.Details)));
    }

    [Fact]
    public void Rows_ReadAsOneLine_ForNarrator()
    {
        _store.Add(Entry("Example.Editor", Now, reason: "RestartNeeded"));
        _store.Add(Entry("Example.Chat", Now - TimeSpan.FromMinutes(5), HistoryResult.Failed, reason: "DiskFull"));
        _vm.Shown();
        Assert.Equal("Example Editor, Updated, 2.4.1 to 2.5.0, Needed a restart, 08:00", Row("Example Editor").AutomationName);
        Assert.Equal("Example Chat, Failed, Not enough disk space, 07:55", Row("Example Chat").AutomationName);
    }

    [Fact]
    public void Times_AreLocal_InTheUsersFormat()
    {
        Start(new DateTimeOffset(2026, 9, 25, 10, 32, 0, TimeSpan.Zero), Fixed(4));
        _culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        _culture.DateTimeFormat.ShortTimePattern = "h:mm tt";
        _culture.DateTimeFormat.PMDesignator = "PM";
        _store.Add(Entry("Example.Editor", _time.GetUtcNow()));
        _vm.Shown();
        Assert.Equal("2:32 PM", Row("Example Editor").Time);
    }

    [Fact]
    public void Retry_ShowsOnlyOnAnAppsNewestFailure_ThatItsRowStillOffers()
    {
        _rows.Retryable.UnionWith([("Example.Editor", "2.5.0"), ("Example.Paint", "2.5.0"), ("Example.Chat", "2.5.0")]);
        _store.Add(Entry("Example.Paint", Now - TimeSpan.FromHours(2), HistoryResult.Failed, reason: "DiskFull"));
        _store.Add(Entry("Example.Paint", Now - TimeSpan.FromHours(1)));
        _store.Add(Entry("Example.Editor", Now, HistoryResult.Failed, reason: "DiskFull"));
        _store.Add(Entry("Example.Chat", Now, HistoryResult.Failed, reason: "DiskFull", to: "2.4.9"));
        _store.Add(Entry("Example.Clock", Now, HistoryResult.Failed, reason: "DiskFull"));
        _vm.Shown();
        var failures = _vm.Groups.SelectMany(g => g.Rows).Where(r => r.IsFailed).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [("Example Chat", false), ("Example Clock", false), ("Example Editor", true), ("Example Paint", false)],
            failures.Select(r => (r.Name, r.CanRetry)));
        Assert.False(Row("Example Editor").ShowsTime);
    }

    [Fact]
    public void Retry_QueuesTheUpdate_AndTellsThePage()
    {
        _rows.Retryable.Add(("Example.Editor", "2.5.0"));
        _store.Add(Entry("Example.Editor", Now, HistoryResult.Failed, reason: "DiskFull"));
        _vm.Shown();
        var retried = 0;
        _vm.Retried += (_, _) => retried++;
        Row("Example Editor").RetryCommand.Execute(null);
        Assert.Equal((1, 1), (_rows.Retried.Count, retried));
    }

    [Fact]
    public void RetryThatNoLongerFits_RefreshesInstead()
    {
        _rows.Retryable.Add(("Example.Editor", "2.5.0"));
        _store.Add(Entry("Example.Editor", Now, HistoryResult.Failed, reason: "DiskFull"));
        _vm.Shown();
        var retried = 0;
        _vm.Retried += (_, _) => retried++;
        _rows.Retryable.Clear();
        Row("Example Editor").RetryCommand.Execute(null);
        Assert.Equal(0, retried);
        Assert.False(Row("Example Editor").CanRetry);
    }

    [Fact]
    public void RowChanges_ShowRetryAndIcons_WhileThePageShows()
    {
        _store.Add(Entry("Example.Editor", Now, HistoryResult.Failed, reason: "DiskFull"));
        _rows.KnowsIcons = false;
        _vm.Shown();
        var row = Row("Example Editor");
        Assert.Equal((false, ""), (row.CanRetry, row.LocalId));
        _rows.Retryable.Add(("Example.Editor", "2.5.0"));
        _rows.KnowsIcons = true;
        _rows.Change();
        Assert.True(row.CanRetry);
        Assert.Equal(@"ARP\Machine\X64\Example.Editor", row.LocalId);
        Assert.Same(row, Row("Example Editor"));
    }

    [Fact]
    public void EntriesOlderThan90Days_LeaveWhileTheAppRuns()
    {
        _store.Add(Entry("Example.Editor", Now - TimeSpan.FromDays(89)));
        _vm.Shown();
        Assert.False(_vm.IsEmpty);
        _time.Advance(TimeSpan.FromDays(2));
        _vm.Changed();
        Assert.Empty(_vm.Groups);
        Assert.True(_vm.IsEmpty);
    }

    [Fact]
    public void NoHistory_SaysSo()
    {
        _vm.Shown();
        Assert.Equal((true, false, false), (_vm.IsEmpty, _vm.CanClear, _vm.CanShowOlder));
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task DamagedFileFoundWhenReadAgain_ShowsTheRecoveredNotice()
    {
        File.WriteAllText(HistoryPath, "{ broken");
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None)) Start(Now, TimeZoneInfo.Utc);
        _vm.Shown();
        await Written();
        _vm.Changed();
        Assert.Equal([NoticeKind.HistoryRecovered], _vm.Notices.Select(n => n.Kind));
        Assert.True(_vm.IsEmpty);
    }

    [Fact]
    public async Task UnreadableHistory_ShowsItsNotice_ThenIsReadAgainWhenThePageShows()
    {
        _store.Add(Entry("Example.Editor", Now));
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Start(Now, TimeZoneInfo.Utc);
            _vm.Shown();
            Assert.Equal((false, false), (_vm.IsEmpty, _vm.CanClear));
            Assert.Equal(NoticeKind.HistoryUnreadable, Assert.Single(_vm.Notices).Kind);
            await Written();
            _vm.Hidden();
        }
        _vm.Shown();
        await Written();
        _vm.Changed();
        Assert.Empty(_vm.Notices);
        Assert.Equal("Example Editor", Row("Example Editor").Name);
    }

    [Fact]
    public async Task Clear_ClearsWhatWasShown()
    {
        _store.Add(Entry("Example.Editor", Now));
        _vm.Shown();
        _vm.ClearCommand.Execute(null);
        _rows.Change();
        Assert.False(_vm.ClearCommand.CanExecute(null));
        await Written();
        Assert.Empty(_store.Entries);
        Assert.Equal((true, false), (_vm.IsEmpty, _vm.CanClear));
    }

    [Fact]
    public void IdenticalEntries_GetARowEach()
    {
        _store.Add(Entry("Example.Editor", Now));
        _store.Add(Entry("Example.Editor", Now));
        _vm.Shown();
        _vm.Changed();
        Assert.Equal(2, Assert.Single(_vm.Groups).Rows.Count);
    }

    [Fact]
    public async Task Clear_KeepsAnEntryThePageHasntShownYet()
    {
        _store.Add(Entry("Example.Editor", Now));
        _vm.Shown();
        _store.Add(Entry("Example.Paint", Now + TimeSpan.FromSeconds(1)));
        _vm.ClearCommand.Execute(null);
        await Written();
        Assert.Equal(["Example.Paint"], _store.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task ClearThatCantBeSaved_Explains_AndKeepsTheEntries()
    {
        _store.Add(Entry("Example.Editor", Now));
        Directory.CreateDirectory(HistoryPath + ".tmp");
        _vm.Shown();
        _vm.ClearCommand.Execute(null);
        await Written();
        Assert.Single(_store.Entries);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal(NoticeKind.HistoryNotCleared, notice.Kind);
        Assert.StartsWith("Code: 0x", notice.Details);
        Assert.True(_vm.CanClear);
    }

    [Fact]
    public void LongHistory_ShowsAPageAtATime()
    {
        for (var i = 0; i < 60; i++) _store.Add(Entry($"Example.App{i:00}", Now - TimeSpan.FromMinutes(i)));
        _vm.Shown();
        Assert.Equal((50, true), (_vm.Groups.Sum(g => g.Rows.Count), _vm.CanShowOlder));
        _vm.ShowOlderCommand.Execute(null);
        Assert.Equal((60, false), (_vm.Groups.Sum(g => g.Rows.Count), _vm.CanShowOlder));
    }

    // The page puts the focus there (spec §4.8).
    [Fact]
    public void ShowOlder_NamesTheFirstEntryItAdded()
    {
        for (var i = 0; i < 60; i++) _store.Add(Entry($"Example.App{i:00}", Now - TimeSpan.FromMinutes(i)));
        _vm.Shown();
        HistoryRow? first = null;
        _vm.ShowedOlder += (_, row) => first = row;
        _vm.ShowOlderCommand.Execute(null);
        Assert.Same(_vm.Groups.SelectMany(g => g.Rows).ElementAt(50), first);
        Assert.Equal(Name("Example.App50"), first!.Name);
    }

    [Fact]
    public void HidingThePage_LetsTheRowsGo_AndChangesWaitForTheNextShow()
    {
        _store.Add(Entry("Example.Editor", Now));
        _vm.Shown();
        _vm.Hidden();
        Assert.Empty(_vm.Groups);
        _store.Add(Entry("Example.Paint", Now));
        _vm.Changed();
        _rows.Change();
        Assert.Empty(_vm.Groups);
        _vm.Shown();
        Assert.Equal(2, _vm.Groups.Sum(g => g.Rows.Count));
    }

    [Fact]
    public void HidingThePage_StartsTheNextShowAtOnePage()
    {
        for (var i = 0; i < 60; i++) _store.Add(Entry($"Example.App{i:00}", Now - TimeSpan.FromMinutes(i)));
        _vm.Shown();
        _vm.ShowOlderCommand.Execute(null);
        _vm.Hidden();
        _vm.Shown();
        Assert.Equal((50, true), (_vm.Groups.Sum(g => g.Rows.Count), _vm.CanShowOlder));
    }

    private sealed class FakeRows : IHistoryRows
    {
        public event EventHandler? RowsChanged;

        public HashSet<(string Id, string Version)> Retryable { get; } = [];
        public List<(PackageKey Package, string Version)> Retried { get; } = [];
        // Before the first check, the Updates rows don't exist yet.
        public bool KnowsIcons { get; set; } = true;

        public string LocalIdOf(PackageKey package) => KnowsIcons ? $@"ARP\Machine\X64\{package.Id}" : "";

        public bool CanRetry(PackageKey package, string version) => Retryable.Contains((package.Id, version));

        public bool Retry(PackageKey package, string version)
        {
            if (!CanRetry(package, version)) return false;
            Retried.Add((package, version));
            return true;
        }

        public void Change() => RowsChanged?.Invoke(this, EventArgs.Empty);
    }
}
