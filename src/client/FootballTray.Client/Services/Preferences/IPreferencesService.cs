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
    Task RemoveFollowedTeam(string teamId, CancellationToken cancellationToken = default) => UnfollowTeamAsync(teamId, cancellationToken);
    bool IsCompetitionFollowed(string? id, string? name = null, string? country = null) => false;
    Task FollowCompetitionAsync(FollowedCompetition competition, CancellationToken cancellationToken = default);
    Task UnfollowCompetitionAsync(string competitionId, CancellationToken cancellationToken = default);
    Task UnfollowCompetitionAsync(string competitionId, string? name, string? country, CancellationToken cancellationToken = default) => UnfollowCompetitionAsync(competitionId, cancellationToken);
    Task RemoveFollowedCompetition(string competitionId, CancellationToken cancellationToken = default) => UnfollowCompetitionAsync(competitionId, cancellationToken);

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
    Task SetRunAtStartupAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetLoggingEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetDebugLoggingEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetKickoffReminderEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetKickoffReminderOffsetMinutesAsync(int minutes, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetTickerRotationScopeAsync(string scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetEnableTickerEventPreemptionAsync(bool enable, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetCustomTickerQueueMatchIdsAsync(IReadOnlyList<string> matchIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task ActivateCustomQueueAsync(IReadOnlyList<string> matchIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetManualPinnedMatchIdAsync(string? matchId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetTeamAlertProfileAsync(string teamId, FootballTray.Core.Dto.EntityAlertProfileDto profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task SetCompetitionAlertProfileAsync(string competitionId, FootballTray.Core.Dto.EntityAlertProfileDto profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
