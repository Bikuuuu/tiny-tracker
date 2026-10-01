using System.Text.Json.Serialization;
using TinyTracker.Core.Installing;

namespace TinyTracker.Core.Elevation;

// What the app and the admin helper say over the pipe (spec §5.1, §8). Anything else is refused.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UpgradeRequest), "upgrade")]
[JsonDerivedType(typeof(LimitRequest), "limit")]
[JsonDerivedType(typeof(CancelRequest), "cancel")]
[JsonDerivedType(typeof(RegisterTaskRequest), "registerTask")]
[JsonDerivedType(typeof(RemoveTaskRequest), "removeTask")]
[JsonDerivedType(typeof(EnableProxyOptionRequest), "enableProxyOption")]
[JsonDerivedType(typeof(SelfUpdateRequest), "selfUpdate")]
[JsonDerivedType(typeof(StayRequest), "stay")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ProgressMessage), "progress")]
[JsonDerivedType(typeof(DoneMessage), "done")]
[JsonDerivedType(typeof(TaskDoneMessage), "taskDone")]
public abstract record HelperMessage;

// App to helper. Number ties an upgrade's progress and result to its request. Limit is the speed limit in KB/s, 0 for none.
public sealed record UpgradeRequest(int Number, string Id, string Source, string Version, int Limit) : HelperMessage;

// A new speed limit for that upgrade's download.
public sealed record LimitRequest(int Number, int KBps) : HelperMessage;

public sealed record CancelRequest(int Number) : HelperMessage;

public sealed record RegisterTaskRequest : HelperMessage;

public sealed record RemoveTaskRequest : HelperMessage;

// Turns winget's proxy option on, for the user the helper runs as (spec §6.4).
public sealed record EnableProxyOptionRequest : HelperMessage;

// Tiny Tracker's own update to that version, which the helper looks up on GitHub itself (spec §6.5). Its result is Updated once
// Setup started; the helper then exits.
public sealed record SelfUpdateRequest(int Number, string Version, int Limit) : HelperMessage;

// The app holds the helper for admin updates that wait their turn behind others: it isn't idle (spec §5.1).
public sealed record StayRequest : HelperMessage;

// Helper to app. Hello comes first; Problem is null when winget answers the elevated helper.
public sealed record HelloMessage(string? Problem) : HelperMessage;

public sealed record ProgressMessage(int Number, UpgradeStage Stage, ulong BytesDownloaded, ulong BytesRequired, double DownloadFraction, double InstallFraction) : HelperMessage
{
    public static ProgressMessage Of(int number, UpgradeProgress progress) =>
        new(number, progress.Stage, progress.BytesDownloaded, progress.BytesRequired, progress.DownloadFraction, progress.InstallFraction);

    public UpgradeProgress ToProgress() => new(Stage, BytesDownloaded, BytesRequired, DownloadFraction, InstallFraction);
}

public sealed record DoneMessage(int Number, UpgradeResult Result, UpgradeFailure Failure, string? Code) : HelperMessage
{
    public static DoneMessage Of(int number, UpgradeOutcome outcome) => new(number, outcome.Result, outcome.Failure, outcome.Code);

    public UpgradeOutcome ToOutcome() => new(Result, Failure, Code);
}

// Error is null once the task was registered or removed, or the proxy option reads on.
public sealed record TaskDoneMessage(string? Error) : HelperMessage;
