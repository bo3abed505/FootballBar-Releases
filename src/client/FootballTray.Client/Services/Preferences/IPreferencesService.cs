using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Interface for client-side user preferences persistence, retrieval, and following mutations.
/// </summary>
public interface IPreferencesService
{
    event EventHandler<UserPreferences>? PreferencesChanged;

    UserPreferences Current => GetPreferences();
    UserPreferences GetPreferences();
    Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task SetPreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken = default);

    Task FollowTeamAsync(FollowedTeam team, CancellationToken cancellationToken = default);
    Task UnfollowTeamAsync(string teamId, CancellationToken cancellationToken = default);
    Task FollowCompetitionAsync(FollowedCompetition competition, CancellationToken cancellationToken = default);
    Task UnfollowCompetitionAsync(string competitionId, CancellationToken cancellationToken = default);

    Task SetNotificationsGloballyEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task SetNotificationTypeEnabledAsync(NotificationType type, bool enabled, CancellationToken cancellationToken = default);
    Task SetSoundModeAsync(NotificationSoundMode mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetAudioEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetAudioVolumeAsync(double volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetTickerVisibleAsync(bool visible, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetTickerOffsetXAsync(int offset, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetTargetScreenDeviceNameAsync(string? deviceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetEnableTickerCarouselAsync(bool enable, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetUse24HourFormatAsync(bool use24Hour, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
