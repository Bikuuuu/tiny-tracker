namespace TinyTracker.Core.Storage;

// Per-user data files. Nothing is created until something is saved.
public sealed record DataPaths(string Root)
{
    public static DataPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name));

    public string Settings => Path.Combine(Root, "settings.json");
    public string History => Path.Combine(Root, "history.json");
    public string Log => Path.Combine(Root, "logs", "app.log");
}
