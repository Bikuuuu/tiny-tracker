using System.Threading.Channels;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Tests.Installing;

// Replies to reads with Reply.
internal sealed class FakeSource : IPackageSource
{
    public Func<IReadOnlyList<TrackedApp>, CatalogRead> Reply { get; set; } = _ => new([], []);

    public Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(Reply(apps));
        }
        catch (Exception e)
        {
            return Task.FromException<CatalogRead>(e);
        }
    }
}

// Upgrades the test answers by hand.
internal sealed class FakeUpgrader : IPackageUpgrader
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Channel<UpgradeCall> _calls = Channel.CreateUnbounded<UpgradeCall>();
    private int _count;
    private int _running;
    private int _maxRunning;

    public int Count => Volatile.Read(ref _count);
    public int MaxRunning => Volatile.Read(ref _maxRunning);

    public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxRunning, running);
        var call = new UpgradeCall(package, version, progress, ct);
        _calls.Writer.TryWrite(call);
        return call.Result.Task.ContinueWith(t =>
        {
            Interlocked.Decrement(ref _running);
            // An error the test sets comes as it is, not wrapped.
            return t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);
    }

    public async Task<UpgradeCall> NextCall(CancellationToken ct) => await _calls.Reader.ReadAsync(ct).AsTask().WaitAsync(Wait, ct);

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}

// Like winget, a cancel stops only a queued or downloading upgrade.
internal sealed class UpgradeCall
{
    private const ulong MB = 1024 * 1024;
    private readonly IProgress<UpgradeProgress>? _progress;
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _installing;

    public UpgradeCall(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        Package = package;
        Version = version;
        Token = ct;
        _progress = progress;
        ct.Register(() =>
        {
            _cancelled.TrySetResult();
            if (!_installing) Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Cancelled));
        });
    }

    public PackageKey Package { get; }
    public string Version { get; }
    public CancellationToken Token { get; }
    public Task Cancelled => _cancelled.Task;
    public TaskCompletionSource<UpgradeOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Queue() => _progress?.Report(new UpgradeProgress(UpgradeStage.Queued, 0, 0, 0, 0));

    public void Download(ulong bytes, ulong total = 100 * MB) =>
        _progress?.Report(new UpgradeProgress(UpgradeStage.Downloading, bytes, total, total == 0 ? 0 : (double)bytes / total, 0));

    public void Install(double fraction = 0)
    {
        _installing = true;
        _progress?.Report(new UpgradeProgress(UpgradeStage.Installing, 100 * MB, 100 * MB, 1, fraction));
    }

    public void Finish(UpgradeResult result, UpgradeFailure failure = UpgradeFailure.None, string? code = null) =>
        Result.TrySetResult(new UpgradeOutcome(result, failure, code));
}

// Starts the admin helper when the test answers its prompt.
internal sealed class FakeElevation : IElevation
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Channel<ElevationCall> _calls = Channel.CreateUnbounded<ElevationCall>();
    private int _count;

    public int Count => Volatile.Read(ref _count);

    // Answers every start at once, without the test.
    public HelperStartResult? AnswerAtOnce { get; init; }

    // False: silent mode's task starts the helper, with no prompt.
    public bool Prompts { get; init; } = true;

    public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct) => StartAsync(mayPrompt, _ => { }, ct);

    // As the launcher does, the prompt shows at once unless silent mode's task starts the helper, and it closes before the helper answers.
    public Task<HelperStart> StartAsync(bool mayPrompt, Action<bool> prompting, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        if (AnswerAtOnce is { } answer) return Task.FromResult(new HelperStart(answer));
        var call = new ElevationCall(mayPrompt, prompting);
        if (mayPrompt && Prompts) call.OpenPrompt();
        _calls.Writer.TryWrite(call);
        return call.Result.Task;
    }

    public async Task<ElevationCall> NextCall(CancellationToken ct) => await _calls.Reader.ReadAsync(ct).AsTask().WaitAsync(Wait, ct);
}

