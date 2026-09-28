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

    public bool IsTypeEnabled(NotificationType type)
    {
        return GlobalEnabled && EnabledTypes != null && EnabledTypes.Contains(type);
    }
}
