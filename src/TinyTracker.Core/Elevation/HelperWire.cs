using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyTracker.Core.Elevation;

// Messages over the pipe: a 4-byte little-endian length, then that many bytes of UTF-8 JSON, at most 16 KB.
public static class HelperWire
{
    public const int MaxBytes = 16 * 1024;

    public static async Task WriteAsync(Stream stream, HelperMessage message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, HelperJson.Default.HelperMessage);
        if (body.Length > MaxBytes) throw new InvalidDataException($"A {body.Length}-byte message is too long.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    // Null when the other side hung up between messages. A malformed, oversized or unknown message throws InvalidDataException.
    public static async Task<HelperMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, ct)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxBytes) throw new InvalidDataException($"A message of {length} bytes is out of range.");
        var body = new byte[length];
        if (!await FillAsync(stream, body, ct)) throw new InvalidDataException("The message was cut short.");
        try
        {
            return JsonSerializer.Deserialize(body, HelperJson.Default.HelperMessage) ?? throw new InvalidDataException("The message was empty.");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The message is malformed.", e);
        }
    }

    // False at a clean end before the first byte; an end partway throws.
    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (count == 0) return read == 0 ? false : throw new InvalidDataException("The message was cut short.");
            read += count;
        }
        return true;
    }
}

// Strict: unknown fields, missing fields and nulls where a value belongs are all refused.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(HelperMessage))]
internal sealed partial class HelperJson : JsonSerializerContext
{
}
