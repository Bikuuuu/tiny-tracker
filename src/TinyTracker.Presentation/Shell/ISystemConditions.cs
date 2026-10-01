using TinyTracker.Core.Tracking;

namespace TinyTracker.Presentation.Shell;

// What the PC is doing. The App reads it from Windows; each read is cheap.
public interface ISystemConditions
{
    // Full screen, metered and Energy saver: what the auto-install rules look at.
    SystemState Read();

    // True while a full-screen app or presentation runs, or the PC is locked: toasts wait (spec §4.7).
    bool Busy();

    // Raised on a worker thread when the network or Energy saver changes. Full screen and locking have no event.
    event EventHandler? Changed;
}
