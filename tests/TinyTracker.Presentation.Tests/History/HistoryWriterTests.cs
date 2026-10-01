using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.History;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation.History;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.History;

public sealed class HistoryWriterTests : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly HistoryStore _store;
    private readonly FileLog _log;
    private readonly HistoryWriter _writer;

    public HistoryWriterTests()
    {
        _store = new HistoryStore(_folder.PathOf("history.json"), _time);
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new HistoryWriter(_store, _log, _ui.Post);
    }

    // Writes a test left queued land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10));
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HistoryEntry Entry(string id, TimeSpan age) => new()
    {
        Time = Now - age,
        Id = id,
        Source = "winget",
        Name = Name(id),
        Result = HistoryResult.Updated,
        FromVersion = "1.0",
        ToVersion = "1.1",
    };

    [Fact]
    public async Task Writes_AreSavedInTheOrderAsked()
    {
        _writer.Add(Entry("Example.Editor", TimeSpan.Zero));
        _writer.Clear(Now, _ => { });
        _writer.Add(Entry("Example.Paint", TimeSpan.FromHours(1)));
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(["Example.Paint"], _store.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task ClearThatCantBeSaved_IsRefused_AndKeepsTheEntries()
    {
        _store.Add(Entry("Example.Editor", TimeSpan.Zero));
        Directory.CreateDirectory(_folder.PathOf("history.json.tmp"));
        Exception? refused = null;
        _writer.Clear(Now, error => refused = error);
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _ui.Pump();
        Assert.IsType<IOException>(refused, exactMatch: false);
        Assert.Single(_store.Entries);
        Assert.Contains("WARN History not saved", File.ReadAllText(_folder.PathOf("app.log")));
    }
}
