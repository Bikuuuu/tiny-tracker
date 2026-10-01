using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using static TinyTracker.WinGet.Interop.NativeMethods;

namespace TinyTracker.WinGet.Elevation;

// Starts the helper (spec §6.6) by silent mode's task or a UAC prompt, then checks its pipe; the demo's fakes winget, unelevated
// in its silent mode. taskMissing: no task to start; prompting: a prompt opens or closes; limit: for its upgrades.
public sealed class HelperLauncher(string helperPath, Func<bool> silentMode, Func<nint> owner, bool demo, Action? taskMissing = null, Action<bool>? prompting = null,
    SpeedLimit? limit = null) : IElevation
{
    // How long an open prompt may wait for its answer, and the started helper for the app.
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private const int Declined = 1223;
    private const int Unanswered = -1;

    // Silent mode's task starts the helper with no prompt.
    public bool Prompts => !silentMode();

    public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct) => StartAsync(mayPrompt, _ => { }, ct);

    // A cancelled start starts nothing, least of all a prompt.
    public async Task<HelperStart> StartAsync(bool mayPrompt, Action<bool> prompting, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new HelperStart(HelperStartResult.Failed, Code: "0x800704C7");
        var silent = silentMode();
        var unelevated = demo && silent;
        var arguments = new HelperArguments(HelperRules.NewPipeName(), WindowsIdentity.GetCurrent().User!, demo);
        try
        {
            if (unelevated) StartUnelevated(arguments);
            else if (!silent || !RunTask(arguments))
            {
                if (!mayPrompt) return new HelperStart(HelperStartResult.NeedsPrompt);
                var error = await PromptAsync(arguments, prompting, ct);
                if (error == Declined) return new HelperStart(HelperStartResult.Declined);
                if (error != 0) return new HelperStart(HelperStartResult.Failed, Code: error == Unanswered ? "0x800705B4" : $"0x{0x80070000 | (uint)error:X8}");
            }
            return new HelperStart(HelperStartResult.Started, await HelperClient.ConnectAsync(arguments.Pipe, helperPath, !unelevated, ConnectTimeout, ct, limit));
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or Win32Exception or InvalidOperationException
            or OperationCanceledException)
        {
            return new HelperStart(HelperStartResult.Failed, Code: $"0x{e.HResult:X8}");
        }
    }

    // The task starts the helper with no prompt. One that's gone turns silent mode off, and a click prompts instead.
    private bool RunTask(HelperArguments arguments)
    {
        if (SilentTask.Run(arguments.User, arguments.Pipe) is null) return true;
        taskMissing?.Invoke();
        return false;
    }

    // ShellExecuteEx waits for the answer, so it runs on a worker. A prompt still open after 5 minutes is given up on;
    // a helper that starts later finds no app and exits. told: the caller's own ear for the prompt.
    private async Task<int> PromptAsync(HelperArguments arguments, Action<bool> told, CancellationToken ct)
    {
        var window = owner();
        prompting?.Invoke(true);
        told(true);
        var prompt = Task.Run(() => RunAs(helperPath, arguments.Format(), window), CancellationToken.None);
        try
        {
            return await prompt.WaitAsync(AnswerTimeout, ct);
        }
        catch (TimeoutException)
        {
            return Unanswered;
        }
        finally
        {
            prompting?.Invoke(false);
            told(false);
        }
    }

    private void StartUnelevated(HelperArguments arguments)
    {
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments.Format()) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The admin helper didn't start.");
    }

    // 0 once started, else the Win32 error; 1223 means the user said no.
    private static int RunAs(string path, IReadOnlyList<string> arguments, nint owner)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = FlagNoUi | NoAsync,
            Window = owner,
            Verb = "runas",
            File = path,
            Parameters = Join(arguments),
        };
        return ShellExecuteEx(ref info) ? 0 : Marshal.GetLastWin32Error();
    }

    // Each argument is a checked token without spaces or quotes, so joining them can't change what the helper gets.
    private static string Join(IReadOnlyList<string> arguments) =>
        arguments.All(a => a.Length > 0 && a.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            ? string.Join(' ', arguments)
            : throw new ArgumentException("A helper argument needs quoting.", nameof(arguments));

    private const uint FlagNoUi = 0x400;
    private const uint NoAsync = 0x100;
}
