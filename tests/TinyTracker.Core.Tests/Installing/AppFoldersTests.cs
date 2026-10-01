using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

// Close & update closes processes only from the app's own folder, never from Windows' own ones (spec §6.3).
public class AppFoldersTests
{
    private static readonly AppFolders Folders = new(
        [@"C:\Windows", @"D:\Home\Example\AppData\Local\Temp", @"C:\ProgramData\Package Cache"],
        [
            @"C:\ProgramData", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\Program Files (x86)\Common Files", @"D:\Home", @"D:\Home\Example",
            @"D:\Home\Example\AppData", @"D:\Home\Example\AppData\Roaming", @"D:\Home\Example\AppData\Local", @"D:\Home\Example\AppData\Local\Programs",
            @"D:\Home\Example\Desktop", @"D:\Home\Example\Documents", @"D:\Home\Example\Downloads",
        ],
        @"C:\ProgramData",
        @"C:\Program Files\Tiny Tracker");

    [Theory]
    [InlineData(@"C:\Program Files\Example Editor", @"C:\Program Files\Example Editor")]
    [InlineData(@"  ""C:\Program Files\Example Editor\""  ", @"C:\Program Files\Example Editor")]
    [InlineData(@"C:\Program Files\Example Editor\bin\..", @"C:\Program Files\Example Editor")]
    [InlineData(@"C:\ProgramData\Example\Editor", @"C:\ProgramData\Example\Editor")]
    [InlineData(@"D:\Home\Example\AppData\Local\Programs\Example Chat", @"D:\Home\Example\AppData\Local\Programs\Example Chat")]
    [InlineData(@"D:\Games\Example", @"D:\Games\Example")]
    public void InstallLocation_IsTheFolder(string location, string folder) => Assert.Equal(folder, Folders.Choose(location, null));

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"c:\program files (x86)\")]
    [InlineData(@"D:\Home\Example")]
    [InlineData(@"D:\Home\Example\AppData\Local\Programs")]
    [InlineData(@"D:\Home\Example\Documents")]
    [InlineData(@"C:\Program Files\Tiny Tracker")]
    [InlineData(@"C:\Program Files\Tiny Tracker\update")]
    [InlineData(@"C:\Program Files (x86)\Common Files")]
    [InlineData(@"D:\Home\Example\Downloads")]
    [InlineData(@"D:\Home\Example\AppData\Local\Temp\setup-1234")]
    [InlineData(@"C:\ProgramData\Package Cache\{EXAMPLE-1234}")]
    [InlineData(@"Example Editor")]
    [InlineData("")]
    public void FoldersThatAreNeverUsed_AreRefused(string location) => Assert.Null(Folders.Choose(location, null));

    [Fact]
    public void FolderThatHoldsTinyTracker_IsRefused() =>
        Assert.Null(new AppFolders([@"C:\Windows"], [], @"C:\ProgramData", @"D:\Apps\Tiny Tracker").Choose(@"D:\Apps", null));

    [Theory]
    [InlineData(@"C:\Program Files\Example\editor.exe,0", @"C:\Program Files\Example")]
    [InlineData(@"""C:\Program Files\Example Editor\editor.exe""", @"C:\Program Files\Example Editor")]
    public void WithoutAnInstallLocation_TheIconsFolderIsUsed(string icon, string folder)
    {
        Assert.Equal(folder, Folders.Choose(null, icon));
        Assert.Equal(folder, Folders.Choose(@"C:\Program Files", icon));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Package Cache\{EXAMPLE-1234}\setup.exe,0")]
    [InlineData(@"C:\Windows\Installer\{EXAMPLE-1234}\icon.exe")]
    [InlineData(@"C:\Program Files\editor.exe")]
    [InlineData(@"D:\Home\Example\Downloads\ExampleSetup.exe")]
    [InlineData(@"D:\Home\Example\AppData\Local\Temp\setup-1234\setup.exe")]
    [InlineData(@"editor.exe")]
    [InlineData(null)]
    public void IconsInWindowsOrProgramDataOrAnUnusableFolder_AreNotUsed(string? icon) => Assert.Null(Folders.Choose(null, icon));

    [Theory]
    [InlineData(@"C:\Program Files\Example", @"C:\Program Files\Example\editor.exe", true)]
    [InlineData(@"C:\Program Files\Example", @"c:\program files\example\bin\helper.exe", true)]
    [InlineData(@"C:\Program Files\Example", @"C:\Program Files\Example Editor\editor.exe", false)]
    [InlineData(@"C:\Program Files\Example", @"C:\Program Files\editor.exe", false)]
    [InlineData(@"C:\Program Files\Example", @"C:\Program Files\Example\..\Other\editor.exe", false)]
    public void Holds_MeansInsideTheFolder(string folder, string path, bool holds) => Assert.Equal(holds, AppFolders.Holds(folder, path));

    // The helper runs a self-update only from Tiny Tracker's own folder (spec §8).
    [Theory]
    [InlineData(@"C:\Program Files\Tiny Tracker", @"C:\Program Files\Tiny Tracker\TinyTracker.Helper.exe", true)]
    [InlineData(@"C:\Program Files\Tiny Tracker", @"c:\program files\tiny tracker\TinyTracker.Helper.exe", true)]
    [InlineData(@"C:\Program Files\Tiny Tracker", @"C:\Program Files\Tiny Tracker\update\TinyTracker.Helper.exe", false)]
    [InlineData(@"C:\Program Files\Tiny Tracker", @"C:\Program Files\Other\TinyTracker.Helper.exe", false)]
    [InlineData(@"C:\Program Files\Tiny Tracker", @"C:\Program Files\Tiny Tracker Old\TinyTracker.Helper.exe", false)]
    [InlineData(@"C:\Program Files\Tiny Tracker", @"C:\Program Files\TinyTracker.Helper.exe", false)]
    public void DirectlyIn_MeansRightInTheFolder(string folder, string path, bool directly) => Assert.Equal(directly, AppFolders.DirectlyIn(folder, path));
}
