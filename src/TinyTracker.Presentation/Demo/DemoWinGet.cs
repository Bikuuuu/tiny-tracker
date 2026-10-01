using TinyTracker.Core.Checking;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Settings;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Presentation.Demo;

// Made-up apps whose updates walk every row state, for --demo in Debug builds. Checks 2 and 3 fail, to show the banners, check 4
// finds two new versions, the apps close for Close & update with no process behind them, and downloads run at the speed limit.
public sealed class DemoWinGet(TimeProvider time, SpeedLimit? limit = null) : IPackageSource, IPackageUpgrader, IAppInventory, IReleaseDates, IAppCloser
{
    // The app with the longest notes, which the self-check shows.
    public const string LongNotes = "Contoso Editor";

    // Short enough to watch: a stalled download is retried after 6 seconds, a busy one after 5.
    public static InstallTimings Timings { get; } = new(TimeSpan.FromSeconds(5), 3, TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(2));

    // An app in use closes this long after Close & update asks; a stubborn one only after the row has asked about Force close.
    public static readonly TimeSpan ClosesAfter = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StubbornClosesAfter = TimeSpan.FromSeconds(20);

    private const ulong MB = 1024 * 1024;
    private const string LocalIdPrefix = @"demo\";
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, App> _apps = Apps().ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    private int _checks;
    private int _busyAnswers;

    private enum Script
    {
        Updates,
        UpdatesWithoutSize,
        DiskFull,
        // In use until Close & update closes it.
        InUse,
        // In use, and slow to close.
        InUseStubborn,
        // In use, with no folder to close it from.
        InUseNoFolder,
        Restart,
        Phantom,
        BusyTwice,
        Stalls,
        Declined,
        // Its helper stops partway.
        HelperStops,
        // Under the speed limit its winget refuses the proxy, as when winget's proxy option went off.
        RefusesProxy,
    }

    // At start: a skipped version and three Auto apps (3-day wait, window two hours on): Tailspin Player waits for it, Wingtip
    // Studio a day more, Proseware Maps for Install (admin). Only Proseware Draw is new; the first check offers it.
    public static SettingsFile Settings(DateTimeOffset now, TimeZoneInfo zone) => new()
    {
        Settings = new AppSettings
        {
            AutoInstallWaitDays = 3,
            InstallWindowEnabled = true,
            InstallWindowFrom = (TimeZoneInfo.ConvertTime(now, zone).Hour + 2) % 24,
            InstallWindowTo = (TimeZoneInfo.ConvertTime(now, zone).Hour + 4) % 24,
        },
        Apps = [.. Apps().Where(a => a.Tracked).Select(a => new TrackedApp
        {
            Id = a.Id,
            Source = TrackedApp.WinGet,
            Name = a.Name,
            AutoChoice = a.Id is "Tailspin.Player" or "Wingtip.Studio" or "Proseware.Maps" ? true : null,
            SkippedVersion = a.Id == "Northwind.Clock" ? a.Available : null,
            Offer = a.Available is null ? null : new Offer { Version = a.Available, FirstSeen = now - TimeSpan.FromDays(2), Announced = true },
        })],
        KnownApps = [.. Apps().Where(a => a.Installed is not null && a.InCatalog && a.Id != "Proseware.Draw").Select(a => a.Id)],
    };

    // History the demo starts with: every kind of entry over four days. Fabrikam Chat's failure can be retried once a check
    // has run; Wingtip Studio's can't, because a later update followed it.
    public static IReadOnlyList<HistoryEntry> History(DateTimeOffset now)
    {
        HistoryEntry Entry(TimeSpan age, string id, string name, HistoryResult result, string? from, string to, string? reason = null, string? code = null) => new()
        {
            Time = now - age,
            Id = id,
            Source = TrackedApp.WinGet,
            Name = name,
            Result = result,
            FromVersion = from,
            ToVersion = to,
            Reason = reason,
            Code = code,
        };
        return
        [
            Entry(TimeSpan.FromHours(1), "Fabrikam.Chat", "Fabrikam Chat", HistoryResult.Failed, "1.9.3", "1.10.0", "DiskFull", "0x8A150105"),
            Entry(TimeSpan.FromHours(2), "Proseware.Maps", "Proseware Maps", HistoryResult.Failed, "2025.1", "2025.2", "NeedsAdmin", "0x8A150019"),
            Entry(TimeSpan.FromDays(1), "Contoso.Editor", "Contoso Editor", HistoryResult.Updated, "2.4.0", "2.4.1"),
            Entry(TimeSpan.FromDays(1.1), "Tailspin.Player", "Tailspin Player", HistoryResult.Updated, "3.0.19", "3.0.20", "RestartNeeded"),
            Entry(TimeSpan.FromDays(2), "Litware.Sync", "Litware Sync", HistoryResult.Updated, "5.0.9", "5.1.0", "Phantom"),
            Entry(TimeSpan.FromDays(2.1), "Northwind.Clock", "Northwind Clock", HistoryResult.Skipped, "1.0", "1.1"),
            Entry(TimeSpan.FromDays(2.2), "Wingtip.Studio", "Wingtip Studio", HistoryResult.Updated, "7.9", "8.0"),
            Entry(TimeSpan.FromDays(3), "Wingtip.Studio", "Wingtip Studio", HistoryResult.Failed, "7.9", "8.0", "Other"),
            Entry(TimeSpan.FromDays(3.1), "Adatum.Photos", "Adatum Photos", HistoryResult.Cancelled, "11.9", "12.0"),
        ];
    }

