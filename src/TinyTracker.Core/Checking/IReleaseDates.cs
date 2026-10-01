namespace TinyTracker.Core.Checking;

// A package version's release date from its public manifest, or null when it isn't known.
public interface IReleaseDates
{
    Task<DateOnly?> GetAsync(string id, string version, CancellationToken ct);
}
