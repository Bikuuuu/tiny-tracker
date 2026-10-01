using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;

namespace TinyTracker.WinGet;

// One connection to winget's COM server, for a single check, listing or upgrade.
// The server's vectors don't expose IIterable, so they're indexed, never enumerated.
public sealed class WinGetSession : IWinGetQueries
{
    private const int BatchSize = 100;
    private const uint SearchLimit = 500;
    // winget allows no longer release notes.
    private const int NotesLimit = 10_000;

    private readonly PackageManager _manager;
    // Installed packages matched with the winget catalog, and the catalog alone.
    private readonly PackageCatalog _installed;
    private readonly PackageCatalog _catalog;

    private WinGetSession(PackageManager manager, PackageCatalog installed, PackageCatalog catalog, string version)
    {
        _manager = manager;
        _installed = installed;
        _catalog = catalog;
        Version = version;
    }

    public string Version { get; }

    // Throws PackageSourceException when winget is missing, too old or not answering.
    public static Task<WinGetSession> OpenAsync(CancellationToken ct) => Run(Open, ct);

    // Only the version, with no catalog opened.
    public static Task<string> ReadVersionAsync(CancellationToken ct) => Run(() => VersionOf(new PackageManager()), ct);

    public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(IReadOnlySet<string> details, CancellationToken ct) =>
        Run(() => Installed(_installed.FindPackages(new FindPackagesOptions()), details.Contains), ct);

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByIdAsync(IReadOnlyCollection<string> ids, bool details, CancellationToken ct) =>
        Batched(ids, batch => Installed(_installed.FindPackages(Selecting(PackageMatchField.Id, PackageFieldMatchOption.EqualsCaseInsensitive, batch)), _ => details), ct);

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Batched(names, batch => Installed(_installed.FindPackages(Selecting(PackageMatchField.Name, PackageFieldMatchOption.EqualsCaseInsensitive, batch)), _ => false), ct);

    public Task<IReadOnlyList<CatalogEntry>> FindCatalogByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Batched(ids, batch => Entries(_catalog.FindPackages(Selecting(PackageMatchField.Id, PackageFieldMatchOption.EqualsCaseInsensitive, batch))), ct);

    public Task<IReadOnlyList<CatalogEntry>> SearchCatalogByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Batched(names, batch => Entries(_catalog.FindPackages(Selecting(PackageMatchField.Name, PackageFieldMatchOption.ContainsCaseInsensitive, batch, SearchLimit))), ct);

    // Upgrades to exactly this version; a late cancel is ignored, as winget can't stop a started installer. limited: through the
    // command line, under the speed limit, once the checks passed, with the catalog's id, as its --exact matches case.
    internal async Task<UpgradeOutcome> UpgradeAsync(string id, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct,
        Func<string, IProgress<UpgradeProgress>?, CancellationToken, Task<UpgradeOutcome>>? limited = null)
    {
        var (package, versionId, installed) = await Run(() => Find(id, version), ct);
        if (package is null) return new UpgradeOutcome(UpgradeResult.NotInstalled);
        // winget must offer exactly this version, and it must be newer than the one installed.
        if (versionId is null || !PackageVersion.IsNewer(version, installed))
            return new UpgradeOutcome(UpgradeResult.NoUpdate, Code: versionId is null ? $"installed {installed}, not in winget" : $"installed {installed}");
        ct.ThrowIfCancellationRequested();
        if (limited is not null) return await limited(package.Id, progress, ct);
        var stage = (int)UpgradeStage.Queued;
        try
        {
            var operation = _manager.UpgradePackageAsync(package, new InstallOptions
            {
                PackageVersionId = versionId,
                PackageInstallMode = PackageInstallMode.Silent,
                AcceptPackageAgreements = true,
            });
            operation.Progress = (_, raw) =>
            {
                var mapped = ProgressMap.From(raw);
                Volatile.Write(ref stage, (int)mapped.Stage);
                progress?.Report(mapped);
            };
            // Cancel on the thread pool: the canceller may be a UI thread, and the call crosses into winget's process.
            using var registration = ct.Register(() => _ = Task.Run(() =>
            {
                if ((UpgradeStage)Volatile.Read(ref stage) is not (UpgradeStage.Queued or UpgradeStage.Downloading)) return;
                try
                {
                    operation.Cancel();
                }
                catch (Exception e) when (e is COMException or InvalidOperationException)
                {
                }
            }));
            var result = await operation;
            return ErrorMap.ForUpgrade(result.Status, result.ExtendedErrorCode?.HResult ?? 0, result.InstallerErrorCode);
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        catch (Exception e)
        {
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable, $"0x{e.HResult:X8}");
        }
    }

