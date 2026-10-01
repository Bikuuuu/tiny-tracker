using System.Text.Json;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Storage;

// settings.json: the settings plus the tracked apps with their bookkeeping.
public sealed record SettingsFile
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public IReadOnlyList<TrackedApp> Apps { get; set; } = [];
    // One-time tips already shown.
    public IReadOnlyList<string> TipsShown { get; set; } = [];
    // The app turned winget's proxy option on, so the uninstaller turns it off again (spec §10).
    public bool TurnedOnProxyOption { get; set; }
    public SelfUpdateBook SelfUpdate { get; set; } = new();
    // New apps (spec §4.3): the winget ids never offered, that is installed at the first check that listed apps, tracked at any
    // time, or turned down. Null until that check.
    public IReadOnlyList<string>? KnownApps { get; set; }

    public SettingsFile Normalize()
    {
        var apps = Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Source))
            .DistinctBy(a => (a.Id.ToUpperInvariant(), a.Source.ToUpperInvariant()))
            // An older file's Auto on becomes the app's own choice; off follows the switch, which starts off (spec §5.4).
            .Select(a => a.OldAuto is not { } old ? a : a with { AutoChoice = a.AutoChoice ?? (old ? true : null), OldAuto = null })
            .ToList();
        return this with
        {
            Settings = Settings.Normalize(),
            Apps = apps,
            TipsShown = TipsShown.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToList(),
            KnownApps = KnownApps?.Concat(apps.Where(a => a.IsFromWinGet()).Select(a => a.Id)).Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    // For the uninstaller: read only, so a damaged file isn't set aside as a load would. False when it can't be read.
    public static bool TurnedOnProxyOptionIn(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, CoreJson.Default.SettingsFile)?.TurnedOnProxyOption == true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
