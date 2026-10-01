using System.Text;
using System.Text.Json;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Storage;

public sealed record BackedUpApp
{
    public required string Id { get; set; }
    public string Name { get; set; } = "";
    public bool Auto { get; set; }
}

public sealed record AppListFile
{
    public int Format { get; set; }
    // Whether Update apps automatically was on; 0.9's files have no such key.
    public bool AutoUpdateApps { get; set; }
    public IReadOnlyList<BackedUpApp>? Apps { get; set; }
}

// A backup once read: its apps, and whether the switch was on.
public sealed record AppList(IReadOnlyList<BackedUpApp> Apps, bool AutoUpdateApps);

// A backup of the list (spec §4.5): whether Update apps automatically is on, and each tracked winget app's id, name and Auto.
public static class AppListBackup
{
    public const string FileName = "Tiny Tracker apps.json";
    public const int MaxBytes = 1024 * 1024;
    private const int Format = 1;
    private const int MaxName = 256;

    // Throws IOException when the file can't be written; an old file stays. An id Load would refuse is left out.
    public static void Save(string path, IReadOnlyList<TrackedApp> apps, AppSettings settings) =>
        JsonFile.Save(path, new AppListFile
        {
            Format = Format,
            AutoUpdateApps = settings.AutoUpdateApps,
            Apps = [.. apps.Where(a => a.IsFromWinGet() && HelperRules.IsId(a.Id))
                .Select(a => new BackedUpApp { Id = a.Id, Name = Clean(a.Name), Auto = a.IsAuto(settings) })],
        }, CoreJson.Default.AppListFile);

    // As Load wants names, so a file this writes always restores: control characters become spaces, and a long name is cut, never
    // inside a character.
    private static string Clean(string name)
    {
        var text = new string([.. name.Select(c => char.IsControl(c) ? ' ' : c)]);
        return text.Length <= MaxName ? text : text[..(char.IsHighSurrogate(text[MaxName - 1]) ? MaxName - 1 : MaxName)];
    }

    // Null for any file that isn't a backup (spec §8). Throws IOException when the file can't be read.
    public static AppList? Load(string path)
    {
        var buffer = new byte[MaxBytes + 1];
        int length;
        try
        {
            using var stream = File.OpenRead(path);
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e) { HResult = e.HResult };
        }
        if (length > MaxBytes) return null;
        var json = buffer.AsSpan(0, length);
        // Editors may add a BOM.
        if (json.StartsWith(Encoding.UTF8.Preamble)) json = json[Encoding.UTF8.Preamble.Length..];
        AppListFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CoreJson.Default.AppListFile);
        }
        catch (JsonException)
        {
            return null;
        }
        if (file is not { Format: Format, Apps: { } apps } || apps.Any(a => a is null || !HelperRules.IsId(a.Id) || a.Name.Length > MaxName || a.Name.Any(char.IsControl))) return null;
        return new AppList([.. apps.DistinctBy(a => a.Id.ToUpperInvariant())], file.AutoUpdateApps);
    }
}
