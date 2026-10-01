using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Settings;

namespace TinyTracker.Core.Tracking;

// What the PC is doing, as the auto-install rules see it.
public sealed record SystemState(bool FullScreen, bool Metered, bool BatterySaver, bool Offline = false);

public enum AutoBlock
{
    None,
    AutoOff,
    NotAvailable,
    // The update would show a UAC prompt, so it waits for the user.
    NeedsAdmin,
    TooNew,
    // The install window hasn't opened yet.
    OutsideWindow,
    Offline,
    Metered,
    BatterySaver,
    RecentlyAttempted,
    FullScreen,
}

public static class AutoInstallRules
{
    public static readonly TimeSpan AttemptCooldown = TimeSpan.FromHours(12);

    // The first rule holding the app back, or None (spec §6.2); zone is for the window. Admin comes before the wait, as such an
    // update never installs by itself; full screen comes last, as it ends soonest and the row can show a reason that lasts.
    public static AutoBlock Check(AppCheck check, AppSettings settings, SystemState system, DateTimeOffset now, TimeZoneInfo zone)
    {
        var app = check.App;
        if (!app.IsAuto(settings)) return AutoBlock.AutoOff;
        if (check.Status != AppStatus.Available || app.Offer is not { } offer || check.Package is not { } package) return AutoBlock.NotAvailable;
        if (WaitsForPermission(package, settings)) return AutoBlock.NeedsAdmin;
        if (DaysLeft(offer, settings, now) > 0 && !(settings.SecurityFirst && package.IsSecurityFix)) return AutoBlock.TooNew;
        if (InstallWindow.Of(settings) is { } window && !window.Contains(now, zone)) return AutoBlock.OutsideWindow;
        if (system.Offline) return AutoBlock.Offline;
        if (system.Metered) return AutoBlock.Metered;
        if (system.BatterySaver) return AutoBlock.BatterySaver;
        // An attempt after now means the clock went back, so it doesn't count.
        if (offer.LastAutoAttempt is { } last && last <= now && now - last < AttemptCooldown) return AutoBlock.RecentlyAttempted;
        if (settings.PauseDuringGames && system.FullScreen) return AutoBlock.FullScreen;
        return AutoBlock.None;
    }

    // In default mode an update that would show a UAC prompt waits for the user's Install.
    public static bool WaitsForPermission(PackageSnapshot package, AppSettings settings) => !settings.SilentMode && package.AsksForAdmin;

    public static int DaysLeft(Offer offer, AppSettings settings, DateTimeOffset now) => DaysLeft(ReleasedAt(offer, now), settings, now);

    // Whole days until the wait ends, rounded up; 0 once it has. A date after now (the clock went back) never makes it
    // longer than the setting.
    public static int DaysLeft(DateTimeOffset released, AppSettings settings, DateTimeOffset now)
    {
        if (settings.AutoInstallWaitDays <= 0) return 0;
        var left = released + TimeSpan.FromDays(settings.AutoInstallWaitDays) - now;
        return left <= TimeSpan.Zero ? 0 : Math.Min(settings.AutoInstallWaitDays, (int)Math.Ceiling(left.TotalDays));
    }

    // Release date when known and not ahead of now, else the day we first saw the version.
    private static DateTimeOffset ReleasedAt(Offer offer, DateTimeOffset now)
    {
        if (offer.ReleaseDate is not { } date) return offer.FirstSeen;
        var released = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return released <= now ? released : offer.FirstSeen;
    }
}
