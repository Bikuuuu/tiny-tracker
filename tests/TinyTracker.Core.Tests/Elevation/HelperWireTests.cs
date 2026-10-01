using System.Buffers.Binary;
using System.Text;
using TinyTracker.Core.Elevation;
using TinyTracker.Core.Installing;
using Xunit;

namespace TinyTracker.Core.Tests.Elevation;

public sealed class HelperWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    private static async Task<HelperMessage?> Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await HelperWire.ReadAsync(stream, Ct);
    }

    [Fact]
    public async Task EveryMessage_ComesBackAsSent()
    {
        HelperMessage[] messages =
        [
            new UpgradeRequest(1, "Mozilla.Firefox", "winget", "131.0", 0),
            new UpgradeRequest(2, "Mozilla.Firefox", "winget", "131.0", 17500),
            new LimitRequest(2, 2000),
            new CancelRequest(1),
            new RegisterTaskRequest(),
            new RemoveTaskRequest(),
            new EnableProxyOptionRequest(),
            new SelfUpdateRequest(3, "0.2.0", 0),
            new SelfUpdateRequest(4, "0.2.0", 17500),
            new StayRequest(),
            new HelloMessage(null),
            new HelloMessage("0x80040154"),
            ProgressMessage.Of(2, new UpgradeProgress(UpgradeStage.Downloading, 10, 100, 0.1, 0)),
            DoneMessage.Of(2, new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DiskFull, "0x8A150105")),
            new TaskDoneMessage(null),
            new TaskDoneMessage("0x80070005"),
        ];
        using var stream = new MemoryStream();
        foreach (var message in messages) await HelperWire.WriteAsync(stream, message, Ct);
        stream.Position = 0;
        foreach (var message in messages) Assert.Equal(message, await HelperWire.ReadAsync(stream, Ct));
        Assert.Null(await HelperWire.ReadAsync(stream, Ct));
    }

    [Fact]
    public async Task Message_IsItsLengthThenJson()
    {
        using var stream = new MemoryStream();
        await HelperWire.WriteAsync(stream, new CancelRequest(7), Ct);
        var bytes = stream.ToArray();
        Assert.Equal((bytes.Length - 4, """{"type":"cancel","number":7}"""), (BinaryPrimitives.ReadInt32LittleEndian(bytes), Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4)));
    }

    [Fact]
    public void ProgressAndOutcome_TravelWhole()
    {
        var progress = new UpgradeProgress(UpgradeStage.Installing, 5, 9, 1, 0.5);
        var outcome = new UpgradeOutcome(UpgradeResult.RestartNeeded, Code: "installer 3010");
        Assert.Equal((progress, outcome), (ProgressMessage.Of(3, progress).ToProgress(), DoneMessage.Of(3, outcome).ToOutcome()));
    }

    [Theory]
    [InlineData("""{"type":"launch","path":"C:\\evil.exe"}""")]
    [InlineData("""{"type":"upgrade","number":1,"id":"Mozilla.Firefox","source":"winget","version":"131.0","override":"/S"}""")]
    [InlineData("""{"type":"upgrade","number":1,"id":"Mozilla.Firefox","source":"winget"}""")]
    [InlineData("""{"type":"upgrade","number":1,"id":"Mozilla.Firefox","source":"winget","version":"131.0"}""")]
    [InlineData("""{"type":"limit","number":1}""")]
    [InlineData("""{"type":"selfUpdate","number":1,"version":"0.2.0"}""")]
    [InlineData("""{"type":"selfUpdate","number":1,"version":"0.2.0","limit":0,"path":"C:\\setup.exe"}""")]
    [InlineData("""{"type":"selfUpdate","number":1,"version":"0.2.0","limit":0,"url":"https://example.com/setup.exe"}""")]
    [InlineData("""{"type":"upgrade","number":1,"id":null,"source":"winget","version":"131.0"}""")]
    [InlineData("""{"number":7,"type":"cancel"}""")]
    [InlineData("""{"number":7}""")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task MalformedMessage_IsRefused(string json) => await Assert.ThrowsAsync<InvalidDataException>(() => Read(Frame(json)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(HelperWire.MaxBytes + 1)]
    public async Task LengthOutOfRange_IsRefused(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(header));
    }

    [Fact]
    public async Task MessageCutShort_IsRefused()
    {
        var frame = Frame("""{"type":"cancel","number":7}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(frame[..^3]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(frame[..2]));
    }

    [Fact]
    public async Task MessageTooLongToSend_IsNotSent()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperWire.WriteAsync(stream, new HelloMessage(new string('x', HelperWire.MaxBytes)), Ct));
        Assert.Equal(0, stream.Length);
    }
}
