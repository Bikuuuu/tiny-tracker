using System.Security.Principal;
using TinyTracker.Core.Elevation;

namespace TinyTracker.WinGet.Elevation;

// The helper's only command line (spec §5.1): --pipe <name> --user <SID>, plus --demo in Debug builds. The user is the app's,
// whom the pipe lets in: a local, domain or Entra ID account, never a group such as Everyone.
public sealed record HelperArguments(string Pipe, SecurityIdentifier User, bool Demo)
{
    public static HelperArguments? Parse(IReadOnlyList<string> args)
    {
        var demo = args.Count == 5 && args[4] == "--demo" && DebugBuild;
        if (args.Count != 4 && !demo) return null;
        if (args[0] != "--pipe" || !HelperRules.IsPipeName(args[1]) || args[2] != "--user") return null;
        try
        {
            var user = new SecurityIdentifier(args[3]);
            var account = user.IsAccountSid() || user.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);
            return account && user.Value == args[3] ? new HelperArguments(args[1], user, demo) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> Format() => Demo ? ["--pipe", Pipe, "--user", User.Value, "--demo"] : ["--pipe", Pipe, "--user", User.Value];

#if DEBUG
    private const bool DebugBuild = true;
#else
    private const bool DebugBuild = false;
#endif
}
