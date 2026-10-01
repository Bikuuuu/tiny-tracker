using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.WinGet.Tests;

internal static class Downloads
{
    // The average from the first bytes to the last, in KB/s.
    public static double SpeedOf(IReadOnlyList<(TimeSpan At, UpgradeProgress Progress)> seen)
    {
        var downloading = seen.Where(s => s.Progress.Stage == UpgradeStage.Downloading && s.Progress.BytesDownloaded > 0).ToList();
        Assert.True(downloading.Count > 4, "Too little of the download was seen.");
        var (first, last) = (downloading[0], downloading[^1]);
        return (last.Progress.BytesDownloaded - first.Progress.BytesDownloaded) / 1024.0 / (last.At - first.At).TotalSeconds;
    }
}
