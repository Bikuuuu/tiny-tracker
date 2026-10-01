using TinyTracker.Core.Elevation;
using TinyTracker.Presentation.Demo;
using Xunit;

namespace TinyTracker.Presentation.Tests.Demo;

// The demo's switch takes no prompt and registers no task (spec §12).
public sealed class DemoSilentModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switch_TurnsOnAndOff_WithNothingToAsk()
    {
        var silent = new DemoSilentMode();
        Assert.Equal((SilentModeAvailability.Available, false), (silent.Availability, silent.TaskExists));
        Assert.Equal((SwitchResult.Done, (string?)null), await silent.TurnOnAsync(Ct));
        Assert.True(silent.TaskExists);
        Assert.Equal((SwitchResult.Done, (string?)null), await silent.TurnOffAsync(Ct));
        Assert.False(silent.TaskExists);
    }
}
