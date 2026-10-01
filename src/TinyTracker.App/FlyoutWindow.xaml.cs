using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TinyTracker.App.Interop;
using TinyTracker.App.Pages;
using TinyTracker.Core;
using TinyTracker.Core.Layout;
#if DEBUG
using TinyTracker.Presentation.Demo;
#endif
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace TinyTracker.App;

public sealed partial class FlyoutWindow : Window
{
    private readonly nint _hwnd;
    private readonly FlyoutToggle _toggle = new(TimeProvider.System);
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly FlyoutSizer _sizer;
    private AppServices? _services;
    private FlyoutPage? _page;
    // One fit per dispatcher turn, after layout, at once when the turn changed the page.
    private bool _fitQueued;
    private bool _fitPageChange;
    // UAC prompts open now.
    private int _prompts;
    // Work queued before a quit still runs after it, and the closed window has no AppWindow.
    private bool _closed;

    private void ApplySystemTheme()
    {
        if (_closed) return;
        Root.RequestedTheme = WindowsMode.IsLight() ? ElementTheme.Light : ElementTheme.Dark;
    }

    public FlyoutWindow()
    {
        InitializeComponent();
        var queue = DispatcherQueue;
        _sizer = new FlyoutSizer(TimeProvider.System, action => queue.TryEnqueue(() => action()), Resize);
        Closed += (_, _) =>
        {
            _closed = true;
            _sizer.Dispose();
        };
        Title = AppInfo.Name;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        Dwm.SetRoundedCorners(_hwnd);
        ApplySystemTheme();
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplySystemTheme);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && Volatile.Read(ref _prompts) == 0) Hide(); };
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            Back();
            e.Handled = true;
        };
        Root.KeyboardAccelerators.Add(escape);
        Pages.Navigated += (_, e) => Attach(e.Content as FlyoutPage);
        FlyoutPage.FlyoutOpen = () => _toggle.IsOpen;
    }

    public bool IsOpen => _toggle.IsOpen;

    public nint Handle => _hwnd;

    // The flyout stays open behind a UAC prompt, so its rows show how it went. Called on the prompt's thread before it shows, so
    // the focus loss can't come first. After the last prompt it takes the focus back, whatever the answer, or else hides.
    public void Prompting(bool open)
    {
        if (open)
        {
            Interlocked.Increment(ref _prompts);
            return;
        }
        if (Interlocked.Decrement(ref _prompts) > 0) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!await TakeFocusSoonAsync()) Hide();
        });
    }

    // The desktop can take a moment to let go of the foreground, as after a prompt. False when it never did.
    private async Task<bool> TakeFocusSoonAsync()
    {
        for (var tries = 0; tries < 5; tries++)
        {
            if (_closed || Volatile.Read(ref _prompts) > 0 || !_toggle.IsOpen || TakeFocus()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    // After a No, Windows gives the focus to the app used before and refuses a plain request for it back. Sharing that window's
    // input for a moment lets the flyout take it.
    private bool TakeFocus()
    {
        if (NativeMethods.SetForegroundWindow(_hwnd)) return true;
        var front = NativeMethods.GetForegroundWindow();
        var theirs = front == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(front, out _);
        var ours = NativeMethods.GetCurrentThreadId();
        if (theirs == 0 || theirs == ours || !NativeMethods.AttachThreadInput(ours, theirs, true)) return false;
        try
        {
            NativeMethods.BringWindowToTop(_hwnd);
            return NativeMethods.SetForegroundWindow(_hwnd);
        }
        finally
        {
            NativeMethods.AttachThreadInput(ours, theirs, false);
        }
    }

    // Raised when the flyout shows or hides.
    public event EventHandler? OpenChanged;

    public void Start(AppServices services)
    {
        _services = services;
        Pages.Navigate(typeof(UpdatesPage), services, new SuppressNavigationTransitionInfo());
    }

    public void Prewarm()
    {
        Dwm.SetCloaked(_hwnd, true);
        MoveToCorner();
        AppWindow.Show(false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_closed || _toggle.IsOpen) return;
            AppWindow.Hide();
            Root.Visibility = Visibility.Collapsed;
            OpenChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void OnTrayClick()
    {
        switch (_toggle.OnTrayClick())
        {
            case ToggleAction.Open: Show(); break;
            case ToggleAction.Close: Hide(); break;
        }
    }

    // The shortcut opens the flyout, or closes it when it's open.
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    // Shows the flyout, on this page when one is given.
    public void Show(Type? page = null)
    {
        if (_closed) return;
        if (page is not null && _services is not null && Pages.CurrentSourcePageType != page)
            Pages.Navigate(page, _services, _toggle.IsOpen ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight } : new SuppressNavigationTransitionInfo());
        if (_toggle.IsOpen) return;
        // Before the flyout counts as open, so fresh text shows at once, and before layout, so the first frame fits it.
        _page?.SetVisible(true);
        _toggle.Opened();
        _services?.Updates.FlyoutOpened();
        _services?.Announcer.FlyoutOpened();
        Root.Visibility = Visibility.Visible;
        Root.Opacity = 0;
        Root.UpdateLayout();
        MoveToCorner();
        AppWindow.Show(true);
        // WinUI windows refuse WS_EX_TOPMOST; the flyout relies on foreground activation instead, even when another program
        // started the app, so it closes when the user clicks elsewhere.
        if (!TakeFocus()) DispatcherQueue.TryEnqueue(async () => await TakeFocusSoonAsync());
        OpenChanged?.Invoke(this, EventArgs.Empty);
        // Uncloak one frame later so the first frame is already rendered.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_closed) return;
            Dwm.SetCloaked(_hwnd, false);
            PlayOpenAnimation();
            // A keyboard user starts on the page's first control (spec §4.8).
            _page?.FocusFirst();
        });
    }

    public void Hide()
    {
        if (_closed || !_toggle.IsOpen) return;
        _toggle.Closed();
        _page?.SetVisible(false);
        Dwm.SetCloaked(_hwnd, true);
        AppWindow.Hide();
        // The next open starts on Updates. Leaving Choose apps this way still checks the apps it added.
        while (Pages.CanGoBack) Pages.GoBack(new SuppressNavigationTransitionInfo());
        // Nothing renders or animates while the flyout is hidden.
        Root.Visibility = Visibility.Collapsed;
        OpenChanged?.Invoke(this, EventArgs.Empty);
    }

    // For --self-check: loads and shows every page out of sight, and names the ones that loaded. It waits for Choose apps to list
    // apps (or to fail), and in the demo for update and history rows too, so the row templates are built.
    public async Task<IReadOnlyList<string>> LoadEveryPageAsync()
    {
        var loaded = new List<string>();
        if (_closed || _services is not { } services) return loaded;
        Dwm.SetCloaked(_hwnd, true);
        Root.Visibility = Visibility.Visible;
        AppWindow.Show(false);
        if (services.Demo) services.Updates.CheckNowCommand.Execute(null);
        foreach (var type in new[] { typeof(UpdatesPage), typeof(ChooseAppsPage), typeof(SettingsPage), typeof(HistoryPage), typeof(WhatsNewPage) })
        {
            if (_closed) return loaded;
#if DEBUG
            // In the demo, the page shows an app's notes, so their lines are built too.
            if (services.Demo && type == typeof(WhatsNewPage)) services.Updates.Updates.FirstOrDefault(r => r.Name == DemoWinGet.LongNotes)?.OpenNotesCommand.Execute(null);
#endif
            if (Pages.CurrentSourcePageType != type) Pages.Navigate(type, services, new SuppressNavigationTransitionInfo());
            if (Pages.Content is not FlyoutPage page) continue;
            if (!page.IsLoaded)
            {
                var ready = new TaskCompletionSource();
                page.Loaded += (_, _) => ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            page.SetVisible(true);
            if (services.Demo && type == typeof(UpdatesPage)) await Until(() => services.Updates.Updates.Count > 0 && services.Updates.UpToDate.Count > 0);
            if (type == typeof(ChooseAppsPage)) await Until(() => services.Choose.Apps.Count > 0 || services.Choose.Problem is not null);
            if (services.Demo && type == typeof(HistoryPage)) await Until(() => services.HistoryView.Groups.Count > 0);
            if (services.Demo && type == typeof(WhatsNewPage)) await Until(() => services.Updates.WhatsNew.Lines.Count > 0);
            loaded.Add(type.Name);
            page.SetVisible(false);
        }
        if (_closed) return loaded;
        while (Pages.CanGoBack) Pages.GoBack(new SuppressNavigationTransitionInfo());
        AppWindow.Hide();
        Root.Visibility = Visibility.Collapsed;
        return loaded;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var tries = 0; tries < 100 && !done(); tries++) await Task.Delay(100);
        // One more moment for the rows to be laid out.
        await Task.Delay(300);
    }

    // Esc goes back a page, or closes the flyout on Updates.
    private void Back()
    {
        if (Pages.CanGoBack) Pages.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
        else Hide();
    }

    private void Attach(FlyoutPage? page)
    {
        if (_page is not null) _page.NaturalHeightChanged -= OnNaturalHeightChanged;
        _page = page;
        if (page is null) return;
        page.NaturalHeightChanged += OnNaturalHeightChanged;
        QueueFit(pageChange: true);
    }

    private void OnNaturalHeightChanged(object? sender, EventArgs e) => QueueFit(pageChange: false);

    // After this turn, so a page's Shown and every part's new size count, and it resizes once, never to an old height first.
    private void QueueFit(bool pageChange)
    {
        _fitPageChange |= pageChange;
        if (_fitQueued) return;
        _fitQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _fitQueued = false;
            var page = _fitPageChange;
            _fitPageChange = false;
            Fit(page);
        });
    }

    private void Fit(bool pageChange)
    {
        if (_closed || !_toggle.IsOpen || _page is null) return;
        Root.UpdateLayout();
        if (_page.NaturalHeight <= 0) return;
        var (work, dpi) = Screens.TaskbarMonitor();
        _sizer.Request(FlyoutPlacement.Compute(work, dpi, _page.NaturalHeight).Height, atOnce: pageChange || !_page.IsLeaving);
    }

    // Keeps the bottom edge above the taskbar while the content grows or shrinks.
    private void Resize(int height)
    {
        if (_closed || !_toggle.IsOpen) return;
        var (work, dpi) = Screens.TaskbarMonitor();
        var rect = FlyoutPlacement.Place(work, dpi, height);
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
    }

    private void MoveToCorner()
    {
        var (work, dpi) = Screens.TaskbarMonitor();
        var height = _page?.NaturalHeight ?? 0;
        if (height <= 0)
        {
            Root.Measure(new Windows.Foundation.Size(FlyoutPlacement.WidthDip, double.PositiveInfinity));
            height = Root.DesiredSize.Height;
        }
        var rect = FlyoutPlacement.Compute(work, dpi, height);
        // Move onto the target monitor first so a DPI change can't rescale the final size.
        AppWindow.Move(new PointInt32(rect.X, rect.Y));
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
        _sizer.Reset(rect.Height);
    }

    private void PlayOpenAnimation()
    {
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation
        {
            From = 16,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new ExponentialEase { Exponent = 7, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, Slide);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
