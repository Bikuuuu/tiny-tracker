using System.ComponentModel;

namespace TinyTracker.WinGet.Cli;

// winget's command line (spec §6.4, §8). The winget.exe in the user's folder is an alias that any program the user runs could
// replace, so what it starts runs only once trusted says it's the real one; anything else is ended before it runs a step.
public sealed class WinGetCli(string path, Func<ProgramIdentity, bool> trusted)
{
    public const string AppInstallerFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    // A quick command that takes longer has hung.
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(1);
    // The rest of a command's output comes in right after it exits.
    private static readonly TimeSpan OutputAfterExit = TimeSpan.FromSeconds(2);

    // App Installer's alias, which is there even when the user turned off the plain winget.exe alias.
    public static string AliasPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps",
        AppInstallerFamily, "winget.exe");

    public static WinGetCli Real { get; } = new(AliasPath, IsAppInstallers);

    // App Installer's own winget.exe: in its package's folder, directly inside Program Files\WindowsApps, which only Windows
    // writes, and running as that package.
    public static bool IsAppInstallers(ProgramIdentity program)
    {
        if (program is not { Path: { } exe, PackageFamily: { } family, PackageFolder: { } folder }) return false;
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        return string.Equals(family, AppInstallerFamily, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(exe), "winget.exe", StringComparison.OrdinalIgnoreCase)
            && Same(Path.GetDirectoryName(exe), folder)
            && Same(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)), windowsApps);
    }

    // Running, or null when there's no such program or it isn't the one trusted; then nothing of it ran. paused hears its process
    // id before it runs.
    public WinGetProcess? Start(IReadOnlyList<string> arguments, Action<int>? paused = null)
    {
        WinGetProcess process;
        try
        {
            process = WinGetProcess.StartPaused(path, arguments);
        }
        catch (Win32Exception)
        {
            return null;
        }
        if (!trusted(process.Identity))
        {
            process.End();
            process.Dispose();
            return null;
        }
        paused?.Invoke(process.Id);
        process.Resume();
        return process;
    }

    // A command that answers quickly, to its end: its exit code and output. Null when it didn't start, as above, or hung.
    public async Task<(int ExitCode, IReadOnlyList<string> Lines)?> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = Start(arguments);
        if (process is null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RunTimeout);
        int code;
        try
        {
            code = await process.Exited.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.End();
            if (ct.IsCancellationRequested) throw;
            return null;
        }
        await Task.WhenAny(process.Lines.Completion, Task.Delay(OutputAfterExit, CancellationToken.None));
        var lines = new List<string>();
        while (process.Lines.TryRead(out var line)) lines.Add(line);
        return (code, lines);
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
