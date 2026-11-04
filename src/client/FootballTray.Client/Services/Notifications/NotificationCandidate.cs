using System;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Represents a fully contextualized notification candidate generated from football domain state changes.
/// Contains all metadata needed to render a rich alert without requiring additional network queries.
/// </summary>
public sealed record NotificationCandidate
{
    public required string FixtureId { get; init; }
    public required NotificationType Type { get; init; }
    public required string DeduplicationKey { get; init; }
    public string? EventUid { get; init; }
    public string? PreviousState { get; init; }
    public required string NewState { get; init; }
    public required string HomeTeamName { get; init; }
    public required string AwayTeamName { get; init; }
    public required int HomeScore { get; init; }
    public required int AwayScore { get; init; }
    public int ElapsedMinute { get; set; }
    public int ExtraMinute { get; set; }
    public string? CompetitionId { get; init; }
    public string? CompetitionLegacyId { get; init; }
    public string? HomeTeamId { get; init; }
    public string? HomeTeamLegacyId { get; init; }
    public string? AwayTeamId { get; init; }
    public string? AwayTeamLegacyId { get; init; }
    public string? EventPlayerName { get; set; }
    public string? EventDescription { get; set; }
    public string? ScoringTeamName { get; set; }
    public string? AssistPlayerName { get; set; }
    public string? EventDetail { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
