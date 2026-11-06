using System;
using System.Collections.Generic;
using System.Globalization;
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

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

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

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("syncState")]
    public string? SyncState { get; set; }

    [JsonPropertyName("followedCountsByDate")]
    public Dictionary<string, int>? FollowedCountsByDate { get; set; }

    [JsonPropertyName("followedTeamCompetitions")]
    public List<CompetitionSummaryDto>? FollowedTeamCompetitions { get; set; }

    [JsonPropertyName("featuredMatch")]
    public TrayMatchDto? FeaturedMatch { get; set; }
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

    [JsonPropertyName("redCards")]
    public int RedCards { get; set; }
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

    [JsonPropertyName("standings")]
    public List<StandingRowDto>? Standings { get; set; }

    [JsonPropertyName("topScorers")]
    public List<PlayerStatLeaderDto>? TopScorers { get; set; }

    [JsonPropertyName("topAssists")]
    public List<PlayerStatLeaderDto>? TopAssists { get; set; }

    [JsonPropertyName("fixtures")]
    public List<CompetitionFixtureDto>? Fixtures { get; set; }

    [JsonPropertyName("knockoutStages")]
    public List<KnockoutStageDto>? KnockoutStages { get; set; }

    [JsonPropertyName("trust")]
    public V2TrustMetadataDto? Trust { get; set; }

    [JsonPropertyName("tacticalRecapBadge")]
    public string? TacticalRecapBadge { get; set; }

    [JsonPropertyName("tacticalRecapSummary")]
    public string? TacticalRecapSummary { get; set; }

    [JsonPropertyName("keyMatchInsights")]
    public List<string> KeyMatchInsights { get; set; } = new();

    [JsonPropertyName("playerOfTheMatch")]
    public PlayerOfTheMatchDto? PlayerOfTheMatch { get; set; }
}

public class PlayerOfTheMatchDto
{
    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = string.Empty;

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    [JsonPropertyName("rating")]
    public string? Rating { get; set; }

    [JsonPropertyName("summaryText")]
    public string? SummaryText { get; set; }
}

public class MatchCenterDto : MatchDetailDto
{
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

    [JsonPropertyName("goals")]
    public int Goals { get; set; }

    [JsonPropertyName("assists")]
    public int Assists { get; set; }

    [JsonPropertyName("yellowCards")]
    public int YellowCards { get; set; }

    [JsonPropertyName("redCards")]
    public int RedCards { get; set; }

    [JsonPropertyName("subInMinute")]
    public int? SubInMinute { get; set; }

    [JsonPropertyName("subOutMinute")]
    public int? SubOutMinute { get; set; }

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

    [JsonPropertyName("homeTeamId")]
    public string? HomeTeamId { get; set; }

    [JsonPropertyName("awayTeamId")]
    public string? AwayTeamId { get; set; }

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

public class TeamFormMatchDto
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = "D"; // "W", "D", "L"

    [JsonPropertyName("opponentName")]
    public string OpponentName { get; set; } = string.Empty;

    [JsonPropertyName("scoreText")]
    public string ScoreText { get; set; } = string.Empty;

    [JsonPropertyName("matchSummaryTooltip")]
    public string MatchSummaryTooltip { get; set; } = string.Empty;

    [JsonPropertyName("dateText")]
    public string? DateText { get; set; }

    [JsonPropertyName("matchId")]
    public string? MatchId { get; set; }
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

    [JsonPropertyName("homeFormMatches")]
    public List<TeamFormMatchDto> HomeFormMatches { get; set; } = new();

    [JsonPropertyName("awayFormMatches")]
    public List<TeamFormMatchDto> AwayFormMatches { get; set; } = new();

    [JsonPropertyName("homeWinProbabilityPct")]
    public int HomeWinProbabilityPct { get; set; }

    [JsonPropertyName("drawProbabilityPct")]
    public int DrawProbabilityPct { get; set; }

    [JsonPropertyName("awayWinProbabilityPct")]
    public int AwayWinProbabilityPct { get; set; }

    [JsonPropertyName("summaryDisplay")]
    public string? SummaryDisplay { get; set; }
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

public class FormBadgeItem
{
    public char Result { get; set; }
    public string BackgroundHex => Result == 'W' ? "#107C41" : (Result == 'D' ? "#5A5A5A" : "#C42B1C");
    public string ForegroundHex => "#FFFFFF";
}

public class StandingRowDto
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    [JsonPropertyName("teamShortName")]
    public string? TeamShortName { get; set; }

