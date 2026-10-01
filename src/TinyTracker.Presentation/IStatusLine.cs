namespace TinyTracker.Presentation;

// A status line whose "Details" or "What's new" link wraps with its words.
public interface IStatusLine
{
    string Status { get; }

    string? Details { get; }

    bool ShowNotes { get; }
}
