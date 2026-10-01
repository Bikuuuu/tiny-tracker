using System.Reflection;

namespace TinyTracker.WinGet.Tests.Integration;

// The helper the runner tests start: this build's, or the installed one that TINYTRACKER_HELPER names (spec §12).
internal static class HelperExe
{
    public static readonly string Location = Environment.GetEnvironmentVariable("TINYTRACKER_HELPER") is { Length: > 0 } installed
        ? installed
        : typeof(HelperExe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "HelperPath").Value!;

    // An installed helper is in Program Files already.
    public static bool Installed =>
        Location.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + '\\', StringComparison.OrdinalIgnoreCase);
}
