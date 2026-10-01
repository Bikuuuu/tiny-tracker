using TinyTracker.Core.SelfUpdate;
using Xunit;

namespace TinyTracker.Core.Tests.SelfUpdate;

// After a self-update's restart, the app reads its note (spec §6.5).
public class SelfUpdateNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly SelfVersion Old = new(0, 1, 0);
    private static readonly SelfUpdateNote Note = new() { From = "0.1.0", To = "0.2.0", Automatic = true, StartedAt = Now.AddMinutes(-5), SetupStarted = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningTheNewVersion_IsUpdated(bool setupRunning) =>
        Assert.Equal(SelfUpdateOutcome.Updated, Note.Outcome(new SelfVersion(0, 2, 0), setupRunning, Now));

    [Fact]
    public void RunningALaterOne_IsUpdated() => Assert.Equal(SelfUpdateOutcome.Updated, Note.Outcome(new SelfVersion(0, 3, 0), false, Now));

    [Fact]
    public void OldVersion_WhileSetupRuns_IsPending() => Assert.Equal(SelfUpdateOutcome.Pending, Note.Outcome(Old, true, Now));

    [Fact]
    public void OldVersion_OnceSetupEnded_Failed() => Assert.Equal(SelfUpdateOutcome.Failed, Note.Outcome(Old, false, Now));

    [Theory]
    [InlineData(59, SelfUpdateOutcome.Pending)]
    [InlineData(60, SelfUpdateOutcome.Failed)]
    [InlineData(61, SelfUpdateOutcome.Failed)]
    public void NoteAnHourOld_FailedEvenWhileSetupRuns(int minutes, SelfUpdateOutcome outcome) =>
        Assert.Equal(outcome, (Note with { StartedAt = Now.AddMinutes(-minutes) }).Outcome(Old, true, Now));

    // A note from after now means the clock went back: a Setup that still runs has just started.
    [Theory]
    [InlineData(true, SelfUpdateOutcome.Pending)]
    [InlineData(false, SelfUpdateOutcome.Failed)]
    public void NoteFromAfterNow_CountsAsJustStarted(bool setupRunning, SelfUpdateOutcome outcome) =>
        Assert.Equal(outcome, (Note with { StartedAt = Now.AddMinutes(5) }).Outcome(Old, setupRunning, Now));

    [Fact]
    public void NoteWithAnUnreadableVersion_Failed() => Assert.Equal(SelfUpdateOutcome.Failed, (Note with { To = "latest" }).Outcome(new SelfVersion(9, 9, 9), false, Now));

    // Stopped before Setup started, such as by a shutdown during the download: nothing failed.
    [Fact]
    public void SetupThatNeverStarted_OnTheOldVersion_IsNotStarted() =>
        Assert.Equal(SelfUpdateOutcome.NotStarted, (Note with { SetupStarted = false }).Outcome(Old, false, Now));

    // A Setup that runs did start, though Restart Manager closed the app before it marked its note.
    [Fact]
    public void UnmarkedNote_WhileSetupRuns_IsPending() =>
        Assert.Equal(SelfUpdateOutcome.Pending, (Note with { SetupStarted = false }).Outcome(Old, true, Now));

    // Setup may close the app before the app marked its note.
    [Fact]
    public void SetupThatNeverStarted_ButTheNewVersionRuns_IsUpdated() =>
        Assert.Equal(SelfUpdateOutcome.Updated, (Note with { SetupStarted = false }).Outcome(new SelfVersion(0, 2, 0), false, Now));
}
