using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.Core.Tests.Installing;

public class UpgradeOutcomeTests
{
    [Theory]
    [InlineData(UpgradeResult.Busy, UpgradeFailure.None)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.NoNetwork)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.DownloadFailed)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.Stalled)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.TookTooLong)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.HelperStopped)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.HelperNotStarted)]
    // The refusal turned the speed limit off, so the next try runs without it.
    [InlineData(UpgradeResult.Failed, UpgradeFailure.ProxyRefused)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.GitHubUnreachable)]
    public void ProblemsThatCanPass_AreTemporary(UpgradeResult result, UpgradeFailure failure) =>
        Assert.True(new UpgradeOutcome(result, failure).IsTemporary());

    [Theory]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.DiskFull)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.InstallerFailed)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.BlockedByPolicy)]
    [InlineData(UpgradeResult.NeedsAdmin, UpgradeFailure.None)]
    [InlineData(UpgradeResult.AppInUse, UpgradeFailure.None)]
    [InlineData(UpgradeResult.PermissionDeclined, UpgradeFailure.None)]
    [InlineData(UpgradeResult.NoUpdate, UpgradeFailure.None)]
    [InlineData(UpgradeResult.Updated, UpgradeFailure.None)]
    // A self-update's: another account's copy waits for the user, and an immutable release stays as it is.
    [InlineData(UpgradeResult.Failed, UpgradeFailure.OtherAccounts)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.ReleaseRefused)]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.DigestMismatch)]
    public void LastingProblems_AreNot(UpgradeResult result, UpgradeFailure failure) =>
        Assert.False(new UpgradeOutcome(result, failure).IsTemporary());
}
