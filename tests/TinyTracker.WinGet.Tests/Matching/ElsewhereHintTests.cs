using TinyTracker.Core.Inventory;
using TinyTracker.WinGet.Matching;
using Xunit;

namespace TinyTracker.WinGet.Tests.Matching;

public class ElsewhereHintTests
{
    [Theory]
    [InlineData(@"ARP\Machine\X64\Steam App 12345", "Example Game", "Example Studio", "Unknown", UpdatedBy.Steam)]
    [InlineData(@"ARP\User\X64\Steam App 67890", "Example Game 2", "Example Studio", "1.0", UpdatedBy.Steam)]
    [InlineData(@"MSIX\Example.App_1.0.0.0_x64__abcdefgh", "Example App", "Example", "1.0.0.0", UpdatedBy.MicrosoftStore)]
    [InlineData(@"ARP\Machine\X64\{EXAMPLE}_Display.Driver", "Example Graphics Driver 1.2", "Example Corp", "1.2", UpdatedBy.DriverTool)]
    [InlineData(@"ARP\Machine\X64\{EXAMPLE-UPDATE}", "Microsoft Update Health Tools", "Microsoft Corporation", "5.72.0.0", UpdatedBy.WindowsUpdate)]
    [InlineData(@"ARP\Machine\X64\Example Launcher", "Example Launcher", "Example", "Unknown", UpdatedBy.ItSelf)]
    [InlineData(@"ARP\Machine\X86\Example Editor", "Example Editor", "Example", "2.0", UpdatedBy.Unknown)]
    [InlineData(@"ARP\Machine\X64\{EXAMPLE-TOOLS}", "Windows Example Tools", "Microsoftware Inc", "1.0", UpdatedBy.Unknown)]
    [InlineData(@"ARP\Machine\X64\{EXAMPLE-RUNTIME}", "Windows Example Runtime", "Microsoft", "1.0", UpdatedBy.WindowsUpdate)]
    public void Hint_NamesWhatUpdatesTheApp(string localId, string name, string publisher, string version, UpdatedBy hint) =>
        Assert.Equal(hint, ElsewhereHint.For(new InstalledPackage(localId, name, publisher, version)));
}
