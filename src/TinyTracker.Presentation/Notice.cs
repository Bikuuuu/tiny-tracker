using TinyTracker.Core.Checking;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation;

// Same names as InfoBarSeverity.
public enum NoticeSeverity
{
    Informational,
    Warning,
    Error,
}

public enum NoticeKind
{
    WinGet,
    Check,
    SettingsRecovered,
    SettingsUnreadable,
    HistoryRecovered,
    HistoryUnreadable,
    SaveFailed,
    StartupNotChanged,
    HistoryNotCleared,
    SilentModeNotChanged,
    SilentModeTaskMissing,
    AdminFallback,
    HiddenIconsTip,
    SpeedLimitNotOn,
    SpeedLimitOptionOff,
    NewApp,
    BackupNotSaved,
    NotABackup,
    BackupNotRead,
    RestoreFailed,
    Restored,
    FileDialogFailed,
}

// A button on a notice. Name: what Narrator says, when the text alone isn't enough.
public sealed record NoticeAction(string Text, Action Run, string? Name = null);

// A banner at the top of a page. A page shows one notice per kind, or per kind and key.
public sealed record Notice(NoticeKind Kind, NoticeSeverity Severity, string Title, string Message = "", string? Details = null, bool Closable = false)
{
    // App Installer's Microsoft Store page, where winget is updated.
    public const string AppInstallerStoreLink = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    public string? Key { get; init; }

    public IReadOnlyList<NoticeAction> Actions { get; init; } = [];

    // Key: the app's winget id (spec §4.3).
    public static Notice NewApp(string id, string name, Action track, Action decline) =>
        new(NoticeKind.NewApp, NoticeSeverity.Informational, Words.Format(Strings.NewApp, name))
        {
            Key = id,
            Actions = [new(Strings.Track, track, Words.Format(Strings.SpokenAction, Strings.Track, name)), new(Strings.NoThanks, decline, Words.Format(Strings.SpokenAction, Strings.NoThanks, name))],
        };

    // What a restore added, and what isn't installed here (spec §4.5).
    public static Notice Restored(int added, IReadOnlyList<string> missing) =>
        new(NoticeKind.Restored, NoticeSeverity.Informational, Words.AppsAdded(added), missing.Count > 0 ? Words.NotInstalledHere(missing) : "", Closable: true);

    public static Notice AllTracked { get; } = new(NoticeKind.Restored, NoticeSeverity.Informational, Strings.AllTrackedAlready, Closable: true);

    public static Notice NotABackup { get; } = new(NoticeKind.NotABackup, NoticeSeverity.Warning, Strings.NotABackup, Closable: true);

    public static Notice BackupNotRead(Exception error) =>
        new(NoticeKind.BackupNotRead, NoticeSeverity.Error, Strings.BackupNotRead, Details: Code(error), Closable: true);

    // detail: winget's message, when it gave one.
    public static Notice RestoreFailed(string? detail) => new(NoticeKind.RestoreFailed, NoticeSeverity.Warning, Strings.RestoreFailed, Details: detail, Closable: true);

    public static Notice FileDialogFailed(Exception error) =>
        new(NoticeKind.FileDialogFailed, NoticeSeverity.Error, Strings.FileDialogFailed, Details: Code(error), Closable: true);

    public static Notice BackupNotSaved(Exception error) =>
        new(NoticeKind.BackupNotSaved, NoticeSeverity.Error, Strings.BackupNotSaved, Details: Code(error), Closable: true);

    public bool OffersStore => Kind == NoticeKind.WinGet;

    public bool HasDetails => Details is not null;

    public static Notice? ForProblem(CheckProblem problem, string? detail) => problem switch
    {
        CheckProblem.None => null,
        CheckProblem.WinGetMissing or CheckProblem.WinGetTooOld => new(NoticeKind.WinGet, NoticeSeverity.Error, Strings.WinGetNeedsUpdate, Strings.WinGetNeedsUpdateMessage, detail),
        CheckProblem.WinGetUnreachable => new(NoticeKind.Check, NoticeSeverity.Warning, Strings.WinGetUnreachable, Details: detail),
        CheckProblem.TimedOut => new(NoticeKind.Check, NoticeSeverity.Warning, Strings.CheckTimedOut, Details: detail),
        CheckProblem.SettingsNotSaved => new(NoticeKind.Check, NoticeSeverity.Error, Strings.SettingsNotSaved, Details: detail),
        _ => new(NoticeKind.Check, NoticeSeverity.Error, Strings.CheckFailed, Details: detail),
    };

    public static Notice SettingsRecovered { get; } = new(NoticeKind.SettingsRecovered, NoticeSeverity.Warning, Strings.SettingsRecovered, Closable: true);
    public static Notice SettingsUnreadable { get; } = new(NoticeKind.SettingsUnreadable, NoticeSeverity.Error, Strings.SettingsUnreadable);
    public static Notice HistoryRecovered { get; } = new(NoticeKind.HistoryRecovered, NoticeSeverity.Warning, Strings.HistoryRecovered, Closable: true);
    public static Notice HistoryUnreadable { get; } = new(NoticeKind.HistoryUnreadable, NoticeSeverity.Error, Strings.HistoryUnreadable);
    public static Notice SaveFailed(Exception error) => new(NoticeKind.SaveFailed, NoticeSeverity.Error, Strings.SaveFailed, Details: Code(error), Closable: true);

    public static Notice SilentModeTaskMissing { get; } = new(NoticeKind.SilentModeTaskMissing, NoticeSeverity.Warning, Strings.SilentModeTaskMissing, Closable: true);

    public static Notice SpeedLimitOptionOff { get; } = new(NoticeKind.SpeedLimitOptionOff, NoticeSeverity.Warning, Strings.SpeedLimitOptionOff, Closable: true);

    public static Notice SpeedLimitNotOn(string? code) => new(NoticeKind.SpeedLimitNotOn, NoticeSeverity.Error, Strings.SpeedLimitNotOn, Details: code, Closable: true);

    // A one-time tip.
    public static Notice AdminFallback { get; } = new(NoticeKind.AdminFallback, NoticeSeverity.Informational, "", Strings.AdminFallbackTip, Closable: true);
    public static Notice HiddenIconsTip { get; } = new(NoticeKind.HiddenIconsTip, NoticeSeverity.Informational, "", Strings.HiddenIconsTip, Closable: true);

    public static Notice SilentModeNotChanged(bool on, string? code) =>
        new(NoticeKind.SilentModeNotChanged, NoticeSeverity.Error, on ? Strings.SilentModeNotOn : Strings.SilentModeNotOff, Details: code, Closable: true);

    public static Notice StartupNotChanged(Exception error) =>
        new(NoticeKind.StartupNotChanged, NoticeSeverity.Error, Strings.StartupNotChanged, Details: Code(error), Closable: true);

    public static Notice HistoryNotCleared(Exception error) =>
        new(NoticeKind.HistoryNotCleared, NoticeSeverity.Error, Strings.HistoryNotCleared, Details: Code(error), Closable: true);

    private static string Code(Exception error) => Words.Format(Strings.DetailsCode, $"0x{error.HResult:X8}");
}
