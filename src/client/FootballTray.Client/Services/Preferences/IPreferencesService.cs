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
}
