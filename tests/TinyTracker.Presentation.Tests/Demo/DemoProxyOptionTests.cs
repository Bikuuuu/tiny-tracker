using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using TinyTracker.Presentation.Demo;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Demo;

// The demo's proxy option goes through the demo's helper and its prompt, and the helper fakes the rest (spec §12).
public sealed class DemoProxyOptionTests
{
    private readonly FakeTimeProvider _time = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TurningOn_TakesThePrompt_ThenReadsOn()
    {
        var option = new DemoProxyOption(new DemoHelper(new DemoWinGet(_time), _time));
        Assert.Equal((SpeedLimitAvailability.Available, false), (option.Availability, await option.IsOnAsync(Ct)));
        var turning = option.TurnOnAsync(Ct);
        _time.Advance(DemoHelper.PromptShownFor);
        Assert.Equal((SwitchResult.Done, (string?)null), await turning);
        Assert.True(await option.IsOnAsync(Ct));
    }

    [Fact]
    public async Task DeclinedPrompt_LeavesItOff()
    {
        var option = new DemoProxyOption(new Declining());
        Assert.Equal((SwitchResult.Declined, (string?)null), await option.TurnOnAsync(Ct));
        Assert.False(await option.IsOnAsync(Ct));
    }

    [Fact]
    public void Prompts_AsTheHelpersLauncherSays() => Assert.True(new DemoProxyOption(new Declining()).Prompts);

    private sealed class Declining : IElevation
    {
        public Task<HelperStart> StartAsync(bool mayPrompt, CancellationToken ct) => Task.FromResult(new HelperStart(HelperStartResult.Declined));
    }
}
