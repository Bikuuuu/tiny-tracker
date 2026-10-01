using TinyTracker.Core;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Updates;

// The row states of spec §4.3, plus the up to date and skipped rows of the Up to date group.
public enum RowState
{
    Available,
    Waiting,
    Closing,
    NotClosed,
    Downloading,
    Installing,
    AppInUse,
    CouldNotClose,
    NeedsPermission,
    Failed,
    RestartNeeded,
    Updated,
    Phantom,
    VersionUnknown,
    NotFound,
    NotInCatalog,
    UpToDate,
    Skipped,
}

public enum RowAction
{
    None,
    Update,
    Retry,
    UpdateAnyway,
    StopTracking,
    // The app is in use: close it, update it, open it again.
    CloseAndUpdate,
    // Through the admin helper, one prompt for the batch.
    Install,
    // Close & update found the app still open.
    ForceClose,
}

// What one row shows, from the last check and the app's install. Pure, so every state is tested without the UI.
public sealed record RowView : IStatusLine
{
    public required RowState State { get; init; }
    public required string Status { get; init; }
    public bool Warning { get; init; }
    public RowAction Action { get; init; }
    public bool CanCancel { get; init; }
    public bool ShowProgress { get; init; }
    public bool Indeterminate { get; init; }
    // 0 to 100.
    public double Percent { get; init; }
    public string? PercentText { get; init; }
    // "What's new" after the status line.
    public bool ShowNotes { get; init; }
    public string? Details { get; init; }
    // "2.4.1 → 2.5.0", with the changed part of the new version in the accent color.
    public string From { get; init; } = "";
    public VersionDiff To { get; init; }
    public bool ShowVersions { get; init; }
    public bool CanUpdateNow { get; init; }
    public bool CanSkip { get; init; }
    public bool HasNotes { get; init; }
    // The Security pill (spec §4.3).
    public bool Security { get; init; }

    public bool HasAction => Action != RowAction.None;
    // The What's new page's button, which waits while the row is busy.
    public bool OffersAction => HasAction && !IsActive;

    public string ActionText => Action switch
    {
        RowAction.Update => Strings.Update,
        RowAction.Retry => Strings.Retry,
        RowAction.UpdateAnyway => Strings.UpdateAnyway,
        RowAction.StopTracking => Strings.StopTracking,
        RowAction.CloseAndUpdate => Strings.CloseAndUpdate,
        RowAction.Install => Strings.Install,
        RowAction.ForceClose => Strings.ForceClose,
        _ => "",
    };

    public bool HasDetails => Details is not null;
    public bool CanUndoSkip => State == RowState.Skipped;
    public bool InUpToDateGroup => State is RowState.UpToDate or RowState.Skipped or RowState.VersionUnknown;
    public bool IsActive => State is RowState.Waiting or RowState.Closing or RowState.NotClosed or RowState.Downloading or RowState.Installing;
    // Update all counts these (spec §4.3).
    public bool CountsForUpdateAll => State is RowState.Available or RowState.NeedsPermission or RowState.Failed;
    // The summary counts updates that aren't installed yet.
    public bool IsPending => State is RowState.Available or RowState.Waiting or RowState.Closing or RowState.NotClosed or RowState.Downloading
        or RowState.Installing or RowState.AppInUse or RowState.CouldNotClose or RowState.NeedsPermission or RowState.Failed;

    // In progress first, then security fixes, then rows that need attention, then available ones.
    public int Rank => State switch
    {
        RowState.Closing or RowState.NotClosed or RowState.Downloading or RowState.Installing or RowState.Updated => 0,
        RowState.Waiting => 1,
        _ when Security => 2,
        RowState.Available => 4,
        _ => 3,
    };

    // auto: why an Auto app hasn't installed by itself; daysLeft goes with TooNew, and windowStart, the window's first hour as
    // shown, with OutsideWindow. whatsNew and securityFirst are the Settings switches.
    public static RowView Of(AppCheck check, InstallItem? install, DateTimeOffset now, AutoBlock auto = AutoBlock.AutoOff, int daysLeft = 0, string? windowStart = null,
        bool whatsNew = true, bool securityFirst = true)
    {
        var view = install switch
        {
            { Stage: InstallStage.Waiting } => new RowView { State = RowState.Waiting, Status = Waiting(install), CanCancel = true },
            { Stage: InstallStage.Closing } => new RowView { State = RowState.Closing, Status = Words.Format(Strings.ClosingApp, NameOf(check.App)) },
            { Stage: InstallStage.NotClosed } => new RowView
            {
                State = RowState.NotClosed,
                Status = Words.Format(Strings.AppDidntClose, NameOf(check.App)),
                Warning = true,
                Action = RowAction.ForceClose,
                CanCancel = true,
            },
            { Stage: InstallStage.Downloading } => Downloading(install),
            { Stage: InstallStage.Installing } => Installing(install),
            { Done: { } done } => Finished(check, install, done) ?? Declined(check, install, done, now, auto, daysLeft, windowStart),
            _ => FromCheck(check, now, false, auto, daysLeft, windowStart),
        };
        return WithVersions(view, check, install, whatsNew, securityFirst);
    }

