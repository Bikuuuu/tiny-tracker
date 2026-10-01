using TinyTracker.Core.Versions;

namespace TinyTracker.Core.Tracking;

// NewVersion: this check is the first to offer the version.
public sealed record AppCheck(TrackedApp App, AppStatus Status, PackageSnapshot? Package, bool NewVersion);

public static class CheckMerge
{
    // An app still not installed this long after a check first missed it stops being tracked (spec §4.3).
    public static readonly TimeSpan ForgetMissingAfter = TimeSpan.FromDays(1);

    // A missing app is NotInCatalog when notInCatalog lists it, else NotFound.
    public static IReadOnlyList<AppCheck> Apply(
        IReadOnlyList<TrackedApp> apps,
        IReadOnlyList<PackageSnapshot> packages,
        DateTimeOffset now,
        IReadOnlyCollection<PackageKey>? notInCatalog = null) =>
        apps.Select(app => Merge(
            app,
            packages.FirstOrDefault(p => app.Matches(p.Id, p.Source)),
            notInCatalog?.Any(k => app.Matches(k.Id, k.Source)) == true,
            now)).ToList();

    // Only a check forgets an app: one that still misses it a day after the first miss, which a check or the read
    // after an install noted (so at least two reads a day apart).
    public static bool Forget(AppCheck check, DateTimeOffset now) =>
        check.Status == AppStatus.NotFound && check.App.MissingSince is { } since && now - since >= ForgetMissingAfter;

    private static AppCheck Merge(TrackedApp app, PackageSnapshot? package, bool gone, DateTimeOffset now)
    {
        // Missing or unknown tells us nothing new, so bookkeeping stays; a missing app notes since when.
        // A time after now means the clock went back.
        if (package is null && gone) return new AppCheck(app, AppStatus.NotInCatalog, null, false);
        if (package is null)
            return new AppCheck(app.MissingSince is { } since && since <= now ? app : app with { MissingSince = now }, AppStatus.NotFound, null, false);
        if (app.MissingSince is not null) app = app with { MissingSince = null };
        if (package.Name.Length > 0) app = app with { Name = package.Name };
        if (PackageVersion.Parse(package.InstalledVersion).IsUnknown) return new AppCheck(app, AppStatus.VersionUnknown, package, false);
        if (PackageVersion.Parse(package.AvailableVersion).IsUnknown)
            return new AppCheck(app with { Offer = null }, AppStatus.UpToDate, package, false);

        var available = package.AvailableVersion!;
        var isNew = app.Offer is null || !PackageVersion.Same(app.Offer.Version, available);
        var offer = isNew ? new Offer { Version = available, FirstSeen = now } : app.Offer!;
        // First seen after now means the clock went back.
        if (offer.FirstSeen > now) offer = offer with { FirstSeen = now };
        app = app with { Offer = offer };
        if (PackageVersion.Same(app.SkippedVersion, available)) return new AppCheck(app, AppStatus.Skipped, package, false);
        if (offer.Phantom) return new AppCheck(app, AppStatus.Phantom, package, false);
        return new AppCheck(app, AppStatus.Available, package, isNew);
    }
}
