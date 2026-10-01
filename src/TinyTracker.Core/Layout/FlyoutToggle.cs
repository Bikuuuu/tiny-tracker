namespace TinyTracker.Core.Layout;

public enum ToggleAction { Open, Close, Ignore }

public sealed class FlyoutToggle(TimeProvider time)
{
    public static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private long? _closedAt;

    public bool IsOpen { get; private set; }

    public ToggleAction OnTrayClick()
    {
        if (IsOpen) return ToggleAction.Close;
        if (_closedAt is long closedAt && time.GetElapsedTime(closedAt) < ReopenGuard) return ToggleAction.Ignore;
        return ToggleAction.Open;
    }

    public void Opened() => IsOpen = true;

    public void Closed()
    {
        IsOpen = false;
        _closedAt = time.GetTimestamp();
    }
}