    [JsonPropertyName("teamLogoUrl")]
    public string? TeamLogoUrl { get; set; }

    [JsonPropertyName("played")]
    public int Played { get; set; }

    [JsonPropertyName("wins")]
    public int Wins { get; set; }

    [JsonPropertyName("draws")]
    public int Draws { get; set; }

    [JsonPropertyName("losses")]
    public int Losses { get; set; }

    [JsonPropertyName("goalsFor")]
    public int GoalsFor { get; set; }

    [JsonPropertyName("goalsAgainst")]
    public int GoalsAgainst { get; set; }

    [JsonPropertyName("goalDiff")]
    public int GoalDiff { get; set; }

    [JsonPropertyName("points")]
    public int Points { get; set; }

    [JsonPropertyName("form")]
    public string? Form { get; set; }

    [JsonPropertyName("qualificationColor")]
    public string? QualificationColor { get; set; }

    [JsonPropertyName("isHomeTeam")]
    public bool IsHomeTeam { get; set; }

    [JsonPropertyName("isAwayTeam")]
    public bool IsAwayTeam { get; set; }

    [JsonIgnore]
    public bool IsFollowedTeam { get; set; }

    [JsonIgnore]
    public string GoalDiffDisplay => GoalDiff > 0 ? $"+{GoalDiff}" : GoalDiff.ToString();

    [JsonIgnore]
    public string RowBackground => IsHomeTeam ? "#250078D4" : (IsAwayTeam ? "#25107C41" : (IsFollowedTeam ? "#2538BDF8" : "Transparent"));

    [JsonIgnore]
    public string RowBorderBrush => IsHomeTeam ? "#800078D4" : (IsAwayTeam ? "#80107C41" : (IsFollowedTeam ? "#8038BDF8" : "#1AFFFFFF"));

    [JsonIgnore]
    public double RowBorderThickness => (IsHomeTeam || IsAwayTeam || IsFollowedTeam) ? 1.5 : 0.5;

    [JsonIgnore]
    public List<FormBadgeItem> FormBadges => (Form ?? "")
        .Where(char.IsLetter)
        .Select(c => new FormBadgeItem { Result = char.ToUpperInvariant(c) })
        .TakeLast(5)
        .ToList();
}

public class PlayerStatLeaderDto
{
    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = string.Empty;

    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = string.Empty;

    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    [JsonPropertyName("teamLogoUrl")]
    public string? TeamLogoUrl { get; set; }

    [JsonPropertyName("goals")]
    public int? Goals { get; set; }

    [JsonPropertyName("assists")]
    public int? Assists { get; set; }

    [JsonPropertyName("penalties")]
    public int? Penalties { get; set; }

    [JsonPropertyName("matchesPlayed")]
    public int? MatchesPlayed { get; set; }

    [JsonIgnore]
    public string RankDisplay => $"#{Rank}";

    [JsonIgnore]
    public string MatchesDisplay => MatchesPlayed.HasValue ? $"{MatchesPlayed.Value} matches" : "";

    [JsonIgnore]
    public string StatDisplay => Goals.HasValue
        ? (Penalties.HasValue && Penalties.Value > 0 ? $"{Goals.Value} goals ({Penalties.Value} pen)" : $"{Goals.Value} goals")
        : (Assists.HasValue ? $"{Assists.Value} assists" : "");
}

public class CompetitionHubDto
{
    [JsonPropertyName("competitionId")]
    public string CompetitionId { get; set; } = string.Empty;

    [JsonPropertyName("competitionName")]
    public string CompetitionName { get; set; } = string.Empty;

    [JsonPropertyName("competitionCode")]
    public string? CompetitionCode { get; set; }

    [JsonPropertyName("competitionLogoUrl")]
    public string? CompetitionLogoUrl { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("countryName")]
    public string? CountryName { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonPropertyName("standings")]
    public List<StandingRowDto> Standings { get; set; } = new();

    [JsonPropertyName("topScorers")]
    public List<PlayerStatLeaderDto> TopScorers { get; set; } = new();

    [JsonPropertyName("topAssists")]
    public List<PlayerStatLeaderDto> TopAssists { get; set; } = new();

    [JsonPropertyName("fixtures")]
    public List<CompetitionFixtureDto> Fixtures { get; set; } = new();

    [JsonPropertyName("knockoutStages")]
    public List<KnockoutStageDto> KnockoutStages { get; set; } = new();
}

