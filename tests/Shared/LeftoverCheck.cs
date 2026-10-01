using System.Collections.Concurrent;
using Xunit;

[assembly: AssemblyFixture(typeof(TinyTracker.Tests.LeftoverCheck))]

namespace TinyTracker.Tests;

// A unique folder under %TEMP%, deleted when the test ends. LeftoverCheck fails the run if one comes back.
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Directory.CreateDirectory(Root);
        Created.Enqueue((Root, TestContext.Current.Test?.TestDisplayName));
    }

    // Every folder this run made, and the test that made it.
    internal static ConcurrentQueue<(string Root, string? Test)> Created { get; } = new();

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));

    public string PathOf(string name) => Path.Combine(Root, name);

    public void Dispose() => Delete(Root);

    // A worker thread may still be writing a last log line, so deleting retries briefly.
    internal static void Delete(string root)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}

// After the last test: a test folder that exists again was written after its test ended, so the test left work running.
// xUnit reports the failure once per test, as a "Test Assembly Cleanup Failure".
public sealed class LeftoverCheck : IAsyncDisposable
{
    public static readonly TimeSpan LateWrites = TimeSpan.FromMilliseconds(500);

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(LateWrites);
        var back = TempFolder.Created.Where(c => Directory.Exists(c.Root)).ToList();
        var text = Describe(back);
        foreach (var (root, _) in back) TempFolder.Delete(root);
        if (back.Count > 0) throw new InvalidOperationException(text);
    }

    // The test that made each folder, and what came back into it: the late writer's files.
    internal static string Describe(IReadOnlyList<(string Root, string? Test)> back) =>
        $"{back.Count} test folder(s) came back after their test: " + string.Join("; ", back.Select(c =>
            $"{c.Test ?? Path.GetFileName(c.Root)} ({string.Join(", ", Directory.EnumerateFileSystemEntries(c.Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(c.Root, f)))})"));
}
