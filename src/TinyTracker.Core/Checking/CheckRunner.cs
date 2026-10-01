using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;

namespace TinyTracker.Core.Checking;

// Apps: a row per tracked app the check covered. Detail: the technical reason behind a problem.
public sealed record CheckCompleted(CheckTicket Ticket, DateTimeOffset At, IReadOnlyList<AppCheck> Apps, CheckProblem Problem, string? Detail = null)
{
    // Apps that stopped being tracked because they stayed uninstalled.
    public IReadOnlyList<PackageKey> Untracked { get; init; } = [];

    // Apps to offer (spec §4.3); null when the check had no list.
    public IReadOnlyList<ListedApp>? NewApps { get; init; }
}

// Runs each check the scheduler asks for, saves what it learns, and answers every CheckDue with Finished.
public sealed class CheckRunner : IDisposable
{
    // A check ends a minute before the scheduler's watchdog would give up on it.
    public static readonly TimeSpan Deadline = CheckScheduler.CheckTimeout - TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _store;
    private readonly IPackageSource _source;
    private readonly IReleaseDates _dates;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private CancellationTokenSource? _running;
    // The runs that haven't ended, cancelled ones included.
    private readonly HashSet<Task> _runs = [];
    private bool _disposed;
    // Set while settings.json couldn't be read. Once it's read, by a check or by a save, its interval times the checks.
    private volatile bool _syncInterval;

    public CheckRunner(CheckScheduler scheduler, SettingsStore store, IPackageSource source, IReleaseDates dates, TimeProvider time, FileLog log)
    {
        _scheduler = scheduler;
        _store = store;
        _source = source;
        _dates = dates;
        _time = time;
        _log = log;
        _syncInterval = store.Unreadable;
        scheduler.CheckDue += OnCheckDue;
    }

    // Raised on a worker thread after each check, unless a newer check replaced it.
    public event EventHandler<CheckCompleted>? Completed;

    // Every run so far has ended. A run cancelled by Dispose may still save, so wait on this before removing its files.
    public Task Stopped
    {
        get
        {
            lock (_gate) return Task.WhenAll(_runs);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            running = _running;
            _running = null;
        }
        _scheduler.CheckDue -= OnCheckDue;
        Cancel(running);
    }

