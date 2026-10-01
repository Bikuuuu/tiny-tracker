using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Choose;
using TinyTracker.Presentation.Settings;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Choose;

public sealed class ChooseAppsViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly InventoryApp Editor = Trackable("Example.Editor", "2.4.1");
    private static readonly InventoryApp Paint = Trackable("Example.Paint", "7.0", "Contoso");
    private static readonly InventoryApp Viewer = Trackable("Example.Viewer", "3.3");
    private static readonly ElsewhereApp Game = new("Example Game", "1.0", "Example Studio", @"ARP\Machine\X64\Steam App 123", UpdatedBy.Steam);
    private static readonly ElsewhereApp Launcher = new("Example Launcher", "Unknown", "Example", @"ARP\Machine\X64\Example Launcher", UpdatedBy.ItSelf);

    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly FakeInventory _inventory = new();
    // What the last check found, by app id.
    private readonly Dictionary<string, AppStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SettingsStore _settings;
    private readonly FileLog _log;
    private readonly SettingsWriter _writer;
    private readonly ChooseAppsViewModel _vm;

    public ChooseAppsViewModelTests()
    {
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _log = new FileLog(_folder.PathOf("app.log"), new FakeTimeProvider(Now));
        _writer = new SettingsWriter(_settings, _log, _ui.Post);
        _vm = new ChooseAppsViewModel(_inventory, _settings, _writer, _log, _ui.Post, (id, _) => _statuses.TryGetValue(id, out var status) ? status : null);
        _inventory.First = new AppInventory([Editor, Viewer], [Game, Launcher, new ElsewhereApp("Example Paint", "7.0", "Contoso", Paint.LocalId, UpdatedBy.Unknown)]);
    }

    // Ticks a test left saving land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(Wait);
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InventoryApp Trackable(string id, string version, string publisher = "Example Publisher") =>
        new(id, "winget", Name(id), version, publisher, $@"ARP\Machine\X64\{Name(id)}");

    private async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            _ui.Pump();
            if (condition()) return;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(5, Ct);
        }
    }

    private async Task OpenFully(int rows = 3)
    {
        _vm.Open();
        _inventory.Finish(new AppInventory([Editor, Paint, Viewer], [Game, Launcher]));
        await Until(() => _vm.Apps.Count == rows);
    }

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    [Fact]
    public async Task Open_ShowsTheFirstList_ThenTheMatchesMovedIn()
    {
        _vm.Open();
        Assert.True(_vm.IsLoading);
        await Until(() => _vm.Apps.Count == 2);
        Assert.False(_vm.IsLoading);
        Assert.Equal(["Example Editor", "Example Viewer"], _vm.Apps.Select(r => r.Name));
        Assert.Equal("Updated elsewhere (3)", _vm.ElsewhereText);
        _inventory.Finish(new AppInventory([Editor, Paint, Viewer], [Game, Launcher]));
        await Until(() => _vm.Apps.Count == 3);
        Assert.Equal("Updated elsewhere (2)", _vm.ElsewhereText);
    }

    [Fact]
    public async Task NothingIsTicked_UntilTheUserChooses()
    {
        await OpenFully();
        Assert.DoesNotContain(_vm.Apps, r => r.IsTracked);
        Assert.Equal("0 of 3 selected", _vm.SelectedText);
        Assert.Equal("Nothing is ticked until you choose", _vm.FooterText);
    }

    [Fact]
    public async Task TrackedApps_AreTicked()
    {
        _settings.Update(f => f with { Apps = [App("Example.Paint")] });
        await OpenFully();
        Assert.Equal(["Example Paint"], _vm.Apps.Where(r => r.IsTracked).Select(r => r.Name));
        Assert.Equal("1 of 3 selected", _vm.SelectedText);
        Assert.Equal("Changes apply as you tick", _vm.FooterText);
    }

    [Fact]
    public async Task Ticking_TracksTheApp_AndAsksForACheck()
    {
        await OpenFully();
        _vm.Apps[0].IsTracked = true;
        await Saved();
        var app = Assert.Single(_settings.Current.Apps);
        Assert.Equal(("Example.Editor", "winget", "Example Editor"), (app.Id, app.Source, app.Name));
        Assert.True(_vm.Close());
    }

    [Fact]
    public async Task TickThenUntick_LeavesNothingToCheck()
    {
        await OpenFully();
        _vm.Apps[0].IsTracked = true;
        _vm.Apps[0].IsTracked = false;
        await Saved();
        Assert.Empty(_settings.Current.Apps);
        Assert.False(_vm.Close());
    }

    // Settings counts every tracked app, so Choose apps shows them all (spec §4.4).
    [Fact]
    public async Task TrackedAppsItCantList_ComeFirst_TickedAndSayingWhy()
    {
        _settings.Update(f => f with
        {
            Apps = [App("Example.Editor"), App("Contoso.Mail") with { MissingSince = Now }, App("Fabrikam.Paint"), App("Example.Launcher"), App("Northwind.Notes"), App("Wingtip.Radio")],
        });
        _statuses["Fabrikam.Paint"] = AppStatus.NotInCatalog;
        _statuses["Example.Launcher"] = AppStatus.VersionUnknown;
        _statuses["Wingtip.Radio"] = AppStatus.NotFound;
        await OpenFully(8);
        Assert.Equal(["Contoso Mail", "Example Launcher", "Fabrikam Paint", "Northwind Notes", "Wingtip Radio", "Example Editor", "Example Paint", "Example Viewer"], _vm.Apps.Select(r => r.Name));
        Assert.Equal(["Not installed anymore", "Version unknown · this app updates itself", "Not found in winget", "Not found right now", "Not installed anymore"], _vm.Apps.Take(5).Select(r => r.Detail));
        Assert.All(_vm.Apps.Take(6), r => Assert.True(r.IsTracked));
        Assert.Equal("6 of 8 selected", _vm.SelectedText);
        // The launcher shows once, ticked at the top.
        Assert.Equal("Updated elsewhere (1)", _vm.ElsewhereText);
    }

    // Only an app that's still installed shows once: one that's gone leaves another of its name where it is.
    [Fact]
    public async Task AppNotInstalledAnymore_LeavesTheSameNameUpdatedElsewhere()
    {
        _settings.Update(f => f with { Apps = [App("Example.Game") with { MissingSince = Now }, App("Example.Launcher")] });
        _statuses["Example.Launcher"] = AppStatus.VersionUnknown;
        await OpenFully(5);
        Assert.Equal(["Not installed anymore", "Version unknown · this app updates itself"], _vm.Apps.Take(2).Select(r => r.Detail));
        Assert.Equal(["Example Game"], _vm.Elsewhere.Select(r => r.Name));
    }

    // The plain list comes before winget's lookups, which may still find them.
    [Fact]
    public async Task FirstList_WaitsForTheLookups_BeforeCallingAnAppMissing()
    {
        _settings.Update(f => f with { Apps = [App("Example.Paint")] });
        _vm.Open();
        await Until(() => _vm.Apps.Count == 2);
        Assert.Equal(["Example Editor", "Example Viewer"], _vm.Apps.Select(r => r.Name));
        _inventory.Finish(new AppInventory([Editor, Paint, Viewer], [Game, Launcher]));
        await Until(() => _vm.Apps.Count == 3);
        Assert.Equal("Example Paint", Assert.Single(_vm.Apps, r => r.IsTracked).Name);
        Assert.Equal("7.0 · Contoso", Assert.Single(_vm.Apps, r => r.IsTracked).Detail);
    }

    [Fact]
    public async Task UntickingATrackedAppItCantList_StopsTrackingIt()
    {
        _settings.Update(f => f with { Apps = [App("Contoso.Mail") with { MissingSince = Now }] });
        await OpenFully(4);
        var mail = _vm.Apps[0];
        mail.IsTracked = false;
        await Saved();
        Assert.Empty(_settings.Current.Apps);
        Assert.Contains(mail, _vm.Apps);
        Assert.Equal("0 of 4 selected", _vm.SelectedText);
    }

    [Fact]
    public async Task Unticking_StopsTracking()
    {
        _settings.Update(f => f with { Apps = [App("Example.Paint")] });
        await OpenFully();
        _vm.Apps.Single(r => r.Name == "Example Paint").IsTracked = false;
        await Saved();
        Assert.Empty(_settings.Current.Apps);
    }

    [Fact]
    public async Task Search_FiltersBothLists_ByNameOrPublisher()
    {
        await OpenFully();
        _vm.Search = "contoso";
        Assert.Equal(["Example Paint"], _vm.Apps.Select(r => r.Name));
        Assert.Empty(_vm.Elsewhere);
        _vm.Search = " game ";
        Assert.Empty(_vm.Apps);
        Assert.Equal(["Example Game"], _vm.Elsewhere.Select(r => r.Name));
        Assert.Equal("Updated elsewhere (1)", _vm.ElsewhereText);
        _vm.Search = "nothing like this";
        Assert.True(_vm.NoMatches);
    }

    [Fact]
    public async Task ShowSelected_ListsOnlyTickedApps()
    {
        _settings.Update(f => f with { Apps = [App("Example.Viewer")] });
        await OpenFully();
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.Equal(["Example Viewer"], _vm.Apps.Select(r => r.Name));
        Assert.Equal("Show all", _vm.FilterText);
        Assert.False(_vm.HasElsewhere);
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.Equal(3, _vm.Apps.Count);
    }

    [Fact]
    public async Task EmptyList_SaysWhy()
    {
        await OpenFully();
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.True(_vm.NoMatches);
        Assert.Equal("No apps selected yet", _vm.EmptyText);
        _vm.ToggleShowSelectedCommand.Execute(null);
        _vm.Search = "nothing like this";
        Assert.Equal("No apps match your search", _vm.EmptyText);
    }

    // Select all ticks the apps the list shows (spec §4.4).
    [Fact]
    public async Task SelectAll_TicksTheAppsShown_AndTracksThem()
    {
        await OpenFully();
        _vm.Search = "Paint";
        Assert.Equal((true, false, "Select all"), (_vm.ShowsSelectAll, _vm.DeselectsAll, _vm.SelectAllText));
        _vm.SelectAllCommand.Execute(null);
        await Saved();
        Assert.Equal(["Example.Paint"], _settings.Current.Apps.Select(a => a.Id));
        Assert.Equal(("1 of 3 selected", true, "Deselect all"), (_vm.SelectedText, _vm.DeselectsAll, _vm.SelectAllText));
        Assert.True(_vm.Close());
    }

    // Once they're all ticked it reads Deselect all, whose question says how many apps it stops tracking.
    [Fact]
    public async Task DeselectAll_UnticksTheAppsShown()
    {
        _settings.Update(f => f with { Apps = [App("Example.Editor"), App("Example.Paint"), App("Example.Viewer")] });
        await OpenFully();
        Assert.Equal((true, "Deselect all", "Stop tracking 3 apps?"), (_vm.DeselectsAll, _vm.SelectAllText, _vm.DeselectQuestion));
        _vm.Search = "Editor";
        Assert.Equal("Stop tracking 1 app?", _vm.DeselectQuestion);
        _vm.DeselectAllCommand.Execute(null);
        await Saved();
        Assert.Equal(["Example.Paint", "Example.Viewer"], _settings.Current.Apps.Select(a => a.Id));
        Assert.Equal((true, false, "Select all"), (_vm.ShowsSelectAll, _vm.DeselectsAll, _vm.SelectAllText));
        Assert.False(_vm.Close());
    }

    // Show selected narrows it to the ticked apps, those listed with a reason too.
    [Fact]
    public async Task DeselectAll_CoversTrackedAppsListedWithAReason()
    {
        _settings.Update(f => f with { Apps = [App("Contoso.Mail") with { MissingSince = Now }, App("Example.Paint")] });
        await OpenFully(4);
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.Equal(["Contoso Mail", "Example Paint"], _vm.Apps.Select(r => r.Name));
        Assert.Equal((true, "Stop tracking 2 apps?"), (_vm.DeselectsAll, _vm.DeselectQuestion));
        _vm.DeselectAllCommand.Execute(null);
        await Saved();
        Assert.Empty(_settings.Current.Apps);
        Assert.Equal("0 of 4 selected", _vm.SelectedText);
    }

    [Fact]
    public async Task SelectAll_IsHidden_WhileTheListLoads_AndWhenNothingShows()
    {
        _vm.Open();
        Assert.False(_vm.ShowsSelectAll);
        await Until(() => _vm.Apps.Count == 2);
        Assert.True(_vm.ShowsSelectAll);
        _vm.Search = "nothing like this";
        Assert.False(_vm.ShowsSelectAll);
    }

    [Fact]
    public async Task SelectAllThatCantBeSaved_IsUndone()
    {
        await OpenFully();
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        _vm.SelectAllCommand.Execute(null);
        await Saved();
        Assert.DoesNotContain(_vm.Apps, r => r.IsTracked);
        Assert.Equal(NoticeKind.SaveFailed, _vm.Problem!.Kind);
        Assert.False(_vm.Close());
    }

    [Fact]
    public async Task NoInstalledApps_SaysNoneFound()
    {
        _inventory.First = new AppInventory([], []);
        _vm.Open();
        _inventory.Finish(new AppInventory([], []));
        await Until(() => _vm.NoMatches);
        Assert.Equal("No apps found", _vm.EmptyText);
    }

    [Fact]
    public async Task UpdatedElsewhere_StartsCollapsed_AndSaysWhatUpdatesEachApp()
    {
        await OpenFully();
        Assert.False(_vm.IsElsewhereExpanded);
        Assert.Equal(["1.0 · Updated by Steam", "Updates itself"], _vm.Elsewhere.Select(r => r.Detail));
        _vm.ToggleElsewhereCommand.Execute(null);
        Assert.True(_vm.IsElsewhereExpanded);
    }

    [Fact]
    public async Task Rows_ShowVersionAndPublisher()
    {
        await OpenFully();
        Assert.Equal("7.0 · Contoso", _vm.Apps.Single(r => r.Name == "Example Paint").Detail);
    }

    [Fact]
    public async Task WinGetFailure_ShowsTheProblem_AndRetryReadsAgain()
    {
        _inventory.Error = new PackageSourceException(CheckProblem.WinGetTooOld, "winget 1.11.510 is older than 1.29.280.");
        _vm.Open();
        await Until(() => _vm.Problem is not null);
        Assert.Equal("winget needs an update", _vm.Problem!.Title);
        Assert.False(_vm.IsLoading);
        _inventory.Error = null;
        _vm.RetryCommand.Execute(null);
        _inventory.Finish(new AppInventory([Editor], []));
        await Until(() => _vm.Apps.Count == 1);
        Assert.Null(_vm.Problem);
    }

    [Fact]
    public async Task ProblemGone_ClosesTheBanner()
    {
        _inventory.Error = new PackageSourceException(CheckProblem.WinGetTooOld, "winget 1.11.510 is older than 1.29.280.");
        _vm.Open();
        await Until(() => _vm.Problem is not null);
        Assert.True(_vm.HasProblem);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _inventory.Error = null;
        _vm.RetryCommand.Execute(null);
        _inventory.Finish(new AppInventory([Editor], []));
        await Until(() => _vm.Apps.Count == 1);
        Assert.False(_vm.HasProblem);
        Assert.Contains(nameof(ChooseAppsViewModel.HasProblem), changed);
    }

    [Fact]
    public async Task Reopening_KeepsTheListWhileItReloads()
    {
        await OpenFully();
        _vm.Close();
        _inventory.Reset();
        _vm.Open();
        Assert.False(_vm.IsLoading);
        Assert.Equal(3, _vm.Apps.Count);
    }

    [Fact]
    public async Task EarlierRead_ThatEndsLate_IsIgnored()
    {
        var slow = _inventory.Pending;
        _vm.Open();
        await Until(() => _vm.Apps.Count == 2);
        _inventory.Reset();
        _vm.Open();
        _inventory.Finish(new AppInventory([Viewer], []));
        await Until(() => _vm.Apps.Count == 1);
        slow.SetResult(new AppInventory([Editor, Paint, Viewer], []));
        await Task.Delay(50, Ct);
        _ui.Pump();
        Assert.Equal(["Example Viewer"], _vm.Apps.Select(r => r.Name));
    }

    [Fact]
    public async Task TickThatCantBeSaved_IsUndone()
    {
        await OpenFully();
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        _vm.Apps[0].IsTracked = true;
        await Saved();
        Assert.False(_vm.Apps[0].IsTracked);
        Assert.Equal((NoticeKind.SaveFailed, "Code: 0x80070005"), (_vm.Problem!.Kind, _vm.Problem.Details));
        Assert.False(_vm.Close());
    }

    // The tick comes back, and so does the check the app was getting.
    [Fact]
    public async Task UntickThatCantBeSaved_KeepsTheAppsCheck()
    {
        await OpenFully();
        _vm.Apps[0].IsTracked = true;
        await Saved();
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        _vm.Apps[0].IsTracked = false;
        await Saved();
        Assert.True(_vm.Apps[0].IsTracked);
        Assert.True(_vm.Close());
    }

    // Reports First at once, then waits for the test to finish the read.
    private sealed class FakeInventory : IAppInventory
    {
        public AppInventory First { get; set; } = new([], []);
        public PackageSourceException? Error { get; set; }
        public TaskCompletionSource<AppInventory> Pending { get; private set; } = New();

        public Task<AppInventory> ReadAsync(IProgress<AppInventory>? firstList, CancellationToken ct)
        {
            if (Error is not null) return Task.FromException<AppInventory>(Error);
            firstList?.Report(First);
            return Pending.Task.WaitAsync(ct);
        }

        public void Finish(AppInventory result) => Pending.TrySetResult(result);

        public void Reset() => Pending = New();

        private static TaskCompletionSource<AppInventory> New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
