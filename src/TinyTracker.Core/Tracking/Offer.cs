namespace TinyTracker.Core.Tracking;

// Bookkeeping for the version winget currently offers.
public sealed record Offer
{
    public required string Version { get; set; }
    public required DateTimeOffset FirstSeen { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public DateTimeOffset? LastAutoAttempt { get; set; }
    // A toast named this version, or the flyout showed it.
    public bool Announced { get; set; }
    public bool Phantom { get; set; }
}
