using System;
using System.Collections.Generic;
using System.Linq;
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
    public List<NotificationType> EnabledTypes { get; init; } = Enum.GetValues<NotificationType>().ToList();

    [JsonIgnore]
    public bool GoalsEnabled => IsTypeEnabled(NotificationType.Goal);

    [JsonIgnore]
    public bool CardsEnabled => IsTypeEnabled(NotificationType.RedCard);

    [JsonIgnore]
    public bool MatchStartEnabled => IsTypeEnabled(NotificationType.MatchStarted);

    [JsonIgnore]
    public bool HalfTimeEnabled => IsTypeEnabled(NotificationType.HalfTime);

    [JsonIgnore]
    public bool FullTimeEnabled => IsTypeEnabled(NotificationType.MatchFinished);

    [JsonIgnore]
    public bool PostponedEnabled => IsTypeEnabled(NotificationType.MatchPostponed);

    [JsonIgnore]
    public bool CancelledEnabled => IsTypeEnabled(NotificationType.MatchCancelled);

    [JsonIgnore]
    public bool PenaltyAwardedEnabled => IsTypeEnabled(NotificationType.PenaltyAwarded);

    [JsonIgnore]
    public bool KickoffReminderEnabled => IsTypeEnabled(NotificationType.Kickoff);

    public bool IsTypeEnabled(NotificationType type)
    {
        return GlobalEnabled && EnabledTypes != null && EnabledTypes.Contains(type);
    }

    public NotificationPreferences WithTypeEnabled(NotificationType type, bool enabled)
    {
        var types = EnabledTypes?.ToList() ?? new List<NotificationType>();
        if (enabled && !types.Contains(type))
        {
            types.Add(type);
        }
        else if (!enabled && types.Contains(type))
        {
            types.Remove(type);
        }
        return this with { EnabledTypes = types };
    }
}
