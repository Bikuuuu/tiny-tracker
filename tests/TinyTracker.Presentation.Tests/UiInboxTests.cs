using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Logging;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests;

public sealed class UiInboxTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly FileLog _log;

    public UiInboxTests() => _log = new FileLog(_folder.PathOf("app.log"), new FakeTimeProvider(Now));

    public void Dispose() => _folder.Dispose();

    private string Log => File.Exists(_folder.PathOf("app.log")) ? File.ReadAllText(_folder.PathOf("app.log")) : "";

    [Fact]
    public void Event_RunsWhenTheUiThreadPumps()
    {
        using var inbox = new UiInbox(_ui.Post, _log);
        var seen = new List<int>();
        inbox.For<int>(seen.Add)(this, 7);
        Assert.Empty(seen);
        _ui.Pump();
        Assert.Equal([7], seen);
    }

    [Fact]
    public void EventAfterDispose_IsDropped()
    {
        var inbox = new UiInbox(_ui.Post, _log);
        var seen = new List<int>();
        var handler = inbox.For<int>(seen.Add);
        handler(this, 1);
        inbox.Dispose();
        handler(this, 2);
        _ui.Pump();
        Assert.Empty(seen);
    }

    [Fact]
    public void FailingHandler_IsLogged_NotThrown()
    {
        using var inbox = new UiInbox(_ui.Post, _log);
        inbox.For<int>(_ => throw new InvalidOperationException("broken row"))(this, 1);
        _ui.Pump();
        Assert.Contains("broken row", Log);
    }

    [Fact]
    public void FailingPost_IsLogged_NotThrown()
    {
        using var inbox = new UiInbox(_ => throw new InvalidOperationException("dispatcher gone"), _log);
        inbox.For<int>(_ => { })(this, 1);
        Assert.Contains("dispatcher gone", Log);
    }
}
