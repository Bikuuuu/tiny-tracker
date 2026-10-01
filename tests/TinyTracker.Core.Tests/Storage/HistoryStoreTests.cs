using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.History;
using TinyTracker.Core.Storage;
using Xunit;

namespace TinyTracker.Core.Tests.Storage;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));

    private string HistoryPath => _folder.PathOf("history.json");

    public void Dispose() => _folder.Dispose();

    private HistoryStore Store()
    {
        var store = new HistoryStore(HistoryPath, _time);
        store.Load();
        return store;
    }

    private HistoryEntry Entry(string id, TimeSpan age, HistoryResult result = HistoryResult.Updated) => new()
    {
        Time = _time.GetUtcNow() - age,
        Id = id,
        Source = "winget",
        Name = id,
        Result = result,
        FromVersion = "1.0",
        ToVersion = "1.1",
    };

    [Fact]
    public void Add_KeepsNewestFirstAndPersists()
    {
        var store = Store();
        store.Add(Entry("Old", TimeSpan.FromHours(2)));
        store.Add(Entry("New", TimeSpan.Zero));
        store.Add(Entry("Middle", TimeSpan.FromHours(1)));
        Assert.Equal(["New", "Middle", "Old"], Store().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Entries_RoundTripAllFields()
    {
        var failed = Entry("Example.Editor", TimeSpan.Zero, HistoryResult.Failed) with { Reason = "The installer failed", Code = "0x8A150011" };
        Store().Add(failed);
        Assert.Equal(failed, Assert.Single(Store().Entries));
    }

    // An undone skip is no longer something that happened.
    [Fact]
    public void UndoneSkip_IsTakenOut()
    {
        var store = Store();
        store.Add(Entry("Example.Editor", TimeSpan.FromHours(1)));
        store.Add(Entry("Example.Editor", TimeSpan.Zero, HistoryResult.Skipped) with { ToVersion = "1.2" });
        store.RemoveSkip("example.editor", "winget", "1.2.0");
        Assert.Equal([HistoryResult.Updated], Store().Entries.Select(e => e.Result));
    }

    // Only while it's the app's newest entry, and only that version's.
    [Fact]
    public void UndoneSkip_LeavesWhatCameAfter_AndOtherSkips()
    {
        var store = Store();
        store.Add(Entry("Example.Editor", TimeSpan.FromHours(2), HistoryResult.Skipped) with { ToVersion = "1.2" });
        store.Add(Entry("Example.Editor", TimeSpan.FromHours(1), HistoryResult.Failed));
        store.Add(Entry("Contoso.Mail", TimeSpan.Zero, HistoryResult.Skipped) with { ToVersion = "1.2" });
        store.RemoveSkip("Example.Editor", "winget", "1.2");
        store.RemoveSkip("Contoso.Mail", "winget", "1.3");
        Assert.Equal(3, Store().Entries.Count);
    }

    [Fact]
    public void Load_DropsEntriesOlderThan90Days()
    {
        var store = Store();
        store.Add(Entry("Kept", TimeSpan.FromDays(89)));
        store.Add(Entry("Dropped", TimeSpan.FromDays(91)));
        Assert.Equal(["Kept"], Store().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Add_DropsEntriesThatAgedOut()
    {
        var store = Store();
        store.Add(Entry("Aging", TimeSpan.FromDays(89)));
        _time.Advance(TimeSpan.FromDays(2));
        store.Add(Entry("Fresh", TimeSpan.Zero));
        Assert.Equal(["Fresh"], store.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Clear_EmptiesAndPersists()
    {
        var store = Store();
        store.Add(Entry("A", TimeSpan.Zero));
        store.Clear(_time.GetUtcNow());
        Assert.Empty(store.Entries);
        Assert.Empty(Store().Entries);
    }

    [Fact]
    public void Clear_KeepsEntriesNewerThanWhatWasShown()
    {
        var store = Store();
        store.Add(Entry("Shown", TimeSpan.FromHours(1)));
        var shownUpTo = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(1));
        store.Add(Entry("Later", TimeSpan.Zero));
        store.Clear(shownUpTo);
        Assert.Equal(["Later"], Store().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Changed_IsRaisedOncePerSavedChange()
    {
        var store = Store();
        var raised = 0;
        store.Changed += (_, _) => raised++;
        store.Add(Entry("A", TimeSpan.Zero));
        store.Clear(_time.GetUtcNow());
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ClearThatRemovesNothing_RaisesNothing()
    {
        var store = Store();
        store.Add(Entry("Later", TimeSpan.Zero));
        var raised = 0;
        store.Changed += (_, _) => raised++;
        store.Clear(_time.GetUtcNow() - TimeSpan.FromHours(1));
        Assert.Equal(0, raised);
        Assert.Equal(["Later"], store.Entries.Select(e => e.Id));
    }

    [Fact]
    public void DamagedFileFoundWhenReadAgain_IsSetAside_AndTakenOnce()
    {
        File.WriteAllText(HistoryPath, "{ broken");
        var store = new HistoryStore(HistoryPath, _time);
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Load();
        Assert.False(store.TakeRecovered());
        store.Retry();
        Assert.False(store.Unreadable);
        Assert.Equal("{ broken", File.ReadAllText(HistoryPath + ".bak"));
        Assert.True(store.TakeRecovered());
        Assert.False(store.TakeRecovered());
    }

    [Fact]
    public void DamagedFileAtStartup_IsNotTakenAgain()
    {
        File.WriteAllText(HistoryPath, "{ broken");
        var store = new HistoryStore(HistoryPath, _time);
        Assert.True(store.Load());
        Assert.False(store.TakeRecovered());
    }

    [Fact]
    public void FailedAdd_RaisesNothing()
    {
        var blocker = _folder.PathOf("blocker");
        File.WriteAllText(blocker, "");
        var store = new HistoryStore(Path.Combine(blocker, "history.json"), _time);
        var raised = 0;
        store.Changed += (_, _) => raised++;
        Assert.ThrowsAny<IOException>(() => store.Add(Entry("A", TimeSpan.Zero)));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void RereadThatSucceeds_IsRaised_EvenWhenTheSaveFails()
    {
        Store().Add(Entry("A", TimeSpan.Zero));
        var store = new HistoryStore(HistoryPath, _time);
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Load();
        Directory.CreateDirectory(HistoryPath + ".tmp");
        var raised = 0;
        store.Changed += (_, _) => raised++;
        Assert.Throws<IOException>(() => store.Add(Entry("B", TimeSpan.Zero)));
        Assert.Equal(1, raised);
        Assert.False(store.Unreadable);
        Assert.Equal(["A"], store.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Retry_ReadsAnUnreadableFileAgain()
    {
        Store().Add(Entry("A", TimeSpan.Zero));
        var store = new HistoryStore(HistoryPath, _time);
        var raised = 0;
        store.Changed += (_, _) => raised++;
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Load();
            store.Retry();
            Assert.True(store.Unreadable);
            Assert.Equal(0, raised);
        }
        store.Retry();
        Assert.False(store.Unreadable);
        Assert.Equal(["A"], store.Entries.Select(e => e.Id));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void HandlerThatThrows_DoesNotFailTheAdd()
    {
        var store = Store();
        store.Changed += (_, _) => throw new InvalidOperationException("bug");
        store.Add(Entry("A", TimeSpan.Zero));
        Assert.Equal(["A"], Store().Entries.Select(e => e.Id));
    }

    [Fact]
    public void Entries_CanBeReadWhileASaveWaitsForTheFile()
    {
        var store = Store();
        store.Add(Entry("A", TimeSpan.FromHours(1)));
        using var held = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Exception? refused = null;
        // Off the pool: parallel tests can keep every pool thread busy past the rename retries.
        var adding = new Thread(() => refused = Record.Exception(() => store.Add(Entry("B", TimeSpan.Zero))));
        adding.Start();
        // The temp file stays while the save retries its rename, holding the lock.
        while (!File.Exists(HistoryPath + ".tmp") && adding.IsAlive) Thread.Sleep(1);
        Assert.True(adding.IsAlive, "The save ended before the read was tried.");
        var count = -1;
        var reader = new Thread(() => count = store.Entries.Count);
        reader.Start();
        Assert.True(reader.Join(TimeSpan.FromMilliseconds(150)), "The read waited for the save.");
        Assert.Equal(1, count);
        adding.Join();
        Assert.IsType<IOException>(refused);
    }

    [Fact]
    public void Entries_CannotBeChangedFromOutside()
    {
        var store = Store();
        store.Add(Entry("A", TimeSpan.Zero));
        Assert.Throws<NotSupportedException>(() => ((IList<HistoryEntry>)store.Entries)[0] = Entry("B", TimeSpan.Zero));
        Assert.Equal("A", Assert.Single(store.Entries).Id);
    }

    [Fact]
    public void FailedSave_LeavesHistoryUnchanged()
    {
        var blocker = _folder.PathOf("blocker");
        File.WriteAllText(blocker, "");
        var store = new HistoryStore(Path.Combine(blocker, "history.json"), _time);
        Assert.ThrowsAny<IOException>(() => store.Add(Entry("A", TimeSpan.Zero)));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void EmptyObject_LoadsEmptyHistory()
    {
        File.WriteAllText(HistoryPath, "{}");
        var store = new HistoryStore(HistoryPath, _time);
        Assert.False(store.Load());
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void CorruptFile_IsKeptAsBakAndHistoryStartsEmpty()
    {
        File.WriteAllText(HistoryPath, "{ broken");
        var store = new HistoryStore(HistoryPath, _time);
        Assert.True(store.Load());
        Assert.Empty(store.Entries);
        Assert.Equal("{ broken", File.ReadAllText(HistoryPath + ".bak"));
    }

    [Fact]
    public void LockedFile_IsUnreadableAndNotSavedOver()
    {
        Store().Add(Entry("A", TimeSpan.Zero));
        var saved = File.ReadAllText(HistoryPath);
        var store = new HistoryStore(HistoryPath, _time);
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.Load());
            Assert.True(store.Unreadable);
            Assert.Throws<IOException>(() => store.Add(Entry("B", TimeSpan.Zero)));
            Assert.Throws<IOException>(() => store.Clear(_time.GetUtcNow()));
        }
        Assert.Equal(saved, File.ReadAllText(HistoryPath));
    }

    [Fact]
    public void UnreadableFile_IsReadAgainOnTheNextAdd()
    {
        Store().Add(Entry("A", TimeSpan.FromHours(1)));
        var store = new HistoryStore(HistoryPath, _time);
        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None)) store.Load();
        store.Add(Entry("B", TimeSpan.Zero));
        Assert.False(store.Unreadable);
        Assert.Equal(["B", "A"], store.Entries.Select(e => e.Id));
    }
}
