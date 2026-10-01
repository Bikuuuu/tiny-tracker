using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using TinyTracker.Core.Logging;
using TinyTracker.WinGet.Closing;
using TinyTracker.WinGet.Tests.Integration;
using Xunit;

namespace TinyTracker.WinGet.Tests.Closing;

// Windows allows a user 64 Restart Manager sessions at once. This test holds all of them, so no other test runs beside it.
[Collection<UpgradeCollection>]
public sealed class RestartManagerLimitTests : IDisposable
{
    private const string LocalId = @"ARP\User\X64\Example App";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly string DummyFolder = typeof(RestartManagerLimitTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DummyAppFolder").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;
    private readonly FileLog _log;

    public RestartManagerLimitTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_root, "Example App")).FullName;
        foreach (var file in Directory.EnumerateFiles(DummyFolder, "TinyTracker.DummyApp.*")) File.Copy(file, Path.Combine(_app, Path.GetFileName(file)));
        _log = new FileLog(Path.Combine(_root, "logs", "app.log"), TimeProvider.System);
    }

    public void Dispose()
    {
        _log.Close();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string LogText() => File.Exists(Path.Combine(_root, "logs", "app.log")) ? File.ReadAllText(Path.Combine(_root, "logs", "app.log")) : "";

    [Fact]
    public async Task SessionThatWontStart_IsLogged_AndForceCloseStillWorks()
    {
        using var dummy = Process.Start(Path.Combine(_app, "TinyTracker.DummyApp.exe"))!;
        dummy.WaitForInputIdle(10000);
        var held = new List<uint>();
        try
        {
            while (held.Count < 256 && RmStartSession(out var session, 0, new char[33]) == 0) held.Add(session);
            var closing = new AppCloser(id => id == LocalId ? _app : null, _ => [], _log).Close(LocalId);
            await Task.Delay(1000, Ct);
            Assert.False(closing.Closed.IsCompleted);
            // 353 (too many sessions) on older Windows builds, 14 (out of memory) on newer ones.
            Assert.Matches(@"WARN Restart Manager didn't start: \d+", LogText());
            Assert.True(closing.ForceClose());
            await closing.Closed.WaitAsync(Wait, Ct);
        }
        finally
        {
            foreach (var session in held) RmEndSession(session);
            if (!dummy.HasExited) dummy.Kill();
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, char[] key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
