using TinyTracker.Core.SelfUpdate;

namespace TinyTracker.Presentation.Demo;

// The demo's pretend update of Tiny Tracker itself (spec §12): GitHub's latest release is 9.9.9, the demo's helper pretends to
// download it and start Setup, and a moment later the demo pretends the restart. Nothing is downloaded or installed.
public sealed class DemoSelfUpdate(TimeProvider time) : ISelfReleases
{
    public static readonly SelfVersion Version = new(9, 9, 9);
    public static readonly TimeSpan RestartsAfter = TimeSpan.FromSeconds(3);

    // Old enough for the demo's 3-day wait.
    public Task<SelfRelease?> LatestAsync(CancellationToken ct) => Task.FromResult<SelfRelease?>(new SelfRelease(Version, time.GetUtcNow() - TimeSpan.FromDays(5)));

    // Once its pretend Setup started, the app starts again on the new version, as far as the updater can tell.
    public void Follow(SelfUpdater updater) => updater.Changed += (_, state) =>
    {
        if (state.Stage != SelfUpdateStage.Installing) return;
        ITimer? timer = null;
        timer = time.CreateTimer(_ =>
        {
            timer?.Dispose();
            updater.Restarted(Version);
        }, null, RestartsAfter, Timeout.InfiniteTimeSpan);
    };
}