    // The installed package with this id (the one with an update when there are two), its catalog entry for this version,
    // and the installed version.
    private (CatalogPackage? Package, PackageVersionId? Version, string? Installed) Find(string id, string version)
    {
        var options = new FindPackagesOptions();
        options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
        var matches = Checked(_installed.FindPackages(options));
        CatalogPackage? chosen = null;
        for (var i = 0; i < matches.Count; i++)
        {
            var package = matches[i].CatalogPackage;
            if (package.InstalledVersion is null || package.DefaultInstallVersion is null) continue;
            if (!string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (chosen is null || (package.IsUpdateAvailable && !chosen.IsUpdateAvailable)) chosen = package;
        }
        if (chosen is null) return (null, null, null);
        var installed = chosen.InstalledVersion.Version;
        var versions = chosen.AvailableVersions;
        for (var i = 0; i < versions.Count; i++)
            if (PackageVersion.Same(versions[i].Version, version)) return (chosen, versions[i], installed);
        return (chosen, null, installed);
    }

    private static WinGetSession Open()
    {
        var manager = new PackageManager();
        var version = VersionOf(manager);
        if (!WinGetVersion.IsSupported(version))
            throw new PackageSourceException(CheckProblem.WinGetTooOld, $"winget {version} is older than {WinGetVersion.Minimum}.");
        var reference = Reference(manager);
        var options = new CreateCompositePackageCatalogOptions { CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs };
        options.Catalogs.Add(reference);
        return new WinGetSession(manager, Connect(manager.CreateCompositePackageCatalog(options)), Connect(reference), version);
    }

    // A winget too old to know the call throws InvalidCastException.
    private static string VersionOf(PackageManager manager)
    {
        try
        {
            return manager.Version;
        }
        catch (InvalidCastException e)
        {
            throw new PackageSourceException(CheckProblem.WinGetTooOld, $"winget is older than {WinGetVersion.Minimum}.", e);
        }
    }

    private static PackageCatalogReference Reference(PackageManager manager) =>
        manager.GetPackageCatalogByName(TrackedApp.WinGet) ?? throw new PackageSourceException(CheckProblem.Failed, "The winget source isn't set up.");

    private static PackageCatalog Connect(PackageCatalogReference reference)
    {
        var result = reference.Connect();
        if (result.Status == ConnectResultStatus.Ok) return result.PackageCatalog;
        throw new PackageSourceException(CheckProblem.WinGetUnreachable, $"Can't open the winget catalog: {result.Status}.", result.ExtendedErrorCode);
    }

    // details: whether a catalog id's update needs its notes and elevation, which take a call each.
    private static IReadOnlyList<InstalledPackage> Installed(FindPackagesResult result, Func<string, bool> details)
    {
        var matches = Checked(result);
        var packages = new List<InstalledPackage>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var package = matches[i].CatalogPackage;
            var installed = package.InstalledVersion;
            if (installed is null) continue;
            var latest = package.DefaultInstallVersion;
            var update = latest is not null && package.IsUpdateAvailable;
            var detailed = update && details(package.Id);
            var scope = ScopeOf(installed);
            var (notes, notesUrl) = detailed ? Notes(latest!) : (null, null);
            packages.Add(new InstalledPackage(
                installed.Id,
                package.Name,
                string.IsNullOrWhiteSpace(installed.Publisher) ? latest?.Publisher ?? "" : installed.Publisher,
                installed.Version,
                latest is null ? null : package.Id,
                latest?.DisplayName,
                latest?.Version,
                update,
                notesUrl,
                scope,
                detailed ? ElevationOf(latest!, scope) : InstallerElevation.Unknown,
                notes));
        }
        return packages;
    }

