namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Fully formatted and normalized payload ready for Windows notification dispatch.
/// </summary>
public sealed record NotificationPayload
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string ActivationArguments { get; init; }
    public required string DeduplicationKey { get; init; }
    public NotificationType Type { get; init; }
    public string FixtureId { get; init; } = string.Empty;
}
