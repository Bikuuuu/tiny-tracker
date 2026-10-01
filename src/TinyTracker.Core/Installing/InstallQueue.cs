using TinyTracker.Core.Checking;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.History;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;

namespace TinyTracker.Core.Installing;

// Installs one package at a time, because parallel installers conflict. After each package it reads the app again,
// flags a phantom update, saves the result and writes History. Admin updates go through the helper (spec §6.3).
public sealed class InstallQueue : IInstaller, IDisposable
{
    // Reading the app again is a winget list, which takes seconds.
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(2);
    // While bytes don't come, the shown speed is refreshed this often, so it falls to zero.
    public static readonly TimeSpan SpeedRefresh = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    // History stops with the queue: a start that settles after Quit writes nothing.
    private readonly Lock _records = new();
    private readonly List<Entry> _waiting = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly IPackageUpgrader _upgrader;
    private readonly IElevation? _elevation;
    private readonly IAppCloser? _closer;
    private readonly SpeedLimit? _limit;
    private readonly IPackageSource _source;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly InstallTimings _timings;
    private Entry? _current;
    private Task _worker = Task.CompletedTask;
    private Task _stopped = Task.CompletedTask;
    private bool _working;
    private bool _disposed;
    private bool _recordsClosed;
    // The admin helper starts when the first admin update is queued, so its prompt shows at the click, and serves every
    // admin update queued while it runs. It goes once none needs it.
    private HelperAttempt? _attempt;
    private IHelperSession? _helper;
    // While it's held, the helper is told to stay: the updates it waits for may come after a long install (spec §5.1).
    private ITimer? _stay;
    // Helper upgrades the queue stopped waiting for:
    // the helper stays until they end, so the queue hears when their installers do.
    private int _helperCalls;
    // winget didn't answer the elevated helper: until the app restarts, admin updates run in the app (spec §6.6).
    private bool _helperUnusable;
    // Tiny Tracker's own update runs: nothing starts meanwhile (spec §6.5).
    private bool _held;

    public InstallQueue(IPackageUpgrader upgrader, IPackageSource source, SettingsStore settings, HistoryStore history, TimeProvider time, FileLog log,
        InstallTimings? timings = null, IElevation? elevation = null, IAppCloser? closer = null, SpeedLimit? limit = null)
    {
        _upgrader = upgrader;
        _source = source;
        _settings = settings;
        _history = history;
        _time = time;
        _log = log;
        _timings = timings ?? InstallTimings.Default;
        _elevation = elevation;
        _closer = closer;
        _limit = limit;
    }

    // Raised on worker threads, in order. Every request ends with a Done item.
    public event EventHandler<InstallItem>? Changed;

    // Raised once, on a worker thread, when winget doesn't answer the elevated helper: admin updates then run in the app,
    // and each installer asks for itself.
    public event EventHandler? AdminFallback;

    // Raised on a worker thread when winget refused the speed limit's proxy. The limit is off by then, for the updates after it.
    public event EventHandler? SpeedLimitRefused;

    // Raised, under the queue's lock, when nothing waits or runs any more and no admin helper is held.
    public event EventHandler? BecameIdle;

    // Completes once the worker has stopped after Dispose, so its last History write has landed.
    public Task Stopped
    {
        get { lock (_gate) return _stopped; }
    }

