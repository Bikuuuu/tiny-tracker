using System.IO.Pipes;
using System.Threading.Channels;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;

namespace TinyTracker.WinGet.Elevation;

// The app's end of the pipe (spec §8): it checks that the helper serves the pipe before it sends anything, then runs upgrades
// through it, each under the speed limit it starts with (spec §6.4). Disposing it hangs up, and the helper exits.
public sealed class HelperClient : IHelperSession
{
    private static readonly UpgradeOutcome Stopped = new(UpgradeResult.Failed, UpgradeFailure.HelperStopped);

    private readonly NamedPipeClientStream _pipe;
    private readonly Channel<HelperMessage> _outbox = Channel.CreateUnbounded<HelperMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _gone = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Call> _calls = [];
    private readonly SpeedLimit? _limit;
    private TaskCompletionSource<string?>? _task;
    private int _number;
    private bool _stopped;

    private HelperClient(NamedPipeClientStream pipe, string? problem, SpeedLimit? limit)
    {
        _pipe = pipe;
        Problem = problem;
        _limit = limit;
        _ = ReadAsync();
        _ = WriteAsync();
    }

    // Why winget doesn't answer the elevated helper; null when it does.
    public string? Problem { get; }

    public bool WinGetAvailable => Problem is null;

    // Throws IOException when the pipe's server isn't the helper exe named, running elevated when it must, and TimeoutException
    // when no helper answers in time. limit: the speed limit its upgrades run under.
    public static async Task<HelperClient> ConnectAsync(string pipeName, string helperPath, bool elevated, TimeSpan timeout, CancellationToken ct, SpeedLimit? limit = null)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var wait = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, ct);
        try
        {
            await pipe.ConnectAsync(linked.Token);
            if (ProcessIdentity.ServerOf(pipe.SafePipeHandle) is not var (path, isElevated) || !SamePath(path, helperPath) || elevated && !isElevated)
                throw new IOException("The pipe isn't served by the admin helper.");
            if (await HelperWire.ReadAsync(pipe, linked.Token) is not HelloMessage hello) throw new IOException("The admin helper didn't say hello.");
            return new HelperClient(pipe, hello.Problem, limit);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync();
            throw new TimeoutException("The admin helper didn't answer.");
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    // A cancel stops a download in the helper; a started installer finishes, and its result comes back. The request goes before
    // its cancel; a cancelled token starts nothing. A limited upgrade follows the limit; COM's can't be slowed.
    public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
        CallAsync((number, kbps) => new UpgradeRequest(number, package.Id, package.Source, version, kbps), progress, ct);

    // The version alone: the helper asks GitHub for its release itself (spec §6.5). Its own download follows each change of the
    // limit, one turned on meanwhile too.
    public Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, IProgress<UpgradeProgress>? progress, CancellationToken ct) =>
        CallAsync((number, kbps) => new SelfUpdateRequest(number, version.ToString(), kbps), progress, ct, followsAny: true);

    // followsAny: the call takes the limit's changes even when it started without one.
    private async Task<UpgradeOutcome> CallAsync(Func<int, int, HelperMessage> request, IProgress<UpgradeProgress>? progress, CancellationToken ct,
        bool followsAny = false)
    {
        if (ct.IsCancellationRequested) return new UpgradeOutcome(UpgradeResult.Cancelled);
        var call = new Call(progress);
        int number;
        lock (_gate)
        {
            if (_stopped) return Stopped;
            number = ++_number;
            _calls[number] = call;
        }
        var kbps = _limit?.KBps ?? 0;
        _outbox.Writer.TryWrite(request(number, kbps));
        var following = new Lock();
        void Follow(object? sender, EventArgs e)
        {
            lock (following) _outbox.Writer.TryWrite(new LimitRequest(number, _limit!.KBps));
        }
        var follows = _limit is not null && (kbps > 0 || followsAny);
        if (follows)
        {
            _limit!.Changed += Follow;
            // A change before it was followed.
            if (_limit.KBps != kbps) Follow(null, EventArgs.Empty);
        }
        try
        {
            using var cancel = ct.Register(() => _outbox.Writer.TryWrite(new CancelRequest(number)));
            return await call.Result.Task.ConfigureAwait(false);
        }
        finally
        {
            if (follows) _limit!.Changed -= Follow;
        }
    }

    // Asks the helper to register or remove silent mode's task. Null once done, else why not; "stopped" when the helper went.
    public Task<string?> RegisterTaskAsync(CancellationToken ct) => TaskAsync(new RegisterTaskRequest(), ct);

    public Task<string?> RemoveTaskAsync(CancellationToken ct) => TaskAsync(new RemoveTaskRequest(), ct);

    public Task<string?> EnableProxyOptionAsync(CancellationToken ct) => TaskAsync(new EnableProxyOptionRequest(), ct);

    // Heard, the helper waits its idle minutes again (spec §5.1).
    public void Stay() => _outbox.Writer.TryWrite(new StayRequest());

    public void Dispose()
    {
        _gone.Cancel();
        _pipe.Dispose();
        Stop();
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private async Task ReadAsync()
    {
        try
        {
            while (await HelperWire.ReadAsync(_pipe, _gone.Token) is { } message)
            {
                if (message is ProgressMessage progress) Find(progress.Number)?.Progress?.Report(progress.ToProgress());
                else if (message is DoneMessage done) Take(done.Number)?.Result.TrySetResult(done.ToOutcome());
                else if (message is TaskDoneMessage taskDone) TakeTask()?.TrySetResult(taskDone.Error);
                else break;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Stop();
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(_gone.Token)) await HelperWire.WriteAsync(_pipe, message, _gone.Token);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
            Stop();
        }
    }

    // The helper quit or the pipe broke: every upgrade running through it fails, and later ones fail at once.
    private void Stop()
    {
        List<Call> left;
        TaskCompletionSource<string?>? task;
        lock (_gate)
        {
            _stopped = true;
            left = [.. _calls.Values];
            _calls.Clear();
            (task, _task) = (_task, null);
        }
        foreach (var call in left) call.Result.TrySetResult(Stopped);
        task?.TrySetResult("stopped");
        _outbox.Writer.TryComplete();
    }

    private async Task<string?> TaskAsync(HelperMessage request, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stopped) return "stopped";
            if (_task is not null) throw new InvalidOperationException("The task is already being changed.");
            _task = answer;
        }
        _outbox.Writer.TryWrite(request);
        return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private TaskCompletionSource<string?>? TakeTask()
    {
        lock (_gate)
        {
            var task = _task;
            _task = null;
            return task;
        }
    }

    private Call? Find(int number)
    {
        lock (_gate) return _calls.GetValueOrDefault(number);
    }

    private Call? Take(int number)
    {
        lock (_gate) return _calls.Remove(number, out var call) ? call : null;
    }

    private sealed class Call(IProgress<UpgradeProgress>? progress)
    {
        public IProgress<UpgradeProgress>? Progress => progress;
        public TaskCompletionSource<UpgradeOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