    private static string Waiting(InstallItem install) =>
        install.AwaitingPermission ? Strings.WaitingForPermission : install.Busy ? Strings.WaitingForOtherInstall : Strings.Waiting;

    // A declined prompt puts the row back as it was: an update known to need admin keeps its Install.
    private static RowView Declined(AppCheck check, InstallItem install, InstallDone done, DateTimeOffset now, AutoBlock auto, int daysLeft, string? windowStart)
    {
        var declined = done.Outcome.Result == UpgradeResult.PermissionDeclined;
        return FromCheck(check, now, declined, declined && install.Request.NeedsAdmin ? AutoBlock.NeedsAdmin : auto, daysLeft, windowStart);
    }

    private static RowView Downloading(InstallItem install)
    {
        var progress = install.Progress;
        var known = progress.BytesRequired > 0 || progress.DownloadFraction > 0;
        var fraction = progress.BytesRequired > 0 ? Math.Clamp((double)progress.BytesDownloaded / progress.BytesRequired, 0, 1) : progress.DownloadFraction;
        var amount = Words.Downloaded(progress.BytesDownloaded, progress.BytesRequired);
        if (install.BytesPerSecond > 0) amount = Words.Joined(amount, Words.Speed(install.BytesPerSecond));
        return new RowView
        {
            State = RowState.Downloading,
            Status = amount,
            CanCancel = progress.CanCancel,
            ShowProgress = true,
            Indeterminate = !known,
            Percent = fraction * 100,
            PercentText = known ? $"{(int)(fraction * 100)}%" : null,
        };
    }

    private static RowView Installing(InstallItem install)
    {
        var fraction = install.Progress.InstallFraction;
        return new RowView { State = RowState.Installing, Status = Strings.Installing, ShowProgress = true, Indeterminate = fraction <= 0, Percent = fraction * 100 };
    }

    // Null when the row goes back to what the check says.
    private static RowView? Finished(AppCheck check, InstallItem install, InstallDone done)
    {
        var details = done.Outcome.Code is { } code ? Words.Format(Strings.DetailsCode, code) : null;
        return done.Outcome.Result switch
        {
            UpgradeResult.Updated when done.Phantom => Phantom(check.App),
            UpgradeResult.Updated => new RowView { State = RowState.Updated, Status = Words.Format(Strings.UpdatedTo, install.Request.ToVersion) },
            UpgradeResult.RestartNeeded => new RowView { State = RowState.RestartNeeded, Status = Strings.RestartToFinish },
            UpgradeResult.AppInUse => new RowView
            {
                State = RowState.AppInUse,
                Status = Words.Format(Strings.AppRunning, NameOf(check.App)),
                Warning = true,
                Action = RowAction.CloseAndUpdate,
                Details = details,
                CanUpdateNow = true,
            },
            UpgradeResult.CouldNotClose => new RowView
            {
                State = RowState.CouldNotClose,
                Status = Words.Format(Strings.CloseAppFirst, NameOf(check.App)),
                Warning = true,
                Action = RowAction.Retry,
                Details = details,
                CanUpdateNow = true,
            },
            UpgradeResult.NeedsAdmin => new RowView
            {
                State = RowState.NeedsPermission,
                Status = Strings.NeedsAdmin,
                Warning = true,
                Action = RowAction.Install,
                Details = details,
                CanUpdateNow = true,
            },
            UpgradeResult.NotInstalled => new RowView { State = RowState.NotFound, Status = Strings.NotInstalledAnymore, Action = RowAction.StopTracking },
            UpgradeResult.Cancelled or UpgradeResult.PermissionDeclined => null,
            _ => new RowView { State = RowState.Failed, Status = Words.Reason(done.Reason), Warning = true, Action = RowAction.Retry, Details = details, CanUpdateNow = true },
        };
    }

