using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Core.History;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.History;

public enum HistoryIcon
{
    Updated,
    // Updated, but Windows still showed the old version.
    Warning,
    Failed,
    Skipped,
    Cancelled,
}

// One day of History, newest entry first.
public sealed partial class HistoryGroup(DateOnly day) : ObservableObject
{
    public DateOnly Day { get; } = day;

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Title { get; internal set; } = "";
}

// One History entry: what happened, in words, and whether it can be tried again.
public sealed partial class HistoryRow : ObservableObject, IStatusLine
{
    private readonly HistoryViewModel _owner;
    private readonly string _spoken;

    internal HistoryRow(HistoryViewModel owner, HistoryEntry entry)
    {
        _owner = owner;
        Entry = entry;
        Name = entry.Name.Length > 0 ? entry.Name : entry.Id;
        (Icon, Status, _spoken) = Describe(entry);
        Details = entry.Result == HistoryResult.Failed && entry.Code is { } code ? Words.Format(Strings.DetailsCode, code) : null;
        RetryName = Words.Format(Strings.RetryApp, Name);
    }

    public HistoryEntry Entry { get; }
    public PackageKey Package => new(Entry.Id, Entry.Source);
    public string Name { get; }
    public HistoryIcon Icon { get; }
    public string Status { get; }
    public string? Details { get; }
    public bool ShowNotes => false;
    public bool HasDetails => Details is not null;
    // For the page's status line. Typed object, as the line's attached property is.
    public object StatusLine => this;
    public string RetryName { get; }

    // One bool per icon, for the page: each icon has its own color.
    public bool IsUpdated => Icon == HistoryIcon.Updated;
    public bool IsWarning => Icon == HistoryIcon.Warning;
    public bool IsFailed => Icon == HistoryIcon.Failed;
    public bool IsSkipped => Icon == HistoryIcon.Skipped;
    public bool IsCancelled => Icon == HistoryIcon.Cancelled;

    [ObservableProperty]
    public partial string Time { get; private set; } = "";

    [ObservableProperty]
    public partial string LocalId { get; private set; } = "";

    // Retry takes the time's place.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTime))]
    public partial bool CanRetry { get; private set; }

    public bool ShowsTime => !CanRetry;

    // Narrator reads the row as one line, with "to" instead of the arrow.
    [ObservableProperty]
    public partial string AutomationName { get; private set; } = "";

    internal void Show(string time, string localId, bool canRetry)
    {
        Time = time;
        LocalId = localId;
        CanRetry = canRetry;
        AutomationName = Joined(Name, ResultWord(Entry.Result), _spoken, time);
    }

    [RelayCommand]
    private void Retry() => _owner.Retry(this);

    private static (HistoryIcon Icon, string Status, string Spoken) Describe(HistoryEntry entry)
    {
        var to = entry.ToVersion;
        switch (entry.Result)
        {
            case HistoryResult.Updated:
            {
                var (versions, spoken) = entry.FromVersion is { } from && to is not null
                    ? (Words.Format(Strings.VersionChange, from, to), Words.Format(Strings.VersionChangeSpoken, from, to))
                    : to is not null ? (Words.Format(Strings.UpdatedTo, to), to) : (Strings.HistoryUpdated, "");
                var note = entry.Reason switch
                {
                    "RestartNeeded" => Strings.HistoryRestartNeeded,
                    "Phantom" => Strings.HistoryPhantom,
                    _ => null,
                };
                var icon = entry.Reason == "Phantom" ? HistoryIcon.Warning : HistoryIcon.Updated;
                return note is null ? (icon, versions, spoken) : (icon, Words.Joined(versions, note), Joined(spoken, note));
            }
            case HistoryResult.Failed:
            {
                var reason = Words.Reason(entry.Reason);
                // "Failed · The update failed" says nothing more than "Failed".
                return reason.Length == 0 || reason == Strings.Reason_Other
                    ? (HistoryIcon.Failed, Strings.HistoryFailed, "")
                    : (HistoryIcon.Failed, Words.Joined(Strings.HistoryFailed, reason), reason);
            }
            case HistoryResult.Skipped:
                return to is null ? (HistoryIcon.Skipped, Strings.HistorySkipped, "") : (HistoryIcon.Skipped, Words.Format(Strings.SkippedVersion, to), to);
            default:
                return to is null ? (HistoryIcon.Cancelled, Strings.HistoryCancelled, "") : (HistoryIcon.Cancelled, Words.Format(Strings.HistoryCancelledTo, to), to);
        }
    }

    private static string ResultWord(HistoryResult result) => result switch
    {
        HistoryResult.Updated => Strings.HistoryUpdated,
        HistoryResult.Failed => Strings.HistoryFailed,
        HistoryResult.Skipped => Strings.HistorySkipped,
        _ => Strings.HistoryCancelled,
    };

    private static string Joined(params string[] parts) =>
        parts.Where(p => p.Length > 0).Aggregate((a, b) => Words.Format(Strings.SpokenJoin, a, b));
}
