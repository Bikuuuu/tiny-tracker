using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TinyTracker.Presentation.Updates;

// Tiny Tracker's own update, at the top of the Updates page. Its commands go to the page.
public sealed partial class SelfUpdateRow : ObservableObject
{
    private readonly UpdatesViewModel _owner;

    internal SelfUpdateRow(UpdatesViewModel owner) => _owner = owner;

    [ObservableProperty]
    public partial SelfUpdateView View { get; internal set; } = SelfUpdateView.Hidden;

    // Update, or Retry after a failure.
    [RelayCommand]
    private void Primary() => _owner.UpdateSelf();

    [RelayCommand]
    private void Cancel() => _owner.CancelSelf();

    [RelayCommand]
    private void OpenNotes() => _owner.OpenSelfNotes();
}
