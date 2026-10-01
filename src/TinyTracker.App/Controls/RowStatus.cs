using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using TinyTracker.Presentation;
using Windows.Foundation;

namespace TinyTracker.App.Controls;

// Fills a status line: the words, then "Details" or "What's new" as a link, wrapping as one text.
public static class RowStatus
{
    // Typed object, as XAML can't build RowView's type info: its members are required.
    // Anything but an IStatusLine shows nothing.
    public static readonly DependencyProperty ViewProperty = DependencyProperty.RegisterAttached(
        "View", typeof(object), typeof(RowStatus), new PropertyMetadata(null, (d, _) => Fill((TextBlock)d)));

    public static readonly DependencyProperty NotesProperty = DependencyProperty.RegisterAttached(
        "Notes", typeof(ICommand), typeof(RowStatus), new PropertyMetadata(null));

    public static object? GetView(DependencyObject text) => text.GetValue(ViewProperty);

    public static void SetView(DependencyObject text, object? value) => text.SetValue(ViewProperty, value);

    public static ICommand? GetNotes(DependencyObject text) => (ICommand?)text.GetValue(NotesProperty);

    public static void SetNotes(DependencyObject text, ICommand? value) => text.SetValue(NotesProperty, value);

    private static void Fill(TextBlock text)
    {
        text.Inlines.Clear();
        if (GetView(text) is not IStatusLine view) return;
        text.Inlines.Add(new Run { Text = view.Status });
        var link = view.Details is not null ? Strings.Details : view.ShowNotes ? Strings.WhatsNew : null;
        if (link is null) return;
        // The dot stays with the words before it.
        if (view.Status.Length > 0) text.Inlines.Add(new Run { Text = "\u00a0· " });
        var hyperlink = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
        hyperlink.Inlines.Add(new Run { Text = link });
        hyperlink.Click += (_, _) => Open(text, hyperlink);
        text.Inlines.Add(hyperlink);
    }

    private static void Open(TextBlock text, Hyperlink link)
    {
        if (GetView(text) is not IStatusLine view) return;
        if (view.Details is not { } details)
        {
            GetNotes(text)?.Execute(null);
            return;
        }
        var flyout = new Flyout { Content = new TextBlock { Text = details, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } };
        var at = link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        flyout.ShowAt(text, new FlyoutShowOptions { Position = new Point(at.X, at.Bottom), Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }
}
