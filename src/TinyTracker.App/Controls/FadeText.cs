using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using TinyTracker.App.Pages;

namespace TinyTracker.App.Controls;

// Text that crossfades to its new words (spec §4.3); while the flyout is hidden it changes at once.
public sealed partial class FadeText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(FadeText),
        new PropertyMetadata("", (d, e) => ((FadeText)d).Show((string)e.NewValue ?? "")));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(nameof(TextStyle), typeof(Style), typeof(FadeText),
        new PropertyMetadata(null, (d, e) => ((FadeText)d).Restyle((Style)e.NewValue)));

    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(150);
    private TextBlock _shown = Line(1, Visibility.Visible);
    private TextBlock _next = Line(0, Visibility.Collapsed);
    private Storyboard? _fading;

    public FadeText()
    {
        Children.Add(_shown);
        Children.Add(_next);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    private static TextBlock Line(double opacity, Visibility visibility) => new() { TextWrapping = TextWrapping.Wrap, Opacity = opacity, Visibility = visibility };

    private void Restyle(Style? style) => _shown.Style = _next.Style = style;

    private void Show(string text)
    {
        Settle();
        if (!IsLoaded || !FlyoutPage.FlyoutOpen() || _shown.Text.Length == 0)
        {
            _shown.Text = text;
            return;
        }
        _next.Text = text;
        _next.Visibility = Visibility.Visible;
        var fading = new Storyboard();
        fading.Children.Add(To(_shown, 0));
        fading.Children.Add(To(_next, 1));
        fading.Completed += (_, _) =>
        {
            if (_fading == fading) Settle();
        };
        _fading = fading;
        fading.Begin();
    }

    // Ends a crossfade at once: the new words stay, the old ones go.
    private void Settle()
    {
        if (_fading is not { } fading) return;
        _fading = null;
        (_shown, _next) = (_next, _shown);
        _shown.Opacity = 1;
        _next.Opacity = 0;
        _next.Visibility = Visibility.Collapsed;
        fading.Stop();
    }

    private static DoubleAnimation To(TextBlock line, double opacity)
    {
        var animation = new DoubleAnimation { To = opacity, Duration = Fade };
        Storyboard.SetTarget(animation, line);
        Storyboard.SetTargetProperty(animation, nameof(Opacity));
        return animation;
    }
}
