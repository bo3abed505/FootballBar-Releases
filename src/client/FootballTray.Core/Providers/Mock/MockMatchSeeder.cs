using System;
using System.Collections.Generic;
using FootballTray.Core.Models;

namespace FootballTray.Core.Providers.Mock;

/// <summary>
/// Seeds realistic development and testing mock fixtures, teams, lineups, and standings.
/// </summary>
public static class MockMatchSeeder
{
    public static readonly Competition LaLiga = new(
        Id: "comp_laliga",
        Name: "La Liga",
        Code: "ESP_L1",
        Country: "Spain",
        LogoUrl: "https://media.api-sports.io/football/leagues/140.png");

    public static readonly Season CurrentSeason = new(
        Id: "season_2025_2026",
        CompetitionId: "comp_laliga",
        Year: 2025,
        DisplayYear: "2025/2026",
        StartDate: new DateTime(2025, 8, 15, 0, 0, 0, DateTimeKind.Utc),
        EndDate: new DateTime(2026, 5, 24, 0, 0, 0, DateTimeKind.Utc),
        IsCurrent: true);

    public static readonly Team Barcelona = new(
        Id: "team_barcelona",
        Name: "FC Barcelona",
        ShortName: "BAR",
        Country: "Spain",
        LogoUrl: "https://media.api-sports.io/football/teams/529.png");

    public static readonly Team RealMadrid = new(
        Id: "team_realmadrid",
        Name: "Real Madrid",
        ShortName: "RMA",
        Country: "Spain",
        LogoUrl: "https://media.api-sports.io/football/teams/541.png");

    public static readonly Player PlayerLMartinez = new("ply_bar_10", "L. Martinez", "Martinez", 10, "FWD", "team_barcelona");
    public static readonly Player PlayerPGonzalez = new("ply_bar_8", "P. Gonzalez", "Pedri", 8, "MID", "team_barcelona");
    public static readonly Player PlayerFDeJong = new("ply_bar_21", "F. De Jong", "De Jong", 21, "MID", "team_barcelona");

    public static readonly Player PlayerKBecker = new("ply_rma_9", "K. Becker", "Becker", 9, "FWD", "team_realmadrid");
    public static readonly Player PlayerRHernandez = new("ply_rma_7", "R. Hernandez", "Hernandez", 7, "FWD", "team_realmadrid");
    public static readonly Player PlayerVSilva = new("ply_rma_5", "V. Silva", "Silva", 5, "DEF", "team_realmadrid");

    /// <summary>
    /// Creates the standard development El Clásico match seeded at pre-match state.
    /// </summary>
    public static Fixture CreateElClasicoFixture(string fixtureId = "fix_elclasico_2026")
    {
        return new Fixture(
            id: fixtureId,
            competition: LaLiga,
            season: CurrentSeason,
            homeTeam: Barcelona,
            awayTeam: RealMadrid,
            kickoffTime: DateTime.UtcNow.AddMinutes(-45),
            state: MatchState.PreMatch,
            elapsedMinute: 0,
            extraMinute: 0,
            score: Score.Empty);
    }
}
