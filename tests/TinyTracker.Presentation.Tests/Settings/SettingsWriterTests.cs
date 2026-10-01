using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation.Settings;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Settings;

public sealed class SettingsWriterTests : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly SettingsStore _store;
    private readonly FileLog _log;
    private readonly SettingsWriter _writer;

    public SettingsWriterTests()
    {
        _store = new SettingsStore(_folder.PathOf("settings.json"));
        var time = new FakeTimeProvider(Now);
        _log = new FileLog(_folder.PathOf("app.log"), time);
        _writer = new SettingsWriter(_store, _log, _ui.Post);
    }

    // Saves a test left queued land, and later log lines are dropped, before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10));
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changes_AreSavedInOrder_OffTheCallersThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var insideUpdate = false;
        var ranInline = false;
        for (var i = 1; i <= 20; i++)
        {
            var hours = i % 2 == 0 ? 12 : 24;
            Volatile.Write(ref insideUpdate, true);
            _writer.Update(file =>
            {
                // The test's own pool thread may run later saves once it awaits; only an inline save sees it inside Update.
                if (Environment.CurrentManagedThreadId == caller && Volatile.Read(ref insideUpdate)) ranInline = true;
                return file with { Settings = file.Settings with { CheckIntervalHours = hours } };
            });
            Volatile.Write(ref insideUpdate, false);
        }
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(12, _store.Current.Settings.CheckIntervalHours);
        Assert.False(ranInline);
    }

    // No error: saved.
    [Fact]
    public async Task Result_ComesBackThroughThePost()
    {
        var answered = false;
        Exception? error = new InvalidOperationException("no answer yet");
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = true } }, e => (answered, error) = (true, e));
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(answered);
        _ui.Pump();
        Assert.True(answered);
        Assert.Null(error);
    }

    // The error comes back, for the notice's code.
    [Fact]
    public async Task FileThatCantBeSaved_IsRefused()
    {
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        Exception? error = null;
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = true } }, e => error = e);
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _ui.Pump();
        Assert.IsAssignableFrom<IOException>(error);
        Assert.False(_store.Current.Settings.SilentMode);
    }

    [Fact]
    public async Task BrokenChange_DoesNotStopTheNextOne()
    {
        _writer.Update(_ => throw new InvalidOperationException("bug"));
        _writer.Update(file => file with { Settings = file.Settings with { SilentMode = true } });
        await _writer.Idle.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(_store.Current.Settings.SilentMode);
    }
}
