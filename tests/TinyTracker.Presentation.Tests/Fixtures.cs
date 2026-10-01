using System.Collections.Concurrent;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;
using TinyTracker.Presentation.Shell;

namespace TinyTracker.Presentation.Tests;

// Example apps, checks and installs for the view-model tests.
internal static class Fixtures
{
    public const ulong MB = 1024 * 1024;
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    public static TrackedApp App(string id = "Example.Editor", string? offer = "2.5.0", string? skipped = null, bool auto = false, bool phantom = false) => new()
    {
        Id = id,
        Source = "winget",
        Name = Name(id),
        AutoChoice = auto ? true : null,
        SkippedVersion = skipped,
        Offer = offer is null ? null : new Offer { Version = offer, FirstSeen = Now - TimeSpan.FromDays(2), Phantom = phantom },
    };

    // Installed for all users by default, so an update asks for admin rights; InstallScope.User needs none. notes is the link, text the notes.
    public static PackageSnapshot Package(string id = "Example.Editor", string installed = "2.4.1", string? available = "2.5.0", string? notes = "https://example.com/notes",
        InstallScope scope = InstallScope.Machine, string? text = null) =>
        new(id, "winget", Name(id), installed, available, "Example Publisher", notes, $@"ARP\{scope}\X64\{Name(id)}", scope, ReleaseNotes: text);

    public static AppCheck Check(AppStatus status, string id = "Example.Editor", string installed = "2.4.1", string? offer = "2.5.0", string? skipped = null, bool auto = false,
        string? notes = "https://example.com/notes", InstallScope scope = InstallScope.Machine, bool newVersion = false, string? text = null) =>
        new(App(id, offer, skipped, auto, status == AppStatus.Phantom), status,
            status is AppStatus.NotFound or AppStatus.NotInCatalog ? null : Package(id, installed, offer, notes, scope, text), newVersion);

    public const string SecurityText = "- Fixed a security issue in the updater.";

    public static InstallRequest Request(string id = "Example.Editor", string from = "2.4.1", string to = "2.5.0") => new(new PackageKey(id, "winget"), Name(id), from, to);

    public static InstallItem Item(InstallStage stage, string id = "Example.Editor", UpgradeProgress progress = default, double speed = 0, bool busy = false) =>
        new(Request(id), stage) { Progress = progress, BytesPerSecond = speed, Busy = busy };

    public static InstallItem Done(UpgradeResult result, string id = "Example.Editor", UpgradeFailure failure = UpgradeFailure.None, string? code = null, bool phantom = false, AppCheck? after = null) =>
        new(Request(id), InstallStage.Done) { Done = new InstallDone(new UpgradeOutcome(result, failure, code), phantom, after) };

    public static UpgradeProgress Downloading(ulong bytes, ulong total) => new(UpgradeStage.Downloading, bytes, total, total == 0 ? 0 : (double)bytes / total, 0);

    // An amount as shown: no-break spaces, and a word joiner after the slash, so a wrap never splits a number from its unit.
    public static string Nb(string amount) => amount.Replace(' ', '\u00a0').Replace("/", "/\u2060");

    // "Example.Editor" becomes "Example Editor".
    public static string Name(string id) => id.Replace('.', ' ');
}

// A stand-in UI thread: posted work runs when the test pumps it.
internal sealed class TestUi
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    public int Pump()
    {
        var ran = 0;
        while (_queue.TryDequeue(out var action))
        {
            action();
            ran++;
        }
        return ran;
    }
}

// What the PC is doing, as a test sets it. Reads counts the reads.
internal sealed class FakeConditions : ISystemConditions
{
    public SystemState State { get; set; } = new(FullScreen: false, Metered: false, BatterySaver: false);
    public bool Away { get; set; }
    public int Reads { get; private set; }

    public event EventHandler? Changed;

    public SystemState Read()
    {
        Reads++;
        return State;
    }

    public bool Busy() => State.FullScreen || Away;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

// Records what the page asks of the install queue.
internal sealed class FakeInstaller : IInstaller
{
    public List<InstallRequest> Enqueued { get; } = [];
    public List<PackageKey> Cancelled { get; } = [];
    public List<PackageKey> ForceClosed { get; } = [];

    public void Enqueue(IEnumerable<InstallRequest> requests) => Enqueued.AddRange(requests);

    public void Cancel(PackageKey package) => Cancelled.Add(package);

    public void ForceClose(PackageKey package) => ForceClosed.Add(package);
}
