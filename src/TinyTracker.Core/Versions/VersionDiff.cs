namespace TinyTracker.Core.Versions;

// The new version split into the part that matches the old one and the part that changed.
public readonly record struct VersionDiff(string Unchanged, string Changed)
{
    public static VersionDiff Between(string? oldVersion, string? newVersion)
    {
        var next = newVersion?.Trim() ?? "";
        var newParts = next.Split('.');
        var oldParts = (oldVersion ?? "").Trim().Split('.');
        var same = 0;
        while (same < newParts.Length && same < oldParts.Length
               && string.Equals(newParts[same], oldParts[same], StringComparison.OrdinalIgnoreCase))
            same++;
        if (same == newParts.Length) return new VersionDiff(next, "");
        var unchanged = same == 0 ? "" : string.Join('.', newParts[..same]) + ".";
        return new VersionDiff(unchanged, string.Join('.', newParts[same..]));
    }
}
