using System.Text.Json;

namespace TinyTracker.WinGet.Cli;

// winget's own settings, through its command line (spec §6.4).
public static class WinGetSettings
{
    // Whether winget's proxy option is on, from `winget settings export`, policies included. Null when winget can't say.
    public static async Task<bool?> ProxyOptionAsync(WinGetCli cli, CancellationToken ct) =>
        await cli.RunAsync(["settings", "export"], ct) is { ExitCode: 0 } run ? ProxyOptionOf(run.Lines) : null;

    // The export is JSON; whatever winget prints before it doesn't count.
    public static bool? ProxyOptionOf(IEnumerable<string> export)
    {
        var json = string.Concat(export.SkipWhile(line => !line.TrimStart().StartsWith('{')));
        if (json.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("adminSettings", out var admin) && admin.ValueKind == JsonValueKind.Object
                && admin.TryGetProperty("ProxyCommandLineOptions", out var option) && option.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? option.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // For the user this runs as, which takes an administrator. Null once it reads on, else why not: winget's own code when it
    // refused, for Details.
    public static async Task<string?> EnableProxyOptionAsync(WinGetCli cli, CancellationToken ct)
    {
        if (await cli.RunAsync(["settings", "--enable", "ProxyCommandLineOptions"], ct) is not { } run) return "winget didn't run";
        var on = await ProxyOptionAsync(cli, ct);
        if (on == true) return null;
        if (run.ExitCode != 0) return $"0x{run.ExitCode:X8}";
        return on == false ? "still off" : "winget didn't say";
    }

    // The uninstaller's, for the account it runs as (spec §10). Null once it reads off, else why not.
    public static async Task<string?> DisableProxyOptionAsync(WinGetCli cli, CancellationToken ct)
    {
        if (await cli.RunAsync(["settings", "--disable", "ProxyCommandLineOptions"], ct) is not { } run) return "winget didn't run";
        var on = await ProxyOptionAsync(cli, ct);
        if (on == false) return null;
        if (run.ExitCode != 0) return $"0x{run.ExitCode:X8}";
        return on == true ? "still on" : "winget didn't say";
    }
}
