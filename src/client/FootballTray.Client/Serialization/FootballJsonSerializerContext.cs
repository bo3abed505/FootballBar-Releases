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
[JsonSerializable(typeof(MatchDetailDto))]
[JsonSerializable(typeof(CachedTrayPayload))]
[JsonSerializable(typeof(UserPreferences))]
[JsonSerializable(typeof(FollowedTeam))]
[JsonSerializable(typeof(FollowedCompetition))]
[JsonSerializable(typeof(NotificationPreferences))]
[JsonSerializable(typeof(NotificationHistoryModel))]
[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
[JsonSerializable(typeof(V2TrustMetadataDto))]
public partial class FootballJsonSerializerContext : JsonSerializerContext
{
}
