using TinyTracker.Core.SelfUpdate;

namespace TinyTracker.Core.Installing;

// Starts the admin helper (spec §6.3, §6.6): through silent mode's task when it's on, else with a UAC prompt.
public interface IElevation
{
    // A start shows a UAC prompt first; not when silent mode's task starts the helper.
    bool Prompts => true;

    // mayPrompt is false for an update that started by itself, which never shows a prompt. Never throws.
    Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct);

    // The same, and prompting hears the UAC prompt open (true) and close (false). A start that never prompts needn't tell.
    Task<HelperStart> StartAsync(bool mayPrompt, Action<bool> prompting, CancellationToken ct) => StartAsync(mayPrompt, ct);
}

public enum HelperStartResult
{
    Started,
    // The user said no to the prompt.
    Declined,
    // Silent mode's task couldn't start it, and a prompt wasn't allowed.
    NeedsPrompt,
    // It didn't start or didn't answer; Code says why.
    Failed,
}

public sealed record HelperStart(HelperStartResult Result, IHelperSession? Session = null, string? Code = null);

// A running helper. Disposing it hangs up: upgrades still running through it end as HelperStopped, and the helper exits.
public interface IHelperSession : IPackageUpgrader, IDisposable
{
    // False when winget doesn't answer the elevated helper; the app then runs those updates itself.
    bool WinGetAvailable { get; }

    // Turns winget's proxy option on for the user the helper runs as (spec §6.4). Null once it reads on, else why not.
    Task<string?> EnableProxyOptionAsync(CancellationToken ct);

    // It's held for admin updates that wait their turn behind others, so it isn't idle (spec §5.1).
    void Stay()
    {
    }

    // Tiny Tracker's own update (spec §6.5): Updated once Setup started, and the helper exits. Never throws.
    Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
        Task.FromResult(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "not supported"));
}
