namespace TinyTracker.Core.Tracking;

// One installed package as a check saw it. AvailableVersion is null when no update is offered.
public sealed record PackageSnapshot(
    string Id,
    string Source,
    string Name,
    string InstalledVersion,
    string? AvailableVersion,
    string Publisher = "",
    string? ReleaseNotesUrl = null,
    // winget's id for the uninstall entry or MSIX package; the app's icon comes from it.
    string LocalId = "",
    InstallScope Scope = InstallScope.Unknown,
    InstallerElevation Elevation = InstallerElevation.Unknown,
    // The offered version's release notes as winget has them, in memory only.
    string? ReleaseNotes = null)
{
    // Read once: a copy made with other notes reads them again.
    public string? ReleaseNotes
    {
        get;
        init
        {
            field = value;
            IsSecurityFix = SecurityNotes.Mention(value);
        }
    } = ReleaseNotes;

    public bool IsSecurityFix { get; private init; } = SecurityNotes.Mention(ReleaseNotes);

    // An update run without admin rights would show a UAC prompt (spec §6.3). When winget can't tell, it counts as one.
    public bool AsksForAdmin => Elevation switch
    {
        InstallerElevation.Prohibited => false,
        InstallerElevation.Required or InstallerElevation.ElevatesSelf => true,
        _ => Scope != InstallScope.User,
    };
}
