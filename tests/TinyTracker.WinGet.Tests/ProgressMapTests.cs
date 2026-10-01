using Microsoft.Management.Deployment;
using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.WinGet.Tests;

public class ProgressMapTests
{
    [Theory]
    [InlineData(PackageInstallProgressState.Queued, UpgradeStage.Queued)]
    [InlineData(PackageInstallProgressState.Downloading, UpgradeStage.Downloading)]
    [InlineData(PackageInstallProgressState.Installing, UpgradeStage.Installing)]
    [InlineData(PackageInstallProgressState.PostInstall, UpgradeStage.Finishing)]
    [InlineData(PackageInstallProgressState.Finished, UpgradeStage.Finishing)]
    public void States_MapToStages(PackageInstallProgressState state, UpgradeStage stage) =>
        Assert.Equal(stage, ProgressMap.From(new InstallProgress { State = state }).Stage);

    [Fact]
    public void BytesAndFractions_AreKept() =>
        Assert.Equal(
            new UpgradeProgress(UpgradeStage.Downloading, 180, 400, 0.45, 0),
            ProgressMap.From(new InstallProgress { State = PackageInstallProgressState.Downloading, BytesDownloaded = 180, BytesRequired = 400, DownloadProgress = 0.45 }));

    [Theory]
    [InlineData(double.NaN, 0.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.7, 1.0)]
    public void Fractions_StayBetweenZeroAndOne(double raw, double expected) =>
        Assert.Equal(expected, ProgressMap.From(new InstallProgress { State = PackageInstallProgressState.Installing, InstallationProgress = raw }).InstallFraction);
}
