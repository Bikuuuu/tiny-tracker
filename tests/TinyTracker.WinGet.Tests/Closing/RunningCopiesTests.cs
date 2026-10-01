using System.Diagnostics;
using System.Reflection;
using TinyTracker.WinGet.Closing;
using Xunit;

namespace TinyTracker.WinGet.Tests.Closing;

// The uninstaller closing Tiny Tracker (spec §10), on dummy apps started from a temp folder: one stands in for the app, one
// copy in another folder for its helper.
public sealed class RunningCopiesTests : IDisposable
{
    private const string WindowClass = "TinyTrackerDummy";
    private static readonly string DummyFolder = typeof(RunningCopiesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DummyAppFolder").Value!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public RunningCopiesTests()
    {
        App = Copy("app");
        Helper = Copy("helper");
    }

    private string App { get; }
    private string Helper { get; }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
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

    private string Copy(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(DummyFolder, "TinyTracker.DummyApp.*")) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return Path.Combine(folder, "TinyTracker.DummyApp.exe");
    }

    private Process Start(string exe, params string[] arguments)
    {
        var process = Process.Start(exe, arguments);
        _started.Add(process);
        return process;
    }

    // Even one that has just started, before its window is up.
    [Fact]
    public void Copies_CloseWhenAsked_AsQuitWould()
    {
        var one = Start(App);
        var two = Start(App);
        RunningCopies.Close(App, Helper, WindowClass, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.True(one.WaitForExit(5000) && two.WaitForExit(5000));
        Assert.Equal((0, 0), (one.ExitCode, two.ExitCode));
    }

    [Fact]
    public void CopyThatWontClose_IsEndedAfterItsGrace()
    {
        var stubborn = Start(App, "--stubborn");
        var watch = Stopwatch.StartNew();
        RunningCopies.Close(App, Helper, WindowClass, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        Assert.True(stubborn.WaitForExit(5000));
        Assert.Equal(1, stubborn.ExitCode);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 10);
    }

    // A helper ends once its app hangs up; one that doesn't is ended after its own grace.
    [Fact]
    public void HelperThatStays_IsEndedAfterItsGrace()
    {
        var helper = Start(Helper, "--no-window");
        var watch = Stopwatch.StartNew();
        RunningCopies.Close(App, Helper, WindowClass, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.True(helper.WaitForExit(5000));
        Assert.Equal(1, helper.ExitCode);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1, 10);
    }

    [Fact]
    public void TheSameProgramFromAnotherFolder_IsLeftAlone()
    {
        var bystander = Start(Copy("other"));
        RunningCopies.Close(App, Helper, WindowClass, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.False(bystander.WaitForExit(1000));
    }
}
