using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Input;
using TinyTracker.Core.Settings;
using TinyTracker.Presentation;
using TinyTracker.Presentation.Settings;
using Windows.Globalization.NumberFormatting;
using Windows.System;
using Windows.UI.Core;

namespace TinyTracker.App.Pages;

// Kept while the app runs, so it subscribes once.
public sealed partial class SettingsPage : FlyoutPage
{
    // The key that ended a recording lifts later; it mustn't click the button again.
    private bool _swallowKeyUp;

    public SettingsPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
        // Whole kilobytes, with no grouping.
        SpeedLimitBox.NumberFormatter = new DecimalFormatter { IntegerDigits = 1, FractionDigits = 0, IsGrouped = false, NumberRounder = new IncrementNumberRounder { Increment = 1 } };
    }

    public SettingsViewModel Settings => Services.SettingsView;

    protected override Control FirstControl => ChooseAppsButton;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (first) Settings.PropertyChanged += OnSettingsChanged;
    }

    protected override void Shown()
    {
        Scroller.ChangeView(null, 0, null, true);
        Settings.Open();
    }

    protected override void Hidden()
    {
        _swallowKeyUp = false;
        Settings.Close();
    }

    // Narrator hears how the copy went, and that Restore is at work.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AppListText) && Settings.AppListText == Strings.FindingInstalledApps)
            Announce(RestoreButton, AutomationNotificationKind.Other, Settings.AppListText, "Restore");
        if (e.PropertyName != nameof(SettingsViewModel.CopyText) || Settings.CopyText is not { } text || (text != Strings.Copied && text != Strings.CopyFailed)) return;
        Announce(CopyButton, AutomationNotificationKind.ActionCompleted, text, "CopyDiagnostics");
    }

    private static void Announce(UIElement element, AutomationNotificationKind kind, string text, string id)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(kind, AutomationNotificationProcessing.ImportantMostRecent, text, id);
    }

    private void OnShortcutClick(object sender, RoutedEventArgs e) => Settings.StartRecording();

    // While recording, keys go to the recorder first, so Esc clears the shortcut instead of going back.
    private void OnShortcutKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!Settings.IsRecording) return;
        e.Handled = Settings.Record(HeldModifiers(), (int)e.Key);
        _swallowKeyUp |= e.Handled;
    }

    private void OnShortcutKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_swallowKeyUp && !Settings.IsRecording) return;
        e.Handled = true;
        _swallowKeyUp = Settings.IsRecording;
    }

    private void OnShortcutLostFocus(object sender, RoutedEventArgs e)
    {
        _swallowKeyUp = false;
        Settings.CancelRecording();
    }

    private static ShortcutModifiers HeldModifiers()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var modifiers = ShortcutModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= ShortcutModifiers.Control;
        if (Down(VirtualKey.Menu)) modifiers |= ShortcutModifiers.Alt;
        if (Down(VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= ShortcutModifiers.Windows;
        return modifiers;
    }

    private void OnBack(object sender, RoutedEventArgs e) => Back();

    private void OnChooseApps(object sender, RoutedEventArgs e) => Go(typeof(ChooseAppsPage));

    private void OnQuit(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

    private void OnGitHub(Hyperlink sender, HyperlinkClickEventArgs args) => Settings.OpenGitHubCommand.Execute(null);

    private void OnLicense(Hyperlink sender, HyperlinkClickEventArgs args) => Settings.OpenLicenseCommand.Execute(null);

    private void OnTipPage(Hyperlink sender, HyperlinkClickEventArgs args) => Settings.OpenTipPageCommand.Execute(null);

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is Notice notice) Settings.DismissCommand.Execute(notice);
    }
}
