using TinyTracker.Core.Tracking;

namespace TinyTracker.Presentation.History;

// What the History page asks of the Updates rows.
public interface IHistoryRows
{
    // Raised on the UI thread after the rows changed.
    event EventHandler? RowsChanged;

    string LocalIdOf(PackageKey package);

    bool CanRetry(PackageKey package, string version);

    // Queues the update; false when the row doesn't offer it anymore.
    bool Retry(PackageKey package, string version);
}
