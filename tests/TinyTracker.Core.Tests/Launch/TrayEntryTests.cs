using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

// Windows' record of a tray icon names its program by path, with a known folder written as its id (spec §10).
public class TrayEntryTests
{
    private const string Exe = @"C:\Program Files\Tiny Tracker\TinyTracker.exe";
    private static readonly Guid ProgramFiles = new("6D809377-6AF0-444B-8957-A3773F02200E");

    private static string? Folder(Guid id) => id == ProgramFiles ? @"C:\Program Files" : null;

    [Theory]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Tiny Tracker\TinyTracker.exe", true)]
    [InlineData(@"C:\Program Files\Tiny Tracker\TinyTracker.exe", true)]
    [InlineData(@"c:\program files\tiny tracker\tinytracker.exe", true)]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Other\TinyTracker.exe", false)]
    [InlineData(@"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe", false)]
    [InlineData(@"{not an id}\TinyTracker.exe", false)]
    [InlineData("", false)]
    public void Entry_IsThisProgramsOnly(string recorded, bool expected) => Assert.Equal(expected, TrayEntry.IsFor(recorded, Exe, Folder));
}