    private static RowView FromCheck(AppCheck check, DateTimeOffset now, bool declined, AutoBlock auto, int daysLeft, string? windowStart) => check.Status switch
    {
        // Default mode: an Auto app's update that needs admin waits for the user's Install (spec §6.3).
        AppStatus.Available when auto == AutoBlock.NeedsAdmin => new RowView
        {
            State = RowState.NeedsPermission,
            Status = declined ? Strings.PermissionDeclined : Strings.NeedsAdmin,
            ShowNotes = !declined && HasNotesOf(check.Package),
            Action = RowAction.Install,
            CanUpdateNow = true,
        },
        AppStatus.Available => new RowView
        {
            State = RowState.Available,
            Status = declined ? Strings.PermissionDeclined : AutoWait(auto, daysLeft, windowStart) ?? Released(check.App.Offer!, now),
            ShowNotes = !declined && HasNotesOf(check.Package),
            Action = RowAction.Update,
            CanUpdateNow = true,
        },
        AppStatus.Phantom => Phantom(check.App),
        AppStatus.Skipped => new RowView { State = RowState.Skipped, Status = Words.Format(Strings.SkippedVersion, check.App.SkippedVersion), CanUpdateNow = true },
        AppStatus.VersionUnknown => new RowView { State = RowState.VersionUnknown, Status = Strings.VersionUnknown },
        AppStatus.NotFound => new RowView { State = RowState.NotFound, Status = Strings.NotInstalledAnymore, Action = RowAction.StopTracking },
        AppStatus.NotInCatalog => new RowView { State = RowState.NotInCatalog, Status = Strings.NotFoundInWinGet, Action = RowAction.StopTracking },
        _ => new RowView { State = RowState.UpToDate, Status = check.Package?.InstalledVersion ?? "" },
    };

    // App Installer is winget itself: Windows finishes its update once Tiny Tracker's winget has closed (spec §6.2).
    private static RowView Phantom(TrackedApp app) => app.Matches(AppInstaller, TrackedApp.WinGet)
        ? new() { State = RowState.Phantom, Status = Words.Format(Strings.PhantomAppInstaller, AppInfo.Name) }
        : new() { State = RowState.Phantom, Status = Strings.PhantomStatus, Action = RowAction.UpdateAnyway };

    private const string AppInstaller = "Microsoft.AppInstaller";

    // Its text for the What's new page, or its link.
    private static bool HasNotesOf(PackageSnapshot? package) => package is { ReleaseNotes: not null } or { ReleaseNotesUrl: not null };

    // A full-screen app holds an Auto app back without a word: the user is in it.
    internal static string? AutoWait(AutoBlock auto, int daysLeft, string? windowStart) => auto switch
    {
        AutoBlock.TooNew => Words.AutoIn(daysLeft),
        AutoBlock.OutsideWindow when windowStart is not null => Words.Format(Strings.AutoAt, windowStart),
        AutoBlock.Offline => Strings.WaitsForNetwork,
        AutoBlock.Metered => Strings.WaitsForUnmetered,
        AutoBlock.BatterySaver => Strings.WaitsForEnergySaver,
        _ => null,
    };

    // The versions come from the install while there is one, else from the check's offer.
    private static RowView WithVersions(RowView view, AppCheck check, InstallItem? install, bool whatsNew, bool securityFirst)
    {
        var from = install?.Request.FromVersion ?? check.Package?.InstalledVersion ?? "";
        var to = install?.Request.ToVersion ?? check.App.Offer?.Version;
        var shows = to is not null && view.State is not (RowState.UpToDate or RowState.Skipped or RowState.VersionUnknown or RowState.NotFound or RowState.NotInCatalog);
        var offered = check.App.Offer is not null && check.Status is AppStatus.Available or AppStatus.Phantom or AppStatus.Skipped;
        return view with
        {
            From = shows ? from : "",
            To = shows ? VersionDiff.Between(from, to) : default,
            ShowVersions = shows,
            CanSkip = offered && check.Status != AppStatus.Skipped && !view.IsActive && view.State is not (RowState.Updated or RowState.RestartNeeded),
            CanUpdateNow = view.CanUpdateNow && offered && !view.IsActive,
            ShowNotes = view.ShowNotes && whatsNew,
            HasNotes = offered && HasNotesOf(check.Package),
            Security = securityFirst && offered && !view.InUpToDateGroup && check.Package?.IsSecurityFix == true,
        };
    }

    // The release date when known and not ahead of now, else the day the version was first seen.
    private static string Released(Offer offer, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var day = offer.ReleaseDate is { } date && date <= today ? date : DateOnly.FromDateTime(offer.FirstSeen.UtcDateTime);
        return Words.Released(day, now);
    }

    private static string NameOf(TrackedApp app) => app.Name.Length > 0 ? app.Name : app.Id;
}
