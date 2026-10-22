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

    [JsonPropertyName("quotaExhausted")]
    public bool QuotaExhausted { get; set; }

    [JsonPropertyName("cachedFallback")]
    public bool CachedFallback { get; set; }

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

    [JsonPropertyName("quotaExhausted")]
    public bool QuotaExhausted { get; set; }

    [JsonPropertyName("cachedFallback")]
    public bool CachedFallback { get; set; }

    [JsonPropertyName("isNotModified")]
    public bool IsNotModified { get; set; }

    [JsonPropertyName("syncState")]
    public string? SyncState { get; set; }

    [JsonPropertyName("followedCountsByDate")]
    public Dictionary<string, int>? FollowedCountsByDate { get; set; }
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

    [JsonPropertyName("syncState")]
    public string? SyncState { get; set; }
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

    [JsonPropertyName("country")]
    public string? Country { get; set; }

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

    [JsonPropertyName("assistPlayerId")]
    public string? AssistPlayerId { get; set; }

    [JsonPropertyName("assist_player_id")]
    public string? AssistPlayerIdAlias
    {
        get => null;
        set { if (string.IsNullOrEmpty(AssistPlayerId) && !string.IsNullOrEmpty(value)) AssistPlayerId = value; }
    }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("teamName")]
    public string? TeamName { get; set; }

    [JsonPropertyName("teamLogoUrl")]
    public string? TeamLogoUrl { get; set; }
}

public class MatchEventDto : TrayEventDto
{
}

public class MatchCommentaryItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("minute")]
    public int Minute { get; set; }

    [JsonPropertyName("extraMinute")]
    public int ExtraMinute { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("teamId")]
    public string? TeamId { get; set; }

    [JsonPropertyName("teamName")]
    public string? TeamName { get; set; }

    [JsonPropertyName("teamLogoUrl")]
    public string? TeamLogoUrl { get; set; }
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

    [JsonPropertyName("commentary")]
    public List<MatchCommentaryItemDto>? Commentary { get; set; }

    [JsonPropertyName("lineups")]
    public List<LineupDto> Lineups { get; set; } = new();

    [JsonPropertyName("statistics")]
    public List<TeamStatisticsDto> Statistics { get; set; } = new();

    [JsonPropertyName("aggregateScore")]
    public string? AggregateScore { get; set; }

    [JsonPropertyName("historicalLeg")]
    public string? HistoricalLeg { get; set; }

    [JsonPropertyName("heatmaps")]
    public List<TeamHeatmapDto>? Heatmaps { get; set; }

    [JsonPropertyName("h2hAndPredictions")]
    public H2HAndPredictionsDto? H2HAndPredictions { get; set; }

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

    [JsonPropertyName("shirtNumber")]
    public int? ShirtNumber { get; set; }

    [JsonPropertyName("position")]
    public string? Position { get; set; }

    [JsonPropertyName("grid")]
    public string? Grid { get; set; }

    [JsonPropertyName("row")]
    public int? Row { get; set; }

    [JsonPropertyName("col")]
    public int? Col { get; set; }

    [JsonPropertyName("isStarter")]
    public bool IsStarter { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("isTopRating")]
    public bool IsManOfTheMatch { get; set; }

    [JsonPropertyName("isCaptain")]
    public bool? IsCaptain { get; set; }

    public int? EffectiveNumber => Number ?? ShirtNumber;
}

public class UnavailablePlayerDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public string? Position { get; set; }

    [JsonPropertyName("shirtNumber")]
    public int? ShirtNumber { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("expectedReturn")]
    public string? ExpectedReturn { get; set; }
}

public class HeatmapPointDto
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("intensity")]
    public double Intensity { get; set; }

    [JsonPropertyName("teamId")]
    public string? TeamId { get; set; }
}

public class TeamHeatmapDto
{
    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("attackingThirdPct")]
    public int AttackingThirdPct { get; set; }

    [JsonPropertyName("middleThirdPct")]
    public int MiddleThirdPct { get; set; }

    [JsonPropertyName("defensiveThirdPct")]
    public int DefensiveThirdPct { get; set; }

    [JsonPropertyName("points")]
    public List<HeatmapPointDto> Points { get; set; } = new();
}

public class H2HMeetingDto
{
    [JsonPropertyName("matchId")]
    public string MatchId { get; set; } = string.Empty;

    [JsonPropertyName("dateUtc")]
    public string DateUtc { get; set; } = string.Empty;

    [JsonPropertyName("competitionName")]
    public string? CompetitionName { get; set; }

    [JsonPropertyName("homeTeamName")]
    public string HomeTeamName { get; set; } = string.Empty;

    [JsonPropertyName("awayTeamName")]
    public string AwayTeamName { get; set; } = string.Empty;

    [JsonPropertyName("homeScore")]
    public int HomeScore { get; set; }

