using System.Text;

namespace TinyTracker.Core.Launch;

// A command line as Windows splits it (CommandLineToArgvW), both ways.
public static class CommandLine
{
    public static string Join(string program, IReadOnlyList<string> arguments)
    {
        // A path can't hold a quote, and the first argument is read up to its closing quote.
        var line = new StringBuilder().Append('"').Append(program).Append('"');
        if (arguments.Count > 0) line.Append(' ').Append(Arguments(arguments));
        return line.ToString();
    }

    // What follows the program, as ShellExecute takes it.
    public static string Arguments(IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++) Append(i > 0 ? line.Append(' ') : line, arguments[i]);
        return line.ToString();
    }

    // The program ends at its closing quote, or else at the first space; backslashes count only before a quote.
    public static IReadOnlyList<string> Split(string line)
    {
        var parts = new List<string>();
        var i = 0;
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            parts.Add(end < 0 ? line[1..] : line[1..end]);
            i = end < 0 ? line.Length : end + 1;
        }
        else
        {
            while (i < line.Length && line[i] is not (' ' or '\t')) i++;
            parts.Add(line[..i]);
        }
        var part = new StringBuilder();
        var started = false;
        var quoted = false;
        while (i < line.Length)
        {
            var c = line[i];
            if (c is ' ' or '\t' && !quoted)
            {
                if (started) parts.Add(part.ToString());
                part.Clear();
                started = false;
                i++;
                continue;
            }
            started = true;
            if (c == '\\')
            {
                var backslashes = 0;
                for (; i < line.Length && line[i] == '\\'; i++) backslashes++;
                if (i < line.Length && line[i] == '"')
                {
                    // Doubled before a quote; an odd one escapes it.
                    part.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        part.Append('"');
                        i++;
                    }
                }
                else part.Append('\\', backslashes);
                continue;
            }
            if (c != '"')
            {
                part.Append(c);
                i++;
                continue;
            }
            // A run of quotes: inside quotes, each doubled one is a quote, and the quotes end there.
            var count = quoted ? 2 : 1;
            for (i++; i < line.Length && line[i] == '"'; i++)
            {
                if (++count == 3)
                {
                    part.Append('"');
                    count = 0;
                }
            }
            quoted = count == 1;
        }
        if (started) parts.Add(part.ToString());
        return parts;
    }

    // Backslashes count only before a quote: there they're doubled, and the quote is escaped.
    private static void Append(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"'))
        {
            line.Append(argument);
            return;
        }
        line.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                i++;
                backslashes++;
            }
            if (i == argument.Length)
            {
                line.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"') line.Append('\\', backslashes * 2 + 1).Append('"');
            else line.Append('\\', backslashes).Append(argument[i]);
        }
        line.Append('"');
    }
}
