using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.SelfUpdate;

public enum SelfUpdateStage
{
    // Nothing newer is known.
    None,
    Available,
    WaitsForOthers,
    AwaitingPermission,
    Downloading,
    // Setup started, and closes the app.
    Installing,
    // Outcome says why; Update tries again.
    Failed,
}

// What the self-update's row shows (spec §4.3). Declined: the prompt was declined, and Update is offered again.
public sealed record SelfUpdateState(SelfUpdateStage Stage, SelfRelease? Release)
{
    public UpgradeProgress Progress { get; init; }
    public double BytesPerSecond { get; init; }
    public UpgradeOutcome? Outcome { get; init; }
    public bool Declined { get; init; }
    // The attempt it shows started by itself.
    public bool Automatic { get; init; }
}

// What the Updates page asks of Tiny Tracker's own update.
public interface ISelfUpdate
{
    SelfVersion Running { get; }

    SelfUpdateState State { get; }

    void Update(bool automatic);

    void Cancel();
}

// Tiny Tracker's own update (spec §6.5): after the other updates it holds the queue, notes itself in settings.json and asks the
// helper to check and start Setup, which updates and restarts the app; Restarted reads the note, and events come on workers.
public sealed class SelfUpdater : ISelfUpdate, IDisposable
{
    // Tiny Tracker's name in History.
    public static readonly PackageKey Key = new("Bikuuuu.TinyTracker", "github");
    // Setup should have closed the app by then, or at least be seen to run.
    public static readonly TimeSpan SetupGrace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly IElevation _elevation;
    private readonly InstallQueue _queue;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly Func<bool> _setupRunning;
    private readonly CancellationTokenSource _stop = new();
    private readonly SpeedMeter _speed;
    private SelfUpdateState _state = new(SelfUpdateStage.None, null);
    private SelfRelease? _latest;
    private CancellationTokenSource? _cancel;
    private Task _run = Task.CompletedTask;
    private Task _stopped = Task.CompletedTask;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Kept while Setup runs, one the last run started too, until the app closes or Setup is seen to have failed.
    private IDisposable? _hold;
    private ITimer? _watch;
    private bool _disposed;

    // setupRunning: whether a Setup of Tiny Tracker runs now.
    public SelfUpdater(SelfVersion running, IElevation elevation, InstallQueue queue, SettingsStore settings, HistoryStore history, TimeProvider time, FileLog log,
        Func<bool> setupRunning)
    {
        Running = running;
        _elevation = elevation;
        _queue = queue;
        _settings = settings;
        _history = history;
        _time = time;
        _log = log;
        _setupRunning = setupRunning;
        _speed = new SpeedMeter(time);
        queue.BecameIdle += (_, _) => Volatile.Read(ref _idle).TrySetResult();
    }

    public event EventHandler<SelfUpdateState>? Changed;

    // It installed by itself, and the app now runs that version: a toast says so.
    public event EventHandler<SelfVersion>? UpdatedByItself;

    public SelfVersion Running { get; private set; }

    public SelfUpdateState State
    {
        get { lock (_gate) return _state; }
    }

    // Completes once a run stopped after Dispose, its note cleared unless Setup started.
    public Task Stopped
    {
        get { lock (_gate) return _stopped; }
    }

