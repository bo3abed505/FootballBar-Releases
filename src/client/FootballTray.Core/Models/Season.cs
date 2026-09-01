using System;

namespace FootballTray.Core.Models;

/// <summary>
/// Represents a specific competition season.
/// </summary>
public sealed record Season(
    string Id,
    string CompetitionId,
    int Year,
    string DisplayYear,
    DateTime StartDate,
    DateTime EndDate,
    bool IsCurrent = true);
