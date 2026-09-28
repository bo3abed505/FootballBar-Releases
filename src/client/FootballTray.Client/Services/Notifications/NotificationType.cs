namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Domain notification types for user-facing match alerts.
/// </summary>
public enum NotificationType
{
    MatchStarted,
    Goal,
    HalfTime,
    MatchFinished,
    MatchPostponed,
    MatchCancelled,
    MatchResumed,
    RedCard,
    PenaltyAwarded
}
