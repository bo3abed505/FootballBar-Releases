using System;
using System.Text.Json.Serialization;
using FootballTray.Core.Serialization;

namespace FootballTray.Core.Dto;

/// <summary>
/// Immutable data transfer object representing a published FootballTray release manifest.
/// </summary>
public sealed record ReleaseManifest
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    [JsonConverter(typeof(UtcDateTimeOffsetJsonConverter))]
    public DateTimeOffset PublishedAt { get; init; }

    [JsonPropertyName("architecture")]
    public string Architecture { get; init; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; init; }
}
