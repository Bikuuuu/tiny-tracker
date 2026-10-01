using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Presentation.Updates;

// One tracked app on the Updates page. Its commands go to the page.
public sealed partial class UpdateRow : ObservableObject
{
    private readonly UpdatesViewModel _owner;

    internal UpdateRow(UpdatesViewModel owner, AppCheck check, DateTimeOffset now)
    {
        _owner = owner;
        Key = new PackageKey(check.App.Id, check.App.Source);
        Check = check;
        Show(now);
    }

    public PackageKey Key { get; }

    [ObservableProperty]
    public partial RowView View { get; private set; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial string LocalId { get; private set; } = "";

    [ObservableProperty]
    public partial bool Auto { get; private set; }

    // "Removed · Undo" shows for a few seconds after Stop tracking.
    [ObservableProperty]
    public partial bool IsRemoved { get; internal set; }

    internal AppCheck Check { get; set; }

    internal InstallItem? Install { get; set; }

    // The app as it was tracked, put back by Undo.
    internal TrackedApp? RemovedApp { get; set; }

    internal ITimer? Timer { get; set; }

    // Counts skips and undos, so a save that lands late knows whether its skip still stands.
    internal int Skips { get; set; }

    // Why an Auto app hasn't installed by itself, as of the last refresh.
    internal AutoBlock AutoBlock { get; private set; } = AutoBlock.AutoOff;

    // isAuto: whether the app is on Auto, by its own choice or the switch.
    internal void Show(DateTimeOffset now, AutoBlock auto = AutoBlock.AutoOff, int daysLeft = 0, string? windowStart = null, bool whatsNew = true, bool securityFirst = true,
        bool isAuto = false)
    {
        AutoBlock = auto;
        View = RowView.Of(Check, Install, now, auto, daysLeft, windowStart, whatsNew, securityFirst);
        Name = Check.App.Name.Length > 0 ? Check.App.Name : Check.App.Id;
        LocalId = Check.Package?.LocalId ?? "";
        Auto = isAuto;
    }

    [RelayCommand]
    private void Primary() => _owner.Primary(this);

    [RelayCommand]
    private void Cancel() => _owner.Cancel(this);

    [RelayCommand]
    private void UpdateNow() => _owner.Update(this);

    [RelayCommand]
    private void Skip() => _owner.Skip(this);

    [RelayCommand]
    private void UndoSkip() => _owner.UndoSkip(this);

    [RelayCommand]
    private void ToggleAuto() => _owner.ToggleAuto(this);

    [RelayCommand]
    private void OpenNotes() => _owner.OpenNotes(this);

    [RelayCommand]
    private void StopTracking() => _owner.StopTracking(this);

    [RelayCommand]
    private void Undo() => _owner.UndoRemove(this);
}
