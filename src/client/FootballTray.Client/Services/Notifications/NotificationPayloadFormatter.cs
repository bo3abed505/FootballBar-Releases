using System;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Formats notification candidates into rich user-facing notification payloads.
/// </summary>
public static class NotificationPayloadFormatter
{
    public static NotificationPayload Format(NotificationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        string title;
        string body;
        string activationArgs = $"action=viewMatch&fixtureId={candidate.FixtureId}";

        string minuteDisplay = candidate.ExtraMinute > 0
            ? $"{candidate.ElapsedMinute}+{candidate.ExtraMinute}'"
            : $"{candidate.ElapsedMinute}'";

        switch (candidate.Type)
        {
            case NotificationType.MatchStarted:
                title = $"{candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = "Match started";
                break;

            case NotificationType.Goal:
                title = $"{candidate.HomeTeamName} {candidate.HomeScore}–{candidate.AwayScore} {candidate.AwayTeamName}";
                if (!string.IsNullOrWhiteSpace(candidate.EventPlayerName))
                {
                    body = $"{minuteDisplay} {candidate.EventPlayerName}";
                }
                else if (!string.IsNullOrWhiteSpace(candidate.EventDescription))
                {
                    body = $"{minuteDisplay} {candidate.EventDescription}";
                }
                else
                {
                    body = $"{minuteDisplay} Goal!";
                }

                if (!string.IsNullOrWhiteSpace(candidate.EventUid))
                {
                    activationArgs += $"&eventUid={candidate.EventUid}";
                }
                break;

            case NotificationType.HalfTime:
                title = $"{candidate.HomeTeamName} {candidate.HomeScore}–{candidate.AwayScore} {candidate.AwayTeamName}";
                body = "Half-time";
                break;

            case NotificationType.MatchFinished:
                title = $"{candidate.HomeTeamName} {candidate.HomeScore}–{candidate.AwayScore} {candidate.AwayTeamName}";
                body = "Full-time";
                break;

            case NotificationType.MatchPostponed:
                title = $"{candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = "Match postponed";
                break;

            case NotificationType.MatchCancelled:
                title = $"{candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = "Match cancelled";
                break;

            case NotificationType.MatchResumed:
                title = $"{candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = "Match resumed";
                break;

            default:
                title = $"{candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = "Match update";
                break;
        }

        return new NotificationPayload
        {
            Title = title,
            Body = body,
            ActivationArguments = activationArgs,
            DeduplicationKey = candidate.DeduplicationKey,
            Type = candidate.Type,
            FixtureId = candidate.FixtureId
        };
    }
}
