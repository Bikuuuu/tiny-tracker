using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Storage;

// "Tiny Tracker apps.json" (spec §4.5, §8).
public sealed class AppListBackupTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string BackupPath => _folder.PathOf(AppListBackup.FileName);

    // Kept for the notice's Details.
    private const int AccessDenied = unchecked((int)0x80070005);

    // Update apps automatically off: an app is on Auto only by its own choice.
    private static readonly AppSettings SwitchOff = new();

    private AppList? LoadText(string text)
    {
        File.WriteAllText(BackupPath, text);
        return AppListBackup.Load(BackupPath);
    }

    [Fact]
    public void Save_KeepsTheSwitch_AndEachWinGetAppsIdNameAndAuto_AndNothingElse()
    {
        AppListBackup.Save(BackupPath, [
            new TrackedApp { Id = "Mozilla.Firefox", Source = "winget", Name = "Mozilla Firefox", AutoChoice = true, SkippedVersion = "131.0" },
            new TrackedApp { Id = "9NBLGGH4NNS1", Source = "msstore", Name = "App Installer" },
        ], SwitchOff);
        Assert.Equal("""
            {
              "format": 1,
              "autoUpdateApps": false,
              "apps": [
                {
                  "id": "Mozilla.Firefox",
                  "name": "Mozilla Firefox",
                  "auto": true
                }
              ]
            }
            """.ReplaceLineEndings("\n"), File.ReadAllText(BackupPath).ReplaceLineEndings("\n"));
    }

    // winget's names can hold anything: a file Back up writes always restores (spec §4.5).
    [Fact]
    public void Save_CleansNames_SoTheFileRestores()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        AppListBackup.Save(BackupPath, [
            new TrackedApp { Id = "Example.Editor", Source = "winget", Name = "Tabs\tand\nlines" + (char)0x7F + "and" + (char)0x85 + "more" },
            new TrackedApp { Id = "Example.Viewer", Source = "winget", Name = new string('x', 255) + smile },
            new TrackedApp { Id = "Example.Player", Source = "winget", Name = new string('z', 254) + smile + "tail" },
        ], SwitchOff);
        var apps = AppListBackup.Load(BackupPath)?.Apps;
        Assert.NotNull(apps);
        Assert.Equal(["Tabs and lines and more", new string('x', 255), new string('z', 254) + smile], apps.Select(a => a.Name));
    }

    // An id Restore would refuse, from a settings.json edited by hand, is left out rather than spoiling the file.
    [Fact]
    public void Save_LeavesOutIdsRestoreRefuses()
    {
        AppListBackup.Save(BackupPath, [new TrackedApp { Id = "NoDot", Source = "winget" }, new TrackedApp { Id = "Example.Editor", Source = "winget" }], SwitchOff);
        Assert.Equal(["Example.Editor"], AppListBackup.Load(BackupPath)!.Apps.Select(a => a.Id));
    }

    [Fact]
    public void Load_ReadsWhatSaveWrote()
    {
        AppListBackup.Save(BackupPath, [new TrackedApp { Id = "VideoLAN.VLC", Source = "winget", Name = "VLC \"media\" player – Café" }], SwitchOff);
        Assert.Equal([new BackedUpApp { Id = "VideoLAN.VLC", Name = "VLC \"media\" player – Café", Auto = false }], AppListBackup.Load(BackupPath)!.Apps);
    }

    // Whether each app is on Auto, the switch's apps too (spec §4.5).
    [Fact]
    public void Save_SaysWhetherEachAppIsOnAuto()
    {
        AppListBackup.Save(BackupPath, [
            new TrackedApp { Id = "Example.Editor", Source = "winget" },
            new TrackedApp { Id = "Example.Viewer", Source = "winget", AutoChoice = false },
        ], new AppSettings { AutoUpdateApps = true });
        Assert.Equal([true, false], AppListBackup.Load(BackupPath)!.Apps.Select(a => a.Auto));
    }

    // So Restore knows whether the apps were on Auto through the switch (spec §4.5).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Save_SaysWhetherTheSwitchWasOn(bool on)
    {
        AppListBackup.Save(BackupPath, [new TrackedApp { Id = "Example.Editor", Source = "winget" }], new AppSettings { AutoUpdateApps = on });
        Assert.Equal(on, AppListBackup.Load(BackupPath)!.AutoUpdateApps);
    }

    // 0.9's files have no switch.
    [Fact]
    public void Load_OfAFileFromBeforeTheSwitch_SaysItWasOff() =>
        Assert.False(LoadText("""{ "format": 1, "apps": [ { "id": "Example.Editor", "auto": true } ] }""")!.AutoUpdateApps);

    // The old file stays until the new one is complete.
    [Fact]
    public void Save_LeavesNoTempFile()
    {
        AppListBackup.Save(BackupPath, [], SwitchOff);
        Assert.Equal([BackupPath], Directory.GetFileSystemEntries(_folder.Root));
    }

    [Fact]
    public void SaveThatFails_Throws_AndLeavesNoTempFile()
    {
        Directory.CreateDirectory(BackupPath);
        Assert.Equal(AccessDenied, Assert.ThrowsAny<IOException>(() => AppListBackup.Save(BackupPath, [], SwitchOff)).HResult);
        Assert.Equal([BackupPath], Directory.GetFileSystemEntries(_folder.Root));
    }

    // Editors may save it with a BOM.
    [Fact]
    public void Load_AcceptsABom()
    {
        File.WriteAllText(BackupPath, """{ "format": 1, "apps": [ { "id": "Example.Editor" } ] }""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(["Example.Editor"], AppListBackup.Load(BackupPath)!.Apps.Select(a => a.Id));
    }

    [Fact]
    public void FileThatCantBeRead_Throws()
    {
        Assert.ThrowsAny<IOException>(() => AppListBackup.Load(BackupPath));
        Directory.CreateDirectory(BackupPath);
        Assert.Equal(AccessDenied, Assert.ThrowsAny<IOException>(() => AppListBackup.Load(BackupPath)).HResult);
    }

    [Fact]
    public void Load_KeepsTheFirstOfEachId() =>
        Assert.Equal(["Mozilla.Firefox"], LoadText("""{ "format": 1, "apps": [ { "id": "Mozilla.Firefox" }, { "id": "MOZILLA.FIREFOX", "auto": true } ] }""")!.Apps.Select(a => a.Id));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ }")]
    [InlineData("""{ "format": 2, "apps": [] }""")]
    [InlineData("""{ "format": 1 }""")]
    [InlineData("""{ "format": 1, "apps": [ { "name": "No id" } ] }""")]
    [InlineData("""{ "format": 1, "apps": [ { "id": "not an id" } ] }""")]
    [InlineData("""{ "format": 1, "apps": [ { "id": "Example.Editor", "name": null } ] }""")]
    [InlineData("""{ "format": 1, "apps": null }""")]
    [InlineData("""{ "format": 1, "autoUpdateApps": null, "apps": [] }""")]
    [InlineData("""{ "format": 1, "apps": [ null ] }""")]
    [InlineData("""{ "format": 1, "apps": [ { "id": "Example.Editor", "name": "Two\nlines" } ] }""")]
    public void OtherFiles_AreRefused(string text) => Assert.Null(LoadText(text));

    [Fact]
    public void LongName_IsRefused() =>
        Assert.Null(LoadText($$"""{ "format": 1, "apps": [ { "id": "Example.Editor", "name": "{{new string('x', 257)}}" } ] }"""));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void FileOverOneMegabyte_IsRefused(int over, bool loads)
    {
        const string list = """{ "format": 1, "apps": [] }""";
        File.WriteAllText(BackupPath, list + new string(' ', AppListBackup.MaxBytes - list.Length + over));
        Assert.Equal(loads, AppListBackup.Load(BackupPath) is not null);
    }
}
