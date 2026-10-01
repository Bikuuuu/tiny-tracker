using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TinyTracker.Presentation;
using Windows.Storage.Streams;

namespace TinyTracker.App;

// Small functions for x:Bind, the hamster logo, a fade, and a walk of the visual tree.
public static class Ui
{
    private static SvgImageSource? s_logo;

    // Every element under the root, in the tree's order.
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    // The element fades in on the compositor when it shows, and goes at once: a fade out would overlap what moves into its place.
    public static void FadeIn(UIElement element)
    {
        var fade = ElementCompositionPreview.GetElementVisual(element).Compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(150);
        ElementCompositionPreview.SetImplicitShowAnimation(element, fade);
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Both(bool first, bool second) => Visible(first && second);

    public static string Chevron(bool expanded) => expanded ? "\uE70E" : "\uE70D";

    public static InfoBarSeverity Severity(NoticeSeverity severity) => severity switch
    {
        NoticeSeverity.Error => InfoBarSeverity.Error,
        NoticeSeverity.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    // Loaded once, from the SVG next to the exe.
    public static async Task<SvgImageSource> LogoAsync()
    {
        if (s_logo is not null) return s_logo;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "hamster-small.svg"));
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var svg = new SvgImageSource();
        await svg.SetSourceAsync(stream);
        return s_logo = svg;
    }
}
