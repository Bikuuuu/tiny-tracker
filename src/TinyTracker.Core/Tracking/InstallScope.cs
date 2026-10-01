namespace TinyTracker.Core.Tracking;

// Where winget says an app is installed: for this user only, or for all users.
public enum InstallScope
{
    Unknown,
    User,
    Machine,
}

// Whether an update's installer needs admin rights, as its winget manifest says.
public enum InstallerElevation
{
    Unknown,
    Required,
    Prohibited,
    // The installer asks for admin rights itself.
    ElevatesSelf,
}
