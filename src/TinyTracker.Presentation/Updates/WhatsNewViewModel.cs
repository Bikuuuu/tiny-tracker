using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Updates;

// The What's new page (spec §4.9): one row's release notes, their link, and the row's own button. Runs on the UI thread.
public sealed partial class WhatsNewViewModel : ObservableObject
{
    private readonly Action<string> _openLink;
    private readonly Func<UpdateRow, bool> _onPage;
    private readonly Action<UpdateRow> _primary;
    private string? _text;
    private string? _link;
    // The offered version the notes shown belong to.
    private string? _version;

    // onPage: the row is still on Updates, where its button works.
    internal WhatsNewViewModel(Action<string> openLink, Func<UpdateRow, bool> onPage, Action<UpdateRow> primary)
    {
        _openLink = openLink;
        _onPage = onPage;
        _primary = primary;
    }

    [ObservableProperty]
    public partial UpdateRow? Row { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<NoteLine> Lines { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasLink { get; private set; }

    // "No release notes for 2.6.0." once a check brings a newer version without notes (spec §4.9).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoNotes))]
    public partial string NoNotes { get; private set; } = "";

    public bool HasNoNotes => NoNotes.Length > 0;

    // The footer's button: none while the row is busy or once its app is no longer tracked.
    [ObservableProperty]
    public partial bool OffersAction { get; private set; }

    // The button did the row's work: back to Updates, where the row shows it.
    public event EventHandler? Done;

    internal void Show(UpdateRow row)
    {
        Row = row;
        _text = null;
        _link = null;
        _version = null;
        Lines = [];
        NoNotes = "";
        Changed();
    }

    // A check may bring another version, with its own notes and link. Notes that go with no newer version, as when the update
    // installs, stay on the page with their link.
    internal void Changed()
    {
        OffersAction = Row is { } row && row.View.OffersAction && _onPage(row);
        if (Row?.Check.Package is not { } package) return;
        if (package.ReleaseNotes is { } text)
        {
            _version = package.AvailableVersion;
            _link = package.ReleaseNotesUrl;
            HasLink = _link is not null;
            NoNotes = "";
            if (text == _text) return;
            _text = text;
            Lines = NotesText.Lines(text);
            return;
        }
        if (package.AvailableVersion is not { } version || version == _version) return;
        _version = version;
        _text = null;
        Lines = [];
        _link = package.ReleaseNotesUrl;
        HasLink = _link is not null;
        NoNotes = Words.Format(Strings.NoReleaseNotes, version);
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (_link is { } url) _openLink(url);
    }

    [RelayCommand]
    private void Primary()
    {
        if (Row is not { } row || !OffersAction) return;
        _primary(row);
        Done?.Invoke(this, EventArgs.Empty);
    }
}
