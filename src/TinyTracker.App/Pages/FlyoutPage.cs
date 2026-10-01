using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using TinyTracker.Core.Layout;

namespace TinyTracker.App.Pages;

// A flyout page: a fixed top, a body that scrolls, and a fixed bottom. The flyout sizes itself to fit it.
public partial class FlyoutPage : Page
{
    private FrameworkElement[] _parts = [];
    private AppServices? _services;
    private bool _shown;
    private long _leavingUntil;
    private DependencyObject? _focused;
    private DependencyObject? _opener;

    // Set by the flyout: true while it shows.
    internal static Func<bool> FlyoutOpen { get; set; } = () => false;

    // Raised when the height the page needs without scrolling changes.
    public event EventHandler? NaturalHeightChanged;

    // The top, the whole body (not just the part in view) and the bottom, in DIPs.
    public double NaturalHeight => _parts.Sum(p => p.ActualHeight);

    // True while an item leaves one of the page's lists with a transition, so the flyout shrinks only once the rest has moved up.
    public bool IsLeaving => Environment.TickCount64 < _leavingUntil;

    // For a list whose items leave with a transition: an item that goes marks the page as leaving for a moment.
    protected void WatchLeaving(INotifyCollectionChanged list) => list.CollectionChanged += (_, e) =>
    {
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
            _leavingUntil = Environment.TickCount64 + (long)FlyoutSizer.ShrinkWait.TotalMilliseconds;
    };

    protected AppServices Services => _services ?? throw new InvalidOperationException("The page was shown without being navigated to.");

    protected bool HasServices => _services is not null;

    public FlyoutPage() => GotFocus += (_, e) => _focused = e.OriginalSource as DependencyObject;

    // Where the focus starts when the page opens (spec §4.8).
    protected virtual Control? FirstControl => null;

    public void FocusFirst() => FocusOn(FirstControl);

    // Focus the app moves opens no tooltip.
    protected static bool FocusOn(DependencyObject? target) => target switch
    {
        Control control => control.Focus(FocusState.Programmatic),
        Hyperlink link => link.Focus(FocusState.Programmatic),
        _ => false,
    };

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _services = (AppServices)e.Parameter;
        base.OnNavigatedTo(e);
        if (FlyoutOpen()) SetVisible(true);
        // Back on a page, the focus returns to the control that opened the next one. After layout, so it can take it, and
        // after the frame's own focus on the page's first element.
        var back = e.NavigationMode == NavigationMode.Back;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (FlyoutOpen() && !(back && FocusOn(_opener))) FocusFirst();
        });
    }

    // Before the focus moves to the next page.
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        _opener = _focused;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SetVisible(false);
    }

    // Shown and Hidden run once per change: when the page starts showing in the open flyout, and when it stops.
    internal void SetVisible(bool visible)
    {
        if (visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            Shown();
            return;
        }
        // A later visit's Back returns the focus to the first control, not to one of this visit's.
        _focused = null;
        Hidden();
    }

    protected virtual void Shown()
    {
    }

    protected virtual void Hidden()
    {
    }

    protected void Go(Type page) => Frame.Navigate(page, Services, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    protected void Back()
    {
        if (Frame.CanGoBack) Frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
    }

    // Straight back to Updates, the first page.
    protected void Home()
    {
        while (Frame.BackStackDepth > 1) Frame.BackStack.RemoveAt(Frame.BackStackDepth - 1);
        Back();
    }

    // The body is the panel inside the page's ScrollViewer, so its height is the full content height.
    // Top-aligned, it keeps that height instead of stretching to the view, so the flyout also shrinks with it.
    protected void TrackHeight(FrameworkElement top, FrameworkElement body, FrameworkElement bottom)
    {
        body.VerticalAlignment = VerticalAlignment.Top;
        _parts = [top, body, bottom];
        foreach (var part in _parts) part.SizeChanged += (_, _) => NaturalHeightChanged?.Invoke(this, EventArgs.Empty);
    }
}