    [JsonPropertyName("awayScore")]
    public int AwayScore { get; set; }

    public string ScoreDisplay => $"{HomeScore} - {AwayScore}";
}

public class FormMatchDto
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "D"; // "W", "D", "L"

    [JsonPropertyName("opponentName")]
    public string OpponentName { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public string Score { get; set; } = string.Empty;

    [JsonPropertyName("isHome")]
    public bool? IsHome { get; set; }

    [JsonPropertyName("dateUtc")]
    public string? DateUtc { get; set; }
}

public class H2HAndPredictionsDto
{
    [JsonPropertyName("homeWins")]
    public int HomeWins { get; set; }

    [JsonPropertyName("draws")]
    public int Draws { get; set; }

    [JsonPropertyName("awayWins")]
    public int AwayWins { get; set; }

    [JsonPropertyName("lastFiveMeetings")]
    public List<H2HMeetingDto> LastFiveMeetings { get; set; } = new();

    [JsonPropertyName("homeForm")]
    public List<FormMatchDto> HomeForm { get; set; } = new();

    [JsonPropertyName("awayForm")]
    public List<FormMatchDto> AwayForm { get; set; } = new();

    [JsonPropertyName("homeWinProbabilityPct")]
    public int HomeWinProbabilityPct { get; set; }

    [JsonPropertyName("drawProbabilityPct")]
    public int DrawProbabilityPct { get; set; }

    [JsonPropertyName("awayWinProbabilityPct")]
    public int AwayWinProbabilityPct { get; set; }
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

    [JsonPropertyName("startXi")]
    public List<LineupPlayerDto>? StartXi { get; set; }

    [JsonPropertyName("starters")]
    public List<LineupPlayerDto>? Starters { get; set; }

    [JsonPropertyName("substitutes")]
    public List<LineupPlayerDto> Substitutes { get; set; } = new();

    [JsonPropertyName("subs")]
    public List<LineupPlayerDto>? Subs { get; set; }

    [JsonPropertyName("bench")]
    public List<LineupPlayerDto>? Bench { get; set; }

    [JsonPropertyName("unavailable")]
    public List<UnavailablePlayerDto> UnavailablePlayers { get; set; } = new();

    [JsonPropertyName("naPlayers")]
    public List<UnavailablePlayerDto>? NaPlayers { get; set; }

    [JsonPropertyName("injured")]
    public List<UnavailablePlayerDto>? Injured { get; set; }

    [JsonPropertyName("players")]
    public List<LineupPlayerDto>? Players { get; set; }

    public string EffectiveCoachName => Coach?.Name ?? CoachName ?? string.Empty;

    public IReadOnlyList<LineupPlayerDto> GetStartingXI()
    {
        if (StartingXI.Count > 0)
        {
            return StartingXI;
        }

        if (StartXi != null && StartXi.Count > 0)
        {
            return StartXi;
        }

        if (Starters != null && Starters.Count > 0)
        {
            return Starters;
        }

        if (Players != null && Players.Count > 0)
        {
            var starters = Players.FindAll(p => p.IsStarter);
            if (starters.Count > 0)
            {
                return starters;
            }

            if (Players.Count >= 11)
            {
                return Players.Take(11).ToList();
            }

            return Players;
        }

        return Array.Empty<LineupPlayerDto>();
    }

    public IReadOnlyList<LineupPlayerDto> GetSubstitutes()
    {
        if (Substitutes.Count > 0)
        {
            return Substitutes;
        }

        if (Subs != null && Subs.Count > 0)
        {
            return Subs;
        }

        if (Bench != null && Bench.Count > 0)
        {
            return Bench;
        }

        if (Players != null && Players.Count > 0)
        {
            var subs = Players.FindAll(p => !p.IsStarter);
            if (subs.Count > 0)
            {
                return subs;
            }

            if (Players.Count > 11)
            {
                return Players.Skip(11).ToList();
            }
        }

        return Array.Empty<LineupPlayerDto>();
    }

    public IReadOnlyList<UnavailablePlayerDto> GetUnavailable()
    {
        if (UnavailablePlayers.Count > 0)
        {
            return UnavailablePlayers;
        }

        if (NaPlayers != null && NaPlayers.Count > 0)
        {
            return NaPlayers;
        }

        if (Injured != null && Injured.Count > 0)
        {
            return Injured;
        }

        return Array.Empty<UnavailablePlayerDto>();
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

    [JsonPropertyName("expectedGoals")]
    public double? ExpectedGoals { get; set; }

    [JsonPropertyName("xg")]
    public double? Xg { get; set; }

    [JsonIgnore]
    public double? EffectiveXg => ExpectedGoals ?? Xg;
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

    [JsonPropertyName("activeProvider")]
    public string? ActiveProvider { get; set; }
}

