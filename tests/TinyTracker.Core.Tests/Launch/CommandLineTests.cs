using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

// A command line as Windows splits it (CommandLineToArgvW), both ways.
public class CommandLineTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\Tiny Tracker\TinyTracker.exe"" --startup", new[] { @"C:\Program Files\Tiny Tracker\TinyTracker.exe", "--startup" })]
    [InlineData(@"C:\Tools\TinyTracker.exe --startup", new[] { @"C:\Tools\TinyTracker.exe", "--startup" })]
    [InlineData(@"""C:\Tools\TinyTracker.exe""", new[] { @"C:\Tools\TinyTracker.exe" })]
    [InlineData(@"app.exe  ""two words""   last", new[] { "app.exe", "two words", "last" })]
    [InlineData(@"app.exe a\\\""b c\\""d e""", new[] { "app.exe", @"a\""b", @"c\d e" })]
    [InlineData(@"app.exe """" x", new[] { "app.exe", "", "x" })]
    [InlineData(@"app.exe D:\a\b\ tail", new[] { "app.exe", @"D:\a\b\", "tail" })]
    // Inside quotes, a doubled quote is one quote, and the quotes end there.
    [InlineData(@"app.exe ""a""""b""", new[] { "app.exe", @"a""b" })]
    [InlineData(@"app.exe ""a""""b""""c""", new[] { "app.exe", @"a""bc" })]
    [InlineData(@"app.exe """""""" x", new[] { "app.exe", @""" x" })]
    [InlineData(@"app.exe """""" x", new[] { "app.exe", @"""", "x" })]
    [InlineData(@"app.exe ""a b""""c d""", new[] { "app.exe", @"a b""c", "d" })]
    [InlineData(@"app.exe a"""""" b", new[] { "app.exe", @"a""", "b" })]
    public void Split_FollowsWindowsRules(string line, string[] expected) => Assert.Equal(expected, CommandLine.Split(line));

    [Fact]
    public void Join_SplitsBackIntoTheSameArguments()
    {
        string[] arguments = ["--self-check", @"D:\Data\My Files\check.json", "", "quote\"inside", @"trailing\", @"two\\", "tab\tand space", @"back\""slash"];
        var line = CommandLine.Join(@"C:\Program Files\Tiny Tracker\TinyTracker.exe", arguments);
        Assert.Equal([@"C:\Program Files\Tiny Tracker\TinyTracker.exe", .. arguments], CommandLine.Split(line));
    }

    // What a program gets after its own name, as the shell's parameters.
    [Fact]
    public void Arguments_AreTheLineAfterTheProgram()
    {
        string[] arguments = ["--startup", "two words", @"end\"];
        Assert.Equal(@"--startup ""two words"" end\", CommandLine.Arguments(arguments));
        Assert.Equal(["app.exe", .. arguments], CommandLine.Split("app.exe " + CommandLine.Arguments(arguments)));
        Assert.Equal("", CommandLine.Arguments([]));
    }
}