public class CompetitionFixtureDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("roundName")]
    public string RoundName { get; set; } = string.Empty;

    [JsonPropertyName("competitionId")]
    public string? CompetitionId { get; set; }

    [JsonPropertyName("competitionName")]
    public string? CompetitionName { get; set; }

    [JsonPropertyName("competitionLogo")]
    public string? CompetitionLogo { get; set; }

    [JsonPropertyName("kickoffUtc")]
    public string KickoffUtc { get; set; } = string.Empty;

    [JsonPropertyName("homeTeamId")]
    public string HomeTeamId { get; set; } = string.Empty;

    [JsonPropertyName("homeTeamName")]
    public string HomeTeamName { get; set; } = string.Empty;

    [JsonPropertyName("homeTeamShortName")]
    public string? HomeTeamShortName { get; set; }

    [JsonPropertyName("homeTeamLogo")]
    public string? HomeTeamLogo { get; set; }

    [JsonPropertyName("awayTeamId")]
    public string AwayTeamId { get; set; } = string.Empty;

    [JsonPropertyName("awayTeamName")]
    public string AwayTeamName { get; set; } = string.Empty;

    [JsonPropertyName("awayTeamShortName")]
    public string? AwayTeamShortName { get; set; }

    [JsonPropertyName("awayTeamLogo")]
    public string? AwayTeamLogo { get; set; }

    [JsonPropertyName("homeScore")]
    public int? HomeScore { get; set; }

    [JsonPropertyName("awayScore")]
    public int? AwayScore { get; set; }

    [JsonPropertyName("statusText")]
    public string StatusText { get; set; } = string.Empty;

    [JsonPropertyName("isFinished")]
    public bool IsFinished { get; set; }

    [JsonPropertyName("isStarted")]
    public bool IsStarted { get; set; }

    [JsonPropertyName("isLive")]
    public bool IsLive { get; set; }

    private string? _customScoreOrTime;

    [JsonIgnore]
    public string? CustomScoreOrTime
    {
        get => _customScoreOrTime;
        set => _customScoreOrTime = value;
    }

    [JsonIgnore]
    public string ScoreOrTime
    {
        get => ScoreOrTimeDisplay;
        set => _customScoreOrTime = value;
    }

    [JsonIgnore]
    public string ScoreOrTimeDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customScoreOrTime)) return _customScoreOrTime;
            if (IsFinished || IsLive)
            {
                if (HomeScore.HasValue && AwayScore.HasValue) return $"{HomeScore.Value} - {AwayScore.Value}";
                if (!string.IsNullOrWhiteSpace(StatusText)) return StatusText;
            }
            if (DateTimeOffset.TryParse(KickoffUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt.ToLocalTime().ToString("MMM d, HH:mm");
            }
            return !string.IsNullOrWhiteSpace(StatusText) ? StatusText : "Upcoming";
        }
        set => _customScoreOrTime = value;
    }
}

public class KnockoutTieDto
{
    [JsonPropertyName("stageName")]
    public string StageName { get; set; } = string.Empty;

    [JsonPropertyName("homeTeamId")]
    public string? HomeTeamId { get; set; }

    [JsonPropertyName("homeTeamName")]
    public string HomeTeamName { get; set; } = "TBD";

    [JsonPropertyName("homeTeamLogo")]
    public string? HomeTeamLogo { get; set; }

    [JsonPropertyName("awayTeamId")]
    public string? AwayTeamId { get; set; }

    [JsonPropertyName("awayTeamName")]
    public string AwayTeamName { get; set; } = "TBD";

    [JsonPropertyName("awayTeamLogo")]
    public string? AwayTeamLogo { get; set; }

    [JsonPropertyName("leg1Score")]
    public string? Leg1Score { get; set; }

    [JsonPropertyName("leg2Score")]
    public string? Leg2Score { get; set; }

    [JsonPropertyName("aggregateScore")]
    public string? AggregateScore { get; set; }

    [JsonPropertyName("penaltyScore")]
    public string? PenaltyScore { get; set; }

    [JsonPropertyName("winnerTeamName")]
    public string? WinnerTeamName { get; set; }

    [JsonPropertyName("scoreOrStatus")]
    public string ScoreOrStatus { get; set; } = "vs";

    [JsonPropertyName("isFinished")]
    public bool IsFinished { get; set; }
}

public class KnockoutStageDto
{
    [JsonPropertyName("stageName")]
    public string StageName { get; set; } = string.Empty;

    [JsonPropertyName("participantCount")]
    public int ParticipantCount { get; set; }