    // An app already waiting or installing isn't added twice.
    public void Enqueue(IEnumerable<InstallRequest> requests)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var request in requests)
            {
                if (_waiting.Any(e => Same(e.Request.Package, request.Package)) || (_current is { } current && Same(current.Request.Package, request.Package))) continue;
                var entry = new Entry(request, _time);
                _waiting.Add(entry);
                if (WantsHelper(entry) && _helper is null && !_helperUnusable && !_held)
                {
                    _attempt ??= StartHelper(!request.ByItself);
                    entry.Awaiting(_attempt.Prompting);
                }
                Raise(entry.Item);
            }
            if (_working || _held || _waiting.Count == 0) return;
            _working = true;
            _worker = Task.Run(WorkAsync);
        }
    }

    // Tiny Tracker's own update runs alone (spec §6.5). An idle queue is held: until the hold is disposed, nothing starts, not
    // even the admin helper, whose files Setup replaces. Null when the queue isn't idle, or is held already.
    public IDisposable? TryHold()
    {
        lock (_gate)
        {
            if (_disposed || _held || !IsIdle) return null;
            _held = true;
            return new Hold(this);
        }
    }

    // A waiting app leaves the queue. A download stops; an installer that already started finishes.
    public void Cancel(PackageKey package)
    {
        lock (_gate)
        {
            if (_waiting.FirstOrDefault(e => Same(e.Request.Package, package)) is { } waiting)
            {
                _waiting.Remove(waiting);
                Raise(waiting.Finish(new InstallDone(new UpgradeOutcome(UpgradeResult.Cancelled))));
                ReleaseHelperIfIdle();
                NoteIdle();
            }
            else if (_current is { } current && Same(current.Request.Package, package))
            {
                current.Cancel();
            }
        }
    }

    // Close & update found the app still open, and the user said to end it.
    public void ForceClose(PackageKey package)
    {
        lock (_gate)
        {
            if (_current is { } current && Same(current.Request.Package, package)) current.ForceClose();
        }
    }

    public void Dispose()
    {
        Task worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _waiting.Clear();
            worker = _worker;
            _stopped = StopAsync(worker);
        }
    }

    private static bool Same(PackageKey a, PackageKey b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);

    // Each handler runs on its own: one that throws is logged, and neither the others nor the queue stop.
    private void Raise(InstallItem item)
    {
        if (_disposed || Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<EventHandler<InstallItem>>())
        {
            try
            {
                handler(this, item);
            }
            catch (Exception e)
            {
                _log.Error($"Install update of {item.Request.Package.Id} not delivered", e);
            }
        }
    }

    // Cancels on the thread pool, not on the caller's thread, and disposes once the worker stopped using the source.
    // Never resumes on the caller's thread: Quit blocks the UI thread while it waits for Stopped.
    private async Task StopAsync(Task worker)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        lock (_records) _recordsClosed = true;
        lock (_gate) DropHelper();
        _stop.Dispose();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Entry entry;
            lock (_gate)
            {
                _current = null;
                ReleaseHelperIfIdle();
                if (_disposed || _waiting.Count == 0)
                {
                    _working = false;
                    NoteIdle();
                    return;
                }
                entry = _waiting[0];
                _waiting.RemoveAt(0);
                _current = entry;
            }
            InstallDone done;
            try
            {
                done = await InstallAsync(entry);
            }
            catch (Exception e)
            {
                _log.Error($"Install of {entry.Request.Package.Id} failed", e);
                done = new InstallDone(new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other));
            }
            lock (_gate)
            {
                _current = null;
                Raise(entry.Finish(done));
            }
        }
    }

    private async Task<InstallDone> InstallAsync(Entry entry)
    {
        var request = entry.Request;
        var (upgrader, early) = await UpgraderForAsync(entry);
        // The app closes only once the update can run, so a declined prompt leaves it open.
        IClosingApp? closing = null;
        if (early is null && request.CloseFirst)
        {
            (closing, early) = await CloseAsync(entry);
            // Closed: there's nothing left to answer, and the update starts.
            if (early is null) lock (_gate) Raise(entry.Moved(InstallStage.Waiting));
        }
        // Nothing ran, so there's nothing to read again. History gets it unless the user cancelled.
        if (early is { } outcomeBefore)
        {
            Reopen(closing);
            var nothing = new InstallDone(outcomeBefore);
            if (outcomeBefore.Result != UpgradeResult.Cancelled && !_stop.IsCancellationRequested) Record(request, nothing);
            return nothing;
        }
        UpgradeOutcome outcome;
        try
        {
            outcome = await UpgradeAsync(entry, upgrader!);
        }
        finally
        {
            // An installer the queue stopped waiting for may still run: the app opens again once it ends.
            if (closing is not null && entry.Abandoned is { } running) _ = running.ContinueWith(t => ReopenOnceEnded(t, closing), TaskScheduler.Default);
            else Reopen(closing);
        }
        if (outcome is { Result: UpgradeResult.Failed, Failure: UpgradeFailure.HelperStopped }) HelperStopped();
        if (outcome is { Result: UpgradeResult.Failed, Failure: UpgradeFailure.ProxyRefused }) LimitRefused();
        if (outcome.Result == UpgradeResult.NoUpdate)
        {
            _log.Info($"{request.Package.Id} {request.ToVersion} isn't offered as an update: {outcome.Code ?? "no reason given"}");
            outcome = outcome with { Code = null };
        }
        // Still in use after Close & update, or with no folder to close it from: the user closes it and tries again.
        if (outcome.Result == UpgradeResult.AppInUse && (closing is not null || _closer is not null && !CanClose(request)))
            outcome = new UpgradeOutcome(UpgradeResult.CouldNotClose, Code: outcome.Code);
        if (_stop.IsCancellationRequested) return new InstallDone(outcome);
        var read = await ReadAgainAsync(request);
        var installed = read?.Installed.FirstOrDefault(p => Same(new PackageKey(p.Id, p.Source), request.Package));
        // A cancel can land just as winget starts the installer; then the update went through.
        if (outcome.Result == UpgradeResult.Cancelled && installed is not null && PackageVersion.Same(installed.InstalledVersion, request.ToVersion))
            outcome = new UpgradeOutcome(UpgradeResult.Updated);
        var phantom = outcome.Result == UpgradeResult.Updated && installed is not null
            && PhantomRule.IsPhantom(request.FromVersion, installed.InstalledVersion, request.ToVersion, installed.AvailableVersion);
        var after = read is null ? null : Save(request, installed, read.NotInCatalog, phantom);
        var done = new InstallDone(outcome, phantom, after);
        Record(request, done);
        return done;
    }

    // Close & update's first steps (spec §6.3): ask the app to close and give it 10 s, then wait for a Force close from the user.
    // The app closing by itself meanwhile goes on; a cancel, a quit, or no answer for 2 minutes ends it as cancelled.
    private async Task<(IClosingApp? Closing, UpgradeOutcome? Early)> CloseAsync(Entry entry)
    {
        var couldNot = new UpgradeOutcome(UpgradeResult.CouldNotClose);
        if (!CanClose(entry.Request)) return (null, couldNot);
        IClosingApp closing;
        try
        {
            closing = _closer!.Close(entry.Request.LocalId);
        }
        catch (Exception e)
        {
            _log.Warn($"{entry.Request.Package.Id} couldn't be closed: {e.Message}");
            return (null, couldNot);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, _stop.Token);
        try
        {
            // Each wait starts before the row says so.
            var wait = Task.Delay(_timings.CloseWait, _time, stop.Token);
            lock (_gate) Raise(entry.Moved(InstallStage.Closing));
            if (await Task.WhenAny(closing.Closed, wait) == closing.Closed) return (closing, null);
            await wait;
            var answer = Task.Delay(_timings.CloseAnswerWait, _time, stop.Token);
            lock (_gate) Raise(entry.Moved(InstallStage.NotClosed));
            var first = await Task.WhenAny(closing.Closed, entry.ForceAsked, answer);
            if (first == closing.Closed) return (closing, null);
            if (first == answer)
            {
                await answer;
                return (closing, new UpgradeOutcome(UpgradeResult.Cancelled));
            }
            var ended = Task.Delay(_timings.CloseWait, _time, stop.Token);
            if (!ForceClose(entry, closing)) return (closing, couldNot);
            if (await Task.WhenAny(closing.Closed, ended) == closing.Closed) return (closing, null);
            await ended;
            return (closing, couldNot);
        }
        catch (OperationCanceledException)
        {
            return (closing, new UpgradeOutcome(UpgradeResult.Cancelled));
        }
    }

    private bool CanClose(InstallRequest request)
    {
        if (_closer is null || request.LocalId.Length == 0) return false;
        try
        {
            return _closer.CanClose(request.LocalId);
        }
        catch (Exception e)
        {
            _log.Warn($"{request.Package.Id}'s folder couldn't be found: {e.Message}");
            return false;
        }
    }

    private bool ForceClose(Entry entry, IClosingApp closing)
    {
        try
        {
            return closing.ForceClose();
        }
        catch (Exception e)
        {
            _log.Warn($"{entry.Request.Package.Id} couldn't be ended: {e.Message}");
            return false;
        }
    }

    private void Reopen(IClosingApp? closing)
    {
        try
        {
            closing?.Reopen();
        }
        catch (Exception e)
        {
            _log.Warn($"An app didn't reopen: {e.Message}");
        }
    }

    // Quitting hangs up on the helper, which ends its upgrades before their installers end.
    private bool LeftAtQuit(Task<UpgradeOutcome> upgrade)
    {
        lock (_gate) return _disposed && upgrade is { IsCompletedSuccessfully: true, Result.Failure: UpgradeFailure.HelperStopped };
    }

    // Those apps stay closed.
    private void ReopenOnceEnded(Task<UpgradeOutcome> upgrade, IClosingApp closing)
    {
        if (!LeftAtQuit(upgrade)) Reopen(closing);
    }

    private void LogEnded(InstallRequest request, Task<UpgradeOutcome> upgrade)
    {
        var how = LeftAtQuit(upgrade) ? "unknown, Quit hung up on the admin helper" : upgrade.IsCompletedSuccessfully ? upgrade.Result.Result.ToString() : "error";
        _log.Info($"{request.Package.Id} ended after the queue moved on: {how}");
    }

    // The app's own winget, or the helper once it's up. Early is the outcome of an admin update that can't run.
    private async Task<(IPackageUpgrader? Upgrader, UpgradeOutcome? Early)> UpgraderForAsync(Entry entry)
    {
        while (true)
        {
            Task settled;
            lock (_gate)
            {
                if (!WantsHelper(entry)) return (_upgrader, null);
                // Nothing starts once the user cancelled or the app quits, least of all a prompt.
                if (entry.Token.IsCancellationRequested || _stop.IsCancellationRequested) return (null, new UpgradeOutcome(UpgradeResult.Cancelled));
                // A start this update waited on settled: it may have been declined, even before this turn began.
                if (entry.Start is { } start)
                {
                    entry.Start = null;
                    if (entry.Item.AwaitingPermission) Raise(entry.Awaiting(false));
                    if (Fate(start, entry.Request) is { } early) return (null, early);
                }
                if (_helperUnusable) return entry.Request.ByItself ? (null, new UpgradeOutcome(UpgradeResult.NeedsAdmin)) : (_upgrader, null);
                if (_helper is not null) return (_helper, null);
                // None running or starting: this update's own start, or one with a prompt after a start that couldn't show one.
                _attempt ??= StartHelper(!entry.Request.ByItself);
                if (entry.Item.AwaitingPermission != _attempt.Prompting) Raise(entry.Awaiting(_attempt.Prompting));
                settled = _attempt.Settled.Task;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, _stop.Token);
            try
            {
                await settled.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return (null, new UpgradeOutcome(UpgradeResult.Cancelled));
            }
        }
    }

    // What an admin update does once a start of the helper settled. Null when it goes on.
    private static UpgradeOutcome? Fate(HelperStart start, InstallRequest request) => start.Result switch
    {
        HelperStartResult.Started => null,
        HelperStartResult.Declined => new UpgradeOutcome(UpgradeResult.PermissionDeclined),
        HelperStartResult.NeedsPrompt => request.ByItself ? new UpgradeOutcome(UpgradeResult.NeedsAdmin) : null,
        _ => new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperNotStarted, start.Code),
    };

    private static bool WantsHelper(Entry entry) => entry.Request.Route == InstallRoute.Helper;

    // Guarded by the lock. The prompt, if any, shows now.
    private HelperAttempt StartHelper(bool prompt)
    {
        var attempt = new HelperAttempt(prompt && _elevation is { Prompts: true });
        var token = _stop.Token;
        var start = _elevation is { } elevation
            ? Task.Run(() => elevation.StartAsync(prompt, open => OnPrompting(attempt, open), token))
            : Task.FromResult(new HelperStart(HelperStartResult.Failed));
        _ = start.ContinueWith(task => OnStarted(attempt, task), TaskScheduler.Default);
        return attempt;
    }

    // The admin updates that wait on the start say "Waiting for permission…" only while its prompt shows (spec §4.3), the one at
    // the front too; one that shows in silent mode, when its task went missing, counts as well.
    private void OnPrompting(HelperAttempt attempt, bool open)
    {
        lock (_gate)
        {
            attempt.Prompting = open;
            if (_disposed || _attempt != attempt || _helper is not null) return;
            foreach (var entry in _waiting.Where(WantsHelper).Concat(_current is { } current && WantsHelper(current) ? [current] : []).ToList())
                if (entry.Item.AwaitingPermission != open) Raise(entry.Awaiting(open));
        }
    }

    // Admin updates still waiting learn at once what came of the start; the one at the front hears last.
    private void OnStarted(HelperAttempt attempt, Task<HelperStart> task)
    {
        var start = task.IsCompletedSuccessfully ? task.Result
            : new HelperStart(HelperStartResult.Failed, Code: task.Exception is { } error ? $"0x{error.InnerException?.HResult ?? error.HResult:X8}" : null);
        var fellBack = false;
        List<(Entry Entry, UpgradeOutcome Outcome)> ended = [];
        lock (_gate)
        {
            if (_attempt == attempt) _attempt = null;
            if (_disposed) start.Session?.Dispose();
            else if (start is { Result: HelperStartResult.Started, Session: { WinGetAvailable: true } session })
            {
                _helper = session;
                _stay = _time.CreateTimer(_ => Stay(), null, HelperRules.StayEvery, HelperRules.StayEvery);
            }
            else
            {
                start.Session?.Dispose();
                if (start.Result == HelperStartResult.Started && !_helperUnusable) _helperUnusable = fellBack = true;
            }
            if (!_disposed)
            {
                foreach (var entry in _waiting.Where(WantsHelper).ToList())
                {
                    var fate = _helperUnusable ? entry.Request.ByItself ? new UpgradeOutcome(UpgradeResult.NeedsAdmin) : null : Fate(start, entry.Request);
                    if (fate is null) continue;
                    _waiting.Remove(entry);
                    ended.Add((entry, fate));
                }
                // The update at the front reads it on its next turn.
                if (_current is { } current && WantsHelper(current)) current.Start = start;
                // A user's update that waited on a start without a prompt gets one.
                if (start.Result == HelperStartResult.NeedsPrompt && _attempt is null && _waiting.Any(e => WantsHelper(e) && !e.Request.ByItself))
                    _attempt = StartHelper(prompt: true);
                var awaiting = _helper is null && _attempt is { Prompting: true };
                foreach (var entry in _waiting.Where(e => WantsHelper(e) && e.Item.AwaitingPermission != awaiting)) Raise(entry.Awaiting(awaiting));
                ReleaseHelperIfIdle();
                NoteIdle();
            }
        }
        foreach (var (entry, outcome) in ended) End(entry, outcome);
        if (fellBack) Tell(AdminFallback);
        attempt.Settled.TrySetResult(start);
    }

    // The helper quit mid-way: the admin updates waiting for it fail the same way, so nothing stays stuck (spec §7).
    private void HelperStopped()
    {
        List<Entry> stranded;
        lock (_gate)
        {
            DropHelper();
            stranded = [.. _waiting.Where(WantsHelper)];
            foreach (var entry in stranded) _waiting.Remove(entry);
        }
        foreach (var entry in stranded) End(entry, new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperStopped));
    }

    // winget's proxy option went off behind the app's back: the updates after this one run at full speed (spec §6.4).
    private void LimitRefused()
    {
        _limit?.Set(0);
        Tell(SpeedLimitRefused);
    }

    // A waiting update that ends without running: History first, then the row.
    private void End(Entry entry, UpgradeOutcome outcome)
    {
        var done = new InstallDone(outcome);
        Record(entry.Request, done);
        lock (_gate) Raise(entry.Finish(done));
    }

    // Guarded by the lock: an admin update waits or runs, or one whose installer still runs.
    private bool HelperNeeded => _helperCalls > 0 || _waiting.Any(WantsHelper) || _current is { } current && WantsHelper(current);

    // Guarded by the lock. The helper goes once no queued update needs it.
    private void ReleaseHelperIfIdle()
    {
        if (_helper is null || HelperNeeded) return;
        DropHelper();
    }

    // Guarded by the lock.
    private void DropHelper()
    {
        _stay?.Dispose();
        _stay = null;
        _helper?.Dispose();
        _helper = null;
    }

    // Only while it's needed, so a helper held by mistake still leaves after its idle wait.
    private void Stay()
    {
        lock (_gate)
        {
            if (HelperNeeded) _helper?.Stay();
        }
    }

    private void KeepHelperUntilEnded(Task upgrade)
    {
        lock (_gate) _helperCalls++;
        _ = upgrade.ContinueWith(_ =>
        {
            lock (_gate)
            {
                _helperCalls--;
                ReleaseHelperIfIdle();
                NoteIdle();
            }
        }, TaskScheduler.Default);
    }

    // Guarded by the lock.
    private bool IsIdle => !_working && _current is null && _waiting.Count == 0 && _helper is null && _attempt is null && _helperCalls == 0;

    // Guarded by the lock.
    private void NoteIdle()
    {
        if (!_disposed && !_held && IsIdle) Tell(BecameIdle);
    }

    // What was queued meanwhile starts now, an admin update with its prompt.
    private void Release()
    {
        lock (_gate)
        {
            _held = false;
            if (_disposed || _waiting.Count == 0) return;
            if (_waiting.FirstOrDefault(WantsHelper) is { } admin && _helper is null && !_helperUnusable)
            {
                _attempt ??= StartHelper(!admin.Request.ByItself);
                foreach (var entry in _waiting.Where(WantsHelper)) Raise(entry.Awaiting(_attempt.Prompting));
            }
            _working = true;
            _worker = Task.Run(WorkAsync);
        }
    }

    // Lets the queue go once, however often it's disposed.
    private sealed class Hold(InstallQueue queue) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) queue.Release();
        }
    }

    private void Tell(EventHandler? handlers)
    {
        if (handlers is null) return;
        try
        {
            handlers(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            _log.Error("Install queue notice not delivered", e);
        }
    }

    // Retries a busy winget and a stalled download, and gives up at the cap.
    private async Task<UpgradeOutcome> UpgradeAsync(Entry entry, IPackageUpgrader upgrader)
    {
        using var cap = new CapClock(_timings.Cap, _time);
        lock (_gate) entry.Cap = cap;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cap.Token, _stop.Token);
        var stalls = 0;
        var busy = 0;
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, wait.Token);
            IProgress<UpgradeProgress> progress;
            lock (_gate) progress = entry.StartAttempt(attempt, this);
            var upgrade = Task.Run(() => upgrader.UpgradeAsync(entry.Request.Package, entry.Request.ToVersion, progress, attempt.Token));
            UpgradeOutcome outcome;
            try
            {
                outcome = await upgrade.WaitAsync(wait.Token);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                // Cancelled before it's disposed, so a download stops.
                // winget can't stop a started installer, so the queue stops waiting for it.
                attempt.Cancel();
                entry.Abandoned = upgrade;
                if (upgrader is IHelperSession) KeepHelperUntilEnded(upgrade);
                _ = upgrade.ContinueWith(t => LogEnded(entry.Request, t), TaskScheduler.Default);
                return _stop.IsCancellationRequested ? new UpgradeOutcome(UpgradeResult.Cancelled) : new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong);
            }
            finally
            {
                lock (_gate) entry.EndAttempt();
            }
            if (outcome.Result == UpgradeResult.Cancelled && !entry.Token.IsCancellationRequested)
            {
                if (cap.Reached) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong);
                if (entry.Stalled && ++stalls < 2) continue;
                if (entry.Stalled) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Stalled);
            }
            if (outcome.Result != UpgradeResult.Busy || busy++ >= _timings.BusyRetries) return outcome;
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(entry.Token, wait.Token);
            var pause = Task.Delay(_timings.BusyRetryDelay, _time, delay.Token);
            lock (_gate) Raise(entry.Waiting(busy: true));
            try
            {
                await pause;
            }
            catch (OperationCanceledException)
            {
                return entry.Token.IsCancellationRequested || _stop.IsCancellationRequested
                    ? new UpgradeOutcome(UpgradeResult.Cancelled)
                    : new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.TookTooLong);
            }
        }
    }

    private async Task<CatalogRead?> ReadAgainAsync(InstallRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ReadTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stop.Token);
            return await _source.ReadAsync([new TrackedApp { Id = request.Package.Id, Source = request.Package.Source, Name = request.Name }], linked.Token);
        }
        catch (Exception e) when (e is PackageSourceException or OperationCanceledException)
        {
            _log.Warn($"{request.Package.Id} couldn't be read again: {e.Message}");
            return null;
        }
    }

    // Merged inside Update, like a check, so a change saved meanwhile survives. An app no longer tracked stays out.
    private AppCheck? Save(InstallRequest request, PackageSnapshot? installed, IReadOnlyList<PackageKey> notInCatalog, bool phantom)
    {
        AppCheck? after = null;
        try
        {
            _settings.Update(file =>
            {
                if (file.Apps.FirstOrDefault(a => a.Matches(request.Package.Id, request.Package.Source)) is not { } app) return file;
                if (phantom) app = PhantomRule.Flag(app, request.ToVersion);
                after = CheckMerge.Apply([app], installed is null ? [] : [installed], _time.GetUtcNow(), notInCatalog)[0];
                var merged = after.App;
                return file with { Apps = file.Apps.Select(a => a.Matches(merged.Id, merged.Source) ? merged : a).ToList() };
            });
            return after;
        }
        catch (IOException e)
        {
            _log.Warn($"Install result of {request.Package.Id} not saved: {e.Message}");
            return null;
        }
    }

    private void Record(InstallRequest request, InstallDone done)
    {
        lock (_records)
        {
            if (_recordsClosed) return;
            try
            {
                _history.Add(new HistoryEntry
                {
                    Time = _time.GetUtcNow(),
                    Id = request.Package.Id,
                    Source = request.Package.Source,
                    Name = request.Name,
                    Result = done.HistoryResult,
                    FromVersion = request.FromVersion,
                    ToVersion = request.ToVersion,
                    Reason = done.Reason,
                    Code = done.Outcome.Code,
                });
            }
            catch (IOException e)
            {
                _log.Warn($"History of {request.Package.Id} not saved: {e.Message}");
            }
        }
    }

    // One start of the helper, and whether its prompt shows: guessed at first, then as the prompt opens and closes. Guarded by
    // the queue's lock. Settled completes once the queue has acted on what came of it.
    private sealed class HelperAttempt(bool prompting)
    {
        public bool Prompting { get; set; } = prompting;
        public TaskCompletionSource<HelperStart> Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Guarded by the queue's lock.
    private sealed class Entry(InstallRequest request, TimeProvider time)
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly SpeedMeter _speed = new(time);
        private InstallItem _item = new(request, InstallStage.Waiting);
        private readonly TaskCompletionSource _force = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _attempt;
        private ITimer? _stallTimer;
        private ITimer? _speedTimer;
        private int _attempts;

        public InstallRequest Request => request;
        public InstallItem Item => _item;
        public CancellationToken Token => _cancel.Token;
        public bool Stalled { get; private set; }
        // What came of a start of the helper that settled while this update was at the front.
        public HelperStart? Start { get; set; }

        // Close & update: the user said to end the app.
        public Task ForceAsked => _force.Task;

        // The upgrade the queue stopped waiting for; its installer may still run.
        public Task<UpgradeOutcome>? Abandoned { get; set; }

        // The update's 30-minute cap, which pauses while it downloads.
        public CapClock? Cap { get; set; }

        public void Cancel() => _ = _cancel.CancelAsync();

        public void ForceClose() => _force.TrySetResult();

        public InstallItem Moved(InstallStage stage) => _item = _item with { Stage = stage };

        public InstallItem Finish(InstallDone done) => _item = _item with { Stage = InstallStage.Done, Done = done, AwaitingPermission = false };

        public InstallItem Waiting(bool busy)
        {
            Cap?.Downloading(false);
            return _item = _item with { Stage = InstallStage.Waiting, Busy = busy, Progress = default, BytesPerSecond = 0 };
        }

        public InstallItem Awaiting(bool awaiting) => _item = _item with { AwaitingPermission = awaiting };

        public IProgress<UpgradeProgress> StartAttempt(CancellationTokenSource attempt, InstallQueue queue)
        {
            var number = ++_attempts;
            _attempt = attempt;
            Stalled = false;
            Cap?.Downloading(false);
            _item = _item with { Progress = default, BytesPerSecond = 0 };
            _speed.Add(0);
            // Watches the wait in winget's queue until the first progress, then the download.
            _stallTimer = time.CreateTimer(_ => queue.OnStall(this, number), null, queue._timings.StallAfter, Timeout.InfiniteTimeSpan);
            _speedTimer = time.CreateTimer(_ => queue.OnSpeedTick(this, number), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return new Reporter(progress => queue.OnProgress(this, number, progress));
        }

        public void EndAttempt()
        {
            _stallTimer?.Dispose();
            _stallTimer = null;
            _speedTimer?.Dispose();
            _speedTimer = null;
            _attempt = null;
        }

        public bool IsAttempt(int number) => number == _attempts && _attempt is not null;

        // A download moves when its byte count or fraction changes. A wait that already said so keeps Busy while queued.
        public InstallItem Report(UpgradeProgress progress, TimeSpan stallAfter)
        {
            var moved = progress.Stage != _item.Progress.Stage || progress.BytesDownloaded != _item.Progress.BytesDownloaded || progress.DownloadFraction != _item.Progress.DownloadFraction;
            var downloading = progress.Stage == UpgradeStage.Downloading;
            Cap?.Downloading(downloading);
            if (downloading) _speed.Add(progress.BytesDownloaded);
            if (moved) _stallTimer?.Change(progress.Stage is UpgradeStage.Downloading or UpgradeStage.Queued ? stallAfter : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _speedTimer?.Change(downloading ? SpeedRefresh : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var stage = progress.Stage switch
            {
                UpgradeStage.Queued => InstallStage.Waiting,
                UpgradeStage.Downloading => InstallStage.Downloading,
                _ => InstallStage.Installing,
            };
            return _item = _item with { Stage = stage, Progress = progress, BytesPerSecond = _speed.BytesPerSecond, Busy = stage == InstallStage.Waiting && _item.Busy };
        }

        // Null when the shown speed is already right. The timer runs again only while there is speed left to fall.
        public InstallItem? RefreshSpeed()
        {
            var speed = _speed.BytesPerSecond;
            if (speed > 0) _speedTimer?.Change(SpeedRefresh, Timeout.InfiniteTimeSpan);
            return speed == _item.BytesPerSecond ? null : _item = _item with { BytesPerSecond = speed };
        }

        public InstallItem QueuedLong() => _item = _item with { Stage = InstallStage.Waiting, Busy = true };

        public void Stall()
        {
            Stalled = true;
            try
            {
                _ = _attempt?.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnProgress(Entry entry, int attempt, UpgradeProgress progress)
    {
        lock (_gate)
        {
            if (entry.IsAttempt(attempt)) Raise(entry.Report(progress, _timings.StallAfter));
        }
    }

    // Still queued in winget: another install is likely running, so say so and keep waiting. Downloading: the download stalled.
    private void OnStall(Entry entry, int attempt)
    {
        lock (_gate)
        {
            if (!entry.IsAttempt(attempt)) return;
            // This attempt's own progress: a retry starts without any.
            if (entry.Item.Progress.Stage == UpgradeStage.Queued)
            {
                Raise(entry.QueuedLong());
                return;
            }
            _log.Warn($"{entry.Request.Package.Id} stalled: no download progress for {_timings.StallAfter.TotalMinutes:0.#} minutes");
            entry.Stall();
        }
    }

    private void OnSpeedTick(Entry entry, int attempt)
    {
        lock (_gate)
        {
            if (entry.IsAttempt(attempt) && entry.Item.Stage == InstallStage.Downloading && entry.RefreshSpeed() is { } item) Raise(item);
        }
    }

    // Reports on the calling thread, unlike Progress<T>, which would post to the UI thread.
    private sealed class Reporter(Action<UpgradeProgress> report) : IProgress<UpgradeProgress>
    {
        public void Report(UpgradeProgress value) => report(value);
    }
}
