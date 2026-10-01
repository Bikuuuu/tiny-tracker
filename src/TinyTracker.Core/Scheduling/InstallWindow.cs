using TinyTracker.Core.Settings;

namespace TinyTracker.Core.Scheduling;

// From:00 up to To:00 local time, across midnight when To comes first (spec §6.2).
public readonly record struct InstallWindow(int From, int To)
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    // Null while the switch is off.
    public static InstallWindow? Of(AppSettings settings) =>
        settings.InstallWindowEnabled ? new InstallWindow(settings.InstallWindowFrom, settings.InstallWindowTo) : null;

    public bool Contains(DateTimeOffset now, TimeZoneInfo zone)
    {
        var hour = TimeZoneInfo.ConvertTime(now, zone).Hour;
        return From < To ? hour >= From && hour < To : hour >= From || hour < To;
    }

    // Every zone's hours start on a UTC quarter hour, so quarter steps survive daylight saving.
    public DateTimeOffset NextStart(DateTimeOffset now, TimeZoneInfo zone)
    {
        var at = new DateTimeOffset(now.UtcTicks - now.UtcTicks % Quarter.Ticks, TimeSpan.Zero) + Quarter;
        var end = at.AddDays(2);
        while (at < end && !Contains(at, zone)) at += Quarter;
        return at;
    }
}
