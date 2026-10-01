using TinyTracker.Presentation.Updates;
using Xunit;

namespace TinyTracker.Presentation.Tests.Updates;

// Release notes as the What's new page shows them (spec §4.9), from text shaped like winget's.
public class NotesTextTests
{
    private static NoteLine Heading(string text) => new(NoteKind.Heading, text);

    private static NoteLine Bullet(string text, int level = 0) => new(NoteKind.Bullet, text, level);

    private static NoteLine Text(string text, int level = 0) => new(NoteKind.Text, text, level);

    private static NoteLine Wrapped(string text, int level = 0) => new(NoteKind.Wrapped, text, level);

    private static readonly NoteLine Gap = new(NoteKind.Gap);

    [Fact]
    public void LinesBeforeBullets_HeadThem() =>
        Assert.Equal([Heading("New Features"), Bullet("Tabs can be pinned"), Bullet("Faster search"), Gap, Heading("Bug Fixes"), Bullet("Fixed a crash")],
            NotesText.Lines("New Features\n- Tabs can be pinned\n- Faster search\n\nBug Fixes\n- Fixed a crash"));

    [Fact]
    public void Indents_AreLevels() =>
        Assert.Equal([Bullet("One"), Bullet("Two", 1), Bullet("Three", 2), Bullet("Tabbed", 2), Bullet("Deep", 3)],
            NotesText.Lines("- One\n  - Two\n    - Three\n\t- Tabbed\n              - Deep"));

    // A bullet indented further than the one above is a level deeper, however many spaces it uses.
    [Fact]
    public void Nesting_FollowsTheIndentAbove()
    {
        Assert.Equal([Bullet("One"), Bullet("Two", 1), Bullet("Three", 2), Bullet("Back", 1)], NotesText.Lines("- One\n    - Two\n        - Three\n    - Back"));
        Assert.Equal([Heading("Fixes"), Bullet("One"), Bullet("Two", 1)], NotesText.Lines("Fixes\n    - One\n        - Two"));
    }

    [Fact]
    public void OtherBulletMarks_AreBullets() => Assert.Equal([Bullet("Dot"), Bullet("Star"), Bullet("Plus")], NotesText.Lines("• Dot\n* Star\n+ Plus"));

    [Fact]
    public void MarkdownMarks_GoAway()
    {
        Assert.Equal([Heading("What's Changed"), Bullet("Bold fix by code in PR 12 (https://example.com/12)")],
            NotesText.Lines("## What's Changed\n* **Bold** fix by `code` in [PR 12](https://example.com/12)"));
        Assert.Equal([Text("See https://example.com/notes")], NotesText.Lines("See <https://example.com/notes>"));
        Assert.Equal([Heading("Fixes"), Heading("C# notes")], NotesText.Lines("## Fixes ##\n## C# notes"));
        Assert.Equal([Heading("Breaking changes"), Bullet("One")], NotesText.Lines("__Breaking changes__\n- One"));
    }

    // Underscores in names aren't marks, nor are they around one word.
    [Fact]
    public void Names_KeepTheirUnderscores()
    {
        Assert.Equal([Bullet("Fixed __init__ and __init__.py in MY__NAME")], NotesText.Lines("- Fixed `__init__` and __init__.py in MY__NAME"));
        Assert.Equal([Bullet("__Fixed__ the importer")], NotesText.Lines("- __Fixed__ the importer"));
    }

    // A rule on its own is a gap, like a blank line.
    [Fact]
    public void Rules_AreGaps() =>
        Assert.Equal([Text("A."), Gap, Text("B."), Gap, Text("C."), Gap, Text("D."), Gap, Text("E.")],
            NotesText.Lines("---\nA.\n\n---\nB.\n***\n\n___\nC.\n- - -\nD.\n- ---\nE."));

    // A line underlined with === or --- is a heading, as Markdown has it.
    [Fact]
    public void UnderlinedLines_AreHeadings()
    {
        Assert.Equal([Heading("Release 2.0"), Bullet("One")], NotesText.Lines("Release 2.0\n===\n- One"));
        Assert.Equal([Heading("Fixes"), Text("Faster start.")], NotesText.Lines("Fixes\n---\nFaster start."));
    }

    // A wrapped line under a bullet continues it: the list goes on below it, and it heads nothing.
    [Theory]
    [InlineData("wrapped text.")]
    [InlineData("wrapped text")]
    public void WrappedLines_ContinueTheirBullet(string wrapped) =>
        Assert.Equal([Bullet("One"), Wrapped(wrapped), Bullet("Two", 1)], NotesText.Lines($"- One\n  {wrapped}\n  - Two"));

    // A wrapped line takes the level of the item it continues, so the page lines it up with that item's text.
    [Fact]
    public void WrappedLines_TakeTheirItemsLevel()
    {
        Assert.Equal([Bullet("One"), Bullet("Two", 1), Wrapped("wrapped", 1), Wrapped("more", 1), Bullet("Three")],
            NotesText.Lines("- One\n  - Two\n    wrapped\n    more\n- Three"));
        Assert.Equal([Text("1. First"), Wrapped("more")], NotesText.Lines("1. First\n   more"));
    }

    // Numbered steps under a bullet nest like bullets.
    [Fact]
    public void NumberedLines_NestLikeBullets() =>
        Assert.Equal([Bullet("Item"), Text("1. Step", 1), Text("2. Next", 1), Bullet("Back")], NotesText.Lines("- Item\n    1. Step\n    2. Next\n- Back"));

    // A sentence or a line that no bullet follows stays as it is.
    [Fact]
    public void Sentences_AreText()
    {
        Assert.Equal([Text("These are new in 2.5:"), Bullet("One")], NotesText.Lines("These are new in 2.5:\n- One"));
        Assert.Equal([Text("Important"), Text("The toolkit was updated.")], NotesText.Lines("Important\nThe toolkit was updated."));
    }

    // A blank line doesn't part a heading from its bullets; a numbered line is a step, not a heading.
    [Fact]
    public void Headings_SkipBlankLines_ButNotNumbers()
    {
        Assert.Equal([Heading("Heading"), Gap, Bullet("bullet")], NotesText.Lines("Heading\n\n- bullet"));
        Assert.Equal([Text("1. First"), Bullet("sub")], NotesText.Lines("1. First\n- sub"));
        Assert.Equal([Text("2) Second"), Bullet("sub", 1)], NotesText.Lines("2) Second\n  - sub"));
    }

    [Fact]
    public void BlankLines_AreOneGap_OnlyBetweenLines() =>
        Assert.Equal([Text("A."), Gap, Text("B.")], NotesText.Lines("\n\nA.\n\n\n\nB.\n  \n"));

    [Fact]
    public void WindowsLineEnds_Work() => Assert.Equal([Heading("Fixes"), Bullet("One")], NotesText.Lines("Fixes\r\n- One\r\n"));

    [Theory]
    [InlineData("")]
    [InlineData("  \n\t\n")]
    public void Nothing_IsNoLines(string text) => Assert.Empty(NotesText.Lines(text));
}
