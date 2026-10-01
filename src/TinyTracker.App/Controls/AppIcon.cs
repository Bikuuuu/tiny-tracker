using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyTracker.App.Icons;
using Windows.UI;

namespace TinyTracker.App.Controls;

// An app's own icon, or a letter tile while it loads or when it has none.
public sealed partial class AppIcon : Grid
{
    public static readonly DependencyProperty LocalIdProperty =
        DependencyProperty.Register(nameof(LocalId), typeof(string), typeof(AppIcon), new PropertyMetadata("", (d, _) => ((AppIcon)d).Load()));

    public static readonly DependencyProperty AppNameProperty =
        DependencyProperty.Register(nameof(AppName), typeof(string), typeof(AppIcon), new PropertyMetadata("", (d, _) => ((AppIcon)d).Letter()));

    private static readonly Color[] Tiles =
    [
        Color.FromArgb(255, 0x0F, 0x7B, 0x0F),
        Color.FromArgb(255, 0x4F, 0x6B, 0xED),
        Color.FromArgb(255, 0xC2, 0x39, 0xB3),
        Color.FromArgb(255, 0xCA, 0x50, 0x10),
        Color.FromArgb(255, 0x03, 0x83, 0x87),
        Color.FromArgb(255, 0x87, 0x64, 0xB8),
        Color.FromArgb(255, 0xD1, 0x34, 0x38),
        Color.FromArgb(255, 0x98, 0x6F, 0x0B),
    ];

    private readonly Border _tile = new() { CornerRadius = new CornerRadius(4) };
    private readonly TextBlock _letter = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Colors.White),
        // Sized to the tile, which keeps its size at large text sizes.
        IsTextScaleFactorEnabled = false,
    };
    private readonly Image _image = new();
    private int _loads;

    public AppIcon()
    {
        _tile.Child = _letter;
        Children.Add(_tile);
        Children.Add(_image);
        SizeChanged += (_, _) =>
        {
            _letter.FontSize = Math.Max(9, ActualHeight * 0.5);
            Load();
        };
    }

    public string LocalId
    {
        get => (string)GetValue(LocalIdProperty);
        set => SetValue(LocalIdProperty, value);
    }

    public string AppName
    {
        get => (string)GetValue(AppNameProperty);
        set => SetValue(AppNameProperty, value);
    }

    private void Letter()
    {
        var name = AppName ?? "";
        _letter.Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "";
        var hash = name.Aggregate(0, (sum, c) => unchecked(sum * 31 + c));
        _tile.Background = new SolidColorBrush(Tiles[(hash & int.MaxValue) % Tiles.Length]);
    }

    private async void Load()
    {
        var load = ++_loads;
        _image.Source = null;
        _tile.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(LocalId) || ActualWidth <= 0 || XamlRoot is null) return;
        var pixels = (int)Math.Ceiling(ActualWidth * XamlRoot.RasterizationScale);
        var icon = await IconLoader.GetAsync(LocalId, pixels);
        if (load != _loads || icon is null) return;
        _image.Source = icon;
        _tile.Visibility = Visibility.Collapsed;
    }
}
