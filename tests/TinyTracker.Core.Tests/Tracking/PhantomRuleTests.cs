using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.Core.Tests.Tracking;

public class PhantomRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TrackedApp Offered = new()
    {
        Id = "Example.Editor",
        Source = "winget",
        Offer = new Offer { Version = "2025.1.3", FirstSeen = Now },
    };

    [Fact]
    public void NothingChangedAndStillOffered_IsPhantom() =>
        Assert.True(PhantomRule.IsPhantom("2025.1.2", "2025.1.2", "2025.1.3", "2025.1.3"));

    [Fact]
    public void TrailingZeros_StillCountAsUnchanged() =>
        Assert.True(PhantomRule.IsPhantom("2025.1.2", "2025.1.2.0", "2025.1.3", "2025.1.3.0"));

    [Fact]
    public void InstalledVersionChanged_IsARealUpdate() =>
        Assert.False(PhantomRule.IsPhantom("2025.1.2", "2025.1.3", "2025.1.3", null));

    [Fact]
    public void NoLongerOffered_IsNotPhantom() =>
        Assert.False(PhantomRule.IsPhantom("2025.1.2", "2025.1.2", "2025.1.3", null));

    [Fact]
    public void ADifferentVersionOffered_IsNotPhantom() =>
        Assert.False(PhantomRule.IsPhantom("2025.1.2", "2025.1.2", "2025.1.3", "2025.1.4"));

    [Fact]
    public void Flag_MarksTheMatchingOffer() =>
        Assert.True(PhantomRule.Flag(Offered, "2025.1.3").Offer!.Phantom);

    [Fact]
    public void Flag_IgnoresAnotherVersion() => Assert.Same(Offered, PhantomRule.Flag(Offered, "2025.1.4"));

    [Fact]
    public void Flag_WithoutAnOffer_ChangesNothing()
    {
        var app = Offered with { Offer = null };
        Assert.Same(app, PhantomRule.Flag(app, "2025.1.3"));
    }
}
