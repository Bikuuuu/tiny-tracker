using TinyTracker.Core.History;

namespace TinyTracker.Core.Storage;

internal sealed record HistoryFile
{
    public int Version { get; set; } = 1;
    public IReadOnlyList<HistoryEntry> Entries { get; set; } = [];
}
