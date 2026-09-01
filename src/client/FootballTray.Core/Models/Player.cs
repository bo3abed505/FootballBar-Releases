namespace FootballTray.Core.Models;

/// <summary>
/// Domain model for a football player or team staff member.
/// </summary>
public sealed record Player(
    string Id,
    string Name,
    string? ShortName = null,
    int? Number = null,
    string? Position = null,
    string? TeamId = null);
