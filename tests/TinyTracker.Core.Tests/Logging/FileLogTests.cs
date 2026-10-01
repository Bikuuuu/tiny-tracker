using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Logging;
using Xunit;

namespace TinyTracker.Core.Tests.Logging;

public sealed class FileLogTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));

    private string LogPath => _folder.PathOf(Path.Combine("logs", "app.log"));

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Info_WritesAUtcStampedLineAndCreatesTheFolder()
    {
        new FileLog(LogPath, _time).Info("Check started");
        Assert.Equal("2026-09-25T08:00:00.0000000Z INFO Check started" + Environment.NewLine, File.ReadAllText(LogPath));
    }

    [Fact]
    public void Error_IncludesTheException()
    {
        new FileLog(LogPath, _time).Error("Check failed", new InvalidOperationException("COM server not responding"));
        Assert.Contains("ERROR Check failed: System.InvalidOperationException: COM server not responding", File.ReadAllText(LogPath));
    }

    [Fact]
    public void FullLog_RotatesBeforeGoingOverTheCap()
    {
        var log = new FileLog(LogPath, _time, maxBytes: 200);
        for (var i = 0; i < 6; i++) log.Info($"line {i} " + new string('x', 40));

        Assert.True(new FileInfo(LogPath).Length <= 200);
        Assert.True(new FileInfo(Path.ChangeExtension(LogPath, ".1.log")).Length <= 200);
        Assert.Contains("line 5", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Rotation_KeepsOnlyOneOlderFile()
    {
        var log = new FileLog(LogPath, _time, maxBytes: 200);
        for (var i = 0; i < 50; i++) log.Info($"line {i} " + new string('x', 40));

        Assert.Equal(["app.1.log", "app.log"], Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void UnwritableFolder_DoesNotThrow()
    {
        var blocker = _folder.PathOf("blocker");
        File.WriteAllText(blocker, "");
        new FileLog(Path.Combine(blocker, "logs", "app.log"), _time).Info("lost");
    }

    [Fact]
    public void Close_DropsLaterLines()
    {
        var log = new FileLog(LogPath, _time);
        log.Info("before");
        log.Close();
        Directory.Delete(_folder.PathOf("logs"), recursive: true);
        log.Warn("after");
        Assert.False(Directory.Exists(_folder.PathOf("logs")));
    }

    [Fact]
    public async Task ParallelWrites_KeepEveryLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new FileLog(LogPath, _time);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => log.Info($"line {i}"), ct)));
        Assert.Equal(50, File.ReadAllLines(LogPath).Length);
    }
}
