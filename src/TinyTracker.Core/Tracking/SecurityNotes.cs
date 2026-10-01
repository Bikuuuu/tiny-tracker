using System.Text.RegularExpressions;

namespace TinyTracker.Core.Tracking;

// Whether notes mention a security fix (spec §6.2), per line: an advisory id, a vulnerability, "security" by a word for a fix or
// flaw, or a line that is only "Security" or starts "Security:", past any mark; not other uses, nor words saying there's none.
public static partial class SecurityNotes
{
    // "no security fixes", "isn't a security update", "no known vulnerabilities"
    private const string NotBefore = @"(?<!(?:\bno|\bnot|\bwithout|\bzero|\bany|n't)[ \t]+(?:(?:a|an|any|known|new|other|further|reported)[ \t]+)*)";

    // "Security: none"
    private const string NoneAfter = @"(?![ \t*]*:[ \t*]*(?:none|n/?a|nothing)\b)";

    public static bool Mention(string? notes) => notes is not null && Fix().IsMatch(notes);

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,}\b|\bGHSA(?:-[0-9a-z]{4}){3}\b|" + NotBefore + @"\bvulnerabilit(?:y|ies)\b"
        + "|" + NotBefore + @"\bsecurity[ \t-]+(?:fix(?:es|ed)?|issues?|updates?|patch(?:es|ed)?|bugs?|flaws?|holes?|advisor(?:y|ies))\b" + NoneAfter
        + @"|\b(?:fix(?:es|ed)?|patch(?:es|ed)?)[ \t]+(?:(?:a|an|the|some|several|multiple)[ \t]+)?security\b"
        + @"(?![ \t]+(?:settings?|tabs?|pages?|icons?|warnings?|prompts?|dialogs?|options?|keys?|center|questions?)\b)"
        + @"|^[ \t#*+•>-]*(?:\d+[.)][ \t]+)?[ \t*_]*security[ \t*_]*(?:" + NoneAfter + @":|\r?$)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Fix();
}
