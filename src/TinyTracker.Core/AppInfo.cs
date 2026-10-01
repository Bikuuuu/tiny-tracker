namespace TinyTracker.Core;

public static class AppInfo
{
    public const string Name = "Tiny Tracker";
    public const string InstanceKey = "TinyTracker";
    // Its uninstall entry, as winget names it; UninstallKey ends it for any install.
    public const string UninstallKey = "TinyTracker_is1";
    public const string LocalId = @"ARP\Machine\X64\" + UninstallKey;
    public const string RepositoryUrl = "https://github.com/Bikuuuu/tiny-tracker";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";
    public const string TipUrl = "https://ko-fi.com/Bikuuuu";
}
