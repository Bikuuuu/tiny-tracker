using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using TinyTracker.Presentation.Updates;

namespace TinyTracker.App.Pages;

// Kept while the app runs, so it subscribes once. It shows the row it was opened from (spec §4.9).
public sealed partial class WhatsNewPage : FlyoutPage
{
    // A bullet and an en space fill about this much, so wrapped lines line up with the first.
    private const double BulletIndent = 12;
    private const double LevelIndent = 14;
    private static readonly string s_bullet = "•" + (char)0x2002;

    public WhatsNewPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
    }

    public WhatsNewViewModel WhatsNew => Services.Updates.WhatsNew;

    // Enter there only goes back; the footer's button would install.
    protected override Control FirstControl => BackButton;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (!first) return;
        // The button did the row's work: back to Updates, where the row shows it.
        WhatsNew.Done += (_, _) => Home();
        WhatsNew.PropertyChanged += OnWhatsNewChanged;
        ShowNotes();
    }

    protected override void Shown() => Scroller.ChangeView(null, 0, null, true);

    // A newer version's notes show from the top.
    private void OnWhatsNewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WhatsNewViewModel.Lines)) return;
        ShowNotes();
        Scroller.ChangeView(null, 0, null, true);
    }

    // One text, so the notes take one Tab stop and a selection can run across lines.
    private void ShowNotes()
    {
        Notes.Blocks.Clear();
        var gap = false;
        foreach (var line in WhatsNew.Lines)
        {
            if (line.IsGap)
            {
                gap = true;
                continue;
            }
            var paragraph = new Paragraph
            {
                Margin = new Thickness(Indent(line), Notes.Blocks.Count == 0 ? 0 : gap ? 10 : 2, 0, 0),
                TextIndent = line.IsBullet ? -BulletIndent : 0,
            };
            if (line.IsBullet) paragraph.Inlines.Add(new Run { Text = s_bullet });
            paragraph.Inlines.Add(new Run { Text = line.Text, FontWeight = line.IsHeading ? FontWeights.SemiBold : FontWeights.Normal });
            Notes.Blocks.Add(paragraph);
            gap = false;
        }
        // Empty, it would put a second gap between the page's texts.
        Notes.Visibility = Notes.Blocks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // A bullet hangs its mark, and a wrapped line lines up with its item's text;
    // a numbered line sits where a bullet of its level would.
    private static double Indent(NoteLine line) => line.Level * LevelIndent + (line.IsBullet || line.IsWrapped ? BulletIndent : 0);

    private void OnBack(object sender, RoutedEventArgs e) => Back();
}
