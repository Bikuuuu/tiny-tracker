using System.Text.RegularExpressions;

namespace TinyTracker.Presentation.Icons;

// How an app's icon is found and drawn. LocalApp says where the app lives.
public static partial class IconSource
{
    // The app's own program in its install folder: the only one left once installers and helpers are set aside,
    // or the only one whose name is part of the app's name.
    public static string? MainProgram(IEnumerable<string> fileNames, string appName)
    {
        var programs = fileNames.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Helper().IsMatch(Path.GetFileNameWithoutExtension(f))).ToList();
        if (programs.Count == 1) return programs[0];
        var name = Squash(appName);
        var named = programs.Where(f => Squash(Path.GetFileNameWithoutExtension(f)) is { Length: > 2 } file && name.Contains(file, StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    // Makes 32-bit icon pixels premultiplied BGRA. Old icons have no alpha, so their AND mask (black shows) decides.
    public static void Premultiply(Span<byte> bgra, ReadOnlySpan<byte> mask)
    {
        var hasAlpha = false;
        for (var i = 3; i < bgra.Length && !hasAlpha; i += 4) hasAlpha = bgra[i] != 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = hasAlpha ? bgra[i + 3] : mask.Length > i && mask[i] != 0 ? (byte)0 : (byte)255;
            bgra[i] = (byte)(bgra[i] * alpha / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * alpha / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * alpha / 255);
            bgra[i + 3] = alpha;
        }
    }

    [GeneratedRegex("unins|setup|install|update|crash|report|helper|elevat|service|notif", RegexOptions.IgnoreCase)]
    private static partial Regex Helper();

    private static string Squash(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
