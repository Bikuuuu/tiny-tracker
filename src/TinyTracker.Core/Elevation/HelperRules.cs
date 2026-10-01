using System.Text.RegularExpressions;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Elevation;

// What the helper accepts (spec §8). Ids and versions follow winget's manifest schema, with no control character at all.
public static partial class HelperRules
{
    private const string PipePrefix = "TinyTracker.Helper.";

    // Nothing asked and nothing running for this long: the app is gone or stuck, and the helper leaves (spec §5.1).
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    // How often an app that holds the helper for updates that wait their turn says so.
    public static readonly TimeSpan StayEvery = TimeSpan.FromMinutes(5);

    public static bool IsValid(UpgradeRequest request) =>
        string.Equals(request.Source, TrackedApp.WinGet, StringComparison.OrdinalIgnoreCase) && IsId(request.Id) && IsVersion(request.Version) && SpeedLimit.IsAllowed(request.Limit);

    // A plain version newer than the helper's own, which it must know.
    public static bool IsValid(SelfUpdateRequest request, SelfVersion? own) =>
        own is not null && SelfVersion.Parse(request.Version) is { } version && version.IsNewerThan(own) && SpeedLimit.IsAllowed(request.Limit);

    public static bool IsId(string? id) => id is { Length: <= 128 } && Id().IsMatch(id);

    public static bool IsVersion(string? version) => version is { Length: <= 128 } && Version().IsMatch(version);

    // A new name for each helper start, so no other program can take it in advance.
    public static string NewPipeName() => PipePrefix + Guid.NewGuid().ToString("N");

    public static bool IsPipeName(string? name) => name is not null && PipeName().IsMatch(name);

    // 2 to 8 parts of 1 to 32 characters. No part holds a dot, a space, a control character or any of \/:*?"<>|.
    [GeneratedRegex(@"\A[^\.\s\\/:\*\?""<>\|\x00-\x1f]{1,32}(\.[^\.\s\\/:\*\?""<>\|\x00-\x1f]{1,32}){1,7}\z")]
    private static partial Regex Id();

    [GeneratedRegex(@"\A[^\\/:\*\?""<>\|\x00-\x1f]+\z")]
    private static partial Regex Version();

    [GeneratedRegex(@"\ATinyTracker\.Helper\.[0-9a-f]{32}\z")]
    private static partial Regex PipeName();
}
