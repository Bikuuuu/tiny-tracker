using System.Text.Json.Nodes;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Storage;

public sealed class SettingsStoreTests : IDisposable
{
    private static readonly DateTimeOffset Seen = new(2026, 9, 25, 8, 30, 0, TimeSpan.Zero);
    private readonly TempFolder _folder = new();

    private string SettingsPath => _folder.PathOf("settings.json");

    public void Dispose() => _folder.Dispose();

    private SettingsStore Loaded(out bool recovered)
    {
        var store = new SettingsStore(SettingsPath);
        recovered = store.Load();
        return store;
    }

    [Fact]
    public void MissingFile_LoadsDefaultsWithoutCreatingAnything()
    {
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(new AppSettings(), store.Current.Settings);
        Assert.Empty(store.Current.Apps);
        Assert.Empty(Directory.GetFileSystemEntries(_folder.Root));
    }

    [Fact]
    public void MissingFolder_LoadsDefaults()
    {
        var store = new SettingsStore(_folder.PathOf(Path.Combine("missing", "settings.json")));
        Assert.False(store.Load());
        Assert.Empty(store.Current.Apps);
    }

    [Fact]
    public void Update_RoundTripsEverything()
    {
        var app = new TrackedApp
        {
            Id = "Mozilla.Firefox",
            Source = "winget",
            Name = "Mozilla Firefox",
            AutoChoice = true,
            SkippedVersion = "130.0",
            Offer = new Offer { Version = "131.0", FirstSeen = Seen, ReleaseDate = new DateOnly(2026, 9, 20), LastAutoAttempt = Seen.AddHours(1), Phantom = true },
        };
        var settings = new AppSettings { CheckIntervalHours = 12, SilentMode = true, AutoUpdateApps = true, SpeedLimitEnabled = true, SpeedLimitKBps = 900, OpenShortcut = null };
        new SettingsStore(SettingsPath).Update(f => f with { Settings = settings, Apps = [app], TipsShown = ["adminFallback"] });

        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(settings, store.Current.Settings);
        Assert.Equal(app, Assert.Single(store.Current.Apps));
        Assert.Equal(["adminFallback"], store.Current.TipsShown);
    }

    [Fact]
    public void SelfUpdateBook_RoundTrips()
    {
        var book = new SelfUpdateBook
        {
            Announced = "0.2.0",
            AttemptedVersion = "0.2.0",
            AttemptedAt = Seen,
            FailedVersion = "0.2.0",
            Note = new SelfUpdateNote { From = "0.1.0", To = "0.2.0", Automatic = true, StartedAt = Seen.AddMinutes(1) },
        };
        new SettingsStore(SettingsPath).Update(f => f with { SelfUpdate = book });
        Assert.Equal(book, Loaded(out _).Current.SelfUpdate);
    }

    // What 0.1.0 wrote has no selfUpdate key.
    [Fact]
    public void FileWithoutSelfUpdate_LoadsAnEmptyBook()
    {
        File.WriteAllText(SettingsPath, """{ "version": 1, "apps": [] }""");
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(new SelfUpdateBook(), store.Current.SelfUpdate);
    }

