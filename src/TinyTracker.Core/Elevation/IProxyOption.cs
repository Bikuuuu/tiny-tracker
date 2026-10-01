namespace TinyTracker.Core.Elevation;

public enum SpeedLimitAvailability
{
    Available,
    // winget keeps the option per user: on a standard account the administrator who approves the prompt would get it instead.
    NotAdmin,
    // A policy turns winget's proxy option, its settings or its command line off.
    Blocked,
}

// winget's proxy option, which the speed limit needs (spec §6.4).
public interface IProxyOption
{
    SpeedLimitAvailability Availability { get; }

    // Turning it on shows a prompt; not through silent mode's task.
    bool Prompts { get; }

    // For this user, policies included. Null when winget can't say. It runs winget, so never on the UI thread.
    Task<bool?> IsOnAsync(CancellationToken ct);

    // Through the admin helper. Done once it reads on for this user.
    Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct);
}
