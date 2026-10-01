namespace TinyTracker.WinGet.Tests;

// Reports on the calling thread, unlike Progress<T>.
internal sealed class Reported<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
