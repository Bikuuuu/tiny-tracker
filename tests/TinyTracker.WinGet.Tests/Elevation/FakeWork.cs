using System.Threading.Channels;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;
using TinyTracker.WinGet.Elevation;

namespace TinyTracker.WinGet.Tests.Elevation;

// Upgrades the test answers by hand; a cancel stops them, like a download.
internal sealed class FakeWork : IHelperWork
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Channel<WorkCall> _calls = Channel.CreateUnbounded<WorkCall>();
    private int _count;

    public string? Problem { get; init; }
    public int Count => Volatile.Read(ref _count);

    public Task<string?> OpenAsync(CancellationToken ct) => Task.FromResult(Problem);

    public Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        var call = new WorkCall(package, version, limit, progress, ct);
        _calls.Writer.TryWrite(call);
        return call.Result.Task;
    }

    // Tiny Tracker's own update: a call with no package.
    public Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        var call = new WorkCall(default, version.ToString(), limit, progress, ct);
        _calls.Writer.TryWrite(call);
        return call.Result.Task;
    }

    public async Task<WorkCall> NextCall(CancellationToken ct) => await _calls.Reader.ReadAsync(ct).AsTask().WaitAsync(Wait, ct);

    // What the task changes answer, and what was asked.
    public string? TaskAnswer { get; init; }
    public List<string> TaskChanges { get; } = [];

    public string? RegisterTask()
    {
        lock (TaskChanges) TaskChanges.Add("register");
        return TaskAnswer;
    }

    public string? RemoveTask()
    {
        lock (TaskChanges) TaskChanges.Add("remove");
        return TaskAnswer;
    }

    public Task<string?> EnableProxyOptionAsync(CancellationToken ct)
    {
        lock (TaskChanges) TaskChanges.Add("enableProxyOption");
        return Task.FromResult(TaskAnswer);
    }
}

internal sealed class WorkCall
{
    public WorkCall(PackageKey package, string version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken token)
    {
        (Package, Version, Limit, Progress, Token) = (package, version, limit, progress, token);
        token.Register(() => Result.TrySetResult(new UpgradeOutcome(UpgradeResult.Cancelled)));
    }

    public PackageKey Package { get; }
    public string Version { get; }
    public SpeedLimit Limit { get; }
    public IProgress<UpgradeProgress> Progress { get; }
    public CancellationToken Token { get; }
    public TaskCompletionSource<UpgradeOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class TokenWaits
{
    public static Task WhenCancelled(this CancellationToken token)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => cancelled.TrySetResult());
        return cancelled.Task;
    }
}
