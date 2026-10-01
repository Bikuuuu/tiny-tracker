using TinyTracker.WinGet.Matching;
using Xunit;

namespace TinyTracker.WinGet.Tests.Matching;

public class MatcherTests
{
    private static readonly HashSet<string> NothingListed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly InstalledPackage Python = Unmatched(@"ARP\Machine\X64\{PYTHON-312}", "Python 3.12.5 (64-bit)", "3.12.5");

    private static InstalledPackage Unmatched(string localId, string name, string version) => new(localId, name, "Example Publisher", version);

    // What a lookup returns: winget matched this installed entry to a catalog package.
    private static InstalledPackage Found(InstalledPackage entry, string id, string catalogName, string latest) =>
        new(entry.LocalId, catalogName, entry.Publisher, entry.Version, id, catalogName, latest);

    [Fact]
    public void SameNameAndNewerVersion_IsAccepted()
    {
        var accepted = Matcher.Accept([Python], NothingListed, [Found(Python, "Python.Python.3.12", "Python 3.12", "3.12.10")]);
        Assert.Equal("Python.Python.3.12", accepted[Python.LocalId].CatalogId);
    }

    [Fact]
    public void OtherEdition_IsRejected()
    {
        var firefox = Unmatched(@"ARP\Machine\X64\Mozilla Firefox", "Mozilla Firefox", "131.0");
        Assert.Empty(Matcher.Accept([firefox], NothingListed, [Found(firefox, "Mozilla.Firefox.Beta", "Mozilla Firefox Beta", "132.0b3")]));
    }

    [Fact]
    public void OlderTrack_IsRejected() =>
        Assert.Empty(Matcher.Accept([Python], NothingListed, [Found(Python, "Python.Python.3.11", "Python 3.11", "3.11.9")]));

    [Fact]
    public void SeveralTracks_TheClosestVersionWins()
    {
        var accepted = Matcher.Accept([Python], NothingListed,
        [
            Found(Python, "Python.Python.3.13", "Python 3.13", "3.13.7"),
            Found(Python, "Python.Python.3.12", "Python 3.12", "3.12.10"),
            Found(Python, "Python.Python.3.11", "Python 3.11", "3.11.9"),
        ]);
        Assert.Equal("Python.Python.3.12", Assert.Single(accepted).Value.CatalogId);
    }

    [Fact]
    public void EquallyCloseFits_AreAmbiguous()
    {
        var tool = Unmatched(@"ARP\Machine\X64\Example Tool", "Example Tool", "2.0.0");
        Assert.Empty(Matcher.Accept([tool], NothingListed,
        [
            Found(tool, "Example.Tool", "Example Tool", "2.0.1"),
            Found(tool, "Other.ExampleTool", "Example Tool", "2.0.3"),
        ]));
    }

    [Fact]
    public void IdTheFullListHas_IsNotMatchedAgain()
    {
        var listed = new HashSet<string>(["Python.Python.3.12"], StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Matcher.Accept([Python], listed, [Found(Python, "python.python.3.12", "Python 3.12", "3.12.10")]));
    }

    [Fact]
    public void MatchForAnotherInstalledApp_IsIgnored()
    {
        var other = Unmatched(@"ARP\Machine\X64\{OTHER}", "Python 3.12.5 (64-bit)", "3.12.5");
        Assert.Empty(Matcher.Accept([Python], NothingListed, [Found(other, "Python.Python.3.12", "Python 3.12", "3.12.10")]));
    }

    [Fact]
    public void UnknownInstalledVersion_IsRejected()
    {
        var unknown = Unmatched(@"ARP\Machine\X64\Example Tool", "Example Tool", "Unknown");
        Assert.Empty(Matcher.Accept([unknown], NothingListed, [Found(unknown, "Example.Tool", "Example Tool", "2.0.1")]));
    }

    [Fact]
    public void OneCatalogIdForTwoApps_IsAmbiguous()
    {
        var x64 = Unmatched(@"ARP\Machine\X64\Example Tool", "Example Tool (x64)", "2.0.0");
        var x86 = Unmatched(@"ARP\Machine\X86\Example Tool", "Example Tool (x86)", "2.0.0");
        Assert.Empty(Matcher.Accept([x64, x86], NothingListed,
        [
            Found(x64, "Example.Tool", "Example Tool", "2.0.1"),
            Found(x86, "Example.Tool", "Example Tool", "2.0.1"),
        ]));
    }

