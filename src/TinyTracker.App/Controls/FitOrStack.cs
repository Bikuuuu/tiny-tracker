using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyTracker.Core.Layout;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace TinyTracker.App.Controls;

// Two children side by side while they fit, else the second under the first (spec §4.8). FirstShare > 0 marks a first child
// that wraps, such as a label: beside the second it needs only that share of the width, more at larger text sizes.
public sealed partial class FitOrStack : Panel
{
    private static readonly UISettings s_settings = new();

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(FitOrStack),
        new PropertyMetadata(12.0, (d, _) => ((FitOrStack)d).InvalidateMeasure()));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(FitOrStack),
        new PropertyMetadata(6.0, (d, _) => ((FitOrStack)d).InvalidateMeasure()));

    public static readonly DependencyProperty FirstShareProperty = DependencyProperty.Register(nameof(FirstShare), typeof(double), typeof(FitOrStack),
        new PropertyMetadata(0.0, (d, _) => ((FitOrStack)d).InvalidateMeasure()));

    private bool _stacked;

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double FirstShare
    {
        get => (double)GetValue(FirstShareProperty);
        set => SetValue(FirstShareProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var (first, second) = Parts();
        if (first is null) return new Size();
        var unbounded = new Size(double.PositiveInfinity, available.Height);
        first.Measure(unbounded);
        if (second is null) return Fitted(first, available);
        second.Measure(unbounded);
        var width = double.IsInfinity(available.Width) ? first.DesiredSize.Width + Spacing + second.DesiredSize.Width : available.Width;
        _stacked = !SideBySide.Fits(width, first.DesiredSize.Width, second.DesiredSize.Width, Spacing, FirstShare, s_settings.TextScaleFactor);
        if (_stacked)
        {
            first.Measure(new Size(width, available.Height));
            second.Measure(new Size(width, available.Height));
            return new Size(Math.Min(width, Math.Max(first.DesiredSize.Width, second.DesiredSize.Width)), first.DesiredSize.Height + RowSpacing + second.DesiredSize.Height);
        }
        first.Measure(new Size(Math.Max(0, width - Spacing - second.DesiredSize.Width), available.Height));
        return new Size(first.DesiredSize.Width + Spacing + second.DesiredSize.Width, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size size)
    {
        var (first, second) = Parts();
        if (first is null) return size;
        if (second is null)
        {
            first.Arrange(new Rect(0, 0, size.Width, size.Height));
            return size;
        }
        if (_stacked)
        {
            first.Arrange(new Rect(0, 0, size.Width, first.DesiredSize.Height));
            second.Arrange(new Rect(0, first.DesiredSize.Height + RowSpacing, size.Width, second.DesiredSize.Height));
            return size;
        }
        // A label fills what the second leaves, and the second sits at the right; fixed parts stay together on the left.
        var right = second.DesiredSize.Width;
        var firstWidth = FirstShare > 0 ? Math.Max(0, size.Width - Spacing - right) : first.DesiredSize.Width;
        first.Arrange(new Rect(0, 0, firstWidth, size.Height));
        second.Arrange(new Rect(FirstShare > 0 ? Math.Max(0, size.Width - right) : firstWidth + Spacing, 0, right, size.Height));
        return size;
    }

    // A collapsed child takes no part, so the other gets the whole width.
    private (UIElement? First, UIElement? Second) Parts()
    {
        var shown = Children.Where(c => c.Visibility == Visibility.Visible).Take(2).ToList();
        return (shown.ElementAtOrDefault(0), shown.ElementAtOrDefault(1));
    }

    private static Size Fitted(UIElement only, Size available)
    {
        only.Measure(available);
        return only.DesiredSize;
    }
}
