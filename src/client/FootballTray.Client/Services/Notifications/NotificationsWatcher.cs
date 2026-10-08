using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Dedicated watcher coordinating match alert verification against granular user preferences.
/// Strictly honors custom user category choices (Goals, Cards, Match Start, Full-Time, etc.)
/// before allowing alerts to fire.
/// </summary>
public sealed class NotificationsWatcher
{
    private readonly NotificationService _notificationService;
    private readonly IPreferencesService _preferencesService;

    public NotificationService NotificationService => _notificationService;
    public IPreferencesService PreferencesService => _preferencesService;

    public NotificationsWatcher(
        NotificationService notificationService,
        IPreferencesService preferencesService)
    {
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
    }

    /// <summary>
    /// Checks whether an alert for the candidate category is permitted by user choices.
    /// Strictly honors custom category preferences.
    /// </summary>
    public static bool ShouldFireNotification(NotificationCandidate candidate, NotificationPreferences? preferences)
    {
        if (candidate == null) return false;
        if (preferences == null) return true;
        return preferences.GlobalEnabled && preferences.IsTypeEnabled(candidate.Type);
    }

    /// <summary>
    /// Checks whether an alert for the candidate category is permitted by user choices.
    /// Strictly honors custom category preferences.
    /// </summary>
    public static bool ShouldFireNotification(NotificationCandidate candidate, UserPreferences? preferences)
    {
        if (candidate == null) return false;
        if (preferences?.Notifications != null)
        {
            return ShouldFireNotification(candidate, preferences.Notifications);
        }
        return true;
    }

    /// <summary>
    /// Checks whether an alert for the candidate category is permitted by user choices.
    /// Strictly honors custom category preferences.
    /// </summary>
    public bool ShouldFireNotification(NotificationCandidate candidate)
    {
        return ShouldFireNotification(candidate, _preferencesService.GetPreferences());
    }

    /// <summary>
    /// Evaluates tray snapshot updates and dispatches alerts strictly honoring custom preferences.
    /// </summary>
    public Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        CancellationToken cancellationToken = default)
    {
        var prefs = _preferencesService.GetPreferences();
        return _notificationService.ProcessTrayUpdateAsync(previous, current, prefs, cancellationToken);
    }
}
