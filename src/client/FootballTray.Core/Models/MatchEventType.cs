namespace FootballTray.Core.Models;

/// <summary>
/// Types of in-match events occurring during a fixture.
/// </summary>
public enum MatchEventType
{
    PeriodStart,
    PeriodEnd,
    Goal,
    OwnGoal,
    PenaltyGoal,
    PenaltyMiss,
    YellowCard,
    RedCard,
    Substitution,
    VarDecision
}
