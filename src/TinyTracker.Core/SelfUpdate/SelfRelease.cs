namespace TinyTracker.Core.SelfUpdate;

// Tiny Tracker's latest release on GitHub (spec §6.5).
public sealed record SelfRelease(SelfVersion Version, DateTimeOffset PublishedAt)
{
    public string NotesUrl => Version.ReleasePage;
}
