using Xunit;

namespace TinyTracker.Core.Tests;

// A folder that came back after its test names that test and the files, so the late writer can be found.
public sealed class LeftoverCheckTests
{
    [Fact]
    public void Leftover_NamesItsTestAndFiles()
    {
        using var folder = new TempFolder();
        Assert.Contains(TempFolder.Created, c => c.Root == folder.Root && c.Test == TestContext.Current.Test?.TestDisplayName);
        Directory.CreateDirectory(folder.PathOf("logs"));
        File.WriteAllText(Path.Combine(folder.PathOf("logs"), "app.log"), "A late line");
        var text = LeftoverCheck.Describe([(folder.Root, "Example.Tests.SomeTest")]);
        Assert.Contains("Example.Tests.SomeTest", text);
        Assert.Contains(Path.Combine("logs", "app.log"), text);
    }

    [Fact]
    public void Leftover_WithNoTestName_NamesItsFolder()
    {
        using var folder = new TempFolder();
        Assert.Contains(Path.GetFileName(folder.Root), LeftoverCheck.Describe([(folder.Root, null)]));
    }
}