    [Fact]
    public void LocalIdCase_DoesNotMatter()
    {
        var found = Found(Python, "Python.Python.3.12", "Python 3.12", "3.12.10") with { LocalId = Python.LocalId.ToLowerInvariant() };
        Assert.Single(Matcher.Accept([Python], NothingListed, [found]));
    }

    [Fact]
    public void SameMatchFromBothLookups_CountsOnce()
    {
        var found = Found(Python, "Python.Python.3.12", "Python 3.12", "3.12.10");
        Assert.Single(Matcher.Accept([Python], NothingListed, [found, found with { CatalogId = "python.python.3.12" }]));
    }

    [Fact]
    public void OnlyANewerTrack_IsRejected() =>
        Assert.Empty(Matcher.Accept([Python], NothingListed, [Found(Python, "Python.Python.3.13", "Python 3.13", "3.13.7")]));

    [Fact]
    public void YearInTheCatalogName_MustBeTheInstalledOne()
    {
        var studio = Unmatched(@"ARP\Machine\X64\{STUDIO-2019}", "Example Studio Community 2019", "16.11.40");
        Assert.Empty(Matcher.Accept([studio], NothingListed, [Found(studio, "Example.Studio.2022.Community", "Example Studio Community 2022", "17.14.3")]));
    }

    [Fact]
    public void NumberInTheInstalledName_Agrees()
    {
        var zip = Unmatched(@"ARP\Machine\X64\7-Zip", "7-Zip 24.08 (x64)", "24.08");
        Assert.Single(Matcher.Accept([zip], NothingListed, [Found(zip, "7zip.7zip", "7-Zip", "24.09")]));
    }

    [Fact]
    public void ListedClosestFit_BlocksAFartherOne()
    {
        var tool = Unmatched(@"ARP\Machine\X64\Example Tool", "Example Tool", "2.1.0");
        var listed = new HashSet<string>(["Example.Tool"], StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Matcher.Accept([tool], listed,
        [
            Found(tool, "Example.Tool", "Example Tool", "2.1.5"),
            Found(tool, "Other.ExampleTool", "Example Tool", "2.4.0"),
        ]));
    }

    [Fact]
    public void IdTiedForAnotherApp_IsAmbiguous()
    {
        var machine = Unmatched(@"ARP\Machine\X64\Example Tool", "Example Tool", "2.0.0");
        var user = Unmatched(@"ARP\User\X64\Example Tool", "Example Tool", "2.0.0");
        Assert.Empty(Matcher.Accept([machine, user], NothingListed,
        [
            Found(machine, "Example.Tool", "Example Tool", "2.0.1"),
            Found(machine, "Other.ExampleTool", "Example Tool", "2.0.1"),
            Found(user, "Example.Tool", "Example Tool", "2.0.1"),
        ]));
    }

    [Fact]
    public void SideBySideTracks_EachGetTheirOwn()
    {
        var python313 = Unmatched(@"ARP\Machine\X64\{PYTHON-313}", "Python 3.13.1 (64-bit)", "3.13.1");
        var accepted = Matcher.Accept([Python, python313], NothingListed,
        [
            Found(Python, "Python.Python.3.12", "Python 3.12", "3.12.10"),
            Found(Python, "Python.Python.3.13", "Python 3.13", "3.13.7"),
            Found(python313, "Python.Python.3.12", "Python 3.12", "3.12.10"),
            Found(python313, "Python.Python.3.13", "Python 3.13", "3.13.7"),
        ]);
        Assert.Equal("Python.Python.3.12", accepted[Python.LocalId].CatalogId);
        Assert.Equal("Python.Python.3.13", accepted[python313.LocalId].CatalogId);
    }

    [Fact]
    public void MissingCatalogName_IsRejected()
    {
        var found = Found(Python, "Python.Python.3.12", "Python 3.12", "3.12.10") with { CatalogName = null };
        Assert.Empty(Matcher.Accept([Python], NothingListed, [found]));
    }

    [Fact]
    public void ListedIds_IgnoreCaseWhateverTheSetsComparer()
    {
        var listed = new HashSet<string> { "Python.Python.3.12" };
        Assert.Empty(Matcher.Accept([Python], listed, [Found(Python, "python.python.3.12", "Python 3.12", "3.12.10")]));
    }

    [Fact]
    public void OnlyANewerMajorWithABareName_IsRejected()
    {
        var runtime = Unmatched(@"ARP\Machine\X64\{RUNTIME}", "Example Runtime", "24.11.0");
        Assert.Empty(Matcher.Accept([runtime], NothingListed, [Found(runtime, "Example.Runtime", "Example Runtime", "25.2.0")]));
    }
}
