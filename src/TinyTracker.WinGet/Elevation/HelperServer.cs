using System.IO.Pipes;
using System.Threading.Channels;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Core.SelfUpdate;
using TinyTracker.Core.Tracking;

namespace TinyTracker.WinGet.Elevation;

// What the helper does for the app. The demo's helper fakes it.
public interface IHelperWork
{
    // Null when winget answers this process, else why not.
    Task<string?> OpenAsync(CancellationToken ct);

    // Like IPackageUpgrader: never throws. limit: the speed limit it runs under, 0 for none; limit requests change it meanwhile.
    Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct);

    // Silent mode's task for the app's user (spec §6.6). Null once done, else why not.
    string? RegisterTask();

    string? RemoveTask();

    // winget's proxy option for the user this runs as (spec §6.4). Null once it reads on, else why not.
    Task<string?> EnableProxyOptionAsync(CancellationToken ct);

    // Tiny Tracker's own update (spec §6.5), like an upgrade: Updated once Setup started.
    Task<UpgradeOutcome> SelfUpdateAsync(SelfVersion version, SpeedLimit limit, IProgress<UpgradeProgress> progress, CancellationToken ct);
}

// Serves one app over the pipe (spec §5.1, §8): a hello, then one upgrade at a time, until the app hangs up or a self-update's
// Setup starts, since it replaces the helper. version: the helper's own, which a self-update must be newer than; none takes none.
public sealed class HelperServer(IHelperWork work, TimeProvider time, SelfVersion? version = null)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    // This many messages waiting unread: the app stopped reading.
    public const int MaxUnread = 32;
    // What's left to say goes out before the helper leaves, unless the app stopped reading.
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(5);
    private static readonly UpgradeOutcome Refused = new(UpgradeResult.Failed, UpgradeFailure.Other, "refused");

    // Ends when the app hangs up or sends what it never sends, when none comes within 30 seconds, after 10 idle minutes, or once
    // 32 messages wait unread.
    public async Task RunAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        if (!await ConnectedAsync(pipe, ct)) return;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        // One writer keeps the messages in order: an upgrade's progress never comes after its result.
        var outbox = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(MaxUnread) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var writer = WriteAsync(pipe, outbox.Reader, session.Token);
        // The app stopped reading or went quiet. A late report may come once reading is gone.
        void Leave()
        {
            try
            {
                reading.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        void Say(HelperMessage message)
        {
            if (!outbox.Writer.TryWrite(new Outgoing(message))) Leave();
        }
        using var watch = new IdleWatch(time, Leave);
        Upgrade? current = null;
        try
        {
            Say(new HelloMessage(await work.OpenAsync(session.Token)));
            while (await HelperWire.ReadAsync(pipe, reading.Token) is { } message)
            {
                watch.Heard();
                if (message is UpgradeRequest request)
                {
                    if (current is { Ended: false }) Say(DoneMessage.Of(request.Number, new UpgradeOutcome(UpgradeResult.Busy)));
                    else if (!HelperRules.IsValid(request)) Say(DoneMessage.Of(request.Number, Refused));
                    else
                    {
                        current?.Cancel.Dispose();
                        current = Start(request.Number, request.Limit, (limit, progress, token) =>
                            work.UpgradeAsync(new PackageKey(request.Id, request.Source), request.Version, limit, progress, token), null, outbox.Writer, Say, Leave,
                            watch, session.Token);
                    }
                }
                else if (message is SelfUpdateRequest self)
                {
                    if (current is { Ended: false }) Say(DoneMessage.Of(self.Number, new UpgradeOutcome(UpgradeResult.Busy)));
                    else if (!HelperRules.IsValid(self, version)) Say(DoneMessage.Of(self.Number, Refused));
                    else
                    {
                        current?.Cancel.Dispose();
                        current = Start(self.Number, self.Limit, (limit, progress, token) => work.SelfUpdateAsync(SelfVersion.Parse(self.Version)!, limit, progress, token),
                            outcome =>
                            {
                                if (outcome.Result == UpgradeResult.Updated) Leave();
                            }, outbox.Writer, Say, Leave, watch, session.Token);
                    }
                }
                else if (message is LimitRequest limit)
                {
                    // Only for the upgrade that runs, and only a limit Settings could have.
                    if (current is { Ended: false } running && running.Number == limit.Number && SpeedLimit.IsAllowed(limit.KBps)) running.Limit.Set(limit.KBps);
                }
                else if (message is CancelRequest cancel)
                {
                    if (current?.Number == cancel.Number) await current.Cancel.CancelAsync();
                }
                else if (message is RegisterTaskRequest) Say(new TaskDoneMessage(work.RegisterTask()));
                else if (message is RemoveTaskRequest) Say(new TaskDoneMessage(work.RemoveTask()));
                else if (message is EnableProxyOptionRequest) Say(new TaskDoneMessage(await work.EnableProxyOptionAsync(session.Token)));
                // A stay was heard, and that's all it asks.
                else if (message is not StayRequest) break;
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A malformed message, or the pipe broke: the helper hangs up.
        }
        finally
        {
            // A download stops; an installer that already started finishes before the helper exits.
            if (current is not null)
            {
                await current.Cancel.CancelAsync();
                await current.Done;
                current.Cancel.Dispose();
            }
            outbox.Writer.TryComplete();
            await Task.WhenAny(writer, Task.Delay(FlushWait, time));
            await session.CancelAsync();
            await writer;
            Disconnect(pipe);
        }
    }

    private async Task<bool> ConnectedAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(ConnectTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            await pipe.WaitForConnectionAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ended hears the outcome once it's on its way to the app. leave: the app stopped reading.
    private static Upgrade Start(int number, int kbps, Func<SpeedLimit, IProgress<UpgradeProgress>, CancellationToken, Task<UpgradeOutcome>> run,
        Action<UpgradeOutcome>? ended, ChannelWriter<Outgoing> outbox, Action<HelperMessage> say, Action leave, IdleWatch watch, CancellationToken session)
    {
        var upgrade = new Upgrade(number, CancellationTokenSource.CreateLinkedTokenSource(session), new SpeedLimit(kbps));
        var reporter = new Reporter(number, outbox, leave);
        watch.Busy(true);
        upgrade.Done = Task.Run(async () =>
        {
            UpgradeOutcome outcome;
            try
            {
                outcome = await run(upgrade.Limit, reporter, upgrade.Cancel.Token);
            }
            catch (Exception e)
            {
                outcome = new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, $"0x{e.HResult:X8}");
            }
            reporter.Close();
            // Ended before the app hears, so the next upgrade it sends is never busy.
            upgrade.End();
            watch.Busy(false);
            say(DoneMessage.Of(number, outcome));
            ended?.Invoke(outcome);
        }, CancellationToken.None);
        return upgrade;
    }

    private static async Task WriteAsync(Stream pipe, ChannelReader<Outgoing> outbox, CancellationToken ct)
    {
        try
        {
            await foreach (var item in outbox.ReadAllAsync(ct))
                if ((item.Message ?? item.Progress?.Take()) is { } message) await HelperWire.WriteAsync(pipe, message, ct);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The app hung up; there's no one left to tell.
        }
    }

    private static void Disconnect(NamedPipeServerStream pipe)
    {
        try
        {
            if (pipe.IsConnected) pipe.Disconnect();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private sealed class Upgrade(int number, CancellationTokenSource cancel, SpeedLimit limit)
    {
        private volatile bool _ended;

        public int Number => number;
        public CancellationTokenSource Cancel => cancel;
        public SpeedLimit Limit => limit;
        public Task Done { get; set; } = Task.CompletedTask;
        public bool Ended => _ended;

        public void End() => _ended = true;
    }

    // A message for the app, or a place in line for an upgrade's newest progress.
    private sealed record Outgoing(HelperMessage? Message, Reporter? Progress = null);

    // Sends winget's progress in line with the rest, and nothing once the upgrade has ended. Progress still waiting gives way to
    // the newer. leave: the app stopped reading.
    private sealed class Reporter(int number, ChannelWriter<Outgoing> outbox, Action leave) : IProgress<UpgradeProgress>
    {
        private readonly Lock _gate = new();
        private UpgradeProgress? _latest;
        private bool _waiting;
        private bool _closed;

        public void Report(UpgradeProgress value)
        {
            bool full;
            lock (_gate)
            {
                if (_closed) return;
                _latest = value;
                if (_waiting) return;
                _waiting = outbox.TryWrite(new Outgoing(null, this));
                full = !_waiting;
            }
            if (full) leave();
        }

        // The newest progress, once its turn comes.
        public HelperMessage? Take()
        {
            lock (_gate)
            {
                _waiting = false;
                return _latest is { } latest ? ProgressMessage.Of(number, latest) : null;
            }
        }

        public void Close()
        {
            lock (_gate) _closed = true;
        }
    }

    // Tells the helper to leave once nothing was asked and nothing ran for the idle wait (spec §5.1).
    private sealed class IdleWatch : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly TimeProvider _time;
        private readonly Action _idle;
        private readonly ITimer _timer;
        private long _heard;
        // Upgrades running: the next may start before the last has said it ended.
        private int _busy;
        private bool _disposed;

        public IdleWatch(TimeProvider time, Action idle)
        {
            _time = time;
            _idle = idle;
            _heard = time.GetTimestamp();
            _timer = time.CreateTimer(_ => Elapsed(), null, HelperRules.IdleTimeout, Timeout.InfiniteTimeSpan);
        }

        // A message came: the wait starts again.
        public void Heard()
        {
            lock (_gate)
            {
                _heard = _time.GetTimestamp();
                if (!_disposed && _busy == 0) _timer.Change(HelperRules.IdleTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        // Nothing counts while an upgrade runs; once the last ends, the wait starts again.
        public void Busy(bool busy)
        {
            lock (_gate)
            {
                _busy += busy ? 1 : -1;
                _heard = _time.GetTimestamp();
                if (!_disposed) _timer.Change(_busy > 0 ? Timeout.InfiniteTimeSpan : HelperRules.IdleTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer.Dispose();
            }
        }

        // A callback that raced a message or an upgrade sees it here and waits on.
        private void Elapsed()
        {
            lock (_gate)
            {
                if (_disposed || _busy > 0) return;
                var quiet = _time.GetElapsedTime(_heard);
                if (quiet < HelperRules.IdleTimeout) _timer.Change(HelperRules.IdleTimeout - quiet, Timeout.InfiniteTimeSpan);
                else _idle();
            }
        }
    }
}
