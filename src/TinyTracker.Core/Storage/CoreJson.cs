using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyTracker.Core.Storage;

// Persisted records use set, not init: generated code would drop the defaults of missing init keys.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(HistoryFile))]
[JsonSerializable(typeof(AppListFile))]
internal sealed partial class CoreJson : JsonSerializerContext
{
}