    // The latest release, from a check. A run goes on with the release it started with.
    public void Offer(SelfRelease? release)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest = release is not null && release.Version.IsNewerThan(Running) ? release : null;
            if (Busy) return;
            if (_latest is null) Show(new SelfUpdateState(SelfUpdateStage.None, null));
            // A failure or a declined prompt stays until another version comes.
            // One after a restart knew the release only from its note.
            else if (_state.Stage == SelfUpdateStage.None || _state.Release?.Version != _latest.Version) Show(new SelfUpdateState(SelfUpdateStage.Available, _latest));
            else if (_state.Release != _latest) Show(_state with { Release = _latest });
        }
    }

    // Update's click, or the Auto rules.
    // Each attempt counts, a cancelled one too, so the next automatic one on that version waits 12 h.
    public void Update(bool automatic)
    {
        lock (_gate)
        {
            if (_disposed || _state.Stage is not (SelfUpdateStage.Available or SelfUpdateStage.Failed) || _state.Release is not { } release) return;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _cancel = cancel;
            var hold = _queue.TryHold();
            Show(new SelfUpdateState(hold is null ? SelfUpdateStage.WaitsForOthers : Starting(automatic), release));
            _run = Task.Run(() => RunAsync(release, automatic, hold, cancel));
        }
    }

    // While it waits, asks or downloads; once Setup started, there's nothing to cancel.
    public void Cancel()
    {
        lock (_gate)
        {
            if (_state.Stage is SelfUpdateStage.WaitsForOthers or SelfUpdateStage.AwaitingPermission or SelfUpdateStage.Downloading) _ = _cancel?.CancelAsync();
        }
    }

    // At start, what came of the self-update settings.json notes (spec §6.5); the demo's pretend restart too.
    public void Restarted(SelfVersion running)
    {
        SelfUpdateNote note;
        SelfUpdateOutcome outcome;
        bool known;
        lock (_gate)
        {
            if (_disposed) return;
            Running = running;
            if (_latest is not null && !_latest.Version.IsNewerThan(running)) _latest = null;
            if (_settings.Current.SelfUpdate.Note is not { } noted) return;
            note = noted;
            outcome = note.Outcome(running, _setupRunning(), _time.GetUtcNow());
            var release = SelfVersion.Parse(note.To) is { } to ? _state.Release?.Version == to ? _state.Release : new SelfRelease(to, note.StartedAt) : null;
            if (outcome == SelfUpdateOutcome.Pending)
            {
                // One this run holds already stays.
                _hold ??= _queue.TryHold();
                Show(new SelfUpdateState(SelfUpdateStage.Installing, release));
                Watch(WatchEvery);
                return;
            }
            // A start whose note couldn't be cleared recorded it already, and told of it: a failure isn't news again either.
            known = Recorded(note, outcome == SelfUpdateOutcome.Updated ? HistoryResult.Updated : HistoryResult.Failed);
            Settle(outcome == SelfUpdateOutcome.Failed && release is not null
                ? new SelfUpdateState(SelfUpdateStage.Failed, release) { Outcome = SetupFailed, Automatic = note.Automatic && !known }
                : Offered);
        }
        if (outcome == SelfUpdateOutcome.NotStarted)
        {
            _log.Info($"The self-update to {note.To} stopped before Setup started.");
            ForgetNote();
            return;
        }
        var updated = outcome == SelfUpdateOutcome.Updated;
        if (!known) Record(note.From, note.To, new InstallDone(updated ? new UpgradeOutcome(UpgradeResult.Updated) : SetupFailed));
        ForgetNote(updated ? null : note.To);
        if (updated && note.Automatic && !known) Tell(running);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watch?.Dispose();
            _stopped = StopAsync(_run);
        }
    }

    private static UpgradeOutcome SetupFailed => new(UpgradeResult.Failed, UpgradeFailure.InstallerFailed);

    // Guarded by the lock.
    private bool Busy => _state.Stage is SelfUpdateStage.WaitsForOthers or SelfUpdateStage.AwaitingPermission or SelfUpdateStage.Downloading or SelfUpdateStage.Installing;

    // Guarded by the lock.
    private SelfUpdateState Offered => _latest is null ? new SelfUpdateState(SelfUpdateStage.None, null) : new SelfUpdateState(SelfUpdateStage.Available, _latest);

    // What the row says once the queue holds: the prompt, unless there's none.
    private SelfUpdateStage Starting(bool automatic) => !automatic && _elevation.Prompts ? SelfUpdateStage.AwaitingPermission : SelfUpdateStage.Downloading;

    private async Task StopAsync(Task run)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);
        lock (_gate)
        {
            _hold?.Dispose();
            _hold = null;
        }
    }

    private async Task RunAsync(SelfRelease release, bool automatic, IDisposable? hold, CancellationTokenSource cancel)
    {
        var outcome = new UpgradeOutcome(UpgradeResult.Cancelled);
        try
        {
            Attempted(release, automatic);
            hold ??= await HoldAsync(automatic, cancel.Token);
            outcome = Noted(release, automatic) ? await InstallAsync(release, automatic, cancel.Token) : new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, "settings not saved");
        }
        catch (OperationCanceledException)
        {
        }
        // One nothing expected is a failure like the others, so the run never faults: Quit waits on it.
        catch (Exception e)
        {
            _log.Error("Self-update failed", e);
            outcome = new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.Other, $"0x{e.HResult:X8}");
        }
        finally
        {
            var started = outcome.Result == UpgradeResult.Updated;
            if (started) MarkStarted();
            else ForgetNote(WaitsForTheUser(outcome, automatic) ? release.Version.ToString() : null);
            // In settings.json and History before the row shows it.
            bool disposed;
            lock (_gate) disposed = _disposed;
            if (!started && !disposed && outcome.Result != UpgradeResult.Cancelled) Record(Running.ToString(), release.Version.ToString(), new InstallDone(outcome));
            lock (_gate)
            {
                if (_cancel == cancel) _cancel = null;
                if (started && !_disposed)
                {
                    _hold = hold;
                    Show(_state with { Stage = SelfUpdateStage.Installing });
                    Watch(SetupGrace);
                }
                else
                {
                    hold?.Dispose();
                    if (!_disposed) Show(After(release, outcome, automatic));
                }
            }
            cancel.Dispose();
        }
    }

    // Once no other update runs and no admin helper is held, the queue holds.
    private async Task<IDisposable> HoldAsync(bool automatic, CancellationToken ct)
    {
        while (true)
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _idle, idle);
            if (_queue.TryHold() is { } hold)
            {
                lock (_gate) Show(_state with { Stage = Starting(automatic) });
                return hold;
            }
            await idle.Task.WaitAsync(ct);
        }
    }

    // Whatever comes of it, even a cancel, the next automatic one waits 12 h. A click answers a failure that waited for the user.
    private void Attempted(SelfRelease release, bool automatic)
    {
        var now = _time.GetUtcNow();
        try
        {
            _settings.Update(file => file with
            {
                SelfUpdate = file.SelfUpdate with
                {
                    AttemptedVersion = release.Version.ToString(),
                    AttemptedAt = now,
                    FailedVersion = automatic ? file.SelfUpdate.FailedVersion : null,
                },
            });
        }
        catch (IOException e)
        {
            _log.Warn($"The self-update's attempt wasn't saved: {e.Message}");
        }
    }

    // Before the helper hears of it: the app that Setup starts again reads it. False when it couldn't be saved.
    private bool Noted(SelfRelease release, bool automatic)
    {
        var now = _time.GetUtcNow();
        try
        {
            _settings.Update(file => file with
            {
                SelfUpdate = file.SelfUpdate with { Note = new SelfUpdateNote { From = Running.ToString(), To = release.Version.ToString(), Automatic = automatic, StartedAt = now } },
            });
            return true;
        }
        catch (IOException e)
        {
            _log.Warn($"The self-update's note wasn't saved: {e.Message}");
            return false;
        }
    }

    private async Task<UpgradeOutcome> InstallAsync(SelfRelease release, bool automatic, CancellationToken ct)
    {
        var starting = _elevation.StartAsync(!automatic, open => OnPrompting(ct, open), ct);
        HelperStart start;
        try
        {
            start = await starting.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // A helper that starts after all is hung up on.
            _ = starting.ContinueWith(t => t.Result.Session?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw;
        }
        if (ct.IsCancellationRequested)
        {
            start.Session?.Dispose();
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        if (start.Result == HelperStartResult.Declined) return new UpgradeOutcome(UpgradeResult.PermissionDeclined);
        if (start.Result == HelperStartResult.NeedsPrompt) return new UpgradeOutcome(UpgradeResult.NeedsAdmin);
        if (start is not { Result: HelperStartResult.Started, Session: { } helper }) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperNotStarted, start.Code);
        using var session = helper;
        lock (_gate)
        {
            _speed.Add(0);
            Show(_state with { Stage = SelfUpdateStage.Downloading, Progress = default, BytesPerSecond = 0 });
        }
        var outcome = await session.SelfUpdateAsync(release.Version, new Reporter(OnProgress), ct);
        // Setup, once started, runs whatever the click said.
        return ct.IsCancellationRequested && outcome.Result != UpgradeResult.Updated ? new UpgradeOutcome(UpgradeResult.Cancelled) : outcome;
    }

    // The row says "Waiting for permission…" only while the prompt shows, one in silent mode with its task missing too
    // (spec §4.3). attempt: the run the prompt is for; one that was cancelled may close its prompt late.
    private void OnPrompting(CancellationToken attempt, bool open)
    {
        lock (_gate)
        {
            if (_disposed || _cancel is null || !_cancel.Token.Equals(attempt)) return;
            var stage = open ? SelfUpdateStage.AwaitingPermission : SelfUpdateStage.Downloading;
            if (_state.Stage is SelfUpdateStage.AwaitingPermission or SelfUpdateStage.Downloading && _state.Stage != stage) Show(_state with { Stage = stage });
        }
    }

    private void OnProgress(UpgradeProgress progress)
    {
        lock (_gate)
        {
            if (_state.Stage != SelfUpdateStage.Downloading) return;
            _speed.Add(progress.BytesDownloaded);
            Show(_state with { Progress = progress, BytesPerSecond = _speed.BytesPerSecond });
        }
    }

    // Only an automatic one whose reason can pass goes again by itself (spec §6.2, §6.5).
    private static bool WaitsForTheUser(UpgradeOutcome outcome, bool automatic) =>
        outcome.Result is not (UpgradeResult.Updated or UpgradeResult.Cancelled or UpgradeResult.NeedsAdmin or UpgradeResult.PermissionDeclined)
        && !(automatic && outcome.IsTemporary());

    // Guarded by the lock. What the row offers after a run that didn't start Setup.
    private SelfUpdateState After(SelfRelease release, UpgradeOutcome outcome, bool automatic)
    {
        var offered = _latest ?? release;
        return outcome.Result switch
        {
            UpgradeResult.Cancelled or UpgradeResult.NeedsAdmin => new SelfUpdateState(SelfUpdateStage.Available, offered),
            UpgradeResult.PermissionDeclined => new SelfUpdateState(SelfUpdateStage.Available, offered) { Declined = true },
            _ => new SelfUpdateState(SelfUpdateStage.Failed, release) { Outcome = outcome, Automatic = automatic },
        };
    }

    // Guarded by the lock. Setup closes the app; one still here once Setup ended failed.
    private void Watch(TimeSpan first)
    {
        _watch?.Dispose();
        _watch = _time.CreateTimer(_ => Look(), null, first, WatchEvery);
    }

    private void Look()
    {
        SelfUpdateNote? note;
        SelfRelease? release;
        lock (_gate)
        {
            if (_disposed || _state.Stage != SelfUpdateStage.Installing) return;
            note = _settings.Current.SelfUpdate.Note;
            // This run started Setup, whether or not the mark was saved.
            // A queue that was busy at the restart is held once it's idle.
            if (note is not null && (note with { SetupStarted = true }).Outcome(Running, _setupRunning(), _time.GetUtcNow()) != SelfUpdateOutcome.Failed)
            {
                _hold ??= _queue.TryHold();
                return;
            }
            release = _state.Release;
            Settle(new SelfUpdateState(SelfUpdateStage.Failed, release) { Outcome = SetupFailed, Automatic = note?.Automatic ?? false });
        }
        Record(note?.From ?? Running.ToString(), note?.To ?? release?.Version.ToString(), new InstallDone(SetupFailed));
        ForgetNote(note?.To ?? release?.Version.ToString());
    }

    // Guarded by the lock: the run is over, and the queue goes on.
    private void Settle(SelfUpdateState state)
    {
        _watch?.Dispose();
        _watch = null;
        _hold?.Dispose();
        _hold = null;
        Show(state);
    }

    // Guarded by the lock.
    private void Show(SelfUpdateState state)
    {
        _state = state;
        try
        {
            Changed?.Invoke(this, state);
        }
        catch (Exception e)
        {
            _log.Error("Self-update state not delivered", e);
        }
    }

    private void Tell(SelfVersion version)
    {
        try
        {
            UpdatedByItself?.Invoke(this, version);
        }
        catch (Exception e)
        {
            _log.Error("Self-update news not delivered", e);
        }
    }

    // Setup started: the note's hour counts from now, and the restarted app can tell.
    private void MarkStarted()
    {
        var now = _time.GetUtcNow();
        try
        {
            _settings.Update(file => file.SelfUpdate.Note is { } note ? file with { SelfUpdate = file.SelfUpdate with { Note = note with { StartedAt = now, SetupStarted = true } } } : file);
        }
        catch (IOException e)
        {
            _log.Warn($"The self-update's note wasn't marked: {e.Message}");
        }
    }

    // failed: a version whose failure waits for the user, across restarts too, so no automatic attempt comes back to it
    // (spec §6.5). It goes in the same write, so it's never lost with the note.
    private void ForgetNote(string? failed = null)
    {
        try
        {
            _settings.Update(file => file.SelfUpdate.Note is null && failed is null ? file
                : file with { SelfUpdate = file.SelfUpdate with { Note = null, FailedVersion = failed ?? file.SelfUpdate.FailedVersion } });
        }
        catch (IOException e)
        {
            _log.Warn($"The self-update's note wasn't cleared: {e.Message}");
        }
    }

    // History has this outcome already, recorded since the note's Setup started; a start dated after now counts as now.
    private bool Recorded(SelfUpdateNote note, HistoryResult result) => _history.Entries.Any(e => e.Id == Key.Id && e.Source == Key.Source && e.Result == result
        && e.FromVersion == note.From && e.ToVersion == note.To && (e.Time >= note.StartedAt || note.StartedAt > _time.GetUtcNow()));

    private void Record(string from, string? to, InstallDone done)
    {
        try
        {
            _history.Add(new HistoryEntry
            {
                Time = _time.GetUtcNow(),
                Id = Key.Id,
                Source = Key.Source,
                Name = AppInfo.Name,
                Result = done.HistoryResult,
                FromVersion = from,
                ToVersion = to,
                Reason = done.Reason,
                Code = done.Outcome.Code,
            });
        }
        catch (IOException e)
        {
            _log.Warn($"History of the self-update not saved: {e.Message}");
        }
    }

    // Reports on the calling thread, unlike Progress<T>.
    private sealed class Reporter(Action<UpgradeProgress> report) : IProgress<UpgradeProgress>
    {
        public void Report(UpgradeProgress value) => report(value);
    }
}
