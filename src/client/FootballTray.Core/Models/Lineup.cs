using System;
using System.Collections.Generic;

namespace FootballTray.Core.Models;

/// <summary>
/// Represents team formation, starting XI, substitutes, and coaching staff for a fixture.
/// </summary>
public sealed record Lineup(
    string FixtureId,
    string TeamId,
    string Formation,
    IReadOnlyList<Player> StartingXI,
    IReadOnlyList<Player> Substitutes,
    Player? Coach = null);
