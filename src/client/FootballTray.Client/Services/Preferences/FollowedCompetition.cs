using System;
using System.Text.Json.Serialization;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Represents a football competition / league followed by the user.
/// </summary>
public sealed record FollowedCompetition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Country) ? $"{Name} • {Country}" : Name;

    public FollowedCompetition() { }

    public FollowedCompetition(string id, string name, string? code = null, string? country = null, string? logoUrl = null)
    {
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        Code = code;
        Country = country;
        LogoUrl = logoUrl;
    }
}
