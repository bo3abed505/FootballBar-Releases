using System;
using System.Security.Cryptography;
using System.Text;

namespace FootballTray.Core.Models;

/// <summary>
/// Domain model for an event occurring during a match, featuring deterministic UID generation for deduplication.
/// </summary>
public sealed record MatchEvent
{
    public string FixtureId { get; init; }
    public string EventUid { get; init; }
    public MatchEventType Type { get; init; }
    public int Minute { get; init; }
    public int ExtraMinute { get; init; }
    public Team Team { get; init; }
    public Player? Player { get; init; }
    public Player? AssistPlayer { get; init; }
    public string? Description { get; init; }
    public DateTime Timestamp { get; init; }

    public MatchEvent(
        string fixtureId,
        string eventUid,
        MatchEventType type,
        int minute,
        int extraMinute,
        Team team,
        Player? player = null,
        Player? assistPlayer = null,
        string? description = null,
        DateTime? timestamp = null)
    {
        FixtureId = fixtureId ?? throw new ArgumentNullException(nameof(fixtureId));
        EventUid = eventUid ?? throw new ArgumentNullException(nameof(eventUid));
        Type = type;
        Minute = minute;
        ExtraMinute = extraMinute;
        Team = team ?? throw new ArgumentNullException(nameof(team));
        Player = player;
        AssistPlayer = assistPlayer;
        Description = description;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }

    /// <summary>
    /// Factory method that creates a MatchEvent with an automatically calculated deterministic EventUid.
    /// </summary>
    public static MatchEvent Create(
        string fixtureId,
        MatchEventType type,
        int minute,
        int extraMinute,
        Team team,
        Player? player = null,
        Player? assistPlayer = null,
        string? description = null,
        DateTime? timestamp = null)
    {
        string uid = GenerateEventUid(fixtureId, type, minute, extraMinute, team.Id, player?.Id, assistPlayer?.Id, description);

        return new MatchEvent(
            fixtureId: fixtureId,
            eventUid: uid,
            type: type,
            minute: minute,
            extraMinute: extraMinute,
            team: team,
            player: player,
            assistPlayer: assistPlayer,
            description: description,
            timestamp: timestamp);
    }

    /// <summary>
    /// Generates a deterministic EventUid from semantic match event properties.
    /// </summary>
    public static string GenerateEventUid(
        string fixtureId,
        MatchEventType type,
        int minute,
        int extraMinute,
        string teamId,
        string? playerId = null,
        string? assistPlayerId = null,
        string? description = null)
    {
        string raw = $"{fixtureId}|{type}|{minute}+{extraMinute}|{teamId}|{playerId ?? "none"}|{assistPlayerId ?? "none"}|{description ?? "none"}";
        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));
        string hashHex = Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();

        return $"evt_{fixtureId}_{minute}_{(extraMinute > 0 ? extraMinute : 0)}_{type}_{hashHex}";
    }

    public string MinuteDisplay => ExtraMinute > 0 ? $"{Minute}+{ExtraMinute}'" : $"{Minute}'";
}
