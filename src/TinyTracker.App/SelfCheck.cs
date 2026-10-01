namespace TinyTracker.App;

internal static class SelfCheck
{
    public static string? RequestedPath()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--self-check");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
