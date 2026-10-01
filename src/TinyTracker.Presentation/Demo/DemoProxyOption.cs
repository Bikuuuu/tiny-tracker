using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;

namespace TinyTracker.Presentation.Demo;

// The demo's proxy option: turning it on starts the demo's helper, with its real prompt, and it fakes the rest (spec §12).
public sealed class DemoProxyOption(IElevation helper) : IProxyOption
{
    private volatile bool _on;

    public SpeedLimitAvailability Availability => SpeedLimitAvailability.Available;

    public bool Prompts => helper.Prompts;

    public Task<bool?> IsOnAsync(CancellationToken ct) => Task.FromResult<bool?>(_on);

    public async Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct)
    {
        var start = await helper.StartAsync(mayPrompt: true, ct);
        if (start.Result == HelperStartResult.Declined) return (SwitchResult.Declined, null);
        if (start.Session is not { } session) return (SwitchResult.Failed, start.Code);
        using (session)
        {
            if (await session.EnableProxyOptionAsync(ct) is { } error) return (SwitchResult.Failed, error);
        }
        _on = true;
        return (SwitchResult.Done, null);
    }
}
