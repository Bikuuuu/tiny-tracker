using TinyTracker.Core.Logging;

namespace TinyTracker.Presentation;

// Runs saves off the UI thread, one at a time and in order, because a save can wait on rename retries.
// Each result comes back on the UI thread.
public abstract class OrderedWriter(FileLog log, Action<Action> post)
{
    private readonly Lock _gate = new();
    private Task _last = Task.CompletedTask;

    // Completes once every write so far is saved or refused.
    public Task Idle
    {
        get { lock (_gate) return _last; }
    }

    // done(null): saved. done(error): the file couldn't be read or saved, and nothing changed.
    protected void Run(string what, Action save, Action<Exception?>? done)
    {
        lock (_gate) _last = _last.ContinueWith(_ => Save(what, save, done), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Save(string what, Action save, Action<Exception?>? done)
    {
        Exception? error = null;
        try
        {
            save();
        }
        catch (IOException e)
        {
            error = e;
            log.Warn($"{what} not saved: {e.Message}");
        }
        catch (Exception e)
        {
            error = e;
            log.Error($"{what} change failed", e);
        }
        if (done is not null) post(() => done(error));
    }
}
