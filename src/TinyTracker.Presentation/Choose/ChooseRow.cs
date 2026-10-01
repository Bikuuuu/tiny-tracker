using CommunityToolkit.Mvvm.ComponentModel;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Choose;

// An app winget can update, or a tracked app the list can't show, with why. Ticking it starts tracking it.
public sealed partial class ChooseRow : ObservableObject
{
    private readonly ChooseAppsViewModel _owner;
    private readonly string? _why;
    private bool _quiet;

    internal ChooseRow(ChooseAppsViewModel owner, InventoryApp app, bool tracked, string? why = null)
    {
        _owner = owner;
        App = app;
        _why = why;
        SetTracked(tracked);
    }

    internal static ChooseRow Missing(ChooseAppsViewModel owner, TrackedApp app, string why, bool installed) =>
        new(owner, new InventoryApp(app.Id, app.Source, app.Name.Length > 0 ? app.Name : app.Id, "", "", ""), true, why) { StillInstalled = installed };

    public InventoryApp App { get; }
    public bool IsMissing => _why is not null;
    // A missing row whose app is installed, just not in winget's list.
    internal bool StillInstalled { get; private init; }
    public string Name => App.Name;
    public string LocalId => App.LocalId;
    public string Detail => _why ?? (App.Publisher.Length > 0 ? Words.Joined(App.Version, App.Publisher) : App.Version);

    [ObservableProperty]
    public partial bool IsTracked { get; set; }

    // Changes the tick without saving anything.
    internal void SetTracked(bool tracked)
    {
        _quiet = true;
        IsTracked = tracked;
        _quiet = false;
    }

    partial void OnIsTrackedChanged(bool value)
    {
        if (!_quiet) _owner.Toggled(this, value);
    }
}

// An installed app winget can't update, with what updates it.
public sealed record ElsewhereRow(string Name, string Publisher, string Detail, string LocalId)
{
    public static ElsewhereRow From(ElsewhereApp app)
    {
        var by = Words.Elsewhere(app.UpdatedBy);
        return new ElsewhereRow(app.Name, app.Publisher, PackageVersion.Parse(app.Version).IsUnknown ? by : Words.Joined(app.Version, by), app.LocalId);
    }
}
