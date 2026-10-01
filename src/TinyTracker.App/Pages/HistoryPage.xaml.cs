using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using TinyTracker.Presentation;
using TinyTracker.Presentation.History;

namespace TinyTracker.App.Pages;

// Kept while the app runs, so it subscribes once. Its rows exist only while it shows.
public sealed partial class HistoryPage : FlyoutPage
{
    public HistoryPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
    }

    public HistoryViewModel History => Services.HistoryView;

    // Enter there only goes back; an entry's would run its Retry.
    protected override Control FirstControl => BackButton;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (!first) return;
        // Retry sends the user to Updates, where the row shows the install.
        History.Retried += (_, _) => Home();
        History.ShowedOlder += (_, row) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusOlder(row));
    }

    // Show older puts the focus on the first entry it added (spec §4.8), once a layout pass has built it.
    private void FocusOlder(HistoryRow row)
    {
        Body.UpdateLayout();
        if (!FocusOn(Ui.Descendants(Body).OfType<ContentControl>().FirstOrDefault(entry => entry.DataContext == row))) FocusFirst();
    }

    // The time zone and time format may have changed while the app ran.
    protected override void Shown()
    {
        TimeZoneInfo.ClearCachedData();
        CultureInfo.CurrentCulture.ClearCachedData();
        Scroller.ChangeView(null, 0, null, true);
        History.Shown();
    }

    protected override void Hidden() => History.Hidden();

    private void OnBack(object sender, RoutedEventArgs e) => Back();

    // Enter or Space on a focused row runs its Retry. Keys on its Details link or Retry button stay theirs.
    private void OnRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not FrameworkElement { DataContext: HistoryRow { CanRetry: true } row }) return;
        row.RetryCommand.Execute(null);
        e.Handled = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        ConfirmClear.Hide();
        History.ClearCommand.Execute(null);
        // The Clear history button is off until the clear ends.
        BackButton.Focus(FocusState.Programmatic);
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is Notice notice) History.DismissCommand.Execute(notice);
    }
}
