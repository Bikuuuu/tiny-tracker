using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

public class StartupEntryTests
{
    private const string Exe = @"C:\Program Files\Tiny Tracker\TinyTracker.exe";
    private const string Ours = "\"" + Exe + "\" --startup";
    private const string OtherCopy = @"""D:\Tools\TinyTracker.exe"" --startup";

    private readonly FakeStartupValues _values = new();
    private readonly StartupEntry _entry;

    public StartupEntryTests() => _entry = new StartupEntry(_values, Exe);

    [Fact]
    public void Command_QuotesTheExe_AndAddsTheStartupFlag() => Assert.Equal(Ours, StartupCommand.Format(Exe));

    [Theory]
    [InlineData(Ours, Exe)]
    [InlineData(@"C:\Tools\app.exe --startup", @"C:\Tools\app.exe")]
    [InlineData(@"C:\Program Files\app.exe", @"C:\Program")]
    [InlineData("app.exe --startup", null)]
    [InlineData(@"""app.exe"" --startup", null)]
    [InlineData(@"""C:\Tools\app.exe --startup", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Target_IsTheFullyQualifiedExe(string? command, string? exe) => Assert.Equal(exe, StartupCommand.Target(command));

    [Fact]
    public void NoRunValue_IsOff() => Assert.False(_entry.IsOn);

    [Theory]
    [InlineData(Ours)]
    [InlineData(@"""c:\program files\tiny tracker\tinytracker.exe"" --startup")]
    [InlineData("  " + Ours + "  ")]
    public void OurValue_IsOn(string run)
    {
        _values.Run = run;
        Assert.True(_entry.IsOn);
    }

    [Theory]
    [InlineData(OtherCopy)]
    [InlineData("\"" + Exe + "\"")]
    [InlineData("\"" + Exe + "\" --startup --demo")]
    [InlineData("TinyTracker.exe --startup")]
    public void AnyOtherValue_IsOff(string run)
    {
        _values.Run = run;
        Assert.False(_entry.IsOn);
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 7, 0, 0, 0 }, false)]
    [InlineData(new byte[0], true)]
    public void TaskManagersMark_DecidesWhetherOurValueRuns(byte[] mark, bool on)
    {
        _values.Run = Ours;
        _values.Approved = mark;
        Assert.Equal(on, _entry.IsOn);
    }

    [Fact]
    public void TurningOn_WritesOurValue_AndClearsTaskManagersOffMark()
    {
        _values.Run = OtherCopy;
        _values.Approved = [3, 0, 0, 0];
        _entry.Set(true);
        Assert.Equal((Ours, (byte[]?)null), (_values.Run, _values.Approved));
        Assert.True(_entry.IsOn);
    }

    [Fact]
    public void TurningOff_RemovesBothValues()
    {
        _values.Run = Ours;
        _values.Approved = [2, 0, 0, 0];
        _entry.Set(false);
        Assert.Equal(((string?)null, (byte[]?)null), (_values.Run, _values.Approved));
    }

    [Fact]
    public void Remove_LeavesAnotherCopysValues()
    {
        _values.Run = OtherCopy;
        _values.Approved = [3, 0, 0, 0];
        _entry.Remove();
        Assert.Equal(OtherCopy, _values.Run);
        Assert.NotNull(_values.Approved);
    }

    [Fact]
    public void Remove_ClearsAMarkLeftWithoutARunValue()
    {
        _values.Approved = [3, 0, 0, 0];
        _entry.Remove();
        Assert.Null(_values.Approved);
    }

    [Fact]
    public void Remove_OfOurValueWithOtherFlags_RemovesIt()
    {
        _values.Run = "\"" + Exe + "\"";
        _entry.Remove();
        Assert.Null(_values.Run);
    }

    private sealed class FakeStartupValues : IStartupValues
    {
        public string? Run { get; set; }
        public byte[]? Approved { get; set; }

        public string? ReadRun() => Run;
        public void WriteRun(string command) => Run = command;
        public void DeleteRun() => Run = null;
        public byte[]? ReadApproved() => Approved;
        public void DeleteApproved() => Approved = null;
    }
}
