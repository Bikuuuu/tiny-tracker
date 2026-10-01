using TinyTracker.Core;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Shell;

public enum TrayIconKind
{
    Idle,
    // Updates ready, or winget needs an update.
    Badge,
    // A check or an install is running.
    Working,
}

// The tray icon and its tooltip (spec §4.1).
public sealed record TrayState(TrayIconKind Icon, string Tooltip)
{
    public static TrayState Of(bool working, bool checking, string? installing, int? percent, Notice? problem, int pending, bool noApps, bool checkedYet)
    {
        var wingetMissing = problem?.Kind == NoticeKind.WinGet;
        var text = installing is not null ? percent is { } p ? Words.Format(Strings.TrayInstallingPercent, installing, p) : Words.Format(Strings.TrayInstalling, installing)
            : checking ? Strings.TrayChecking
            : wingetMissing ? Strings.WinGetNeedsUpdate
            : noApps ? Strings.TrayNoApps
            : pending > 0 ? Words.UpdatesReady(pending)
            : checkedYet ? Strings.AllUpToDate
            : Strings.NotCheckedYet;
        var icon = working ? TrayIconKind.Working : pending > 0 || wingetMissing ? TrayIconKind.Badge : TrayIconKind.Idle;
        return new TrayState(icon, Words.Format(Strings.TrayTip, AppInfo.Name, text));
    }
}
