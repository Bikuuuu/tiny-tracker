using Microsoft.Management.Deployment;
using TinyTracker.Core.Installing;

namespace TinyTracker.WinGet;

public static class ProgressMap
{
    public static UpgradeProgress From(InstallProgress progress) =>
        new(Stage(progress.State), progress.BytesDownloaded, progress.BytesRequired, Fraction(progress.DownloadProgress), Fraction(progress.InstallationProgress));

    private static UpgradeStage Stage(PackageInstallProgressState state) => state switch
    {
        PackageInstallProgressState.Queued => UpgradeStage.Queued,
        PackageInstallProgressState.Downloading => UpgradeStage.Downloading,
        PackageInstallProgressState.Installing => UpgradeStage.Installing,
        _ => UpgradeStage.Finishing,
    };

    private static double Fraction(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
