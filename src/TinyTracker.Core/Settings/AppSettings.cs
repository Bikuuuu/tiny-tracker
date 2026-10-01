using System.Text.Json.Serialization;

namespace TinyTracker.Core.Settings;

// Defaults follow the spec's Settings table. Start with Windows lives in the Run key, not here.
public sealed record AppSettings
{
    public const int DefaultCheckIntervalHours = 6;
    public const int DefaultInstallWindowFrom = 22;
    public const int DefaultInstallWindowTo = 6;
    public const int DefaultSpeedLimitKBps = 17500;
    // Whole KB/s from 0.1 to about 977 MB/s (spec §6.4).
    public const int MinSpeedLimitKBps = 100;
    public const int MaxSpeedLimitKBps = 1_000_000;

    public static IReadOnlyList<int> CheckIntervalChoices { get; } = [1, 3, 6, 12, 24];
    public static IReadOnlyList<int> WaitDayChoices { get; } = [0, 1, 3, 7];

    public int CheckIntervalHours { get; set; } = DefaultCheckIntervalHours;
    // Every tracked app on Auto, but those with their own choice (spec §6.2).
    public bool AutoUpdateApps { get; set; }
    public bool SilentMode { get; set; }
    public int AutoInstallWaitDays { get; set; }
    // A security fix skips the wait, is marked and comes first (spec §6.2).
    public bool SecurityFirst { get; set; } = true;
    // Auto apps install by themselves only in these whole local hours (spec §6.2).
    public bool InstallWindowEnabled { get; set; }
    public int InstallWindowFrom { get; set; } = DefaultInstallWindowFrom;
    public int InstallWindowTo { get; set; } = DefaultInstallWindowTo;
    public bool PauseDuringGames { get; set; } = true;
    public bool SpeedLimitEnabled { get; set; }
    public int SpeedLimitKBps { get; set; } = DefaultSpeedLimitKBps;
    [JsonConverter(typeof(NotificationLevelConverter))]
    public NotificationLevel Notifications { get; set; } = NotificationLevel.All;
    // The switch before the levels, read from older files and never written: off meant no toasts.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowNotifications { get; set; }
    // Null when the user cleared it.
    public Shortcut? OpenShortcut { get; set; } = Shortcut.Default;
    public bool AutoSelfUpdate { get; set; } = true;
    // "What's new" on the rows' status lines; the "…" menu keeps it either way (spec §4.3).
    public bool ShowWhatsNew { get; set; } = true;

    // Out-of-range values from hand edits fall back to defaults.
    public AppSettings Normalize() => this with
    {
        CheckIntervalHours = CheckIntervalChoices.Contains(CheckIntervalHours) ? CheckIntervalHours : DefaultCheckIntervalHours,
        AutoInstallWaitDays = WaitDayChoices.Contains(AutoInstallWaitDays) ? AutoInstallWaitDays : 0,
        InstallWindowFrom = WindowHolds ? InstallWindowFrom : DefaultInstallWindowFrom,
        InstallWindowTo = WindowHolds ? InstallWindowTo : DefaultInstallWindowTo,
        SpeedLimitKBps = SpeedLimitKBps is >= MinSpeedLimitKBps and <= MaxSpeedLimitKBps ? SpeedLimitKBps : DefaultSpeedLimitKBps,
        Notifications = ShowNotifications == false ? NotificationLevel.Off : Enum.IsDefined(Notifications) ? Notifications : NotificationLevel.All,
        ShowNotifications = null,
        OpenShortcut = OpenShortcut is { } shortcut && !shortcut.IsValid() ? Shortcut.Default : OpenShortcut,
    };

    // Two whole hours that differ.
    private bool WindowHolds => InstallWindowFrom is >= 0 and <= 23 && InstallWindowTo is >= 0 and <= 23 && InstallWindowFrom != InstallWindowTo;
}
