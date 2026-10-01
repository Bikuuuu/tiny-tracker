namespace TinyTracker.Presentation.Demo;

// A fresh data folder per demo run, deleted on Quit. Folders left by a demo that crashed go at the next start.
public static class DemoFolder
{
    private const string Prefix = "tinytracker-demo-";
    private const int Attempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    // Only names the folder; nothing is created until something is saved.
    public static string Create(string parent)
    {
        foreach (var old in Directory.EnumerateDirectories(parent, Prefix + "*")) Delete(old);
        return Path.Combine(parent, Prefix + Guid.NewGuid().ToString("N"));
    }

    public static bool Delete(string folder) => Delete(folder, Thread.Sleep);

    // A worker thread may still hold a file for a moment, so deleting retries briefly. False when the folder stays.
    public static bool Delete(string folder, Action<TimeSpan> sleep)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts) return false;
                sleep(RetryDelay);
            }
        }
    }
}
