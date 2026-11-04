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
                string scoringTeam = (candidate.ScoringTeamName ?? string.Empty).Trim();
                if (string.Equals(scoringTeam, "TBD", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scoringTeam, "<TBD>", StringComparison.OrdinalIgnoreCase))
                {
                    scoringTeam = string.Empty;
                }

                string homeName = !string.IsNullOrWhiteSpace(candidate.HomeTeamName) &&
                                  !string.Equals(candidate.HomeTeamName, "TBD", StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(candidate.HomeTeamName, "<TBD>", StringComparison.OrdinalIgnoreCase)
                    ? candidate.HomeTeamName
                    : "Home";

                string awayName = !string.IsNullOrWhiteSpace(candidate.AwayTeamName) &&
                                  !string.Equals(candidate.AwayTeamName, "TBD", StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(candidate.AwayTeamName, "<TBD>", StringComparison.OrdinalIgnoreCase)
                    ? candidate.AwayTeamName
                    : "Away";

                string player = !string.IsNullOrWhiteSpace(candidate.EventPlayerName) &&
                                !string.Equals(candidate.EventPlayerName, "TBD", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(candidate.EventPlayerName, "<TBD>", StringComparison.OrdinalIgnoreCase)
                    ? candidate.EventPlayerName
                    : string.Empty;

                string detailSuffix = string.Empty;
                bool isOg = (!string.IsNullOrWhiteSpace(candidate.EventDetail) && (candidate.EventDetail.Contains("own", StringComparison.OrdinalIgnoreCase) || candidate.EventDetail.Contains("OG", StringComparison.OrdinalIgnoreCase))) ||
                            (!string.IsNullOrWhiteSpace(candidate.EventDescription) && (candidate.EventDescription.Contains("own", StringComparison.OrdinalIgnoreCase) || candidate.EventDescription.Contains("(OG)", StringComparison.OrdinalIgnoreCase)));
                if (isOg) detailSuffix = " (OG)";
                else if (!string.IsNullOrWhiteSpace(candidate.EventDetail) && candidate.EventDetail.Contains("pen", StringComparison.OrdinalIgnoreCase)) detailSuffix = " (pen)";
                if (string.IsNullOrEmpty(detailSuffix) && !string.IsNullOrWhiteSpace(candidate.AssistPlayerName) &&
                    !string.Equals(candidate.AssistPlayerName, "TBD", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(candidate.AssistPlayerName, "<TBD>", StringComparison.OrdinalIgnoreCase))
                {
                    detailSuffix = $" ({candidate.AssistPlayerName})";
                }

                if (!string.IsNullOrWhiteSpace(scoringTeam))
                {
                    title = $"⚽ GOAL! {scoringTeam}";

                    bool isHomeScorer = string.Equals(scoringTeam, homeName, StringComparison.OrdinalIgnoreCase);
                    string scorePart = isHomeScorer
                        ? $"{homeName} [{candidate.HomeScore}] - {candidate.AwayScore} {awayName}"
                        : $"{homeName} {candidate.HomeScore} - [{candidate.AwayScore}] {awayName}";

                    if (!string.IsNullOrWhiteSpace(player))
                    {
                        body = $"{scorePart} • {minuteDisplay} {player}{detailSuffix}";
                    }
                    else
                    {
                        body = $"{scorePart} • ({minuteDisplay}) — Goal for {scoringTeam}!";
                    }
                }
                else
                {
                    title = $"{homeName} {candidate.HomeScore}–{candidate.AwayScore} {awayName}";
                    if (!string.IsNullOrWhiteSpace(player))
                    {
                        body = $"{minuteDisplay} {player}{detailSuffix}";
                    }
                    else if (!string.IsNullOrWhiteSpace(candidate.EventDescription) &&
                             !string.Equals(candidate.EventDescription, "TBD", StringComparison.OrdinalIgnoreCase))
                    {
                        body = $"{minuteDisplay} {candidate.EventDescription}";
                    }
                    else
                    {
                        body = "Goal!";
                    }
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

            case NotificationType.SecondHalf:
                title = $"{candidate.HomeTeamName} {candidate.HomeScore}–{candidate.AwayScore} {candidate.AwayTeamName}";
                body = "Second half started";
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

            case NotificationType.RedCard:
                title = $"🟥 Red Card: {candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = !string.IsNullOrWhiteSpace(candidate.EventPlayerName)
                    ? $"{minuteDisplay} {candidate.EventPlayerName} sent off"
                    : $"{minuteDisplay} Red card awarded";
                break;

            case NotificationType.PenaltyAwarded:
                title = $"Penalty: {candidate.HomeTeamName} vs {candidate.AwayTeamName}";
                body = $"{minuteDisplay} Penalty awarded";
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