    public async Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5), time, ct);
        // A read of one app comes from the install queue, and one with no offers from Restore: neither is a check.
        var check = apps.Count > 1 && apps.Any(a => a.Offer is not null) ? Interlocked.Increment(ref _checks) : 0;
        if (check == 2) throw new PackageSourceException(CheckProblem.WinGetUnreachable, "Demo: winget didn't answer.");
        if (check == 3) throw new PackageSourceException(CheckProblem.WinGetTooOld, "Demo: winget 1.11.510 is older than 1.29.280.");
        lock (_gate)
        {
            // The fourth check finds new versions, for the "updates ready" and "need your permission" toasts.
            if (check == 4)
            {
                _apps["Fabrikam.Viewer"].Available = "3.4";
                _apps["Proseware.Maps"].Available = "2025.3";
            }
            var installed = new List<PackageSnapshot>();
            var gone = new List<PackageKey>();
            foreach (var tracked in apps)
            {
                if (!_apps.TryGetValue(tracked.Id, out var app) || !app.InCatalog) gone.Add(new PackageKey(tracked.Id, tracked.Source));
                else if (app.Installed is { } version)
                    installed.Add(new PackageSnapshot(app.Id, TrackedApp.WinGet, app.Name, version, app.Available, app.Publisher, app.Notes, LocalIdPrefix + app.Id, app.Scope,
                        ReleaseNotes: app.Available is null ? null : app.Text));
            }
            return new CatalogRead(installed, gone)
            {
                Listed = [.. _apps.Values.Where(a => a.Installed is not null && a.InCatalog).Select(a => new ListedApp(a.Id, a.Name, LocalIdPrefix + a.Id))],
            };
        }
    }

    public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        App app;
        lock (_gate) app = _apps[package.Id];
        try
        {
            switch (app.Script)
            {
                case Script.BusyTwice when Interlocked.Increment(ref _busyAnswers) <= 2:
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    return new UpgradeOutcome(UpgradeResult.Busy, Code: "0x8A150102");
                case Script.Declined:
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    return new UpgradeOutcome(UpgradeResult.PermissionDeclined, Code: "0x800704C7");
                case Script.RefusesProxy when limit is { KBps: > 0 }:
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.ProxyRefused, "0x8A150002");
            }
            await DownloadAsync(app, progress, ct);
            if (app.Script == Script.DiskFull) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DiskFull, "0x8A150105");
            if (app.Script == Script.HelperStops) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.HelperStopped);
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        // Like winget, a started installer can't be cancelled.
        await InstallAsync(app, progress);
        lock (_gate)
        {
            if (app.Running) return new UpgradeOutcome(UpgradeResult.AppInUse, Code: "0x8A150101");
        }
        switch (app.Script)
        {
            case Script.Restart:
                return new UpgradeOutcome(UpgradeResult.RestartNeeded, Code: "installer 3010");
            case Script.Phantom:
                return new UpgradeOutcome(UpgradeResult.Updated);
        }
        Installed(package, version);
        return new UpgradeOutcome(UpgradeResult.Updated);
    }

    // An update that went through, here or through the demo's helper.
    public void Installed(PackageKey package, string version)
    {
        lock (_gate)
        {
            if (!_apps.TryGetValue(package.Id, out var app)) return;
            app.Installed = version;
            app.Available = null;
        }
    }

    public async Task<AppInventory> ReadAsync(IProgress<AppInventory>? firstList, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        lock (_gate) firstList?.Report(Inventory(matched: false));
        await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        lock (_gate) return Inventory(matched: true);
    }

    public Task<DateOnly?> GetAsync(string id, string version, CancellationToken ct) =>
        Task.FromResult<DateOnly?>(id is "Contoso.Editor" or "Tailspin.Player" ? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-5) : null);

    // Woodgrove Mail's folder is unknown, so it can't be closed from here.
    public bool CanClose(string localId) => AppOf(localId) is { Script: not Script.InUseNoFolder };

    public IClosingApp Close(string localId) => new Closing(this, AppOf(localId) ?? throw new InvalidOperationException("Not a demo app."), time);

    private App? AppOf(string localId)
    {
        lock (_gate) return localId.StartsWith(LocalIdPrefix, StringComparison.Ordinal) ? _apps.GetValueOrDefault(localId[LocalIdPrefix.Length..]) : null;
    }

    private async Task DownloadAsync(App app, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        const ulong total = 120 * MB;
        var known = app.Script != Script.UpdatesWithoutSize;
        progress?.Report(new UpgradeProgress(UpgradeStage.Queued, 0, 0, 0, 0));
        // A step's share of the limit, which can change meanwhile.
        for (ulong done = 0; done < total; done += limit is { KBps: > 0 } ? (ulong)limit.BytesPerSecond / 4 : 8 * MB)
        {
            progress?.Report(new UpgradeProgress(UpgradeStage.Downloading, done, known ? total : 0, known ? (double)done / total : 0, 0));
            // A stalled download stops moving until the queue gives up on it.
            if (app.Script == Script.Stalls && done >= 24 * MB) await Task.Delay(Timeout.InfiniteTimeSpan, time, ct);
            if (app.Script == Script.HelperStops && done >= 24 * MB) return;
            await Task.Delay(Step, time, ct);
        }
    }

    private async Task InstallAsync(App app, IProgress<UpgradeProgress>? progress)
    {
        for (var step = 0; step <= 8; step++)
        {
            var fraction = app.Script == Script.UpdatesWithoutSize ? 0 : step / 8.0;
            progress?.Report(new UpgradeProgress(UpgradeStage.Installing, 120 * MB, 120 * MB, 1, fraction));
            await Task.Delay(Step, time, CancellationToken.None);
        }
    }

    // The plain list misses one app; the matching pass then moves it into the tickable list.
    private AppInventory Inventory(bool matched)
    {
        var trackable = _apps.Values
            .Where(a => a.Installed is { } version && version != "Unknown" && a.InCatalog && (matched || a.Id != "Northwind.Budget"))
            .Select(a => new InventoryApp(a.Id, TrackedApp.WinGet, a.Name, a.Installed!, a.Publisher, ""))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<ElsewhereApp> elsewhere =
        [
            new("Contoso Launcher", "Unknown", "Contoso", "", UpdatedBy.ItSelf),
            new("Example Game", "1.0", "Example Studio", "", UpdatedBy.Steam),
            new("Fabrikam Audio Driver", "6.0.1", "Fabrikam", "", UpdatedBy.DriverTool),
            new("Fabrikam Studio (64-bit)", "26.2", "Fabrikam", "", UpdatedBy.NoExactMatch),
            new("Litware Store App", "2.3.0", "Litware", "", UpdatedBy.MicrosoftStore),
            new("Windows Example Runtime", "10.0.1", "Microsoft Corporation", "", UpdatedBy.WindowsUpdate),
            new("Woodgrove Toolkit", "4.1", "Woodgrove", "", UpdatedBy.Unknown),
        ];
        if (!matched) elsewhere.Add(new ElsewhereApp("Northwind Budget", "3.2", "Northwind", "", UpdatedBy.Unknown));
        return new AppInventory(trackable, [.. elsewhere.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    // Adatum Backup comes last, so its helper stops only after Proseware Maps has gone through it.
    private static IEnumerable<App> Apps() =>
    [
        new("Contoso.Editor", "Contoso Editor", "Contoso", "2.4.1", "2.5.0", Script.Updates) { Text = EditorNotes },
        new("Fabrikam.Chat", "Fabrikam Chat", "Fabrikam", "1.9.3", "1.10.0", Script.DiskFull),
        new("Northwind.Notes", "Northwind Notes", "Northwind", "7.2", "7.3", Script.InUse) { Running = true, Text = "- Pinned notes stay on top.\n- Sync is faster.", Link = false },
        new("Contoso.Chat", "Contoso Chat", "Contoso", "4.0", "4.1", Script.InUseStubborn) { Running = true },
        new("Woodgrove.Mail", "Woodgrove Mail", "Woodgrove", "9.1", "9.2", Script.InUseNoFolder) { Running = true },
        new("Tailspin.Player", "Tailspin Player", "Tailspin", "3.0.20", "3.0.21", Script.Restart),
        new("Litware.Sync", "Litware Sync", "Litware", "5.1.0", "5.1.2", Script.Phantom),
        new("Adatum.Photos", "Adatum Photos", "Adatum", "12.0", "12.1", Script.BusyTwice)
        {
            Text = "Security\n- Fixed a security issue in the photo importer (CVE-2026-10001).\n- Thumbnails load faster.",
        },
        new("Woodgrove.Wallet", "Woodgrove Wallet", "Woodgrove", "4.4.0", "4.5.0", Script.Stalls),
        new("Woodgrove.Radio", "Woodgrove Radio", "Woodgrove", "1.4", "1.5", Script.RefusesProxy),
        new("Proseware.Maps", "Proseware Maps", "Proseware", "2025.1", "2025.2", Script.Updates) { Scope = InstallScope.Machine },
        new("Wingtip.Studio", "Wingtip Studio", "Wingtip", "8.0", "8.1", Script.UpdatesWithoutSize),
        new("Litware.Reader", "Litware Reader", "Litware", "6.2.0", "6.3.0", Script.Declined),
        new("Fabrikam.Viewer", "Fabrikam Viewer", "Fabrikam", "3.3", null, Script.Updates),
        new("Northwind.Clock", "Northwind Clock", "Northwind", "1.0", "1.1", Script.Updates),
        new("Contoso.Launcher", "Contoso Launcher", "Contoso", "Unknown", null, Script.Updates),
        new("Adatum.Legacy", "Adatum Legacy", "Adatum", null, null, Script.Updates),
        new("Tailspin.Tools", "Tailspin Tools", "Tailspin", "1.0", null, Script.Updates) { InCatalog = false },
        new("Adatum.Backup", "Adatum Backup", "Adatum", "3.1", "3.2", Script.HelperStops) { Scope = InstallScope.Machine },
        new("Northwind.Budget", "Northwind Budget", "Northwind", "3.2", null, Script.Updates) { Tracked = false },
        new("Proseware.Draw", "Proseware Draw", "Proseware", "5.0", null, Script.Updates) { Tracked = false },
    ];

    private sealed record App(string Id, string Name, string Publisher, string? Installed, string? Available, Script Script)
    {
        public string? Installed { get; set; } = Installed;
        public string? Available { get; set; } = Available;
        public bool InCatalog { get; init; } = true;
        public bool Tracked { get; init; } = true;
        // Per-user installs update without admin rights.
        public InstallScope Scope { get; init; } = InstallScope.User;
        // Open, so its update finds it in use.
        public bool Running { get; set; }
        // Its release notes' text, and whether they have a web page too.
        public string? Text { get; init; }
        public bool Link { get; init; } = true;
        public string? Notes => Available is null || !Link ? null : $"https://example.com/notes/{Id.ToLowerInvariant()}";
    }

    private const string EditorNotes = """
        New features
        - Tabs can be pinned, and pinned tabs stay put
          after a restart
        - Search is faster in big files
          - Up to ten times faster in files over 100 MB
          - Results show while the search runs
        - **Split view** shows two files side by side

        Changes
        - The toolbar can move to the side:
          1. Right-click the toolbar
          2. Pick Left or Right
        - Settings sync between PCs, see [the guide](https://example.com/sync)

        Bug fixes
        - Fixed a crash when saving to a network drive
        - Fixed the cursor jumping after an undo
        - Fixed printing of long lines
        """;

    // A demo app closing: it closes after a moment, a stubborn one after the row has asked about Force close.
    private sealed class Closing : IClosingApp
    {
        private readonly DemoWinGet _demo;
        private readonly App _app;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ITimer _timer;

        public Closing(DemoWinGet demo, App app, TimeProvider time)
        {
            _demo = demo;
            _app = app;
            var after = app.Script == Script.InUseStubborn ? StubbornClosesAfter : ClosesAfter;
            _timer = time.CreateTimer(_ => End(), null, after, Timeout.InfiniteTimeSpan);
        }

        public Task Closed => _closed.Task;

        public bool ForceClose()
        {
            End();
            return true;
        }

        public void Reopen()
        {
            _timer.Dispose();
            lock (_demo._gate) _app.Running = true;
        }

        private void End()
        {
            lock (_demo._gate) _app.Running = false;
            _closed.TrySetResult();
        }
    }
}
