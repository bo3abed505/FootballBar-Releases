using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FootballTray.Core.Dto;

public class ApiResponseEnvelope<T>
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; set; }

    [JsonPropertyName("stale")]
    public bool Stale { get; set; }

    [JsonPropertyName("dataAgeSeconds")]
    public int DataAgeSeconds { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("error")]
    public ApiErrorDto? Error { get; set; }
}

public class ApiErrorDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public object? Details { get; set; }
}

public class TrayDataDto
{
    [JsonPropertyName("matches")]
    public List<TrayMatchDto> Matches { get; set; } = new();
}

public class TrayMatchDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("competition")]
    public CompetitionSummaryDto? Competition { get; set; }

    [JsonPropertyName("homeTeam")]
    public TeamSummaryDto HomeTeam { get; set; } = new();

    [JsonPropertyName("awayTeam")]
    public TeamSummaryDto AwayTeam { get; set; } = new();

    [JsonPropertyName("state")]
    public string State { get; set; } = "PREMATCH";

    [JsonPropertyName("elapsedMinute")]
    public int ElapsedMinute { get; set; }

    [JsonPropertyName("extraMinute")]
    public int ExtraMinute { get; set; }

    [JsonPropertyName("score")]
    public ScoreDto Score { get; set; } = new();

    [JsonPropertyName("kickoffTime")]
    public DateTimeOffset KickoffTime { get; set; }

    [JsonPropertyName("recentEvents")]
    public List<TrayEventDto> RecentEvents { get; set; } = new();
}

public class TeamSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("shortName")]
    public string ShortName { get; set; } = string.Empty;

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }
}

public class CompetitionSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public class ScoreDto
{
    [JsonPropertyName("home")]
    public int Home { get; set; }

    [JsonPropertyName("away")]
    public int Away { get; set; }

    [JsonPropertyName("halfTime")]
    public ScorePeriodDto? HalfTime { get; set; }

    [JsonPropertyName("display")]
    public string Display { get; set; } = "0 - 0";
}

public class ScorePeriodDto
{
    [JsonPropertyName("home")]
    public int? Home { get; set; }

    [JsonPropertyName("away")]
    public int? Away { get; set; }
}

public class TrayEventDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("minute")]
    public int Minute { get; set; }

    [JsonPropertyName("extraMinute")]
    public int ExtraMinute { get; set; }

    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("playerName")]
    public string? PlayerName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class MatchDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("competition")]
    public CompetitionSummaryDto? Competition { get; set; }

    [JsonPropertyName("homeTeam")]
    public TeamSummaryDto HomeTeam { get; set; } = new();

    [JsonPropertyName("awayTeam")]
    public TeamSummaryDto AwayTeam { get; set; } = new();

    [JsonPropertyName("state")]
    public string State { get; set; } = "PREMATCH";

    [JsonPropertyName("elapsedMinute")]
    public int ElapsedMinute { get; set; }

    [JsonPropertyName("extraMinute")]
    public int ExtraMinute { get; set; }

    [JsonPropertyName("score")]
    public ScoreDto Score { get; set; } = new();

    [JsonPropertyName("venue")]
    public string? Venue { get; set; }

    [JsonPropertyName("referee")]
    public string? Referee { get; set; }

    [JsonPropertyName("kickoffTime")]
    public DateTimeOffset KickoffTime { get; set; }

    [JsonPropertyName("events")]
    public List<TrayEventDto> Events { get; set; } = new();
}
