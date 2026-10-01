using TinyTracker.Core.Elevation;

namespace TinyTracker.Presentation.Demo;

// The demo's silent mode takes no prompt and registers no task; its helper then starts unelevated (spec §12).
public sealed class DemoSilentMode : ISilentMode
{
    private volatile bool _on;

    public SilentModeAvailability Availability => SilentModeAvailability.Available;

    public bool TaskExists => _on;

    public Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct) => Switch(true);

    public Task<(SwitchResult Result, string? Code)> TurnOffAsync(CancellationToken ct) => Switch(false);

    private Task<(SwitchResult Result, string? Code)> Switch(bool on)
    {
        _on = on;
        return Task.FromResult<(SwitchResult, string?)>((SwitchResult.Done, null));
    }
}
