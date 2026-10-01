using System.Text;
using System.Text.RegularExpressions;

namespace TinyTracker.WinGet.Matching;

// Compares app names loosely: case, brackets, versions, architecture and punctuation don't count.
// A pre-release channel word counts even inside brackets: "Example (Beta)" isn't "Example".
public static partial class NameKey
{
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(?:(?:alpha|beta|preview|insiders?|nightly|dev|canary|rc|esr|eap)\d*|pre-?release|early access|developer edition)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Channel();

    [GeneratedRegex(@"\b(?:x64|x86[-_]64|x86|amd64|arm64|64-?bit|32-?bit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture();

    [GeneratedRegex(@"[™®©]|\bv?\d+(?:[._-]\d+)*\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarksAndVersions();

    [GeneratedRegex(@"\d+(?:\.\d+)*")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // "Python 3.12.5 (64-bit)" and "Python 3.12" both become "python".
    public static string Of(string? name)
    {
        var key = new StringBuilder();
        foreach (var c in Clean(name))
            if (char.IsLetterOrDigit(c)) key.Append(char.ToLowerInvariant(c));
        return key.ToString();
    }

    // The same cleanup kept readable for a catalog search: "Python 3.12.5 (64-bit)" becomes "Python".
    public static string SearchTerm(string? name) => Spaces().Replace(Clean(name), " ").Trim(' ', '-', ',', '.');

    // The numbers in a name, such as a track ("3.13") or a year, leaving out architecture. Leading zeros don't count.
    public static IReadOnlyList<string> Numbers(string? name) =>
        [.. Number().Matches(Architecture().Replace(name ?? "", " ")).Select(m => string.Join('.', m.Value.Split('.').Select(Part)))];

    private static string Clean(string? name)
    {
        var kept = Brackets().Replace(name ?? "", group => " " + string.Join(' ', Channel().Matches(group.Value).Select(m => m.Value)) + " ");
        return MarksAndVersions().Replace(Architecture().Replace(kept, " "), " ");
    }

    private static string Part(string part) => part.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";
}
