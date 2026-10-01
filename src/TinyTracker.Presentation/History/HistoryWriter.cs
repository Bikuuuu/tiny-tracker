using TinyTracker.Core.History;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;

namespace TinyTracker.Presentation.History;

// History writes the UI asks for. The install queue writes from its own thread.
public sealed class HistoryWriter(HistoryStore store, FileLog log, Action<Action> post) : OrderedWriter(log, post)
{
    public void Add(HistoryEntry entry) => Run($"History of {entry.Id}", () => store.Add(entry), null);

    public void RemoveSkip(string id, string source, string version) => Run($"History of {id}", () => store.RemoveSkip(id, source, version), null);

    public void Clear(DateTimeOffset upTo, Action<Exception?> done) => Run("History", () => store.Clear(upTo), done);

    public void Retry() => Run("History", store.Retry, null);
}