    private void OnCheckDue(object? sender, CheckTicket ticket)
    {
        var run = new CancellationTokenSource(Deadline, _time);
        CancellationTokenSource? previous;
        Task task;
        lock (_gate)
        {
            if (_disposed)
            {
                run.Dispose();
                return;
            }
            // A new ticket means the scheduler gave up on the previous check.
            previous = _running;
            _running = run;
            // Started under the lock, so a Dispose that follows finds it in Stopped.
            task = Task.Run(() => RunAsync(ticket, run));
            _runs.Add(task);
        }
        Cancel(previous);
        task.ContinueWith(ended =>
        {
            lock (_gate) _runs.Remove(ended);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Cancel(CancellationTokenSource? run)
    {
        try
        {
            run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CheckTicket ticket, CancellationTokenSource run)
    {
        IReadOnlyList<AppCheck> apps = [];
        IReadOnlyList<PackageKey> untracked = [];
        IReadOnlyList<ListedApp>? newApps = null;
        var problem = CheckProblem.None;
        string? detail = null;
        try
        {
            (apps, untracked, newApps) = await CheckAsync(run.Token);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            (problem, detail) = (CheckProblem.TimedOut, $"No answer within {Deadline.TotalMinutes:0} minutes.");
        }
        catch (PackageSourceException e)
        {
            (problem, detail) = (e.Problem, e.Detail);
        }
        catch (IOException e)
        {
            (problem, detail) = (CheckProblem.SettingsNotSaved, e.Message);
        }
        catch (Exception e)
        {
            (problem, detail) = (CheckProblem.Failed, $"{e.GetType().Name}: {e.Message}");
            _log.Error("Check failed", e);
        }

        bool current;
        lock (_gate)
        {
            current = _running == run;
            if (current) _running = null;
        }
        run.Dispose();
        // A replaced check stays silent: the scheduler has moved on to a newer ticket.
        if (!current) return;
        if (problem is not (CheckProblem.None or CheckProblem.Failed)) _log.Warn($"Check {problem}: {detail}");
        _scheduler.Finished(ticket, problem == CheckProblem.None);
        // A listener that throws can't fault the run, which Stopped waits on.
        try
        {
            Completed?.Invoke(this, new CheckCompleted(ticket, _time.GetUtcNow(), apps, problem, detail) { Untracked = untracked, NewApps = newApps });
        }
        catch (Exception e)
        {
            _log.Error("Check not delivered", e);
        }
    }

    private async Task<(IReadOnlyList<AppCheck> Apps, IReadOnlyList<PackageKey> Untracked, IReadOnlyList<ListedApp>? NewApps)> CheckAsync(CancellationToken ct)
    {
        // A file that couldn't be read is read again first; while it still can't be, the check fails and retries.
        // Once read, its interval times the checks: the scheduler started with the default.
        if (_store.Unreadable)
        {
            _syncInterval = true;
            _store.Update(file => file);
        }
        if (_syncInterval)
        {
            _syncInterval = false;
            var interval = TimeSpan.FromHours(_store.Current.Settings.CheckIntervalHours);
            if (_scheduler.Interval != interval) _scheduler.SetInterval(interval);
        }
        var requested = _store.Current.Apps;
        if (requested.Count == 0) return ([], [], null);
        var read = await _source.ReadAsync(requested, ct);
        ct.ThrowIfCancellationRequested();
        var (apps, untracked, newApps) = Merge(requested, read);
        return (await AddReleaseDatesAsync(apps, ct), untracked, newApps);
    }

    // Merges inside Update, so a toggle saved during the check survives. Apps added meanwhile wait for the next check, removed
    // ones stay removed, and ones that stayed uninstalled go. The first list is only noted (spec §4.3).
    private (IReadOnlyList<AppCheck> Apps, IReadOnlyList<PackageKey> Untracked, IReadOnlyList<ListedApp>? NewApps) Merge(IReadOnlyList<TrackedApp> requested, CatalogRead read)
    {
        IReadOnlyList<AppCheck> checks = [];
        List<TrackedApp> gone = [];
        IReadOnlyList<ListedApp>? newApps = null;
        _store.Update(file =>
        {
            var now = _time.GetUtcNow();
            var covered = file.Apps.Where(app => requested.Any(r => r.Matches(app.Id, app.Source))).ToList();
            var all = CheckMerge.Apply(covered, read.Installed, now, read.NotInCatalog);
            gone = [.. all.Where(c => CheckMerge.Forget(c, now)).Select(c => c.App)];
            checks = [.. all.Where(c => !gone.Contains(c.App))];
            var merged = checks.Select(c => c.App).ToList();
            var apps = file.Apps.Where(app => !gone.Any(g => g.Matches(app.Id, app.Source)))
                .Select(app => merged.FirstOrDefault(m => m.Matches(app.Id, app.Source)) ?? app).ToList();
            if (read.Listed.Count == 0) return file with { Apps = apps };
            newApps = NewApps.Offered(read.Listed, file.KnownApps);
            return file with { Apps = apps, KnownApps = file.KnownApps ?? [.. read.Listed.Select(a => a.Id)] };
        });
        foreach (var app in gone) _log.Info($"Stopped tracking {app.Id}: not installed for a day");
        return (checks, [.. gone.Select(app => new PackageKey(app.Id, app.Source))], newApps);
    }

    // A date is fetched once per offered version and kept with the offer; a miss falls back to first seen.
    // The merge is already saved, so a failure here keeps the merged rows; their dates come on a later check.
    private async Task<IReadOnlyList<AppCheck>> AddReleaseDatesAsync(IReadOnlyList<AppCheck> checks, CancellationToken ct)
    {
        try
        {
            var dates = new List<(TrackedApp App, string Version, DateOnly Date)>();
            foreach (var check in checks)
            {
                if (check.Status != AppStatus.Available || check.App.Offer is not { ReleaseDate: null } offer) continue;
                if (await DateOf(check.Package?.Id ?? check.App.Id, offer.Version, ct) is { } date) dates.Add((check.App, offer.Version, date));
            }
            if (dates.Count == 0) return checks;
            _store.Update(file => file with { Apps = file.Apps.Select(app => Dated(app, dates)).ToList() });
            return checks.Select(check => check with { App = Dated(check.App, dates) }).ToList();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            _log.Warn($"Release dates not saved: {e.Message}");
            return checks;
        }
    }

    private async Task<DateOnly?> DateOf(string id, string version, CancellationToken ct)
    {
        try
        {
            return await _dates.GetAsync(id, version, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"Release date of {id} {version}: {e.Message}");
            return null;
        }
    }

    // Only the version the date was fetched for gets it.
    private static TrackedApp Dated(TrackedApp app, IReadOnlyList<(TrackedApp App, string Version, DateOnly Date)> dates)
    {
        foreach (var (dated, version, date) in dates)
        {
            if (dated.Matches(app.Id, app.Source) && app.Offer is { } offer && PackageVersion.Same(offer.Version, version))
                return app with { Offer = offer with { ReleaseDate = date } };
        }
        return app;
    }
}
