using System.Globalization;
using System.Runtime.InteropServices;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Settings;

namespace TinyTracker.Presentation.Settings;

// What Copy diagnostic info puts on the clipboard: versions, counts and settings, never names, ids, paths or free text.
public sealed record DiagnosticFacts(
    string AppVersion,
    Version Windows,
    Architecture Architecture,
    Version DotNet,
    string? WinGet,
    int TrackedApps,
    AppSettings Settings,
    bool StartWithWindows,
    DateTimeOffset? LastCheck,
    CheckProblem LastProblem,
    DateTimeOffset? LastGoodCheck)
{
    public static DiagnosticFacts Gather(string appVersion, int trackedApps, AppSettings settings, bool startWithWindows,
        DateTimeOffset? lastCheck, CheckProblem lastProblem, DateTimeOffset? lastGoodCheck) =>
        new(appVersion, Environment.OSVersion.Version, RuntimeInformation.OSArchitecture, Environment.Version, null, trackedApps, settings,
            startWithWindows, lastCheck, lastProblem, lastGoodCheck);

    // Fixed English, like log lines, so a report reads the same in any language.
    public string Text()
    {
        var c = CultureInfo.InvariantCulture;
        string OnOff(bool on) => on ? "on" : "off";
        var shortcut = Settings.OpenShortcut is { } s ? string.Create(c, $"{s.Modifiers} + 0x{s.Key:X2}") : "none";
        string[] lines =
        [
            $"{AppInfo.Name} {AppVersion}",
            string.Create(c, $"Windows {Windows} {Architecture}"),
            string.Create(c, $".NET {DotNet}"),
            $"winget {WinGetText()}",
            string.Create(c, $"Tracked apps: {TrackedApps}"),
            LastCheck is { } at ? string.Create(c, $"Last check: {at.ToUniversalTime():O} {LastProblem}") : "Last check: none",
            LastGoodCheck is { } good ? string.Create(c, $"Last good check: {good.ToUniversalTime():O}") : "Last good check: none",
            string.Create(c, $"Check every: {Settings.CheckIntervalHours} h"),
            $"Update apps automatically: {OnOff(Settings.AutoUpdateApps)}",
            $"Silent mode: {OnOff(Settings.SilentMode)}",
            string.Create(c, $"Wait before auto-installing: {Settings.AutoInstallWaitDays} days"),
            $"Security fixes first: {OnOff(Settings.SecurityFirst)}",
            string.Create(c, $"Install window: {OnOff(Settings.InstallWindowEnabled)} ({Settings.InstallWindowFrom} to {Settings.InstallWindowTo})"),
            $"Pause during games: {OnOff(Settings.PauseDuringGames)}",
            string.Create(c, $"Speed limit: {OnOff(Settings.SpeedLimitEnabled)} ({Settings.SpeedLimitKBps} KB/s)"),
            $"Notifications: {Settings.Notifications}",
            $"Start with Windows: {OnOff(StartWithWindows)}",
            $"Shortcut: {shortcut}",
            $"Auto self-update: {OnOff(Settings.AutoSelfUpdate)}",
            $"What's new links: {OnOff(Settings.ShowWhatsNew)}",
        ];
        return string.Join(Environment.NewLine, lines);
    }

    // Only a plain version number gets through.
    private string WinGetText() => WinGet is null ? "unavailable" : Version.TryParse(WinGet.Trim().TrimStart('v', 'V'), out var version) ? version.ToString() : "unknown";
}
