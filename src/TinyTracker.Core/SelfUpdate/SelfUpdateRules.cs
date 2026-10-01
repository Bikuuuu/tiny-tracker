using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.SelfUpdate;

// When Tiny Tracker installs its own update by itself (spec §6.5): as an Auto app would (spec §6.2), waiting from GitHub's
// release date, and only with silent mode on, since installing into Program Files always needs admin rights.
public static class SelfUpdateRules
{
    public static AutoBlock Check(SelfRelease? release, SelfVersion running, SelfUpdateBook book, AppSettings settings, SystemState system, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!settings.AutoSelfUpdate) return AutoBlock.AutoOff;
        if (release is null || !release.Version.IsNewerThan(running)) return AutoBlock.NotAvailable;
        if (SelfVersion.Parse(book.FailedVersion) == release.Version) return AutoBlock.AutoOff;
        if (!settings.SilentMode) return AutoBlock.NeedsAdmin;
        if (AutoInstallRules.DaysLeft(release.PublishedAt, settings, now) > 0) return AutoBlock.TooNew;
        if (InstallWindow.Of(settings) is { } window && !window.Contains(now, zone)) return AutoBlock.OutsideWindow;
        if (system.Offline) return AutoBlock.Offline;
        if (system.Metered) return AutoBlock.Metered;
        if (system.BatterySaver) return AutoBlock.BatterySaver;
        // An attempt after now means the clock went back, so it doesn't count.
        if (book.AttemptedAt is { } last && SelfVersion.Parse(book.AttemptedVersion) == release.Version && last <= now && now - last < AutoInstallRules.AttemptCooldown)
            return AutoBlock.RecentlyAttempted;
        if (settings.PauseDuringGames && system.FullScreen) return AutoBlock.FullScreen;
        return AutoBlock.None;
    }
}
