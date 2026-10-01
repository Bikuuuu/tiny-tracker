using TinyTracker.Core.Logging;

namespace TinyTracker.Presentation;

// Hands events from worker threads to the UI thread. A handler never throws back into the worker,
// and nothing is delivered after Dispose, so a check that ends after Quit is dropped.
public sealed class UiInbox(Action<Action> post, FileLog log) : IDisposable
{
    private volatile bool _closed;

    public EventHandler<T> For<T>(Action<T> handle) => (_, value) => Deliver(() => handle(value));

    public void Deliver(Action action)
    {
        if (_closed) return;
        try
        {
            post(() =>
            {
                if (_closed) return;
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    log.Error("UI update failed", e);
                }
            });
        }
        catch (Exception e)
        {
            log.Error("UI update not delivered", e);
        }
    }

    public void Dispose() => _closed = true;
}
