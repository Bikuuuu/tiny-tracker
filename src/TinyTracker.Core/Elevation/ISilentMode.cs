namespace TinyTracker.Core.Elevation;

public enum SilentModeAvailability
{
    Available,
    // The task may only start a helper in Program Files.
    NotInstalled,
    // On a standard account the task's highest rights aren't admin.
    NotAdmin,
}

public enum SwitchResult
{
    Done,
    // The user said no to the prompt.
    Declined,
    Failed,
}

// Silent mode's task, as Settings turns it on and off (spec §6.6).
public interface ISilentMode
{
    SilentModeAvailability Availability { get; }

    // This user's task, running this copy's helper.
    bool TaskExists { get; }

    Task<(SwitchResult Result, string? Code)> TurnOnAsync(CancellationToken ct);

    Task<(SwitchResult Result, string? Code)> TurnOffAsync(CancellationToken ct);
}