    // winget says "User" or "System" for an uninstall entry, and nothing for an MSIX package, which is per user.
    private static InstallScope ScopeOf(PackageVersionInfo installed)
    {
        try
        {
            var scope = installed.GetMetadata(PackageVersionMetadataField.InstalledScope);
            if (string.Equals(scope, "User", StringComparison.OrdinalIgnoreCase)) return InstallScope.User;
            if (string.Equals(scope, "System", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "Machine", StringComparison.OrdinalIgnoreCase))
                return InstallScope.Machine;
            return installed.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase) ? InstallScope.User : InstallScope.Unknown;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return InstallScope.Unknown;
        }
    }

    // Most manifests don't state a scope, so the filter also accepts installers without one.
    public static PackageInstallScope InstallerScopeFor(InstallScope scope) => scope switch
    {
        InstallScope.User => PackageInstallScope.UserOrUnknown,
        InstallScope.Machine => PackageInstallScope.SystemOrUnknown,
        _ => PackageInstallScope.Any,
    };

    // The installer an update would use: the one for the installed scope (spec §6.3).
    private static InstallerElevation ElevationOf(PackageVersionInfo latest, InstallScope scope)
    {
        try
        {
            var options = new InstallOptions { PackageInstallScope = InstallerScopeFor(scope) };
            return latest.GetApplicableInstaller(options)?.ElevationRequirement switch
            {
                ElevationRequirement.ElevationRequired => InstallerElevation.Required,
                ElevationRequirement.ElevationProhibited => InstallerElevation.Prohibited,
                ElevationRequirement.ElevatesSelf => InstallerElevation.ElevatesSelf,
                _ => InstallerElevation.Unknown,
            };
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return InstallerElevation.Unknown;
        }
    }

    private static IReadOnlyList<CatalogEntry> Entries(FindPackagesResult result)
    {
        var matches = Checked(result);
        var entries = new List<CatalogEntry>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var package = matches[i].CatalogPackage;
            var latest = package.DefaultInstallVersion;
            entries.Add(new CatalogEntry(package.Id, latest?.DisplayName ?? package.Name, latest?.Version ?? ""));
        }
        return entries;
    }

    private static IReadOnlyList<MatchResult> Checked(FindPackagesResult result)
    {
        if (result.Status == FindPackagesResultStatus.Ok) return result.Matches;
        var problem = result.Status == FindPackagesResultStatus.CatalogError ? CheckProblem.WinGetUnreachable : CheckProblem.Failed;
        throw new PackageSourceException(problem, $"winget search failed: {result.Status}.", result.ExtendedErrorCode);
    }

    // The offered version's release notes, their text cut at winget's limit, and their web link.
    private static (string? Text, string? Url) Notes(PackageVersionInfo latest)
    {
        try
        {
            var metadata = latest.GetCatalogPackageMetadata();
            return (CutNotes(metadata.ReleaseNotes), WebLink.Clean(metadata.ReleaseNotesUrl));
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return (null, null);
        }
    }

    // Null when empty; never longer than winget allows, and never half of a character.
    public static string? CutNotes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length <= NotesLimit) return text;
        return text[..(char.IsHighSurrogate(text[NotesLimit - 1]) ? NotesLimit - 1 : NotesLimit)];
    }

    private static FindPackagesOptions Selecting(PackageMatchField field, PackageFieldMatchOption option, IEnumerable<string> values, uint limit = 0)
    {
        var options = new FindPackagesOptions { ResultLimit = limit };
        foreach (var value in values) options.Selectors.Add(new PackageMatchFilter { Field = field, Option = option, Value = value });
        return options;
    }

    private static async Task<IReadOnlyList<T>> Batched<T>(IReadOnlyCollection<string> values, Func<string[], IReadOnlyList<T>> query, CancellationToken ct)
    {
        var results = new List<T>();
        foreach (var batch in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
            results.AddRange(await Run(() => query(batch), ct));
        return results;
    }

    // COM calls run on the thread pool, and the wait ends when ct is cancelled even if winget hangs.
    private static async Task<T> Run<T>(Func<T> call, CancellationToken ct)
    {
        try
        {
            return await Task.Run(call, ct).WaitAsync(ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or PackageSourceException))
        {
            throw new PackageSourceException(ErrorMap.ForException(e), $"winget call failed: {e.Message}", e);
        }
    }
}
