using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Core.History;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.History;

// The History page (spec §4.6). Runs on the UI thread, and holds rows only while the page shows.
public sealed partial class HistoryViewModel : ObservableObject
{
    // Entries shown at first, and added by each "Show older".
    public const int PageSize = 50;

    private readonly HistoryStore _store;
    private readonly HistoryWriter _writer;
    private readonly IHistoryRows _rows;
    private readonly TimeProvider _time;
    private readonly Func<CultureInfo> _culture;
    private readonly Dictionary<string, HistoryRow> _byKey = [];
    private readonly Dictionary<DateOnly, HistoryGroup> _byDay = [];
    private int _limit = PageSize;
    private DateTimeOffset? _newestShown;
    private bool _shown;
    private bool _clearing;

    public HistoryViewModel(HistoryStore store, HistoryWriter writer, IHistoryRows rows, TimeProvider time, Func<CultureInfo> culture)
    {
        _store = store;
        _writer = writer;
        _rows = rows;
        _time = time;
        _culture = culture;
        rows.RowsChanged += (_, _) => Changed();
    }

    public ObservableCollection<HistoryGroup> Groups { get; } = [];

    public ObservableCollection<Notice> Notices { get; } = [];

    // Raised when Retry queued an update, so the page can go to Updates, where the row shows it.
    public event EventHandler? Retried;

    // Raised by Show older with the first entry it added, for the page's focus (spec §4.8).
    public event EventHandler<HistoryRow>? ShowedOlder;

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    public partial bool CanClear { get; private set; }

    [ObservableProperty]
    public partial bool CanShowOlder { get; private set; }

    public void Shown()
    {
        _shown = true;
        if (_store.Unreadable) _writer.Retry();
        Refresh();
    }

    // Rows go when the page hides, so their visuals can go too.
    public void Hidden()
    {
        _shown = false;
        _limit = PageSize;
        Groups.Clear();
        _byKey.Clear();
        _byDay.Clear();
    }

    // History or the Updates rows changed.
    public void Changed()
    {
        if (_shown) Refresh();
    }

    [RelayCommand]
    private void ShowOlder()
    {
        var shown = _limit;
        _limit += PageSize;
        Refresh();
        if (Groups.SelectMany(g => g.Rows).ElementAtOrDefault(shown) is { } first) ShowedOlder?.Invoke(this, first);
    }

    // Clears what the user saw: an entry the page hasn't shown yet stays.
    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        if (_newestShown is not { } newest) return;
        _clearing = true;
        CanClear = false;
        _writer.Clear(newest, error =>
        {
            _clearing = false;
            if (error is not null) Show(Notice.HistoryNotCleared(error));
            Refresh();
        });
    }

    [RelayCommand]
    private void Dismiss(Notice notice) => Notices.Remove(notice);

    // The row may have changed since the page showed it, so Retry is checked again.
    internal void Retry(HistoryRow row)
    {
        if (row.Entry.ToVersion is { } version && _rows.Retry(row.Package, version)) Retried?.Invoke(this, EventArgs.Empty);
        else Refresh();
    }

    // Entries of the last 90 days, newest first; the store prunes only when it writes.
    private List<HistoryEntry> Visible()
    {
        var cutoff = _time.GetUtcNow() - HistoryStore.Retention;
        return [.. _store.Entries.Where(e => e.Time >= cutoff)];
    }

    private void Refresh()
    {
        if (!_shown) return;
        var entries = Visible();
        _newestShown = entries.FirstOrDefault()?.Time;
        var zone = _time.LocalTimeZone;
        var culture = _culture();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).DateTime);
        var newest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<HistoryGroup>();
        var rows = new Dictionary<HistoryGroup, List<HistoryRow>>();
        var keys = new HashSet<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // Only an app's newest entry can offer Retry.
            var isNewest = newest.Add($"{entry.Source}|{entry.Id}");
            if (i >= _limit) continue;
            var local = TimeZoneInfo.ConvertTime(entry.Time, zone);
            var day = DateOnly.FromDateTime(local.DateTime);
            if (!_byDay.TryGetValue(day, out var group)) _byDay[day] = group = new HistoryGroup(day);
            if (!rows.TryGetValue(group, out var list))
            {
                rows[group] = list = [];
                groups.Add(group);
                group.Title = Words.Day(day, today);
            }
            // Two identical entries still get a row each.
            var same = $"{entry.Time.UtcTicks}|{entry.Source}|{entry.Id}|{entry.Result}";
            var key = same;
            for (var n = 1; !keys.Add(key); n++) key = $"{same}#{n}";
            if (!_byKey.TryGetValue(key, out var row)) _byKey[key] = row = new HistoryRow(this, entry);
            var canRetry = isNewest && entry.Result == HistoryResult.Failed && entry.ToVersion is { } version && _rows.CanRetry(row.Package, version);
            row.Show(Words.TimeOfDay(local, culture), _rows.LocalIdOf(row.Package), canRetry);
            list.Add(row);
        }
        foreach (var key in _byKey.Keys.Where(k => !keys.Contains(k)).ToList()) _byKey.Remove(key);
        foreach (var day in _byDay.Keys.Where(d => groups.All(g => g.Day != d)).ToList()) _byDay.Remove(day);
        CollectionSync.Apply(Groups, groups);
        foreach (var group in groups) CollectionSync.Apply(group.Rows, rows[group]);

        var unreadable = _store.Unreadable;
        if (unreadable) Show(Notice.HistoryUnreadable);
        else Hide(NoticeKind.HistoryUnreadable);
        if (_store.TakeRecovered()) Show(Notice.HistoryRecovered);
        IsEmpty = entries.Count == 0 && !unreadable;
        CanClear = entries.Count > 0 && !_clearing;
        CanShowOlder = entries.Count > _limit;
    }

    private void Show(Notice notice)
    {
        if (Notices.Any(n => n.Kind == notice.Kind)) return;
        Notices.Add(notice);
    }

    private void Hide(NoticeKind kind)
    {
        if (Notices.FirstOrDefault(n => n.Kind == kind) is { } notice) Notices.Remove(notice);
    }
}
