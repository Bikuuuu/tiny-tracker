namespace TinyTracker.Core.Installing;

public enum UpgradeResult
{
    Updated,
    RestartNeeded,
    // Offer Close & update.
    AppInUse,
    // In use, and Tiny Tracker can't close it: the user closes it and tries again.
    CouldNotClose,
    NeedsAdmin,
    PermissionDeclined,
    // Another install is running; try again later.
    Busy,
    // winget doesn't offer this version anymore.
    NoUpdate,
    NotInstalled,
    Cancelled,
    Failed,
}

// Why an upgrade failed. The app words each reason in plain language.
public enum UpgradeFailure
{
    None,
    DownloadFailed,
    HashMismatch,
    NoNetwork,
    DiskFull,
    NotEnoughMemory,
    BlockedByPolicy,
    NoApplicableInstaller,
    NotSupported,
    MissingDependency,
    InstallerCancelled,
    NewerInstalled,
    InstallerFailed,
    WinGetUnavailable,
    // No download progress for two minutes, twice.
    Stalled,
    // Waiting or installing for 30 minutes; downloading doesn't count.
    TookTooLong,
    // The admin helper quit or its pipe broke.
    HelperStopped,
    // The admin helper didn't start or didn't answer.
    HelperNotStarted,
    // winget refused the speed limit's proxy: its proxy option is off.
    ProxyRefused,
    // Tiny Tracker's own update (spec §6.5): its copy for another account holds its files, GitHub didn't answer, the release
    // failed a check, or the download didn't match GitHub's digest.
    OtherAccounts,
    GitHubUnreachable,
    ReleaseRefused,
    DigestMismatch,
    Other,
}

// Code is the technical code behind Details, such as "0x8A150006, installer 1603".
// A NoUpdate's says what's installed, for the log only.
public sealed record UpgradeOutcome(UpgradeResult Result, UpgradeFailure Failure = UpgradeFailure.None, string? Code = null)
{
    // A problem that can pass by itself, so a later try may work. A refused proxy turned the speed limit off.
    public bool IsTemporary() => Result == UpgradeResult.Busy
        || Result == UpgradeResult.Failed && Failure is UpgradeFailure.NoNetwork or UpgradeFailure.DownloadFailed or UpgradeFailure.WinGetUnavailable
            or UpgradeFailure.Stalled or UpgradeFailure.TookTooLong or UpgradeFailure.HelperStopped or UpgradeFailure.HelperNotStarted
            or UpgradeFailure.ProxyRefused or UpgradeFailure.GitHubUnreachable;
}
