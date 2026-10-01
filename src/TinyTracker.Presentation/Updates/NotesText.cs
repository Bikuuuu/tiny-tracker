using System.Text.RegularExpressions;

namespace TinyTracker.Presentation.Updates;

public enum NoteKind
{
    Text,
    Heading,
    Bullet,
    // A hand-wrapped line that continues the list item above it.
    Wrapped,
    // A blank line between two others.
    Gap,
}

// One line of release notes as the What's new page shows it.
// Level: how deep a list item sits, from 0; a wrapped line has its item's.
public sealed record NoteLine(NoteKind Kind, string Text = "", int Level = 0)
{
    public bool IsHeading => Kind == NoteKind.Heading;
    public bool IsBullet => Kind == NoteKind.Bullet;
    public bool IsWrapped => Kind == NoteKind.Wrapped;
    public bool IsGap => Kind == NoteKind.Gap;
}

// Release notes into lines (spec §4.9): bullets with their levels, headings, one gap per run of blank lines or rules, and no
// Markdown marks. Addresses stay text.
public static partial class NotesText
{
    private const int MaxLevel = 3;

    public static IReadOnlyList<NoteLine> Lines(string text)
    {
        var lines = new List<NoteLine>();
        // The indents of the list items above: a bullet's level follows how it nests under them.
        var indents = new List<int>();
        // The last list item's level, which its wrapped lines take.
        var item = 0;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var body = raw.Trim();
            // A line underlined with === or --- is a heading.
            if (Underline().IsMatch(body) && lines.Count > 0 && lines[^1] is { Kind: NoteKind.Text } above && !Numbered().IsMatch(above.Text))
            {
                lines[^1] = above with { Kind = NoteKind.Heading };
                indents.Clear();
                continue;
            }
            if (body.Length == 0 || Rule().IsMatch(body))
            {
                if (lines.Count > 0 && !lines[^1].IsGap) lines.Add(new NoteLine(NoteKind.Gap));
                continue;
            }
            if (MarkdownHeading().Match(body) is { Success: true } heading)
            {
                indents.Clear();
                lines.Add(new NoteLine(NoteKind.Heading, Clean(heading.Groups[1].Value)));
            }
            else if (BulletMark().Match(body) is { Success: true } bullet)
            {
                item = Math.Min(MaxLevel, Level(indents, Indent(raw)));
                lines.Add(new NoteLine(NoteKind.Bullet, Clean(body[bullet.Length..]), item));
            }
            // A numbered line is a list item too, and an indented line under one continues it; other text starts a new list.
            else if (Numbered().IsMatch(body))
            {
                item = Math.Min(MaxLevel, Level(indents, Indent(raw)));
                lines.Add(new NoteLine(NoteKind.Text, Clean(body), item));
            }
            else if (indents.Count > 0 && Indent(raw) > indents[0]) lines.Add(new NoteLine(NoteKind.Wrapped, Clean(body), item));
            else
            {
                indents.Clear();
                lines.Add(new NoteLine(NoteKind.Text, Clean(body)));
            }
        }
        if (lines.Count > 0 && lines[^1].IsGap) lines.RemoveAt(lines.Count - 1);
        // A line with no end punctuation heads the bullets after it, unless it's numbered.
        NoteLine? next = null;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i] is { Kind: NoteKind.Text, Text: var words } && next is { IsBullet: true } && words.Length > 0 && !".,;:!?".Contains(words[^1])
                && !Numbered().IsMatch(words))
                lines[i] = lines[i] with { Kind = NoteKind.Heading };
            if (!lines[i].IsGap) next = lines[i];
        }
        return lines;
    }

    // A tab counts as four spaces.
    private static int Indent(string line)
    {
        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ') width++;
            else if (c == '\t') width += 4;
            else break;
        }
        return width;
    }

    // One level deeper than the item above when indented further; back to the level of the item it lines up with otherwise.
    private static int Level(List<int> indents, int indent)
    {
        while (indents.Count > 0 && indents[^1] > indent) indents.RemoveAt(indents.Count - 1);
        if (indents.Count == 0 || indents[^1] < indent) indents.Add(indent);
        return indents.Count - 1;
    }

    private static string Clean(string text) =>
        Link().Replace(Bracketed().Replace(Underlined().Replace(text, "$1"), "$1"), "$1 ($2)").Replace("**", "").Replace("`", "").Trim();

    // Closing hashes go too.
    [GeneratedRegex(@"\A#{1,6}\s+(.*?)(?:\s+#+)?\z")]
    private static partial Regex MarkdownHeading();

    // ---, *** or ___, spaced or not
    [GeneratedRegex(@"\A(?:(?:-[ \t]*){3,}|(?:\*[ \t]*){3,}|(?:_[ \t]*){3,})\z")]
    private static partial Regex Rule();

    // === or ---, under a line of text
    [GeneratedRegex(@"\A(?:={3,}|-{3,})\z")]
    private static partial Regex Underline();

    // __a phrase__, but not the underscores in names such as __init__
    [GeneratedRegex(@"(?<!\w)__(?=\S)([^_]*?\s[^_]*?)(?<=\S)__(?!\w)")]
    private static partial Regex Underlined();

    [GeneratedRegex(@"\A[-*•+]\s+")]
    private static partial Regex BulletMark();

    // 1. or 1)
    [GeneratedRegex(@"\A\d+[.)]\s")]
    private static partial Regex Numbered();

    // [words](address)
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    // <address>
    [GeneratedRegex(@"<(https?://[^>\s]+)>")]
    private static partial Regex Bracketed();
}
