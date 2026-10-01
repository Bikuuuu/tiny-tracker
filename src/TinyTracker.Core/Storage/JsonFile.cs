using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinyTracker.Core.Storage;

internal enum FileState
{
    Read,
    Recovered,
    Unreadable,
}

// Writes go to a temp file that then replaces the real one. A corrupt file is kept once as .bak.
// A file that can't be read (locked, denied, a folder) is Unreadable and must not be saved over.
internal static class JsonFile
{
    private const int MoveAttempts = 5;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(50);

    public static (T Value, FileState State) Load<T>(string path, JsonTypeInfo<T> type, Func<T> defaults)
    {
        TryDelete(path + ".tmp");
        try
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize(stream, type) is { } value) return (value, FileState.Read);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (defaults(), FileState.Read);
        }
        catch (JsonException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (defaults(), FileState.Unreadable);
        }
        try
        {
            Move(path, path + ".bak");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (defaults(), FileState.Unreadable);
        }
        return (defaults(), FileState.Recovered);
    }

    // Throws IOException when the file can't be written; the old file stays.
    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, type);
                stream.Flush(flushToDisk: true);
            }
            Move(temp, path);
        }
        catch (UnauthorizedAccessException e)
        {
            TryDelete(temp);
            throw new IOException(e.Message, e) { HResult = e.HResult };
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Antivirus scanners can hold a file that was just written, so renames retry briefly.
    private static void Move(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < MoveAttempts
                && e is UnauthorizedAccessException or (IOException and not FileNotFoundException and not DirectoryNotFoundException))
            {
                Thread.Sleep(MoveRetryDelay * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
