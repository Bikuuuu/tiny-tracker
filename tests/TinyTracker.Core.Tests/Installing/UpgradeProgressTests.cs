using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

public class UpgradeProgressTests
{
    [Theory]
    [InlineData(UpgradeStage.Queued, true)]
    [InlineData(UpgradeStage.Downloading, true)]
    [InlineData(UpgradeStage.Installing, false)]
    [InlineData(UpgradeStage.Finishing, false)]
    public void Cancel_WorksOnlyBeforeTheInstallerStarts(UpgradeStage stage, bool canCancel) =>
        Assert.Equal(canCancel, new UpgradeProgress(stage, 0, 0, 0, 0).CanCancel);
}