internal sealed class ElevationCall(bool mayPrompt, Action<bool> prompting)
{
    private int _open;

    public bool MayPrompt => mayPrompt;
    public TaskCompletionSource<HelperStart> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A prompt after all, as when silent mode's task went missing; or one answered while the helper still starts.
    public void OpenPrompt()
    {
        if (Interlocked.Exchange(ref _open, 1) == 0) prompting(true);
    }

    public void ClosePrompt()
    {
        if (Interlocked.Exchange(ref _open, 0) == 1) prompting(false);
    }

    public FakeSession Approve(bool winGetAvailable = true)
    {
        ClosePrompt();
        var session = new FakeSession(winGetAvailable);
        Result.TrySetResult(new HelperStart(HelperStartResult.Started, session));
        return session;
    }

    public void Answer(HelperStartResult result, string? code = null)
    {
        ClosePrompt();
        Result.TrySetResult(new HelperStart(result, Code: code));
    }
}

// A running helper whose upgrades the test answers by hand. As the real one, hanging up ends those still running.
internal sealed class FakeSession(bool winGetAvailable) : IHelperSession
{
    private static readonly UpgradeOutcome Stopped = new(UpgradeResult.Failed, UpgradeFailure.HelperStopped);
    private readonly TaskCompletionSource _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stays;

    public FakeUpgrader Upgrades { get; } = new();
    public int Stays => Volatile.Read(ref _stays);
    public bool WinGetAvailable => winGetAvailable;
    public bool Disposed => _gone.Task.IsCompleted;
    public Task Gone => _gone.Task;

    public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        if (Disposed) return Stopped;
        var upgrade = Upgrades.UpgradeAsync(package, version, progress, ct);
        return await Task.WhenAny(upgrade, _gone.Task) == upgrade ? await upgrade : Stopped;
    }

    public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public void Stay() => Interlocked.Increment(ref _stays);

    // Tiny Tracker's own updates, answered by hand as upgrades are; their package is Tiny Tracker's key.
    public FakeUpgrader SelfUpdates { get; } = new();

    public async Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        if (Disposed) return Stopped;
        var update = SelfUpdates.UpgradeAsync(SelfUpdater.Key, version.ToString(), progress, ct);
        return await Task.WhenAny(update, _gone.Task) == update ? await update : Stopped;
    }

    public void Dispose() => _gone.TrySetResult();
}

// Closes apps for Close & update as the test says.
internal sealed class FakeCloser : IAppCloser
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Channel<FakeClosing> _closings = Channel.CreateUnbounded<FakeClosing>();

    // False: the app's folder can't be found.
    public bool Findable { get; set; } = true;
    public List<string> Asked { get; } = [];

    public bool CanClose(string localId) => Findable;

    public IClosingApp Close(string localId)
    {
        var closing = new FakeClosing();
        lock (Asked) Asked.Add(localId);
        _closings.Writer.TryWrite(closing);
        return closing;
    }

    public async Task<FakeClosing> NextClose(CancellationToken ct) => await _closings.Reader.ReadAsync(ct).AsTask().WaitAsync(Wait, ct);
}

// One app being closed; it closes when the test says, and records force-closing and reopening.
internal sealed class FakeClosing : IClosingApp
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _reopened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _forceCloses;

    // False: a process runs as admin, so it can't be ended.
    public bool CanEnd { get; set; } = true;
    // False: it was ended, but it's still open, as when it starts again at once.
    public bool EndsWhenForced { get; set; } = true;
    public Task Closed => _closed.Task;
    public Task Reopened => _reopened.Task;
    public int ForceCloses => Volatile.Read(ref _forceCloses);

    public void Close() => _closed.TrySetResult();

    public bool ForceClose()
    {
        Interlocked.Increment(ref _forceCloses);
        if (!CanEnd) return false;
        if (EndsWhenForced) _closed.TrySetResult();
        return true;
    }

    public void Reopen() => _reopened.TrySetResult();
}