    [JsonPropertyName("ties")]
    public List<KnockoutTieDto> Ties { get; set; } = new();
}

public class TeamSquadPlayerDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("positionGroup")]
    public string PositionGroup { get; set; } = string.Empty;

    [JsonPropertyName("shirtNumber")]
    public int? ShirtNumber { get; set; }

    [JsonPropertyName("countryCode")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("goals")]
    public int? Goals { get; set; }

    [JsonPropertyName("assists")]
    public int? Assists { get; set; }

    [JsonPropertyName("yellowCards")]
    public int? YellowCards { get; set; }

    [JsonPropertyName("redCards")]
    public int? RedCards { get; set; }

    [JsonIgnore]
    public string ShirtNumberDisplay => ShirtNumber.HasValue ? $"#{ShirtNumber.Value}" : "-";

    [JsonIgnore]
    public string RatingDisplay => Rating.HasValue ? Rating.Value.ToString("0.0", CultureInfo.InvariantCulture) : "-";
}

public class TeamHubDto
{
    [JsonPropertyName("teamId")]
    public string TeamId { get; set; } = string.Empty;

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    [JsonPropertyName("shortName")]
    public string ShortName { get; set; } = string.Empty;

    [JsonPropertyName("teamLogo")]
    public string TeamLogo { get; set; } = string.Empty;

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("primaryLeagueId")]
    public string PrimaryLeagueId { get; set; } = string.Empty;

    [JsonPropertyName("primaryLeagueName")]
    public string PrimaryLeagueName { get; set; } = string.Empty;

    [JsonPropertyName("primaryLeagueLogo")]
    public string PrimaryLeagueLogo { get; set; } = string.Empty;

    [JsonPropertyName("stadiumName")]
    public string StadiumName { get; set; } = string.Empty;

    [JsonPropertyName("coachName")]
    public string CoachName { get; set; } = string.Empty;

    [JsonPropertyName("recentForm")]
    public List<string> RecentForm { get; set; } = new();

    [JsonPropertyName("upcomingFixtures")]
    public List<CompetitionFixtureDto> UpcomingFixtures { get; set; } = new();

    [JsonPropertyName("recentResults")]
    public List<CompetitionFixtureDto> RecentResults { get; set; } = new();

    [JsonPropertyName("squadPlayers")]
    public List<TeamSquadPlayerDto> SquadPlayers { get; set; } = new();

    [JsonPropertyName("standings")]
    public List<StandingRowDto> Standings { get; set; } = new();
}

public sealed record EntityAlertProfileDto
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "InheritGlobal"; // "InheritGlobal", "Custom", "Muted"

    [JsonPropertyName("enableKickoffReminder")]
    public bool EnableKickoffReminder { get; init; } = true;

    [JsonPropertyName("enableGoals")]
    public bool EnableGoals { get; init; } = true;

    [JsonPropertyName("enableRedCards")]
    public bool EnableRedCards { get; init; } = true;

    [JsonPropertyName("enableKickoff")]
    public bool EnableKickoff { get; init; } = true;

    [JsonPropertyName("enableHalfTime")]
    public bool EnableHalfTime { get; init; } = true;

    [JsonPropertyName("enableFullTime")]
    public bool EnableFullTime { get; init; } = true;

    [JsonPropertyName("enablePostponed")]
    public bool EnablePostponed { get; init; } = true;

    [JsonPropertyName("enableCancelled")]
    public bool EnableCancelled { get; init; } = true;

    [JsonPropertyName("soundOverride")]
    public string SoundOverride { get; init; } = "InheritGlobal"; // "InheritGlobal", "Stadium", "Mute", "WindowsDefault"

    public bool IsTypeEnabled(string typeName)
    {
        if (string.Equals(Mode, "Muted", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(Mode, "InheritGlobal", StringComparison.OrdinalIgnoreCase)) return true;

        return (typeName ?? string.Empty).ToLowerInvariant() switch
        {
            "goal" => EnableGoals,
            "redcard" or "red_card" => EnableRedCards,
            "matchstarted" or "match_started" or "kickoff" => EnableKickoff,
            "halftime" or "half_time" or "secondhalf" or "second_half" => EnableHalfTime,
            "matchfinished" or "match_finished" or "fulltime" or "full_time" => EnableFullTime,
            "matchpostponed" or "postponed" => EnablePostponed,
            "matchcancelled" or "cancelled" => EnableCancelled,
            "kickoffreminder" or "kickoff_reminder" => EnableKickoffReminder,
            _ => true
        };
    }
}



