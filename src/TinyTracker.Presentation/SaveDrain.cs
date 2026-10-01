using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;

namespace TinyTracker.Presentation;

// Changes made just before Quit still land. Settings saves go first, because their callbacks,
// on the next UI turn, can add History lines (a skip does).
public sealed class SaveDrain(SettingsWriter settings, HistoryWriter history, Action<Action> post)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    // Blocks the calling UI thread for up to Timeout per turn; done runs on the later turn.
    public void Drain(Task alsoWaitFor, Action done)
    {
        settings.Idle.Wait(Timeout);
        post(() =>
        {
            Task.WhenAll(history.Idle, alsoWaitFor).Wait(Timeout);
            done();
        });
    }
}
