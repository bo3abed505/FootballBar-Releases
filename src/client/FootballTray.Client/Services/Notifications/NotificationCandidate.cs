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
    public int ElapsedMinute { get; init; }
    public int ExtraMinute { get; init; }
    public string? EventPlayerName { get; init; }
    public string? EventDescription { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
