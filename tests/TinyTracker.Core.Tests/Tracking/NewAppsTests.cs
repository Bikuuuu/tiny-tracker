using TinyTracker.Core.Checking;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Tracking;

// Apps installed after the first check that were never tracked or turned down (spec §4.3).
public class NewAppsTests
{
    private static ListedApp Listed(string id, string localId = "") => new(id, id.Split('.')[^1], localId.Length > 0 ? localId : @"ARP\Machine\X64\" + id);

    [Fact]
    public void BeforeTheFirstList_NothingIsOffered() => Assert.Empty(NewApps.Offered([Listed("Contoso.Editor")], null));

    [Fact]
    public void AppsNotKnown_AreOffered_OnceEach() =>
        Assert.Equal(["Contoso.Editor"], NewApps.Offered([Listed("Contoso.Editor"), Listed("contoso.editor"), Listed("Mozilla.Firefox")], ["Mozilla.Firefox"]).Select(a => a.Id));

    [Fact]
    public void KnownIds_MatchWhateverTheirCase() => Assert.Empty(NewApps.Offered([Listed("Contoso.Editor")], ["CONTOSO.EDITOR"]));

    // However it was installed.
    [Theory]
    [InlineData(AppInfo.LocalId)]
    [InlineData(@"ARP\User\X64\TinyTracker_is1")]
    [InlineData(@"ARP\Machine\ARM64\tinytracker_IS1")]
    public void TinyTrackerItself_IsNeverOffered(string localId) => Assert.Empty(NewApps.Offered([Listed("Example.Tracker", localId)], []));
}
