using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Serialization;

/// <summary>
/// Source-generated System.Text.Json serializer context for FootballTray client.
/// Provides trimming/AOT-safe metadata for all API DTOs and local persistence models
/// with reflection-based serialization disabled.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
[JsonSerializable(typeof(ApiResponseEnvelope<TrayDataDto>))]
[JsonSerializable(typeof(ApiResponseEnvelope<MatchDetailDto>))]
[JsonSerializable(typeof(ApiResponseEnvelope<object>))]
[JsonSerializable(typeof(ApiErrorDto))]
[JsonSerializable(typeof(TrayDataDto))]
[JsonSerializable(typeof(TrayMatchDto))]
[JsonSerializable(typeof(TrayEventDto))]
[JsonSerializable(typeof(List<TrayEventDto>))]
[JsonSerializable(typeof(MatchDetailDto))]
[JsonSerializable(typeof(CachedTrayPayload))]
[JsonSerializable(typeof(UserPreferences))]
[JsonSerializable(typeof(NotificationSoundMode))]
[JsonSerializable(typeof(FollowedTeam))]
[JsonSerializable(typeof(FollowedCompetition))]
[JsonSerializable(typeof(NotificationPreferences))]
[JsonSerializable(typeof(NotificationHistoryModel))]
[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
[JsonSerializable(typeof(V2TrustMetadataDto))]
[JsonSerializable(typeof(CoachDto))]
[JsonSerializable(typeof(LineupPlayerDto))]
[JsonSerializable(typeof(List<LineupPlayerDto>))]
[JsonSerializable(typeof(LineupDto))]
[JsonSerializable(typeof(List<LineupDto>))]
[JsonSerializable(typeof(TeamStatisticsDto))]
[JsonSerializable(typeof(List<TeamStatisticsDto>))]
[JsonSerializable(typeof(TeamDto))]
[JsonSerializable(typeof(CompetitionDto))]
[JsonSerializable(typeof(ScorePeriodDto))]
[JsonSerializable(typeof(ScoreDto))]
[JsonSerializable(typeof(V2CatalogTeamDto))]
[JsonSerializable(typeof(List<V2CatalogTeamDto>))]
[JsonSerializable(typeof(ApiResponseEnvelope<List<V2CatalogTeamDto>>))]
[JsonSerializable(typeof(V2CatalogCompetitionDto))]
[JsonSerializable(typeof(List<V2CatalogCompetitionDto>))]
[JsonSerializable(typeof(ApiResponseEnvelope<List<V2CatalogCompetitionDto>>))]
[JsonSerializable(typeof(SyncPreferencesRequestDto))]
[JsonSerializable(typeof(CompetitionHubDto))]
[JsonSerializable(typeof(ApiResponseEnvelope<CompetitionHubDto>))]
[JsonSerializable(typeof(TeamSquadPlayerDto))]
[JsonSerializable(typeof(List<TeamSquadPlayerDto>))]
[JsonSerializable(typeof(TeamHubDto))]
[JsonSerializable(typeof(ApiResponseEnvelope<TeamHubDto>))]
[JsonSerializable(typeof(CompetitionFixtureDto))]
[JsonSerializable(typeof(List<CompetitionFixtureDto>))]
[JsonSerializable(typeof(KnockoutStageDto))]
[JsonSerializable(typeof(List<KnockoutStageDto>))]
[JsonSerializable(typeof(KnockoutTieDto))]
[JsonSerializable(typeof(List<KnockoutTieDto>))]
[JsonSerializable(typeof(StandingRowDto))]
[JsonSerializable(typeof(List<StandingRowDto>))]
[JsonSerializable(typeof(PlayerStatLeaderDto))]
[JsonSerializable(typeof(List<PlayerStatLeaderDto>))]
[JsonSerializable(typeof(CompetitionSummaryDto))]
[JsonSerializable(typeof(List<CompetitionSummaryDto>))]
[JsonSerializable(typeof(OtaTelemetryManifestDto))]
[JsonSerializable(typeof(OtaTelemetryEndpointsDto))]
[JsonSerializable(typeof(OtaManifestCacheRecord))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(EntityAlertProfileDto))]
[JsonSerializable(typeof(Dictionary<string, EntityAlertProfileDto>))]
[JsonSerializable(typeof(List<string>))]
public partial class FootballJsonSerializerContext : JsonSerializerContext
{
}
