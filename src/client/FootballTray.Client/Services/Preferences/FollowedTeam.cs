using System;
using System.Text.Json.Serialization;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Represents a football team followed by the user.
/// </summary>
public sealed record FollowedTeam
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("shortName")]
    public string? ShortName { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; init; }

    public FollowedTeam() { }

    public FollowedTeam(string id, string name, string? shortName = null, string? country = null, string? logoUrl = null)
    {
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        ShortName = shortName;
        Country = country;
        LogoUrl = logoUrl;
    }
}
