using System.Globalization;

namespace TinyTracker.Core.Versions;

// winget ordering: dot-separated parts, each a number plus an optional suffix.
public sealed class PackageVersion : IComparable<PackageVersion>, IEquatable<PackageVersion>
{
    private readonly Part[] _parts;

    private PackageVersion(string text, Part[] parts, bool isUnknown)
    {
        Text = text;
        _parts = parts;
        IsUnknown = isUnknown;
    }

    public string Text { get; }

    // Blank, missing or winget's "Unknown".
    public bool IsUnknown { get; }

    public static PackageVersion Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return new PackageVersion(trimmed, [], isUnknown: true);

        var parts = trimmed.Split('.').Select(Part.Parse).ToList();
        // Trailing zeros don't count: 1.0 equals 1.0.0.
        while (parts.Count > 0 && parts[^1].IsZero) parts.RemoveAt(parts.Count - 1);
        return new PackageVersion(trimmed, [.. parts], isUnknown: false);
    }

    public static bool Same(string? a, string? b) => Parse(a).Equals(Parse(b));

    // An upgrade goes up, from a version that's known to one that's known.
    public static bool IsNewer(string? candidate, string? than) =>
        Parse(candidate) is { IsUnknown: false } next && Parse(than) is { IsUnknown: false } current && next.CompareTo(current) > 0;

    public int CompareTo(PackageVersion? other)
    {
        if (other is null) return 1;
        // Unknown sorts below every known version.
        if (IsUnknown || other.IsUnknown) return other.IsUnknown.CompareTo(IsUnknown);
        for (var i = 0; i < Math.Min(_parts.Length, other._parts.Length); i++)
        {
            var result = _parts[i].CompareTo(other._parts[i]);
            if (result != 0) return result;
        }
        return _parts.Length.CompareTo(other._parts.Length);
    }

    public bool Equals(PackageVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is PackageVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsUnknown);
        foreach (var part in _parts)
        {
            hash.Add(part.Number);
            hash.Add(part.Suffix, StringComparer.OrdinalIgnoreCase);
        }
        return hash.ToHashCode();
    }

    public override string ToString() => Text;

    private readonly record struct Part(ulong Number, string Suffix)
    {
        public bool IsZero => Number == 0 && Suffix.Length == 0;

        public static Part Parse(string text)
        {
            var part = text.Trim();
            var digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits])) digits++;
            // An overflowing number stays text, as in winget.
            return digits > 0 && ulong.TryParse(part.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? new Part(number, part[digits..])
                : new Part(0, part);
        }

        public int CompareTo(Part other)
        {
            var result = Number.CompareTo(other.Number);
            if (result != 0) return result;
            // A bare number beats the same number with a suffix: 1.2 > 1.2b.
            if (Suffix.Length == 0 || other.Suffix.Length == 0) return (Suffix.Length == 0).CompareTo(other.Suffix.Length == 0);
            return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
