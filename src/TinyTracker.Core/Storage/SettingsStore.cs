namespace TinyTracker.Core.Storage;

public sealed class SettingsStore(string path)
{
    private readonly Lock _gate = new();
    // Replaced whole under the lock and read without it, so a save waiting on rename retries never blocks a reader.
    private volatile SettingsFile _current = new();
    private volatile bool _unreadable;

    public SettingsFile Current => _current;

    // True while settings.json can't be read (locked, denied, a folder). Defaults are in use and the file isn't saved over.
    public bool Unreadable => _unreadable;

    // True when a corrupt file was set aside and defaults were loaded; the app shows a notice.
    public bool Load()
    {
        lock (_gate) return Read() == FileState.Recovered;
    }

    // Changes are applied and saved one at a time. Throws IOException when the file can't be read or saved; nothing changes then.
    // A change that returns the file it was given saves nothing.
    public SettingsFile Update(Func<SettingsFile, SettingsFile> change)
    {
        lock (_gate)
        {
            EnsureReadable();
            var changed = change(_current);
            if (ReferenceEquals(changed, _current)) return _current;
            var next = changed.Normalize();
            JsonFile.Save(path, next, CoreJson.Default.SettingsFile);
            _current = next;
            return next;
        }
    }

    private FileState Read()
    {
        var (file, state) = JsonFile.Load(path, CoreJson.Default.SettingsFile, () => new SettingsFile());
        _current = file.Normalize();
        _unreadable = state == FileState.Unreadable;
        return state;
    }

    // Tries the file again: the lock may have cleared.
    private void EnsureReadable()
    {
        if (_unreadable && Read() == FileState.Unreadable)
            throw new IOException($"{Path.GetFileName(path)} can't be read, so it isn't saved over.");
    }
}
