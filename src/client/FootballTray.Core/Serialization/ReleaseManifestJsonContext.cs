using System.Text.Json.Serialization;
using FootballTray.Core.Dto;

namespace FootballTray.Core.Serialization;

/// <summary>
/// Source-generated System.Text.Json serializer context for FootballTray release manifest DTO.
/// Enables reflection-free, AOT/trimming-safe deserialization in Core.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = [typeof(UtcDateTimeOffsetJsonConverter)]
)]
[JsonSerializable(typeof(ReleaseManifest))]
public partial class ReleaseManifestJsonContext : JsonSerializerContext
{
}
