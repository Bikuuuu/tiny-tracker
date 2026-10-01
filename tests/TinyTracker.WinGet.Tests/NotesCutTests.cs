using Xunit;

namespace TinyTracker.WinGet.Tests;

// Release notes as a check keeps them: winget allows 10,000 characters.
public class NotesCutTests
{
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    [Fact]
    public void ShortNotes_StayWhole() => Assert.Equal("- Faster start.", WinGetSession.CutNotes("- Faster start."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\t")]
    public void EmptyNotes_AreNone(string? text) => Assert.Null(WinGetSession.CutNotes(text));

    [Fact]
    public void LongNotes_StopAtTheLimit() => Assert.Equal(10_000, WinGetSession.CutNotes(new string('a', 12_000))!.Length);

    // A character in two halves stays whole or goes whole.
    [Fact]
    public void ACharacterAcrossTheLimit_Goes() => Assert.Equal(9_999, WinGetSession.CutNotes(new string('a', 9_999) + Emoji + "b")!.Length);

    [Fact]
    public void ACharacterBeforeTheLimit_Stays() => Assert.Equal(10_000, WinGetSession.CutNotes(new string('a', 9_998) + Emoji + "b")!.Length);
}
