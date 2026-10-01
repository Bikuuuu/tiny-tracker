using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Installing;

// What the UI asks of the install queue.
public interface IInstaller
{
    void Enqueue(IEnumerable<InstallRequest> requests);

    void Cancel(PackageKey package);

    // The user's answer when Close & update finds the app still open.
    void ForceClose(PackageKey package);
}
