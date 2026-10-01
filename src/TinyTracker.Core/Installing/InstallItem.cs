using TinyTracker.Core.History;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Installing;

// One update to install: the version the app has and the version the row offered.
public sealed record InstallRequest(PackageKey Package, string Name, string FromVersion, string ToVersion)
{
    public InstallRoute Route { get; init; }
    // An Auto app's update, which never shows a prompt.
    public bool ByItself { get; init; }
    // winget's id for the app's uninstall entry or MSIX package, which leads to its install folder.
    public string LocalId { get; init; } = "";
    // Close & update: the app's processes close first, and open again after.
    public bool CloseFirst { get; init; }
    // Known to need admin, as after the user's Install: it goes through the helper, and a declined prompt keeps the Install.
    public bool NeedsAdmin { get; init; }
}

public enum InstallStage
{
    // In the queue, queued in winget, or waiting for another install to finish.
    Waiting,
    // Close & update asked the app to close.
    Closing,
    // The app is still open; the row asks before a force close.
    NotClosed,
    Downloading,
    Installing,
    Done,
}

// Phantom: winget reported success, but the installed version didn't change and the same version is still offered.
// After: the app as read again and saved, or null when that failed.
public sealed record InstallDone(UpgradeOutcome Outcome, bool Phantom = false, AppCheck? After = null)
{
    public HistoryResult HistoryResult => Outcome.Result switch
    {
        UpgradeResult.Updated or UpgradeResult.RestartNeeded => HistoryResult.Updated,
        UpgradeResult.Cancelled => HistoryResult.Cancelled,
        _ => HistoryResult.Failed,
    };

    // Why the install didn't simply succeed, as a name History keeps and the app words.
    public string? Reason => Outcome.Result switch
    {
        UpgradeResult.Updated => Phantom ? "Phantom" : null,
        UpgradeResult.Cancelled => null,
        UpgradeResult.Failed => Outcome.Failure.ToString(),
        _ => Outcome.Result.ToString(),
    };
}

// One app's place in the queue, as the rows show it.
public sealed record InstallItem(InstallRequest Request, InstallStage Stage)
{
    public UpgradeProgress Progress { get; init; }
    public double BytesPerSecond { get; init; }
    // Another install is running; the queue tries again in a moment.
    public bool Busy { get; init; }
    // The admin helper's prompt is open.
    public bool AwaitingPermission { get; init; }
    public InstallDone? Done { get; init; }
}

// The spec's timings. The demo passes shorter ones.
public sealed record InstallTimings(TimeSpan BusyRetryDelay, int BusyRetries, TimeSpan StallAfter, TimeSpan Cap)
{
    public static InstallTimings Default { get; } = new(TimeSpan.FromMinutes(2), 3, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));

    // How long Close & update waits for the app to close, then for the user's answer.
    public TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CloseAnswerWait { get; init; } = TimeSpan.FromMinutes(2);
}
