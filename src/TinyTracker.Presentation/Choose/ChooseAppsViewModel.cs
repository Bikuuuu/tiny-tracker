using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Choose;

// The Choose apps page (spec §4.4). Nothing is ticked until the user ticks it; each tick is saved at once.
// statusOf: what the last check found for a tracked app, which says why the list can't show it.
public sealed partial class ChooseAppsViewModel(IAppInventory inventory, SettingsStore settings, SettingsWriter writer, FileLog log, Action<Action> post,
    Func<string, string, AppStatus?>? statusOf = null) : ObservableObject
{
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private List<ChooseRow> _all = [];
    private List<ElsewhereRow> _elsewhere = [];
    private CancellationTokenSource? _loading;

    public ObservableCollection<ChooseRow> Apps { get; } = [];
    public ObservableCollection<ElsewhereRow> Elsewhere { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowSelectedOnly { get; private set; }

    [ObservableProperty]
    public partial string FilterText { get; private set; } = Strings.ShowSelected;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsElsewhereExpanded { get; private set; }

    [ObservableProperty]
    public partial bool HasElsewhere { get; private set; }

    [ObservableProperty]
    public partial string ElsewhereText { get; private set; } = "";

    [ObservableProperty]
    public partial string SelectedText { get; private set; } = "";

    // One link: Select all, or Deselect all once all shown are ticked; hidden while the list loads or shows nothing (spec §4.4).
    [ObservableProperty]
    public partial bool ShowsSelectAll { get; private set; }

    [ObservableProperty]
    public partial bool DeselectsAll { get; private set; }

    [ObservableProperty]
    public partial string SelectAllText { get; private set; } = Strings.SelectAll;

    // What Deselect all asks first: "Stop tracking 12 apps?"
    [ObservableProperty]
    public partial string DeselectQuestion { get; private set; } = "";

    // Nothing starts ticked; once something is, ticks apply as they're made.
    [ObservableProperty]
    public partial string FooterText { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoMatches { get; private set; }

    [ObservableProperty]
    public partial string EmptyText { get; private set; } = Strings.NoAppsFound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial Notice? Problem { get; private set; }

    // What the banner binds to: x:Bind doesn't rerun a function once its argument is null.
    public bool HasProblem => Problem is not null;

    // Reads the installed apps: the plain list first, then again with the lookup matches moved in.
    public void Open()
    {
        _added.Clear();
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        IsLoading = _all.Count == 0;
        Problem = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var first = new Reporter<AppInventory>(list => post(() => Fill(list, loading, final: false)));
                var result = await inventory.ReadAsync(first, loading.Token);
                post(() => Fill(result, loading, final: true));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (e is not PackageSourceException) log.Error("Reading installed apps failed", e);
                post(() => Failed(e as PackageSourceException, loading));
            }
        });
    }

    // True when apps were added, so they get a check.
    public bool Close()
    {
        _loading?.Cancel();
        _loading = null;
        var added = _added.Count > 0;
        _added.Clear();
        return added;
    }

    partial void OnSearchChanged(string value) => Filter();

    [RelayCommand]
    private void ToggleShowSelected()
    {
        ShowSelectedOnly = !ShowSelectedOnly;
        FilterText = ShowSelectedOnly ? Strings.ShowAll : Strings.ShowSelected;
        Filter();
    }

    [RelayCommand]
    private void ToggleElsewhere() => IsElsewhereExpanded = !IsElsewhereExpanded;

    [RelayCommand]
    private void Retry() => Open();

    internal void Toggled(ChooseRow row, bool tracked) => Save([row], tracked);

    // Every app the list shows, in one save.
    [RelayCommand]
    private void SelectAll() => Tick([.. Apps.Where(r => !r.IsTracked)], true);

    // The page asks first.
    [RelayCommand]
    private void DeselectAll() => Tick([.. Apps.Where(r => r.IsTracked)], false);

    private void Tick(List<ChooseRow> rows, bool tracked)
    {
        foreach (var row in rows) row.SetTracked(tracked);
        Save(rows, tracked);
    }

    // A save that fails puts the ticks back, and the checks they asked for.
    private void Save(List<ChooseRow> rows, bool tracked)
    {
        if (rows.Count == 0) return;
        var keys = rows.Select(r => Key(r.App.Id, r.App.Source)).ToList();
        var wereAdded = keys.Where(_added.Contains).ToList();
        foreach (var key in keys)
        {
            if (tracked) _added.Add(key);
            else _added.Remove(key);
        }
        writer.Update(file =>
        {
            if (!tracked) return file with { Apps = file.Apps.Where(a => !rows.Any(r => a.Matches(r.App.Id, r.App.Source))).ToList() };
            var added = rows.Where(r => !file.Apps.Any(a => a.Matches(r.App.Id, r.App.Source)))
                .Select(r => new TrackedApp { Id = r.App.Id, Source = r.App.Source, Name = r.App.Name }).ToList();
            return added.Count == 0 ? file : file with { Apps = [.. file.Apps, .. added] };
        },
            error =>
            {
                if (error is null) return;
                foreach (var row in rows) row.SetTracked(!tracked);
                _added.ExceptWith(keys);
                _added.UnionWith(wereAdded);
                Problem = Notice.SaveFailed(error);
                Counts();
            });
        Counts();
    }

    private static string Key(string id, string source) => $"{source}|{id}";

    // final: winget's lookups are in, so a tracked app the list doesn't have is missing.
    private void Fill(AppInventory list, CancellationTokenSource loading, bool final)
    {
        if (loading != _loading) return;
        // Rows already shown keep their tick, which may not be saved yet.
        var shown = _all.ToDictionary(r => Key(r.App.Id, r.App.Source), StringComparer.OrdinalIgnoreCase);
        var tracked = settings.Current.Apps;
        var listed = list.Trackable
            .Select(app => shown.TryGetValue(Key(app.Id, app.Source), out var row) && !row.IsMissing ? row : new ChooseRow(this, app, tracked.Any(a => a.Matches(app.Id, app.Source))))
            .ToList();
        // They come first, so every tracked app shows and the count agrees with Settings.
        List<ChooseRow> missing = final
            ? [.. tracked.Where(t => !list.Trackable.Any(a => t.Matches(a.Id, a.Source)))
                .Select(t => shown.TryGetValue(Key(t.Id, t.Source), out var row) && row.IsMissing ? row : Missing(t))
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)]
            : [];
        _all = [.. missing, .. listed];
        // A tracked app still installed, such as one whose version winget can't read, shows once: ticked, with the tracked apps.
        _elsewhere = list.Elsewhere.Where(e => !missing.Any(m => m.StillInstalled && string.Equals(m.Name, e.Name, StringComparison.CurrentCultureIgnoreCase)))
            .Select(ElsewhereRow.From).ToList();
        IsLoading = false;
        Filter();
    }

    private ChooseRow Missing(TrackedApp app)
    {
        var status = statusOf?.Invoke(app.Id, app.Source);
        var why = status switch
        {
            AppStatus.NotInCatalog => Strings.NotFoundInWinGet,
            AppStatus.VersionUnknown => Strings.VersionUnknown,
            AppStatus.NotFound => Strings.NotInstalledAnymore,
            _ => app.MissingSince is not null ? Strings.NotInstalledAnymore : Strings.NotListedNow,
        };
        return ChooseRow.Missing(this, app, why, installed: status is AppStatus.NotInCatalog or AppStatus.VersionUnknown);
    }

    private void Failed(PackageSourceException? error, CancellationTokenSource loading)
    {
        if (loading != _loading) return;
        IsLoading = false;
        Problem = error?.Problem is CheckProblem.WinGetMissing or CheckProblem.WinGetTooOld
            ? Notice.ForProblem(error.Problem, error.Message)
            : new Notice(NoticeKind.Check, NoticeSeverity.Error, Strings.InventoryFailed, Details: error?.Message);
    }

    private void Filter()
    {
        var search = Search.Trim();
        bool Matches(string name, string publisher) =>
            search.Length == 0 || name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        CollectionSync.Apply(Apps, _all.Where(r => (!ShowSelectedOnly || r.IsTracked) && Matches(r.App.Name, r.App.Publisher)).ToList());
        CollectionSync.Apply(Elsewhere, _elsewhere.Where(e => !ShowSelectedOnly && Matches(e.Name, e.Publisher)).ToList());
        HasElsewhere = Elsewhere.Count > 0;
        ElsewhereText = Words.Format(Strings.UpdatedElsewhere, Elsewhere.Count);
        NoMatches = !IsLoading && Apps.Count == 0 && Elsewhere.Count == 0;
        EmptyText = search.Length > 0 ? Strings.NoMatches : ShowSelectedOnly && _all.Count > 0 ? Strings.NoneSelected : Strings.NoAppsFound;
        Counts();
    }

    private void Counts()
    {
        var ticked = _all.Count(r => r.IsTracked);
        SelectedText = Words.Format(Strings.SelectedCount, ticked, _all.Count);
        FooterText = ticked == 0 ? Strings.ChooseFooter : Strings.ChooseFooterTicked;
        var shownTicked = Apps.Count(r => r.IsTracked);
        ShowsSelectAll = !IsLoading && Apps.Count > 0;
        DeselectsAll = ShowsSelectAll && shownTicked == Apps.Count;
        SelectAllText = DeselectsAll ? Strings.DeselectAll : Strings.SelectAll;
        DeselectQuestion = Words.StopTrackingApps(shownTicked);
    }

    // Reports on the calling thread, unlike Progress<T>.
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
