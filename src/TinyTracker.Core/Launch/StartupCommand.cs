namespace TinyTracker.Core.Launch;

// The Run value that starts the app at sign-in.
public static class StartupCommand
{
    public static string Format(string exe) => $"\"{exe}\" {LaunchPolicy.StartupFlag}";

    // The exe a Run value starts, or null unless it's a fully qualified path. An unquoted path ends at the first space.
    public static string? Target(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            path = text[1..end];
        }
        else
        {
            var space = text.IndexOf(' ');
            path = space < 0 ? text : text[..space];
        }
        return path.Length > 0 && Path.IsPathFullyQualified(path) ? path : null;
    }
}
