using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// User notification settings governing global enablement and individual category subscriptions.
/// </summary>
public sealed record NotificationPreferences
{
    [JsonPropertyName("globalEnabled")]
    public bool GlobalEnabled { get; init; } = true;

    [JsonPropertyName("enabledTypes")]
    public List<NotificationType> EnabledTypes { get; init; } = new()
    {
        NotificationType.MatchStarted,
        NotificationType.Goal,
        NotificationType.HalfTime,
        NotificationType.MatchFinished,
        NotificationType.MatchPostponed,
        NotificationType.MatchCancelled,
        NotificationType.MatchResumed
    };

    public bool IsTypeEnabled(NotificationType type)
    {
        return GlobalEnabled && EnabledTypes != null && EnabledTypes.Contains(type);
    }
}
