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

    [JsonPropertyName("legacyId")]
    public string? LegacyId { get; set; }

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

    [JsonPropertyName("events")]
    public List<TrayEventDto>? Events
    {
        get => RecentEvents;
        set { if (value != null) RecentEvents = value; }
    }

    [JsonIgnore]
    public IReadOnlyList<string> FormattedGoalscorers
    {
        get
        {
            if (RecentEvents == null || RecentEvents.Count == 0)
                return Array.Empty<string>();

            var list = new List<string>();
            foreach (var e in RecentEvents)
            {
                if (string.Equals(e.Type, "Goal", StringComparison.OrdinalIgnoreCase))
                {
                    var min = e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'";
                    var scorer = !string.IsNullOrWhiteSpace(e.PlayerName) ? e.PlayerName : (!string.IsNullOrWhiteSpace(e.Description) ? e.Description : "Goal");
                    list.Add($"⚽ {scorer} {min}");
                }
            }
            return list;
        }
    }

    [JsonPropertyName("trust")]
    public V2TrustMetadataDto? Trust { get; set; }
}

public sealed class V2TrustMetadataDto
{
    [JsonPropertyName("freshnessState")]
    public string FreshnessState { get; set; } = "UNKNOWN";

    [JsonPropertyName("sourceObservedAt")]
    public DateTimeOffset? SourceObservedAt { get; set; }

    [JsonPropertyName("dataAgeSeconds")]
    public int? DataAgeSeconds { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

public class TeamSummaryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("legacyId")]
    public string? LegacyId { get; set; }

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

    [JsonPropertyName("legacyId")]
    public string? LegacyId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }
}

public class TeamDto : TeamSummaryDto
{
}

public class CompetitionDto : CompetitionSummaryDto
{
}

public class ScoreDto
{
    [JsonPropertyName("home")]
    public int Home { get; set; }

    [JsonPropertyName("away")]
    public int Away { get; set; }

    [JsonPropertyName("halfTime")]
    public ScorePeriodDto? HalfTime { get; set; }

    [JsonPropertyName("extraTime")]
    public ScorePeriodDto? ExtraTime { get; set; }

    [JsonPropertyName("penalty")]
    public ScorePeriodDto? Penalty { get; set; }

    [JsonPropertyName("aggregate")]
    public ScorePeriodDto? Aggregate { get; set; }

    [JsonPropertyName("aggregateDisplay")]
    public string? AggregateDisplay { get; set; }

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

    [JsonPropertyName("team_id")]
    public string? TeamIdAlias
    {
        get => null;
        set { if (string.IsNullOrEmpty(TeamId) && !string.IsNullOrEmpty(value)) TeamId = value; }
    }

    [JsonPropertyName("playerId")]
    public string? PlayerId { get; set; }

    [JsonPropertyName("player_id")]
    public string? PlayerIdAlias
    {
        get => null;
        set { if (string.IsNullOrEmpty(PlayerId) && !string.IsNullOrEmpty(value)) PlayerId = value; }
    }

    [JsonPropertyName("playerName")]
    public string? PlayerName { get; set; }

    [JsonPropertyName("player_name")]
    public string? PlayerNameAlias
    {
        get => null;
        set { if (string.IsNullOrEmpty(PlayerName) && !string.IsNullOrEmpty(value)) PlayerName = value; }
    }

    [JsonPropertyName("assistPlayerName")]
    public string? AssistPlayerName { get; set; }

    [JsonPropertyName("assist_player_name")]
    public string? AssistPlayerNameAlias
    {
        get => null;
        set { if (string.IsNullOrEmpty(AssistPlayerName) && !string.IsNullOrEmpty(value)) AssistPlayerName = value; }
    }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

public class MatchDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("legacyId")]
    public string? LegacyId { get; set; }

    [JsonPropertyName("competition")]
    public CompetitionSummaryDto? Competition { get; set; }

    [JsonPropertyName("homeTeam")]
    public TeamSummaryDto HomeTeam { get; set; } = new();

    [JsonPropertyName("homeTeamId")]
    public string? HomeTeamId { get; set; }

    [JsonPropertyName("awayTeam")]
    public TeamSummaryDto AwayTeam { get; set; } = new();

    [JsonPropertyName("awayTeamId")]
    public string? AwayTeamId { get; set; }

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

    [JsonPropertyName("lineups")]
    public List<LineupDto> Lineups { get; set; } = new();

    [JsonPropertyName("statistics")]
    public List<TeamStatisticsDto> Statistics { get; set; } = new();

    [JsonPropertyName("aggregateScore")]
    public string? AggregateScore { get; set; }

    [JsonPropertyName("historicalLeg")]
    public string? HistoricalLeg { get; set; }

    [JsonPropertyName("trust")]
    public V2TrustMetadataDto? Trust { get; set; }
}

