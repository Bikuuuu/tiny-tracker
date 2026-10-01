using TinyTracker.Core;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Updates;

// What Tiny Tracker's own row shows (spec §4.3), from its self-update's state. Pure, so every state is tested without the UI.
public sealed record SelfUpdateView : IStatusLine
{
    // Before any update shows.
    public static SelfUpdateView Hidden { get; } = new() { Title = "", Status = "" };

    public required string Title { get; init; }
    public required string Status { get; init; }
    public bool Warning { get; init; }
    public RowAction Action { get; init; }
    public bool CanCancel { get; init; }
    public bool ShowProgress { get; init; }
    public bool Indeterminate { get; init; }
    // 0 to 100.
    public double Percent { get; init; }
    public string? PercentText { get; init; }
    // "What's new" after the status line: the release page.
    public bool ShowNotes { get; init; }
    public string? Details { get; init; }
    // Waiting, asking, downloading or installing.
    public bool IsActive { get; init; }

    public bool HasAction => Action != RowAction.None;

    public string ActionText => Action switch
    {
        RowAction.Update => Strings.Update,
        RowAction.Retry => Strings.Retry,
        _ => "",
    };

    // Null while nothing newer is known. auto: why it hasn't installed by itself; daysLeft goes with TooNew, and windowStart with
    // OutsideWindow. whatsNew: the Settings switch for the link.
    public static SelfUpdateView? Of(SelfUpdateState state, SelfVersion running, AutoBlock auto = AutoBlock.AutoOff, int daysLeft = 0, string? windowStart = null,
        bool whatsNew = true)
    {
        if (state is not { Stage: not SelfUpdateStage.None, Release: { } release }) return null;
        var title = Words.Format(Strings.SelfUpdateTitle, AppInfo.Name, release.Version);
        var change = Words.Format(Strings.VersionChange, running, release.Version);
        return state.Stage switch
        {
            SelfUpdateStage.Available when state.Declined => new SelfUpdateView { Title = title, Status = Strings.PermissionDeclined, Action = RowAction.Update },
            SelfUpdateStage.Available => new SelfUpdateView
            {
                Title = title,
                Status = RowView.AutoWait(auto, daysLeft, windowStart) is { } wait ? Words.Joined(change, wait) : change,
                ShowNotes = whatsNew,
                Action = RowAction.Update,
            },
            SelfUpdateStage.WaitsForOthers => Active(title, Strings.WaitsForOthers),
            SelfUpdateStage.AwaitingPermission => Active(title, Strings.WaitingForPermission),
            SelfUpdateStage.Downloading => Downloading(title, state),
            SelfUpdateStage.Installing => new SelfUpdateView
            {
                Title = title,
                Status = Words.Format(Strings.SelfUpdateInstalling, AppInfo.Name),
                ShowProgress = true,
                Indeterminate = true,
                IsActive = true,
            },
            _ => Failed(title, state.Outcome),
        };
    }

    private static SelfUpdateView Active(string title, string status) => new() { Title = title, Status = status, CanCancel = true, IsActive = true };

    private static SelfUpdateView Downloading(string title, SelfUpdateState state)
    {
        var progress = state.Progress;
        if (progress.BytesRequired == 0) return Active(title, Strings.Waiting) with { ShowProgress = true, Indeterminate = true };
        var fraction = Math.Clamp((double)progress.BytesDownloaded / progress.BytesRequired, 0, 1);
        var amount = Words.Downloaded(progress.BytesDownloaded, progress.BytesRequired);
        if (state.BytesPerSecond > 0) amount = Words.Joined(amount, Words.Speed(state.BytesPerSecond));
        return Active(title, amount) with { ShowProgress = true, Percent = fraction * 100, PercentText = $"{(int)(fraction * 100)}%" };
    }

    // Why it couldn't update: what to do about the other accounts, else the reason;
    // empty when "The update failed" would say nothing more.
    public static string Reason(UpgradeOutcome? outcome)
    {
        if (outcome?.Failure == UpgradeFailure.OtherAccounts) return Words.Format(Strings.SelfUpdateOtherAccounts, AppInfo.Name);
        var reason = Words.Reason(outcome is null ? null : new InstallDone(outcome).Reason);
        return reason == Strings.Reason_Other ? "" : reason;
    }

    private static SelfUpdateView Failed(string title, UpgradeOutcome? outcome)
    {
        var details = outcome?.Code is { } code ? Words.Format(Strings.DetailsCode, code) : null;
        var view = new SelfUpdateView { Title = title, Status = "", Warning = true, Action = RowAction.Retry, Details = details };
        if (outcome?.Failure == UpgradeFailure.OtherAccounts) return view with { Status = Reason(outcome) };
        var failed = Words.Format(Strings.SelfUpdateFailed, AppInfo.Name);
        var reason = Reason(outcome);
        return view with { Status = reason.Length == 0 ? failed : Words.Joined(failed, reason) };
    }
}