    [Fact]
    public void Save_LeavesNoTempFile()
    {
        new SettingsStore(SettingsPath).Update(f => f with { Settings = new AppSettings() });
        Assert.Equal(["settings.json"], Directory.GetFiles(_folder.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_WritesReadableJsonInUtc()
    {
        new SettingsStore(SettingsPath).Update(f => f with
        {
            Apps = [new TrackedApp { Id = "A", Source = "winget", Offer = new Offer { Version = "2", FirstSeen = Seen } }],
        });
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"checkIntervalHours\": 6", json);
        Assert.Contains("\"modifiers\": \"Shift, Windows\"", json);
        Assert.Contains("\"firstSeen\": \"2026-09-25T08:30:00+00:00\"", json);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "apps": "x" }""")]
    [InlineData("""{ "apps": [{ "source": "winget" }] }""")]
    [InlineData("""{ "settings": null }""")]
    [InlineData("""{ "settings": { "openShortcut": { "modifiers": "Hyper", "key": 85 } } }""")]
    [InlineData("""{ "apps": [{ "id": "A", "source": "winget", "offer": { "version": "1", "firstSeen": "yesterday" } }] }""")]
    [InlineData("""{ "settings": { "checkIntervalHours": 99999999999 } }""")]
    public void CorruptFile_IsKeptAsBakAndDefaultsLoad(string content)
    {
        File.WriteAllText(SettingsPath, content);
        var store = Loaded(out var recovered);
        Assert.True(recovered);
        Assert.Equal(new AppSettings(), store.Current.Settings);
        Assert.Equal(content, File.ReadAllText(SettingsPath + ".bak"));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void BinaryGarbage_IsTreatedAsCorrupt()
    {
        File.WriteAllBytes(SettingsPath, [0xFF, 0xFE, 0x00, 0xC3, 0x28]);
        Loaded(out var recovered);
        Assert.True(recovered);
    }

    [Fact]
    public void SecondCorruption_ReplacesTheOnlyBackup()
    {
        File.WriteAllText(SettingsPath, "first");
        Loaded(out _);
        File.WriteAllText(SettingsPath, "second");
        Loaded(out _);
        Assert.Equal("second", File.ReadAllText(SettingsPath + ".bak"));
        Assert.Single(Directory.GetFiles(_folder.Root));
    }

    [Fact]
    public void StaleTempFile_IsRemovedOnLoad()
    {
        File.WriteAllText(SettingsPath + ".tmp", "half written");
        Loaded(out _);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void HandEditedFile_WithCommentsTrailingCommasAndUnknownFields_Loads()
    {
        File.WriteAllText(SettingsPath, """
            {
              // edited by hand
              "settings": { "checkIntervalHours": 12, "futureOption": true, },
              "apps": [ { "id": "Mozilla.Firefox", "source": "winget", "auto": true }, ],
            }
            """);
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(12, store.Current.Settings.CheckIntervalHours);
        Assert.True(Assert.Single(store.Current.Apps).AutoChoice);
    }

    // From before Update apps automatically (spec §5.4): Auto on becomes the app's own choice, off follows the switch, which
    // starts off; a choice already there wins, and the old key isn't written again.
    [Fact]
    public void OldAutoKey_BecomesTheAppsOwnChoice()
    {
        File.WriteAllText(SettingsPath, """
            { "apps": [ { "id": "Mozilla.Firefox", "source": "winget", "auto": true }, { "id": "VideoLAN.VLC", "source": "winget", "auto": false },
              { "id": "Notepad++.Notepad++", "source": "winget", "auto": true, "autoChoice": false } ] }
            """);
        var store = Loaded(out _);
        Assert.Equal([true, null, false], store.Current.Apps.Select(a => a.AutoChoice));
        Assert.False(store.Current.Settings.AutoUpdateApps);
        store.Update(f => f with { TipsShown = ["adminFallback"] });
        Assert.DoesNotContain("\"auto\"", File.ReadAllText(SettingsPath));
        Assert.Equal([true, null, false], Loaded(out _).Current.Apps.Select(a => a.AutoChoice));
    }

    [Fact]
    public void Load_NormalizesOutOfRangeValuesAndBadApps()
    {
        File.WriteAllText(SettingsPath, """
            {
              "settings": { "checkIntervalHours": 5, "autoInstallWaitDays": 2, "speedLimitKBps": 0 },
              "apps": [
                { "id": "Mozilla.Firefox", "source": "winget" },
                { "id": "mozilla.firefox", "source": "WinGet" },
                { "id": " ", "source": "winget" }
              ]
            }
            """);
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(6, store.Current.Settings.CheckIntervalHours);
        Assert.Equal(0, store.Current.Settings.AutoInstallWaitDays);
        Assert.Equal(17500, store.Current.Settings.SpeedLimitKBps);
        Assert.Equal("Mozilla.Firefox", Assert.Single(store.Current.Apps).Id);
    }

    [Fact]
    public void TipsShown_LoseBlanksAndRepeats()
    {
        File.WriteAllText(SettingsPath, """{ "tipsShown": [ "adminFallback", " ", "adminFallback" ] }""");
        Assert.Equal(["adminFallback"], Loaded(out _).Current.TipsShown);
    }

    // New apps (spec §4.3): once the first list is noted, every app tracked from winget is known too.
    [Fact]
    public void KnownApps_TakeInTrackedApps_OnceTheFirstListIsNoted()
    {
        File.WriteAllText(SettingsPath, """
            { "apps": [ { "id": "VideoLAN.VLC", "source": "winget" }, { "id": "9NBLGGH4NNS1", "source": "msstore" } ],
              "knownApps": [ "Mozilla.Firefox", " ", "MOZILLA.FIREFOX", "videolan.vlc" ] }
            """);
        Assert.Equal(["Mozilla.Firefox", "videolan.vlc"], Loaded(out _).Current.KnownApps);
    }

    [Fact]
    public void KnownApps_StayUnset_UntilTheFirstList()
    {
        var store = Loaded(out _);
        store.Update(f => f with { Apps = [new TrackedApp { Id = "VideoLAN.VLC", Source = "winget" }] });
        Assert.Null(store.Current.KnownApps);
        store.Update(f => f with { KnownApps = ["Mozilla.Firefox"] });
        Assert.Equal(["Mozilla.Firefox", "VideoLAN.VLC"], Loaded(out _).Current.KnownApps);
    }

    // Tracked at any time: an app that stops being tracked stays known.
    [Fact]
    public void KnownApps_KeepAppsNoLongerTracked()
    {
        var store = Loaded(out _);
        store.Update(f => f with { Apps = [new TrackedApp { Id = "VideoLAN.VLC", Source = "winget" }], KnownApps = [] });
        store.Update(f => f with { Apps = [] });
        Assert.Equal(["VideoLAN.VLC"], Loaded(out _).Current.KnownApps);
    }

    [Fact]
    public void PascalCaseKeys_Load()
    {
        File.WriteAllText(SettingsPath, """{ "Settings": { "CheckIntervalHours": 12 }, "Apps": [ { "Id": "A", "Source": "winget", "Auto": true } ] }""");
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(12, store.Current.Settings.CheckIntervalHours);
        Assert.True(Assert.Single(store.Current.Apps).AutoChoice);
    }

    [Fact]
    public void FailedSave_KeepsTheCurrentFile()
    {
        var blocker = _folder.PathOf("blocker");
        File.WriteAllText(blocker, "");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.ThrowsAny<IOException>(() => store.Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] }));
        Assert.Empty(store.Current.Apps);
    }

    [Fact]
    public void EmptyObject_LoadsDefaults()
    {
        File.WriteAllText(SettingsPath, "{}");
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(1, store.Current.Version);
        Assert.Equal(new AppSettings(), store.Current.Settings);
        Assert.Empty(store.Current.Apps);
    }

    [Fact]
    public void MissingKeys_KeepSpecDefaults()
    {
        File.WriteAllText(SettingsPath, """{ "settings": { "checkIntervalHours": 12 }, "apps": [ { "id": "A", "source": "winget" } ] }""");
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(new AppSettings { CheckIntervalHours = 12 }, store.Current.Settings);
        Assert.Equal("", Assert.Single(store.Current.Apps).Name);
    }

    // A file from before the levels keeps notifications off, and the next save writes the level instead.
    [Fact]
    public void OldNotificationSwitch_LoadsAsALevel_AndIsntWrittenAgain()
    {
        File.WriteAllText(SettingsPath, """{ "settings": { "showNotifications": false } }""");
        var store = Loaded(out _);
        Assert.Equal(NotificationLevel.Off, store.Current.Settings.Notifications);
        store.Update(f => f with { TipsShown = ["adminFallback"] });
        var saved = File.ReadAllText(SettingsPath);
        Assert.Contains("\"notifications\": \"Off\"", saved);
        Assert.DoesNotContain("showNotifications", saved);
    }

    // A level this build doesn't know, from a hand edit or a newer version, reads as All, and the file isn't set aside.
    [Theory]
    [InlineData("\"Sometimes\"")]
    [InlineData("7")]
    [InlineData("{ \"level\": 1 }")]
    public void UnknownNotificationLevel_ReadsAsAll_AndKeepsTheApps(string level)
    {
        File.WriteAllText(SettingsPath, $$"""{ "settings": { "notifications": {{level}}, "checkIntervalHours": 12 }, "apps": [ { "id": "A", "source": "winget" } ] }""");
        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal((NotificationLevel.All, 12), (store.Current.Settings.Notifications, store.Current.Settings.CheckIntervalHours));
        Assert.Single(store.Current.Apps);
    }

    [Theory]
    [InlineData("\"needsme\"", NotificationLevel.NeedsMe)]
    [InlineData("2", NotificationLevel.Failures)]
    public void KnownNotificationLevel_ReadsByNameOrNumber(string level, NotificationLevel read)
    {
        File.WriteAllText(SettingsPath, $$"""{ "settings": { "notifications": {{level}} } }""");
        Assert.Equal(read, Loaded(out _).Current.Settings.Notifications);
    }

    [Fact]
    public void AppsArrayDeletedByHand_Loads()
    {
        new SettingsStore(SettingsPath).Update(f => f with
        {
            Settings = f.Settings with { CheckIntervalHours = 12 },
            Apps = [new TrackedApp { Id = "A", Source = "winget" }],
        });
        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        json.Remove("apps");
        File.WriteAllText(SettingsPath, json.ToJsonString());

        var store = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(12, store.Current.Settings.CheckIntervalHours);
        Assert.Empty(store.Current.Apps);
    }

    [Fact]
    public async Task ParallelUpdates_AreAllKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SettingsStore(SettingsPath);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(
            () => store.Update(f => f with { Apps = [.. f.Apps, new TrackedApp { Id = $"App.{i}", Source = "winget" }] }), ct)));

        var reloaded = Loaded(out var recovered);
        Assert.False(recovered);
        Assert.Equal(20, reloaded.Current.Apps.Count);
    }

    [Fact]
    public void Update_ReturnsAndKeepsTheNormalizedFile()
    {
        var store = new SettingsStore(SettingsPath);
        var saved = store.Update(f => f with { Settings = f.Settings with { CheckIntervalHours = 7 } });
        Assert.Equal(6, saved.Settings.CheckIntervalHours);
        Assert.Same(saved, store.Current);
    }

    [Fact]
    public void ChangeThatReturnsTheSameFile_SavesNothing()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] });
        File.Delete(SettingsPath);
        var kept = store.Update(f => f);
        Assert.False(File.Exists(SettingsPath));
        Assert.Same(store.Current, kept);
        Assert.Equal("A", Assert.Single(kept.Apps).Id);
    }

    [Fact]
    public void LockedFile_IsUnreadableAndNotSavedOver()
    {
        new SettingsStore(SettingsPath).Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] });
        var saved = File.ReadAllText(SettingsPath);
        var store = new SettingsStore(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.Load());
            Assert.True(store.Unreadable);
            Assert.Empty(store.Current.Apps);
            Assert.Throws<IOException>(() => store.Update(f => f with { Apps = [new TrackedApp { Id = "B", Source = "winget" }] }));
        }
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void UnreadableFile_IsReadAgainOnTheNextUpdate()
    {
        new SettingsStore(SettingsPath).Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] });
        var store = new SettingsStore(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Load();
        var saved = store.Update(f => f with { Apps = [.. f.Apps, new TrackedApp { Id = "B", Source = "winget" }] });
        Assert.False(store.Unreadable);
        Assert.Equal(["A", "B"], saved.Apps.Select(a => a.Id));
    }

    [Fact]
    public void FolderInPlaceOfTheFile_IsUnreadable()
    {
        Directory.CreateDirectory(SettingsPath);
        var store = new SettingsStore(SettingsPath);
        Assert.False(store.Load());
        Assert.True(store.Unreadable);
        Assert.Throws<IOException>(() => store.Update(f => f));
        Assert.True(Directory.Exists(SettingsPath));
    }

    [Fact]
    public void BrieflyLockedFile_IsSavedAfterARetry()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(f => f with { Settings = new AppSettings() });
        var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        // Its own thread: parallel tests can keep every pool thread busy past the rename retries.
        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            held.Dispose();
        });
        release.Start();
        store.Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] });
        release.Join();
        Assert.Equal("A", Assert.Single(Loaded(out _).Current.Apps).Id);
    }

    [Fact]
    public void Current_CanBeReadWhileASaveWaitsForTheFile()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] });
        using var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Exception? refused = null;
        // Off the pool: parallel tests can keep every pool thread busy past the rename retries.
        var saving = new Thread(() => refused = Record.Exception(() => store.Update(f => f with { Apps = [] })));
        saving.Start();
        // The temp file stays while the save retries its rename, holding the lock.
        while (!File.Exists(SettingsPath + ".tmp") && saving.IsAlive) Thread.Sleep(1);
        Assert.True(saving.IsAlive, "The save ended before the read was tried.");
        var apps = -1;
        var reader = new Thread(() => apps = store.Current.Apps.Count);
        reader.Start();
        Assert.True(reader.Join(TimeSpan.FromMilliseconds(150)), "The read waited for the save.");
        Assert.Equal(1, apps);
        saving.Join();
        Assert.IsType<IOException>(refused);
    }

    [Fact]
    public void LockThatDoesNotClear_FailsTheSaveAndKeepsTheFile()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(f => f with { Apps = [new TrackedApp { Id = "A", Source = "winget" }] });
        var saved = File.ReadAllText(SettingsPath);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => store.Update(f => f with { Apps = [] }));
        Assert.Equal(saved, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.Equal("A", Assert.Single(store.Current.Apps).Id);
    }
}
