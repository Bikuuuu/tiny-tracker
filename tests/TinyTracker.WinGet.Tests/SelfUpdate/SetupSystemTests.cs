using System.Diagnostics;
using System.Reflection;
using TinyTracker.WinGet.Closing;
using TinyTracker.WinGet.SelfUpdate;
using Xunit;

namespace TinyTracker.WinGet.Tests.SelfUpdate;

// What the self-update asks of Windows, with a dummy app from a temp folder standing in for Tiny Tracker and for Setup.
public sealed class SetupSystemTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly string DummyFolder = typeof(SetupSystemTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DummyAppFolder").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SetupSystem _system = new();

    public SetupSystemTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.EnumerateFiles(DummyFolder, "TinyTracker.DummyApp.*")) File.Copy(file, Path.Combine(_root, Path.GetFileName(file)));
    }

    private string Exe => Path.Combine(_root, "TinyTracker.DummyApp.exe");

    public void Dispose()
    {
        foreach (var app in RunningApps.Of(Exe))
        {
            try
            {
                using var process = Process.GetProcessById(app.Id);
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
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

    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            Thread.Sleep(20);
        }
    }

    // The app may still be writing it.
    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public void ProgramThatDoesntRun_RunsNowhere()
    {
        Assert.False(_system.RunsInAnotherSession(Exe));
        Assert.False(_system.OtherCopiesRun(Exe));
    }

    // The app counts Setup as running while its file runs from the update folder, as before a slow one takes its mutex (spec §6.5).
    [Fact]
    public void ProgramRunningInAFolder_IsSeen()
    {
        Assert.False(RunningApps.AnyIn(_root));
        _system.Start(Exe, ["--no-window"]);
        Until(() => RunningApps.AnyIn(_root));
        Assert.False(RunningApps.AnyIn(Path.Combine(_root, "other")));
    }

    // Started from its own folder with its arguments, as the helper starts Setup; a copy in this session is no other account's.
    [Fact]
    public void Start_RunsItFromItsFolder_WithItsArguments_InThisSession()
    {
        var report = Path.Combine(_root, "report.txt");
        _system.Start(Exe, ["--report", report, "--no-window"]);
        string? text = null;
        Until(() => (text = TryRead(report)) is { } written && written.Contains('\n') && _system.OtherCopiesRun(Exe));
        Assert.Equal(_root, text!.Split('\n')[0]);
        Assert.False(_system.RunsInAnotherSession(Exe));
    }
}
