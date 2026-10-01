using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyTracker.Core.Settings;

// How many toasts show (spec §4.7).
public enum NotificationLevel
{
    All,
    // Toasts that ask for a click, and failures.
    NeedsMe,
    Failures,
    Off,
}

// What a toast tells.
public enum ToastNews
{
    Ready,
    Permission,
    Close,
    Installed,
    Failed,
    Restart,
    SelfAvailable,
    SelfUpdated,
    SelfFailed,
}

public static class NotificationLevels
{
    public static bool Shows(this NotificationLevel level, ToastNews news) => level switch
    {
        NotificationLevel.All => true,
        NotificationLevel.NeedsMe => news is not (ToastNews.Installed or ToastNews.SelfUpdated),
        NotificationLevel.Failures => news is ToastNews.Failed or ToastNews.SelfFailed,
        _ => false,
    };
}

// A level this build doesn't know, from a hand edit or a newer version, reads as All, so settings.json and its apps stay.
public sealed class NotificationLevelConverter : JsonConverter<NotificationLevel>
{
    public override NotificationLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<NotificationLevel>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named))
            return named;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined((NotificationLevel)number)) return (NotificationLevel)number;
        reader.Skip();
        return NotificationLevel.All;
    }

    public override void Write(Utf8JsonWriter writer, NotificationLevel value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
