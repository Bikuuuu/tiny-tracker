using TinyTracker.Core.History;
using TinyTracker.Core.Versions;

namespace TinyTracker.Core.Storage;

public sealed class HistoryStore(string path, TimeProvider time)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly Lock _gate = new();
    // Replaced whole under the lock and read without it, so a save waiting on rename retries never blocks a reader.
    private volatile HistoryEntry[] _entries = [];
    private volatile bool _unreadable;
    private bool _recovered;

    // Raised after Add, Clear or Retry changed the entries or Unreadable, outside the lock, on the caller's thread.
    public event EventHandler? Changed;

    // Newest first, 90 days at most as of the last change.
    public IReadOnlyList<HistoryEntry> Entries => Array.AsReadOnly(_entries);

    // True while history.json can't be read. History starts empty and the file isn't saved over.
    public bool Unreadable => _unreadable;

    // True when a corrupt file was set aside and history started empty.
    public bool Load()
    {
        lock (_gate) return Read() == FileState.Recovered;
    }

    public void Add(HistoryEntry entry) => Change(() =>
    {
        EnsureReadable();
        Save(Prune([entry, .. _entries]));
    });

    // An undone skip goes, while it's the app's newest entry and of that version.
    public void RemoveSkip(string id, string source, string version) => Change(() =>
    {
        EnsureReadable();
        var newest = _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase));
        if (newest is not { Result: HistoryResult.Skipped } || !PackageVersion.Same(newest.ToVersion, version)) return;
        Save([.. _entries.Where(e => !ReferenceEquals(e, newest))]);
    });

    // Clears what the user saw: an entry written after upTo stays.
    public void Clear(DateTimeOffset upTo) => Change(() =>
    {
        EnsureReadable();
        Save([.. _entries.Where(e => e.Time > upTo)]);
    });

    // Reads a file that couldn't be read again; the lock on it may have cleared.
    public void Retry() => Change(() =>
    {
        if (_unreadable) Reread();
    });

    // True once after a later read found the file damaged and set it aside.
    public bool TakeRecovered()
    {
        lock (_gate)
        {
            var recovered = _recovered;
            _recovered = false;
            return recovered;
        }
    }

    private void Change(Action change)
    {
        var changed = false;
        try
        {
            lock (_gate)
            {
                var (entries, unreadable) = (_entries, _unreadable);
                try
                {
                    change();
                }
                finally
                {
                    changed = !entries.SequenceEqual(_entries) || unreadable != _unreadable;
                }
            }
        }
        finally
        {
            if (changed) Raise();
        }
    }

    // A handler that throws must not fail the write that already happened.
    private void Raise()
    {
        if (Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }
    }

    private FileState Read()
    {
        var (file, state) = JsonFile.Load(path, CoreJson.Default.HistoryFile, () => new HistoryFile());
        _entries = Prune(file.Entries);
        _unreadable = state == FileState.Unreadable;
        return state;
    }

    // Load's own recovery is its return value; one found later is kept for TakeRecovered.
    private FileState Reread()
    {
        var state = Read();
        if (state == FileState.Recovered) _recovered = true;
        return state;
    }

    // Tries the file again: the lock may have cleared.
    private void EnsureReadable()
    {
        if (_unreadable && Reread() == FileState.Unreadable)
            throw new IOException($"{Path.GetFileName(path)} can't be read, so it isn't saved over.");
    }

    private HistoryEntry[] Prune(IEnumerable<HistoryEntry> entries)
    {
        var cutoff = time.GetUtcNow() - Retention;
        return [.. entries.Where(e => e.Time >= cutoff).OrderByDescending(e => e.Time)];
    }

    // The store changes only once the file is saved. The same entries aren't written again.
    private void Save(HistoryEntry[] entries)
    {
        if (entries.SequenceEqual(_entries)) return;
        JsonFile.Save(path, new HistoryFile { Entries = entries }, CoreJson.Default.HistoryFile);
        _entries = entries;
    }
}
