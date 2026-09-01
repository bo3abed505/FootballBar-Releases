using System;
using System.Collections.Generic;

namespace FootballTray.Core.Models;

public sealed record StandingRow(
    int Position,
    Team Team,
    int Played,
    int Won,
    int Drawn,
    int Lost,
    int GoalsFor,
    int GoalsAgainst,
    int Points,
    string? Form = null)
{
    public int GoalDifference => GoalsFor - GoalsAgainst;
}

/// <summary>
/// League table / standings table for a competition season.
/// </summary>
public sealed record Standing(
    string CompetitionId,
    string SeasonId,
    IReadOnlyList<StandingRow> Rows);
