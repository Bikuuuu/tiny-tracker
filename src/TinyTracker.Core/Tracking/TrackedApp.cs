using System.Text.Json.Serialization;
using TinyTracker.Core.Settings;

namespace TinyTracker.Core.Tracking;

public sealed record TrackedApp
{
    public const string WinGet = "winget";

    public required string Id { get; set; }
    public required string Source { get; set; }
    // Last name winget reported, kept for when the app goes missing.
    public string Name { get; set; } = "";
    // Its own Auto from its "…" menu; null follows Update apps automatically (spec §6.2).
    public bool? AutoChoice { get; set; }
    // The flag before that switch, read from older files and never written: on became the app's own choice.
    [JsonPropertyName("auto"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OldAuto { get; set; }
    public string? SkippedVersion { get; set; }
    public Offer? Offer { get; set; }
    // When a check first found it not installed; a check that finds it clears this.
    public DateTimeOffset? MissingSince { get; set; }

    // A method, so settings.json doesn't save it.
    public bool IsFromWinGet() => string.Equals(Source, WinGet, StringComparison.OrdinalIgnoreCase);

    public bool IsAuto(AppSettings settings) => AutoChoice ?? settings.AutoUpdateApps;

    // winget ids are case-insensitive.
    public bool Matches(string id, string source) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase) && string.Equals(Source, source, StringComparison.OrdinalIgnoreCase);
}
