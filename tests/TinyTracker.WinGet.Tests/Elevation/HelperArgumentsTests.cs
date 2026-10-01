using System.Security.Principal;
using TinyTracker.Core.Elevation;
using TinyTracker.WinGet.Elevation;
using Xunit;

namespace TinyTracker.WinGet.Tests.Elevation;

// The helper takes one command line only; anything else is refused (spec §5.1, §8).
public class HelperArgumentsTests
{
    private const string User = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private static readonly string Pipe = HelperRules.NewPipeName();

#if DEBUG
    private const bool DebugBuild = true;
#else
    private const bool DebugBuild = false;
#endif

    [Fact]
    public void TheOneCommandLine_IsRead() =>
        Assert.Equal(new HelperArguments(Pipe, new SecurityIdentifier(User), false), HelperArguments.Parse(["--pipe", Pipe, "--user", User]));

    [Fact]
    public void EntraIdAccount_IsAUserToo() =>
        Assert.NotNull(HelperArguments.Parse(["--pipe", Pipe, "--user", "S-1-12-1-1111111111-2222222222-3333333333-4000000000"]));

    [Fact]
    public void Format_GivesTheSameCommandLineBack()
    {
        var arguments = new HelperArguments(Pipe, new SecurityIdentifier(User), false);
        Assert.Equal(["--pipe", Pipe, "--user", User], arguments.Format());
        Assert.Equal(arguments, HelperArguments.Parse(arguments.Format()));
    }

    [Fact]
    public void Demo_IsKnownOnlyToDebugBuilds() =>
        Assert.Equal(DebugBuild, HelperArguments.Parse(["--pipe", Pipe, "--user", User, "--demo"]) is { Demo: true });

    public static TheoryData<string[]> Refused() => new()
    {
        { [] },
        { ["--pipe", Pipe] },
        { ["--user", User, "--pipe", Pipe] },
        { ["--pipe", "Example.Pipe", "--user", User] },
        { ["--pipe", Pipe, "--user", "nobody"] },
        // Everyone and Administrators are groups, not the app's user.
        { ["--pipe", Pipe, "--user", "S-1-1-0"] },
        { ["--pipe", Pipe, "--user", "S-1-5-32-544"] },
        { ["--pipe", Pipe, "--user", User, "--user", "S-1-1-0"] },
        { ["--pipe", Pipe + " --user S-1-1-0", "--user", User] },
        { ["--pipe", Pipe, "--user", User, "--verbose"] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void OtherCommandLines_AreRefused(string[] args) => Assert.Null(HelperArguments.Parse(args));
}
