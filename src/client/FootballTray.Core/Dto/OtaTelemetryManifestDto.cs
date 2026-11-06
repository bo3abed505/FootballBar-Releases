using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FootballTray.Core.Dto;

/// <summary>
/// Configurable telemetry endpoint URL templates provided via OTA telemetry manifest.
/// Supports runtime hotfixes for upstream API route mutations.
/// </summary>
public class OtaTelemetryEndpointsDto
{
    [JsonPropertyName("matchesByDateUrlTemplate")]
    public string MatchesByDateUrlTemplate { get; set; } = "/api/data/matches?date={date}";

    [JsonPropertyName("matchesByDateFallbackUrlTemplate")]
    public string MatchesByDateFallbackUrlTemplate { get; set; } = "/api/matches?date={date}";

    [JsonPropertyName("matchDetailsUrlTemplate")]
    public string MatchDetailsUrlTemplate { get; set; } = "/api/data/matchDetails?matchId={matchId}";

    [JsonPropertyName("matchDetailsFallbackUrlTemplate")]
    public string MatchDetailsFallbackUrlTemplate { get; set; } = "/api/matchDetails?matchId={matchId}";

    [JsonPropertyName("leagueHubUrlTemplate")]
    public string LeagueHubUrlTemplate { get; set; } = "/api/data/leagues?id={leagueId}";

    [JsonPropertyName("leagueHubFallbackUrlTemplate")]
    public string LeagueHubFallbackUrlTemplate { get; set; } = "/api/leagues?id={leagueId}";

    [JsonPropertyName("teamHubUrlTemplate")]
    public string TeamHubUrlTemplate { get; set; } = "/api/data/teams?id={teamId}";

    [JsonPropertyName("teamHubFallbackUrlTemplate")]
    public string TeamHubFallbackUrlTemplate { get; set; } = "/api/teams?id={teamId}";

    [JsonPropertyName("searchSuggestUrlTemplate")]
    public string SearchSuggestUrlTemplate { get; set; } = "/api/data/search/suggest?term={term}";

    [JsonPropertyName("teamLogoUrlTemplate")]
    public string TeamLogoUrlTemplate { get; set; } = "/image_resources/logo/teamlogo/{teamId}.png";

    [JsonPropertyName("leagueLogoUrlTemplate")]
    public string LeagueLogoUrlTemplate { get; set; } = "/image_resources/logo/leaguelogo/dark/{leagueId}.png";
}

/// <summary>
/// Root OTA telemetry configuration manifest fetched dynamically from GitHub Releases.
/// Enables remote route tuning, routing mode switches, tournament overrides, and priority ranking.
/// </summary>
public class OtaTelemetryManifestDto
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("manifestVersion")]
    public string ManifestVersion { get; set; } = "1.3.1";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "live-telemetry";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("routingMode")]
    public string RoutingMode { get; set; } = "auto"; // "auto", "native-only", "cloud-primary"

    [JsonPropertyName("cloudBaseUrl")]
    public string? CloudBaseUrl { get; set; }

    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    [JsonPropertyName("endpoints")]
    public OtaTelemetryEndpointsDto Endpoints { get; set; } = new();

    [JsonPropertyName("competitionOverrides")]
    public Dictionary<string, int> CompetitionOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("priorityLeagueIds")]
    public List<int> PriorityLeagueIds { get; set; } = new();
}

/// <summary>
/// Disk cache envelope for the OTA telemetry manifest and its ETag header.
/// </summary>
public class OtaManifestCacheRecord
{
    [JsonPropertyName("manifest")]
    public OtaTelemetryManifestDto Manifest { get; set; } = new();

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("lastCheckedAt")]
    public DateTimeOffset LastCheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
