namespace FootballTray.Core.Models;

/// <summary>
/// Represents the deterministic lifecycle states of a football match.
/// </summary>
public enum MatchState
{
    PreMatch,
    Live,
    HalfTime,
    Finished,
    PostMatch,
    Postponed,
    Cancelled
}
