using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using TinyTracker.App.Controls;
using TinyTracker.Presentation;
using TinyTracker.Presentation.Updates;

namespace TinyTracker.App.Pages;

public sealed partial class UpdatesPage : FlyoutPage
{
    private readonly Storyboard _spin = new() { RepeatBehavior = RepeatBehavior.Forever };

    public UpdatesPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
        var turn = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1) };
        Storyboard.SetTarget(turn, Spin);
        Storyboard.SetTargetProperty(turn, "Angle");
        _spin.Children.Add(turn);
        Ui.FadeIn(ProblemBar);
        Ui.FadeIn(SelfCard);
        Ui.FadeIn(UpToDateList);
        Loaded += async (_, _) => Logo.Source = EmptyLogo.Source = SelfLogo.Source = await Ui.LogoAsync();
    }

    public UpdatesViewModel Updates => Services.Updates;

    protected override Control FirstControl => CheckNowButton;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (first)
        {
            Updates.PropertyChanged += OnUpdatesChanged;
            Updates.NotesRequested += (_, _) => Go(typeof(WhatsNewPage));
            Updates.NewAppAnswered += (_, next) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusNextNewApp(next));
            WatchLeaving(Updates.Updates);
            WatchLeaving(Updates.UpToDate);
            WatchLeaving(Updates.Notices);
        }
        Spinning();
    }

    // After an answer, the next new app's Track, else Check now (spec §4.8), once a layout pass has built its notice.
    private void FocusNextNewApp(Notice? next)
    {
        Top.UpdateLayout();
        var track = next is null ? null : Ui.Descendants(Top).OfType<NoticeBar>().FirstOrDefault(bar => ReferenceEquals(bar.DataContext, next))?.MainButton;
        FocusOn(track ?? CheckNowButton);
    }

    protected override void Shown() => Updates.Shown();

    protected override void Hidden() => Updates.Hidden();

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdatesViewModel.IsSpinning)) Spinning();
    }

    private void Spinning()
    {
        if (Updates.IsSpinning) _spin.Begin();
        else _spin.Stop();
    }

    private void OnHistory(object sender, RoutedEventArgs e) => Go(typeof(HistoryPage));

    private void OnSettings(object sender, RoutedEventArgs e) => Go(typeof(SettingsPage));

    private void OnChooseApps(object sender, RoutedEventArgs e) => Go(typeof(ChooseAppsPage));

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is Notice notice) Updates.DismissCommand.Execute(notice);
    }
}
