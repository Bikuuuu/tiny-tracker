using System.Globalization;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Settings;

namespace TinyTracker.Presentation.Text;

// Sizes, times and reasons in plain words. The words live in Strings.resx.
public static class Words
{
    private const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Size(ulong bytes)
    {
        var (value, format) = Unit(bytes);
        return Format(format, Number(value));
    }

    // "180 of 400 MB", or "180 MB" when the total isn't known.
    public static string Downloaded(ulong done, ulong total)
    {
        if (total == 0) return Size(done);
        var (value, format) = Unit(total);
        return Format(Strings.DownloadedOf, Number(done / (total / value)), Format(format, Number(value)));
    }

    public static string Speed(double bytesPerSecond) => Format(Strings.PerSecond, Size((ulong)Math.Max(0, bytesPerSecond)));

    // "≈ 17.1 MB/s": a speed limit in the rows' units, where a megabyte is 1024 kilobytes.
    public static string MegabytesPerSecond(int kbps)
    {
        var mb = kbps / KB;
        return Format(Strings.MBpsEquivalent, mb.ToString(mb < 10 ? "0.##" : mb < 100 ? "0.#" : "0", Culture));
    }

    // A time ahead of now counts as just now.
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var elapsed = now - then;
        if (elapsed < TimeSpan.FromMinutes(1)) return Strings.JustNow;
        if (elapsed < TimeSpan.FromHours(1)) return Format(Strings.MinutesAgo, (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1)) return Format(Strings.HoursAgo, (int)elapsed.TotalHours);
        var days = (int)elapsed.TotalDays;
        return days == 1 ? Strings.DayAgo : Format(Strings.DaysAgo, days);
    }

    // Counted in UTC days, like release dates.
    public static string Released(DateOnly day, DateTimeOffset now)
    {
        var days = DateOnly.FromDateTime(now.UtcDateTime).DayNumber - day.DayNumber;
        return days switch
        {
            <= 0 => Strings.ReleasedToday,
            1 => Strings.ReleasedYesterday,
            <= 30 => Format(Strings.DaysAgo, days),
            _ => day.ToString(Strings.DayYearFormat, Culture),
        };
    }

    // Rounded up to the minute.
    public static string Until(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1)) return Strings.UnderAMinute;
        var (hours, minutes) = Math.DivRem((int)Math.Ceiling(left.TotalMinutes), 60);
        if (hours == 0) return Format(Strings.Minutes, minutes);
        return minutes == 0 ? Format(Strings.Hours, hours) : Format(Strings.HoursMinutes, hours, minutes);
    }

    // Why an install didn't simply succeed, by the name History keeps.
    public static string Reason(string? reason) =>
        reason is null ? "" : Strings.ResourceManager.GetString("Reason_" + reason, Strings.Culture) ?? Strings.Reason_Other;

    public static string Elsewhere(UpdatedBy by) =>
        Strings.ResourceManager.GetString("UpdatedBy_" + by, Strings.Culture) ?? Strings.UpdatedBy_Unknown;

    public static string UpdatesReady(int count) => count == 1 ? Strings.UpdatesReadyOne : Format(Strings.UpdatesReadyMany, count);

    public static string Installed(int count) => count == 1 ? Strings.InstalledOne : Format(Strings.InstalledMany, count);

    public static string Failed(int count) => count == 1 ? Strings.FailedOne : Format(Strings.FailedMany, count);

    public static string NeedPermission(int count) => count == 1 ? Strings.NeedPermissionOne : Format(Strings.NeedPermissionMany, count);

    // One app is named, more are counted.
    public static string NeedToClose(IReadOnlyList<string> names) => names.Count == 1 ? Format(Strings.NeedToCloseOne, names[0]) : Format(Strings.NeedToCloseMany, names.Count);

    // "A", "A and B", "A, B and C", then "A, B, C and 2 more".
    public static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => Format(Strings.NamesTwo, names[0], names[1]),
        3 => Format(Strings.NamesThree, names[0], names[1], names[2]),
        _ => Format(Strings.NamesMore, names[0], names[1], names[2], names.Count - 3),
    };

    public static string UpToDate(int count) => count == 1 ? Strings.UpToDateOne : Format(Strings.UpToDateMany, count);

    public static string AppsTracked(int count) => count switch
    {
        0 => Strings.AppsTrackedNone,
        1 => Strings.AppsTrackedOne,
        _ => Format(Strings.AppsTrackedMany, count),
    };

    public static string AppsAdded(int count) => count switch
    {
        0 => Strings.AppsAddedNone,
        1 => Strings.AppsAddedOne,
        _ => Format(Strings.AppsAddedMany, count),
    };

    public static string StopTrackingApps(int count) => count == 1 ? Strings.StopTrackingAppsOne : Format(Strings.StopTrackingAppsMany, count);

    // Up to five names, then how many more.
    public static string NotInstalledHere(IReadOnlyList<string> names) =>
        Format(Strings.NotInstalledHere, names.Count <= 5 ? string.Join(", ", names) : Format(Strings.NamesAndMore, string.Join(", ", names.Take(5)), names.Count - 5));

    public static string Hours(int hours) => hours == 1 ? Strings.HoursOne : Format(Strings.HoursMany, hours);

    public static string AutoIn(int days) => days <= 1 ? Strings.AutoInOne : Format(Strings.AutoInMany, days);

    public static string WaitDays(int days) => days switch
    {
        0 => Strings.WaitOff,
        1 => Strings.DaysOne,
        _ => Format(Strings.DaysMany, days),
    };

    // What a notification level shows.
    public static string NotificationHelp(NotificationLevel level) => level switch
    {
        NotificationLevel.NeedsMe => Strings.NotifyNeedsMeHelp,
        NotificationLevel.Failures => Strings.NotifyFailuresHelp,
        NotificationLevel.Off => Strings.NotifyOffHelp,
        _ => Strings.NotifyAllHelp,
    };

    // "Win + Shift + U": the modifiers in Windows' order, then the key's name in the keyboard layout.
    public static string Shortcut(Shortcut shortcut, string keyName)
    {
        var parts = new List<string>();
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add(Strings.KeyWin);
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add(Strings.KeyCtrl);
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add(Strings.KeyAlt);
        if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add(Strings.KeyShift);
        parts.Add(keyName);
        return string.Join(Strings.KeyJoin, parts);
    }

    // A History day, both days local. A day ahead of today counts as today.
    public static string Day(DateOnly day, DateOnly today)
    {
        if (day >= today) return Strings.DayToday;
        if (day == today.AddDays(-1)) return Strings.DayYesterday;
        return day.ToString(day.Year == today.Year ? Strings.DayFormat : Strings.DayYearFormat, Culture);
    }

    // In the user's own time format, such as 14:32 or 2:32 PM.
    public static string TimeOfDay(DateTimeOffset local, CultureInfo culture) => local.ToString("t", culture);

    // A whole hour in the user's own time format, such as 22:00 or 10:00 PM, kept on one line.
    public static string Hour(int hour, CultureInfo culture) => new DateTime(2000, 1, 1, hour, 0, 0).ToString("t", culture).Replace(' ', (char)0xA0);

    // "a · b"
    public static string Joined(string first, string second) => Format(Strings.Joined, first, second);

    public static string Format(string format, params object?[] args) => string.Format(Culture, format, args);

    private static (double Value, string Format) Unit(ulong bytes) => bytes switch
    {
        < (ulong)MB => (bytes / KB, Strings.SizeKB),
        < (ulong)GB => (bytes / MB, Strings.SizeMB),
        _ => (bytes / GB, Strings.SizeGB),
    };

    private static string Number(double value) => value.ToString(value < 10 ? "0.#" : "0", Culture);
}