public class CoachDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("photoUrl")]
    public string? PhotoUrl { get; set; }
}

public class LineupPlayerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("playerId")]
    public string? PlayerId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("position")]
    public string? Position { get; set; }

    [JsonPropertyName("grid")]
    public string? Grid { get; set; }

    [JsonPropertyName("isStarter")]
    public bool IsStarter { get; set; }
}

public class LineupDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("fixtureId")]
    public string? FixtureId { get; set; }

    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("formation")]
    public string? Formation { get; set; }

    [JsonPropertyName("coach")]
    public CoachDto? Coach { get; set; }

    [JsonPropertyName("coachName")]
    public string? CoachName { get; set; }

    [JsonPropertyName("coachId")]
    public string? CoachId { get; set; }

    [JsonPropertyName("startingXI")]
    public List<LineupPlayerDto> StartingXI { get; set; } = new();

    [JsonPropertyName("substitutes")]
    public List<LineupPlayerDto> Substitutes { get; set; } = new();

    [JsonPropertyName("players")]
    public List<LineupPlayerDto>? Players { get; set; }

    public string EffectiveCoachName => Coach?.Name ?? CoachName ?? string.Empty;

    public IReadOnlyList<LineupPlayerDto> GetStartingXI()
    {
        if (StartingXI.Count > 0)
        {
            return StartingXI;
        }

        if (Players != null && Players.Count > 0)
        {
            return Players.FindAll(p => p.IsStarter);
        }

        return Array.Empty<LineupPlayerDto>();
    }

    public IReadOnlyList<LineupPlayerDto> GetSubstitutes()
    {
        if (Substitutes.Count > 0)
        {
            return Substitutes;
        }

        if (Players != null && Players.Count > 0)
        {
            return Players.FindAll(p => !p.IsStarter);
        }

        return Array.Empty<LineupPlayerDto>();
    }
}

public class TeamStatisticsDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("fixtureId")]
    public string? FixtureId { get; set; }

    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("shotsOnGoal")]
    public int? ShotsOnGoal { get; set; }

    [JsonPropertyName("shotsOffGoal")]
    public int? ShotsOffGoal { get; set; }

    [JsonPropertyName("totalShots")]
    public int? TotalShots { get; set; }

    [JsonPropertyName("possessionPercent")]
    public int? PossessionPercent { get; set; }

    [JsonPropertyName("possession")]
    public int? Possession { get; set; }

    [JsonPropertyName("fouls")]
    public int? Fouls { get; set; }

    [JsonPropertyName("cornerKicks")]
    public int? CornerKicks { get; set; }

    [JsonPropertyName("corners")]
    public int? Corners { get; set; }

    [JsonPropertyName("offsides")]
    public int? Offsides { get; set; }

    [JsonPropertyName("yellowCards")]
    public int? YellowCards { get; set; }

    [JsonPropertyName("redCards")]
    public int? RedCards { get; set; }

    [JsonPropertyName("goalkeeperSaves")]
    public int? GoalkeeperSaves { get; set; }

    [JsonPropertyName("totalPasses")]
    public int? TotalPasses { get; set; }

    [JsonPropertyName("passesAccurate")]
    public int? PassesAccurate { get; set; }
}

public class V2CatalogTeamDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("legacyId")]
    public string? LegacyId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("shortName")]
    public string? ShortName { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }
}

public class V2CatalogCompetitionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("legacyId")]
    public string? LegacyId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }
}

public class SyncPreferencesRequestDto
{
    [JsonPropertyName("competitionIds")]
    public List<string> CompetitionIds { get; set; } = new();

    [JsonPropertyName("teamIds")]
    public List<string> TeamIds { get; set; } = new();
}

