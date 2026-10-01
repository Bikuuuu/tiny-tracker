namespace TinyTracker.Core.SelfUpdate;

// What settings.json keeps about Tiny Tracker's own updates (spec §5.4, §6.5).
public sealed record SelfUpdateBook
{
    // The newest version a toast or the flyout announced.
    public string? Announced { get; set; }
    // The last attempt: the next automatic one on that version waits 12 h.
    public string? AttemptedVersion { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
    // A failure that waits for the user: no automatic attempt on that version until Update is clicked.
    public string? FailedVersion { get; set; }
    // A self-update under way: written before the helper hears of it, marked once Setup started, read after the restart.
    public SelfUpdateNote? Note { get; set; }
}

public enum SelfUpdateOutcome
{
    // Setup still runs; the app looks again once it ends.
    Pending,
    Updated,
    Failed,
    // It stopped before Setup started, such as with a shutdown during the download: nothing to report.
    NotStarted,
}

public sealed record SelfUpdateNote
{
    // Setup takes minutes, so a note older than this failed.
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public required string From { get; set; }
    public required string To { get; set; }
    public bool Automatic { get; set; }
    // When it was written, and once Setup started, when Setup started.
    public required DateTimeOffset StartedAt { get; set; }
    public bool SetupStarted { get; set; }

    // On the new version it updated; on the old one it failed, unless Setup runs (so it started, even if the note wasn't marked)
    // or never started. A note from after now means the clock went back, so it counts as just written.
    public SelfUpdateOutcome Outcome(SelfVersion running, bool setupRunning, DateTimeOffset now)
    {
        if (SelfVersion.Parse(To) is { } to && !to.IsNewerThan(running)) return SelfUpdateOutcome.Updated;
        if (!SetupStarted && !setupRunning) return SelfUpdateOutcome.NotStarted;
        return setupRunning && now - StartedAt < MaxAge ? SelfUpdateOutcome.Pending : SelfUpdateOutcome.Failed;
    }
}
