using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class SerializationRegressionTests
{
    private string _tempDirectory = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"FootballTray_SerializationTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [TestMethod]
    public async Task Test01_TrayApiEnvelope_Deserialization_WorksWithSourceGeneration()
    {
        // Realistic payload matching production /api/tray contract
        const string trayJson = """
        {
          "schemaVersion": "1.0",
          "requestId": "req_1788911613468_000002",
          "version": 1,
          "generatedAt": "2026-09-08T23:53:33.468Z",
          "serverTime": "2026-09-08T23:53:33.468Z",
          "stale": false,
          "dataAgeSeconds": 0,
          "data": {
            "matches": [
              {
                "id": "fix_1570392",
                "competition": {
                  "id": "comp_140",
                  "name": "La Liga",
                  "code": "LA"
                },
                "homeTeam": {
                  "id": "team_548",
                  "name": "Real Sociedad",
                  "shortName": "REA",
                  "logoUrl": "https://media.api-sports.io/football/teams/548.png"
                },
                "awayTeam": {
                  "id": "team_538",
                  "name": "Celta Vigo",
                  "shortName": "CEL",
                  "logoUrl": "https://media.api-sports.io/football/teams/538.png"
                },
                "state": "FINISHED",
                "elapsedMinute": 90,
                "extraMinute": 3,
                "score": {
                  "home": 0,
                  "away": 0,
                  "display": "0 - 0"
                },
                "kickoffTime": "2026-09-03T19:00:00.000Z",
                "recentEvents": [
                  {
                    "id": "evt_fix_1570392_15",
                    "type": "Substitution",
                    "minute": 88,
                    "extraMinute": 0,
                    "teamId": "team_548",
                    "playerName": "Benat Turrientes",
                    "description": "[subst] Substitution 5"
                  }
                ]
              }
            ]
          }
        }
        """;

        // 1. Test direct source generator deserialization
        var envelope = JsonSerializer.Deserialize(trayJson, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);

        Assert.IsNotNull(envelope, "Deserialized envelope should not be null.");
        Assert.AreEqual("1.0", envelope.SchemaVersion);
        Assert.AreEqual("req_1788911613468_000002", envelope.RequestId);
        Assert.AreEqual(1, envelope.Version);
        Assert.IsFalse(envelope.Stale);
        Assert.IsNotNull(envelope.Data);
        Assert.AreEqual(1, envelope.Data.Matches.Count);

        var match = envelope.Data.Matches[0];
        Assert.AreEqual("fix_1570392", match.Id);
        Assert.AreEqual("La Liga", match.Competition?.Name);
        Assert.AreEqual("Real Sociedad", match.HomeTeam.Name);
        Assert.AreEqual("Celta Vigo", match.AwayTeam.Name);
        Assert.AreEqual("FINISHED", match.State);
        Assert.AreEqual(90, match.ElapsedMinute);
        Assert.AreEqual(3, match.ExtraMinute);
        Assert.AreEqual("0 - 0", match.Score.Display);
        Assert.AreEqual(1, match.RecentEvents.Count);
        Assert.AreEqual("Benat Turrientes", match.RecentEvents[0].PlayerName);

        Assert.IsTrue(TrayPayloadValidator.IsValid(envelope, out var validationError), $"Payload should pass validator: {validationError}");

        // 2. Test FootballApiClient deserialization path via mock handler
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(trayJson, Encoding.UTF8, "application/json")
            }
        };

        using var client = new FootballApiClient(new HttpClient(mockHandler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        var apiResult = await client.GetTrayAsync();

        Assert.IsTrue(apiResult.IsSuccess, $"ApiClient should succeed: {apiResult.ErrorMessage}");
        Assert.IsNotNull(apiResult.Envelope);
        Assert.AreEqual(1, apiResult.Envelope.Data?.Matches.Count);
    }

    [TestMethod]
    public async Task Test02_MatchDetailEnvelope_Deserialization_WorksWithSourceGeneration()
    {
        // Realistic payload matching production /api/match/:id contract
        const string matchDetailJson = """
        {
          "schemaVersion": "1.0",
          "requestId": "req_1788911643689_000005",
          "version": 1,
          "generatedAt": "2026-09-08T23:54:03.689Z",
          "serverTime": "2026-09-08T23:54:03.689Z",
          "stale": false,
          "dataAgeSeconds": 0,
          "data": {
            "id": "fix_1570392",
            "competition": {
              "id": "comp_140",
              "name": "La Liga",
              "code": "LA"
            },
            "homeTeam": {
              "id": "team_548",
              "name": "Real Sociedad",
              "shortName": "REA",
              "logoUrl": "https://media.api-sports.io/football/teams/548.png"
            },
            "awayTeam": {
              "id": "team_538",
              "name": "Celta Vigo",
              "shortName": "CEL",
              "logoUrl": "https://media.api-sports.io/football/teams/538.png"
            },
            "state": "FINISHED",
            "elapsedMinute": 90,
            "extraMinute": 3,
            "score": {
              "home": 0,
              "away": 0,
              "halfTime": {
                "home": 0,
                "away": 0
              },
              "display": "0 - 0"
            },
            "venue": "Anoeta",
            "referee": "V. Garcia",
            "kickoffTime": "2026-09-03T19:00:00.000Z",
            "events": [
              {
                "id": "evt_fix_1570392_1",
                "type": "YellowCard",
                "minute": 22,
                "extraMinute": 0,
                "teamId": "team_548",
                "playerName": "Yangel Herrera",
                "description": "[Card] Yellow Card"
              }
            ]
          }
        }
        """;

        // 1. Test direct source generator deserialization
        var envelope = JsonSerializer.Deserialize(matchDetailJson, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);

        Assert.IsNotNull(envelope, "Match detail envelope should not be null.");
        Assert.IsNotNull(envelope.Data);
        Assert.AreEqual("fix_1570392", envelope.Data.Id);
        Assert.AreEqual("La Liga", envelope.Data.Competition?.Name);
        Assert.AreEqual("Real Sociedad", envelope.Data.HomeTeam.Name);
        Assert.AreEqual("Celta Vigo", envelope.Data.AwayTeam.Name);
        Assert.AreEqual("FINISHED", envelope.Data.State);
        Assert.AreEqual("Anoeta", envelope.Data.Venue);
        Assert.AreEqual("V. Garcia", envelope.Data.Referee);
        Assert.AreEqual(1, envelope.Data.Events.Count);
        Assert.AreEqual("Yangel Herrera", envelope.Data.Events[0].PlayerName);

        // 2. Test FootballApiClient deserialization path via mock handler
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(matchDetailJson, Encoding.UTF8, "application/json")
            }
        };

        using var client = new FootballApiClient(new HttpClient(mockHandler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        var apiResult = await client.GetMatchDetailsAsync("fix_1570392");

        Assert.IsTrue(apiResult.IsSuccess, $"GetMatchDetailsAsync should succeed: {apiResult.ErrorMessage}");
        Assert.IsNotNull(apiResult.Envelope?.Data);
        Assert.AreEqual("fix_1570392", apiResult.Envelope.Data.Id);
    }

    [TestMethod]
    public void Test02B_MatchDetail_WithLineupsAndStatistics_DeserializesSuccessfully()
    {
        const string v2MatchWithLineupsAndStatsJson = """
        {
          "schemaVersion": "2.0",
          "requestId": "req_1788911613468_000003",
          "version": 2,
          "generatedAt": "2026-09-23T01:00:00.000Z",
          "serverTime": "2026-09-23T01:00:00.000Z",
          "stale": false,
          "dataAgeSeconds": 10,
          "data": {
            "id": "ft_fix_elclasico",
            "legacyId": "fix_99901",
            "competition": {
              "id": "ft_comp_laliga",
              "legacyId": "comp_140",
              "name": "La Liga",
              "code": "PD"
            },
            "homeTeam": {
              "id": "ft_team_barcelona",
              "legacyId": "team_529",
              "name": "FC Barcelona",
              "shortName": "BAR",
              "logoUrl": "https://media.api-sports.io/football/teams/529.png"
            },
            "awayTeam": {
              "id": "ft_team_realmadrid",
              "legacyId": "team_541",
              "name": "Real Madrid",
              "shortName": "RMA",
              "logoUrl": "https://media.api-sports.io/football/teams/541.png"
            },
            "state": "LIVE",
            "elapsedMinute": 65,
            "extraMinute": 0,
            "score": {
              "home": 2,
              "away": 1,
              "halfTime": {
                "home": 1,
                "away": 0
              },
              "display": "2 - 1"
            },
            "venue": "Camp Nou",
            "referee": "Gil Manzano",
            "kickoffTime": "2026-09-23T19:00:00.000Z",
            "events": [
              {
                "id": "evt_1",
                "type": "Goal",
                "minute": 23,
                "extraMinute": 0,
                "teamId": "ft_team_barcelona",
                "playerName": "R. Lewandowski",
                "description": "Goal"
              }
            ],
            "lineups": [
              {
                "teamId": "ft_team_barcelona",
                "formation": "4-3-3",
                "coachName": "Hansi Flick",
                "coach": {
                  "id": "coach_1",
                  "name": "Hansi Flick"
                },
                "startingXI": [
                  { "name": "M. ter Stegen", "number": 1, "position": "G", "grid": "1:1", "isStarter": true },
                  { "name": "J. Kounde", "number": 23, "position": "D", "grid": "2:1", "isStarter": true },
                  { "name": "P. Cubarsi", "number": 2, "position": "D", "grid": "2:2", "isStarter": true },
                  { "name": "I. Martinez", "number": 5, "position": "D", "grid": "2:3", "isStarter": true },
                  { "name": "A. Balde", "number": 3, "position": "D", "grid": "2:4", "isStarter": true },
                  { "name": "M. Casado", "number": 17, "position": "M", "grid": "3:1", "isStarter": true },
                  { "name": "Pedri", "number": 8, "position": "M", "grid": "3:2", "isStarter": true },
                  { "name": "D. Olmo", "number": 20, "position": "M", "grid": "3:3", "isStarter": true },
                  { "name": "L. Yamal", "number": 19, "position": "F", "grid": "4:1", "isStarter": true },
                  { "name": "R. Lewandowski", "number": 9, "position": "F", "grid": "4:2", "isStarter": true },
                  { "name": "Raphinha", "number": 11, "position": "F", "grid": "4:3", "isStarter": true }
                ],
                "substitutes": [
                  { "name": "Inaki Pena", "number": 13, "position": "G", "isStarter": false },
                  { "name": "F. de Jong", "number": 21, "position": "M", "isStarter": false },
                  { "name": "Gavi", "number": 6, "position": "M", "isStarter": false },
                  { "name": "Ferran Torres", "number": 7, "position": "F", "isStarter": false },
                  { "name": "Ansu Fati", "number": 10, "position": "F", "isStarter": false }
                ]
              },
              {
                "teamId": "ft_team_realmadrid",
                "formation": "4-2-3-1",
                "coachName": "Carlo Ancelotti",
                "coach": {
                  "id": "coach_2",
                  "name": "Carlo Ancelotti"
                },
                "startingXI": [
                  { "name": "T. Courtois", "number": 1, "position": "G", "grid": "1:1", "isStarter": true },
                  { "name": "D. Carvajal", "number": 2, "position": "D", "grid": "2:1", "isStarter": true },
                  { "name": "E. Militao", "number": 3, "position": "D", "grid": "2:2", "isStarter": true },
                  { "name": "A. Rudiger", "number": 22, "position": "D", "grid": "2:3", "isStarter": true },
                  { "name": "F. Mendy", "number": 23, "position": "D", "grid": "2:4", "isStarter": true },
                  { "name": "F. Valverde", "number": 8, "position": "M", "grid": "3:1", "isStarter": true },
                  { "name": "A. Tchouameni", "number": 14, "position": "M", "grid": "3:2", "isStarter": true },
                  { "name": "Rodrygo", "number": 11, "position": "F", "grid": "4:1", "isStarter": true },
                  { "name": "J. Bellingham", "number": 5, "position": "M", "grid": "4:2", "isStarter": true },
                  { "name": "Vinicius Jr", "number": 7, "position": "F", "grid": "4:3", "isStarter": true },
                  { "name": "K. Mbappe", "number": 9, "position": "F", "grid": "5:1", "isStarter": true }
                ],
                "substitutes": [
                  { "name": "A. Lunin", "number": 13, "position": "G", "isStarter": false },
                  { "name": "L. Modric", "number": 10, "position": "M", "isStarter": false },
                  { "name": "E. Camavinga", "number": 6, "position": "M", "isStarter": false },
                  { "name": "B. Diaz", "number": 21, "position": "F", "isStarter": false },
                  { "name": "Endrick", "number": 16, "position": "F", "isStarter": false }
                ]
              }
            ],
            "statistics": [
              {
                "teamId": "ft_team_barcelona",
                "possessionPercent": 58,
                "totalShots": 14,
                "shotsOnGoal": 6,
                "shotsOffGoal": 8,
                "totalPasses": 520,
                "passesAccurate": 465,
                "fouls": 10,
                "cornerKicks": 7,
                "yellowCards": 2,
                "redCards": 0,
                "goalkeeperSaves": 3
              },
              {
                "teamId": "ft_team_realmadrid",
                "possessionPercent": 42,
                "totalShots": 8,
                "shotsOnGoal": 3,
                "shotsOffGoal": 5,
                "totalPasses": 380,
                "passesAccurate": 320,
                "fouls": 12,
                "cornerKicks": 4,
                "yellowCards": 1,
                "redCards": 0,
                "goalkeeperSaves": 5
              }
            ],
            "trust": {
              "freshnessState": "FRESH",
              "sourceObservedAt": "2026-09-23T01:00:00.000Z",
              "dataAgeSeconds": 5,
              "provider": "api-football"
            }
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize(v2MatchWithLineupsAndStatsJson, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);

        Assert.IsNotNull(envelope);
        Assert.IsNotNull(envelope.Data);
        var match = envelope.Data;

        // Lineup verification
        Assert.AreEqual(2, match.Lineups.Count, "Should deserialize 2 lineups.");
        var barcaLineup = match.Lineups[0];
        Assert.AreEqual("ft_team_barcelona", barcaLineup.TeamId);
        Assert.AreEqual("4-3-3", barcaLineup.Formation);
        Assert.AreEqual("Hansi Flick", barcaLineup.EffectiveCoachName);
        Assert.AreEqual(11, barcaLineup.StartingXI.Count);
        Assert.AreEqual(5, barcaLineup.Substitutes.Count);
        Assert.AreEqual("R. Lewandowski", barcaLineup.StartingXI[9].Name);
        Assert.AreEqual(9, barcaLineup.StartingXI[9].Number);
        Assert.AreEqual("F", barcaLineup.StartingXI[9].Position);

        var realLineup = match.Lineups[1];
        Assert.AreEqual("ft_team_realmadrid", realLineup.TeamId);
        Assert.AreEqual("4-2-3-1", realLineup.Formation);
        Assert.AreEqual("Carlo Ancelotti", realLineup.EffectiveCoachName);
        Assert.AreEqual(11, realLineup.StartingXI.Count);
        Assert.AreEqual("K. Mbappe", realLineup.StartingXI[10].Name);

        // Statistics verification
        Assert.AreEqual(2, match.Statistics.Count, "Should deserialize 2 team statistics.");
        var barcaStats = match.Statistics[0];
        Assert.AreEqual("ft_team_barcelona", barcaStats.TeamId);
        Assert.AreEqual(58, barcaStats.PossessionPercent);
        Assert.AreEqual(14, barcaStats.TotalShots);
        Assert.AreEqual(6, barcaStats.ShotsOnGoal);
        Assert.AreEqual(520, barcaStats.TotalPasses);
        Assert.AreEqual(465, barcaStats.PassesAccurate);
        Assert.AreEqual(10, barcaStats.Fouls);
        Assert.AreEqual(7, barcaStats.CornerKicks);
        Assert.AreEqual(2, barcaStats.YellowCards);
        Assert.AreEqual(0, barcaStats.RedCards);
        Assert.AreEqual(3, barcaStats.GoalkeeperSaves);

        var realStats = match.Statistics[1];
        Assert.AreEqual("ft_team_realmadrid", realStats.TeamId);
        Assert.AreEqual(42, realStats.PossessionPercent);
        Assert.AreEqual(8, realStats.TotalShots);
        Assert.AreEqual(3, realStats.ShotsOnGoal);
    }

    [TestMethod]
    public void Test02C_MatchCenterViewModel_HydratesLineupsAndStatistics_Correctly()
    {
        var match = new MatchDetailDto
        {
            Id = "ft_fix_1001",
            State = "LIVE",
            ElapsedMinute = 74,
            HomeTeam = new TeamSummaryDto { Id = "ft_team_home", Name = "Arsenal" },
            AwayTeam = new TeamSummaryDto { Id = "ft_team_away", Name = "Chelsea" },
            Score = new ScoreDto { Home = 3, Away = 1 },
            Lineups = new List<LineupDto>
            {
                new LineupDto
                {
                    TeamId = "ft_team_home",
                    Formation = "4-3-3",
                    CoachName = "Mikel Arteta",
                    StartingXI = new List<LineupPlayerDto>
                    {
                        new LineupPlayerDto { Name = "D. Raya", Number = 22, Position = "G", IsStarter = true },
                        new LineupPlayerDto { Name = "B. Saka", Number = 7, Position = "F", IsStarter = true }
                    },
                    Substitutes = new List<LineupPlayerDto>
                    {
                        new LineupPlayerDto { Name = "L. Trossard", Number = 19, Position = "F", IsStarter = false }
                    }
                },
                new LineupDto
                {
                    TeamId = "ft_team_away",
                    Formation = "4-2-3-1",
                    CoachName = "Enzo Maresca",
                    StartingXI = new List<LineupPlayerDto>
                    {
                        new LineupPlayerDto { Name = "R. Sanchez", Number = 1, Position = "G", IsStarter = true },
                        new LineupPlayerDto { Name = "C. Palmer", Number = 20, Position = "M", IsStarter = true }
                    },
                    Substitutes = new List<LineupPlayerDto>
                    {
                        new LineupPlayerDto { Name = "J. Felix", Number = 14, Position = "F", IsStarter = false }
                    }
                }
            },
            Statistics = new List<TeamStatisticsDto>
            {
                new TeamStatisticsDto
                {
                    TeamId = "ft_team_home",
                    PossessionPercent = 55,
                    TotalShots = 12,
                    ShotsOnGoal = 5,
                    CornerKicks = 6,
                    Fouls = 9,
                    YellowCards = 1
                },
                new TeamStatisticsDto
                {
                    TeamId = "ft_team_away",
                    PossessionPercent = 45,
                    TotalShots = 8,
                    ShotsOnGoal = 3,
                    CornerKicks = 4,
                    Fouls = 11,
                    YellowCards = 2
                }
            }
        };

        var vm = new MatchCenterViewModel();
        vm.Load(match);

        Assert.AreEqual("Arsenal vs Chelsea - Match Center", vm.Title);
        Assert.AreEqual("LIVE 74'", vm.StatusText);
        Assert.AreEqual("3 - 1", vm.ScoreText);

        // Verify Lineup Hydration
        Assert.IsTrue(vm.HasLineups, "ViewModel should report HasLineups = true");
        Assert.AreEqual("4-3-3", vm.HomeLineup.Formation);
        Assert.AreEqual("Mikel Arteta", vm.HomeLineup.CoachName);
        Assert.AreEqual(2, vm.HomeLineup.StartingXI.Count);
        Assert.AreEqual(1, vm.HomeLineup.Substitutes.Count);
        Assert.AreEqual("D. Raya", vm.HomeLineup.StartingXI[0].Name);
        Assert.AreEqual("GK", vm.HomeLineup.StartingXI[0].PositionBadge);

        Assert.AreEqual("4-2-3-1", vm.AwayLineup.Formation);
        Assert.AreEqual("Enzo Maresca", vm.AwayLineup.CoachName);
        Assert.AreEqual(2, vm.AwayLineup.StartingXI.Count);

        // Verify Statistics Hydration
        Assert.IsTrue(vm.HasStatistics, "ViewModel should report HasStatistics = true");
        Assert.IsTrue(vm.MatchStats.Count > 0, "MatchStats should contain parsed metrics");

        var possessionStat = vm.MatchStats.FirstOrDefault(s => s.Name == "Ball Possession");
        Assert.IsNotNull(possessionStat);
        Assert.AreEqual("55%", possessionStat.HomeValue);
        Assert.AreEqual("45%", possessionStat.AwayValue);
        Assert.AreEqual(55.0, possessionStat.HomeRatio);
        Assert.AreEqual(45.0, possessionStat.AwayRatio);

        var shotsStat = vm.MatchStats.FirstOrDefault(s => s.Name == "Total Shots");
        Assert.IsNotNull(shotsStat);
        Assert.AreEqual("12", shotsStat.HomeValue);
        Assert.AreEqual("8", shotsStat.AwayValue);
        Assert.AreEqual(60.0, shotsStat.HomeRatio);
        Assert.AreEqual(40.0, shotsStat.AwayRatio);
    }

    [TestMethod]
    public void Test02D_LineupDto_FlatPlayersFallback_SplitsStartingXIAndSubstitutes()
    {
        var flatLineup = new LineupDto
        {
            TeamId = "team_1",
            Formation = "3-5-2",
            Players = new List<LineupPlayerDto>
            {
                new LineupPlayerDto { Name = "P1", Number = 1, IsStarter = true },
                new LineupPlayerDto { Name = "P2", Number = 2, IsStarter = true },
                new LineupPlayerDto { Name = "P3", Number = 12, IsStarter = false }
            }
        };

        var starters = flatLineup.GetStartingXI();
        var subs = flatLineup.GetSubstitutes();

        Assert.AreEqual(2, starters.Count);
        Assert.AreEqual(1, subs.Count);
        Assert.AreEqual("P1", starters[0].Name);
        Assert.AreEqual("P3", subs[0].Name);
    }

    [TestMethod]
    public async Task Test03_Preferences_LoadSaveRoundTrip_WorksWithSourceGeneration()
    {
        var preferencesService = new PreferencesService(_tempDirectory);
        var initial = await preferencesService.LoadAsync();

        Assert.IsNotNull(initial);
        Assert.AreEqual(UserPreferences.CurrentSchemaVersion, initial.PreferencesVersion);

        // Follow a team and save
        var barca = new FollowedTeam(CanonicalEntityIds.Barcelona, "FC Barcelona", "BAR", "Spain", "https://media.api-sports.io/football/teams/529.png");
        var alahly = new FollowedTeam(CanonicalEntityIds.AlAhly, "Al Ahly SC", "AHL", "Egypt", "https://media.api-sports.io/football/teams/1029.png");
        await preferencesService.FollowTeamAsync(barca);
        await preferencesService.FollowTeamAsync(alahly);

        // Verify file written to disk
        string prefsFilePath = Path.Combine(_tempDirectory, "preferences.json");
        Assert.IsTrue(File.Exists(prefsFilePath), "preferences.json should exist on disk.");

        string rawJson = await File.ReadAllTextAsync(prefsFilePath);
        Assert.IsTrue(rawJson.Contains(CanonicalEntityIds.Barcelona), "Saved JSON should contain followed team ID.");
        Assert.IsTrue(rawJson.Contains(CanonicalEntityIds.AlAhly), "Saved JSON should contain second followed team ID.");

        // Unfollow team (this was the exact runtime crash reported in smoke test)
        await preferencesService.UnfollowTeamAsync(CanonicalEntityIds.AlAhly);

        // Reload from disk using a fresh PreferencesService instance
        var freshService = new PreferencesService(_tempDirectory);
        var reloaded = await freshService.LoadAsync();

        Assert.IsNotNull(reloaded);
        Assert.AreEqual(1, reloaded.FollowedTeams.Count);
        Assert.AreEqual(CanonicalEntityIds.Barcelona, reloaded.FollowedTeams[0].Id);
        Assert.AreEqual("FC Barcelona", reloaded.FollowedTeams[0].Name);

        // Verify existing disk format compatibility (numeric enabledTypes enum array)
        const string legacyDiskJson = """
        {
          "preferencesVersion": 1,
          "followedTeams": [
            {
              "id": "team_529",
              "name": "FC Barcelona",
              "shortName": "BAR",
              "country": "Spain",
              "logoUrl": "https://media.api-sports.io/football/teams/529.png"
            }
          ],
          "followedCompetitions": [
            {
              "id": "comp_39",
              "name": "Premier League",
              "code": "PL",
              "country": "England",
              "logoUrl": "https://media.api-sports.io/football/leagues/39.png"
            }
          ],
          "notifications": {
            "globalEnabled": true,
            "enabledTypes": [0, 1, 2, 3, 4, 5, 6]
          }
        }
        """;

        var legacyLoaded = JsonSerializer.Deserialize(legacyDiskJson, FootballJsonSerializerContext.Default.UserPreferences);
        Assert.IsNotNull(legacyLoaded);
        Assert.AreEqual(1, legacyLoaded.FollowedTeams.Count);
        Assert.AreEqual(1, legacyLoaded.FollowedCompetitions.Count);
        Assert.AreEqual(7, legacyLoaded.Notifications.EnabledTypes.Count);
        Assert.IsTrue(legacyLoaded.Notifications.IsTypeEnabled(NotificationType.Goal));
    }

    [TestMethod]
    public async Task Test04_LocalCache_LoadSaveRoundTrip_WorksWithSourceGeneration()
    {
        var cacheService = new LocalCacheService(_tempDirectory);

        var sampleEnvelope = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_cache_test",
            Version = 5,
            GeneratedAt = DateTimeOffset.UtcNow,
            ServerTime = DateTimeOffset.UtcNow,
            Stale = false,
            DataAgeSeconds = 0,
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "fix_cache_1",
                        HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team One", ShortName = "T1" },
                        AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team Two", ShortName = "T2" },
                        State = "LIVE",
                        ElapsedMinute = 45,
                        Score = new ScoreDto { Home = 1, Away = 0, Display = "1 - 0" }
                    }
                }
            }
        };

        const string testEtag = "\"cache-test-etag-456\"";
        bool saved = await cacheService.SaveCacheAsync(sampleEnvelope, testEtag);
        Assert.IsTrue(saved, "SaveCacheAsync should return true.");

        string cacheFilePath = Path.Combine(_tempDirectory, "tray.json");
        Assert.IsTrue(File.Exists(cacheFilePath), "tray.json should exist on disk.");

        // Read back via a fresh LocalCacheService instance
        var freshCacheService = new LocalCacheService(_tempDirectory);
        var loaded = await freshCacheService.LoadCacheAsync();

        Assert.IsNotNull(loaded, "LoadCacheAsync should return populated payload.");
        Assert.AreEqual(testEtag, loaded.ETag);
        Assert.IsNotNull(loaded.Envelope?.Data);
        Assert.AreEqual(1, loaded.Envelope.Data.Matches.Count);
        Assert.AreEqual("fix_cache_1", loaded.Envelope.Data.Matches[0].Id);
        Assert.AreEqual("Team One", loaded.Envelope.Data.Matches[0].HomeTeam.Name);
        Assert.AreEqual("LIVE", loaded.Envelope.Data.Matches[0].State);
    }

    [TestMethod]
    public async Task Test05_NotificationDeduplicationStore_PersistenceRoundTrip_WorksWithSourceGeneration()
    {
        var store = new NotificationDeduplicationStore(_tempDirectory);
        await store.LoadAsync();

        string eventKey1 = "match:fix_1570392:event:evt_1";
        string eventKey2 = "match:fix_1570392:event:evt_2";

        Assert.IsFalse(store.IsAlreadyNotified(eventKey1), "New key should not be already notified.");
        store.MarkNotified(eventKey1);
        Assert.IsTrue(store.IsAlreadyNotified(eventKey1), "Key should now be detected as notified.");

        store.MarkNotified(eventKey2);
        await store.SaveAsync();

        string historyFilePath = Path.Combine(_tempDirectory, "notifications_history.json");
        Assert.IsTrue(File.Exists(historyFilePath), "notifications_history.json should exist on disk.");

        // Reload via fresh store instance
        var freshStore = new NotificationDeduplicationStore(_tempDirectory);
        await freshStore.LoadAsync();

        Assert.IsTrue(freshStore.IsAlreadyNotified(eventKey1), "Reloaded store should recognize persisted key 1.");
        Assert.IsTrue(freshStore.IsAlreadyNotified(eventKey2), "Reloaded store should recognize persisted key 2.");
        Assert.IsFalse(freshStore.IsAlreadyNotified("match:fix_1570392:event:evt_3"), "Unsaved key should not be notified.");
    }

    [TestMethod]
    public async Task Test06_UserPreferences_PreservesUnknownExtensionData_AcrossModifyAndSave()
    {
        string prefPath = Path.Combine(_tempDirectory, "preferences.json");
        const string initialJsonWithUnknowns = """
        {
          "preferencesVersion": 1,
          "followedTeams": [
            { "id": "team_529", "name": "FC Barcelona" }
          ],
          "followedCompetitions": [],
          "notifications": { "globalEnabled": true, "enabledTypes": [0, 1] },
          "futureGlobalSetting": "dark_mode",
          "customTelemetryId": 98765
        }
        """;
        await File.WriteAllTextAsync(prefPath, initialJsonWithUnknowns);

        var prefService = new PreferencesService(_tempDirectory);
        var loaded = await prefService.LoadAsync();

        Assert.IsNotNull(loaded, "Preferences should load successfully.");
        Assert.IsNotNull(loaded.ExtensionData, "ExtensionData dictionary should be populated with unknown properties.");
        Assert.IsTrue(loaded.ExtensionData.ContainsKey("futureGlobalSetting"), "futureGlobalSetting should be captured.");
        Assert.AreEqual("dark_mode", loaded.ExtensionData["futureGlobalSetting"].GetString());
        Assert.IsTrue(loaded.ExtensionData.ContainsKey("customTelemetryId"), "customTelemetryId should be captured.");
        Assert.AreEqual(98765, loaded.ExtensionData["customTelemetryId"].GetInt32());

        // Perform a modification (e.g. follow a new team)
        await prefService.FollowTeamAsync(new FollowedTeam("team_548", "Real Sociedad"));

        // Verify the persisted file still contains the unknown properties
        string savedJson = await File.ReadAllTextAsync(prefPath);
        using var doc = JsonDocument.Parse(savedJson);
        var root = doc.RootElement;

        Assert.IsTrue(root.TryGetProperty("futureGlobalSetting", out var futureProp), "futureGlobalSetting must survive save cycle.");
        Assert.AreEqual("dark_mode", futureProp.GetString());

        Assert.IsTrue(root.TryGetProperty("customTelemetryId", out var telemProp), "customTelemetryId must survive save cycle.");
        Assert.AreEqual(98765, telemProp.GetInt32());

        // Also verify the team was indeed added
        Assert.IsTrue(root.TryGetProperty("followedTeams", out var teamsProp));
        Assert.AreEqual(2, teamsProp.GetArrayLength());
    }

    [TestMethod]
    public void Test02E_LineupPlayerDto_GridCoordinates_ParseCorrectly()
    {
        // Standard coordinates
        var (r1, c1) = MatchCenterViewModel.ParseGridCoordinates("1:1", "G", 1);
        Assert.AreEqual(1, r1);
        Assert.AreEqual(1, c1);

        var (r2, c2) = MatchCenterViewModel.ParseGridCoordinates("4:2", "F", 2);
        Assert.AreEqual(4, r2);
        Assert.AreEqual(2, c2);

        var (r3, c3) = MatchCenterViewModel.ParseGridCoordinates("2:4", "D", 4);
        Assert.AreEqual(2, r3);
        Assert.AreEqual(4, c3);

        // Fallbacks when grid is missing/invalid
        var (fgkR, _) = MatchCenterViewModel.ParseGridCoordinates(null, "GK", 1);
        Assert.AreEqual(1, fgkR);

        var (fdefR, _) = MatchCenterViewModel.ParseGridCoordinates("", "DEF", 2);
        Assert.AreEqual(2, fdefR);

        var (fmidR, _) = MatchCenterViewModel.ParseGridCoordinates("invalid", "MID", 3);
        Assert.AreEqual(3, fmidR);

        var (fattR, _) = MatchCenterViewModel.ParseGridCoordinates(":", "ATT", 1);
        Assert.AreEqual(4, fattR);
    }

    [TestMethod]
    public void Test02F_MatchCenterViewModel_BuildsPitchNodes_FacingEachOther()
    {
        var match = new MatchDetailDto
        {
            Id = "ft_test_clasico",
            HomeTeam = new TeamSummaryDto { Id = "team_barca", Name = "FC Barcelona" },
            AwayTeam = new TeamSummaryDto { Id = "team_madrid", Name = "Real Madrid" },
            Lineups = new List<LineupDto>
            {
                new()
                {
                    TeamId = "team_barca",
                    Formation = "4-3-3",
                    StartingXI = new List<LineupPlayerDto>
                    {
                        new() { Name = "M. ter Stegen", Number = 1, Position = "G", Grid = "1:1", IsStarter = true },
                        new() { Name = "J. Kounde", Number = 23, Position = "D", Grid = "2:1", IsStarter = true },
                        new() { Name = "P. Cubarsi", Number = 2, Position = "D", Grid = "2:2", IsStarter = true },
                        new() { Name = "I. Martinez", Number = 5, Position = "D", Grid = "2:3", IsStarter = true },
                        new() { Name = "A. Balde", Number = 3, Position = "D", Grid = "2:4", IsStarter = true },
                        new() { Name = "M. Casado", Number = 17, Position = "M", Grid = "3:1", IsStarter = true },
                        new() { Name = "Pedri", Number = 8, Position = "M", Grid = "3:2", IsStarter = true },
                        new() { Name = "D. Olmo", Number = 20, Position = "M", Grid = "3:3", IsStarter = true },
                        new() { Name = "L. Yamal", Number = 19, Position = "F", Grid = "4:1", IsStarter = true },
                        new() { Name = "R. Lewandowski", Number = 9, Position = "F", Grid = "4:2", IsStarter = true },
                        new() { Name = "Raphinha", Number = 11, Position = "F", Grid = "4:3", IsStarter = true }
                    }
                },
                new()
                {
                    TeamId = "team_madrid",
                    Formation = "4-2-3-1",
                    StartingXI = new List<LineupPlayerDto>
                    {
                        new() { Name = "T. Courtois", Number = 1, Position = "G", Grid = "1:1", IsStarter = true },
                        new() { Name = "D. Carvajal", Number = 2, Position = "D", Grid = "2:1", IsStarter = true },
                        new() { Name = "E. Militao", Number = 3, Position = "D", Grid = "2:2", IsStarter = true },
                        new() { Name = "A. Rudiger", Number = 22, Position = "D", Grid = "2:3", IsStarter = true },
                        new() { Name = "F. Mendy", Number = 23, Position = "D", Grid = "2:4", IsStarter = true },
                        new() { Name = "F. Valverde", Number = 8, Position = "M", Grid = "3:1", IsStarter = true },
                        new() { Name = "A. Tchouameni", Number = 14, Position = "M", Grid = "3:2", IsStarter = true },
                        new() { Name = "Rodrygo", Number = 11, Position = "F", Grid = "4:1", IsStarter = true },
                        new() { Name = "J. Bellingham", Number = 5, Position = "M", Grid = "4:2", IsStarter = true },
                        new() { Name = "Vinicius Jr", Number = 7, Position = "F", Grid = "4:3", IsStarter = true },
                        new() { Name = "K. Mbappe", Number = 9, Position = "F", Grid = "5:1", IsStarter = true }
                    }
                }
            }
        };

        var vm = new MatchCenterViewModel();
        vm.Load(match);

        Assert.IsTrue(vm.HasLineups);
        Assert.AreEqual(22, vm.PitchPlayers.Count, "Pitch must contain exactly 22 starting player nodes.");

        // Away team verification (Top Half: 0.06 -> 0.44)
        var awayNodes = vm.PitchPlayers.Where(p => !p.IsHomeTeam).ToList();
        Assert.AreEqual(11, awayNodes.Count);

        var awayGk = awayNodes.First(p => p.Name == "T. Courtois");
        Assert.IsTrue(awayGk.IsGoalkeeper);
        Assert.AreEqual(0.06, awayGk.YPercent, 0.001, "Away GK must be placed at Y = 0.06");
        Assert.AreEqual(0.50, awayGk.XPercent, 0.001, "Solo GK must be centered horizontally at X = 0.50");
        Assert.AreEqual(10.0, awayGk.CanvasTop, 0.5);
        Assert.AreEqual(135.0, awayGk.CanvasLeft, 0.5);

        var awayStriker = awayNodes.First(p => p.Name == "K. Mbappe");
        Assert.IsFalse(awayStriker.IsGoalkeeper);
        Assert.AreEqual(0.44, awayStriker.YPercent, 0.001, "Away highest row striker must be placed at Y = 0.44");
        Assert.AreEqual(200.0, awayStriker.CanvasTop, 0.5);

        foreach (var node in awayNodes)
        {
            Assert.IsTrue(node.YPercent >= 0.06 && node.YPercent <= 0.44,
                $"Away node {node.Name} Y={node.YPercent} must be in top half [0.06, 0.44]");
        }

        // Home team verification (Bottom Half: 0.94 -> 0.56)
        var homeNodes = vm.PitchPlayers.Where(p => p.IsHomeTeam).ToList();
        Assert.AreEqual(11, homeNodes.Count);

        var homeGk = homeNodes.First(p => p.Name == "M. ter Stegen");
        Assert.IsTrue(homeGk.IsGoalkeeper);
        Assert.AreEqual(0.94, homeGk.YPercent, 0.001, "Home GK must be placed at Y = 0.94");
        Assert.AreEqual(0.50, homeGk.XPercent, 0.001, "Solo GK must be centered horizontally at X = 0.50");
        Assert.AreEqual(450.0, homeGk.CanvasTop, 0.5);
        Assert.AreEqual(135.0, homeGk.CanvasLeft, 0.5);

        var homeStriker = homeNodes.First(p => p.Name == "R. Lewandowski");
        Assert.IsFalse(homeStriker.IsGoalkeeper);
        Assert.AreEqual(0.56, homeStriker.YPercent, 0.001, "Home highest row striker must be placed at Y = 0.56");
        Assert.AreEqual(260.0, homeStriker.CanvasTop, 0.5);

        foreach (var node in homeNodes)
        {
            Assert.IsTrue(node.YPercent >= 0.56 && node.YPercent <= 0.94,
                $"Home node {node.Name} Y={node.YPercent} must be in bottom half [0.56, 0.94]");
        }

        // Name short display
        Assert.AreEqual("Lewandowski", homeStriker.ShortDisplayName);
        Assert.AreEqual("Vinicius Jr", awayNodes.First(p => p.Name == "Vinicius Jr").ShortDisplayName);
        Assert.AreEqual("Pedri", homeNodes.First(p => p.Name == "Pedri").ShortDisplayName);

        // Visibility properties
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, homeGk.GoalkeeperVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Collapsed, homeGk.HomeOutfieldVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, homeStriker.HomeOutfieldVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, awayStriker.AwayOutfieldVisibility);
    }

    [TestMethod]
    public void Test02G_MatchDetailDto_AggregateScoreAndHistoricalLeg_DeserializesAndHydrates()
    {
        const string json = """
        {
          "schemaVersion": "2.0",
          "requestId": "req_agg_test",
          "version": 2,
          "generatedAt": "2026-09-24T00:00:00.000Z",
          "serverTime": "2026-09-24T00:00:00.000Z",
          "stale": false,
          "dataAgeSeconds": 0,
          "data": {
            "id": "ft_fix_ucl_leg2",
            "homeTeam": { "id": "team_1", "name": "Arsenal" },
            "awayTeam": { "id": "team_2", "name": "Bayern Munich" },
            "state": "LIVE",
            "elapsedMinute": 75,
            "score": {
              "home": 2,
              "away": 1,
              "aggregate": { "home": 4, "away": 3 },
              "aggregateDisplay": "4 - 3"
            },
            "aggregateScore": "4 - 3",
            "historicalLeg": "1st leg 2-2"
          }
        }
        """;

        var envelope = JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);
        Assert.IsNotNull(envelope?.Data);
        var match = envelope.Data;

        Assert.AreEqual("4 - 3", match.AggregateScore);
        Assert.AreEqual("1st leg 2-2", match.HistoricalLeg);
        Assert.IsNotNull(match.Score.Aggregate);
        Assert.AreEqual(4, match.Score.Aggregate.Home);
        Assert.AreEqual(3, match.Score.Aggregate.Away);
        Assert.AreEqual("4 - 3", match.Score.AggregateDisplay);

        var vm = new MatchCenterViewModel();
        vm.Load(match);

        Assert.IsTrue(vm.HasAggregateScore);
        Assert.AreEqual("4 - 3", vm.AggregateScore);
        Assert.AreEqual("1st leg 2-2", vm.HistoricalLeg);
        Assert.AreEqual("Agg 4 - 3 (1st leg 2-2)", vm.AggregateText);
    }
}
