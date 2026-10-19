using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

[TestClass]
public class Phase10MatchCenterIntegrationTests
{
    [TestMethod]
    public void Test01_TeamStatisticsDto_Deserializes_ExpectedGoals_And_Xg()
    {
        string json = """
        {
            "shotsOnGoal": 5,
            "totalShots": 12,
            "possessionPercent": 55,
            "expectedGoals": 1.76,
            "xg": 1.76
        }
        """;

        var dto = JsonSerializer.Deserialize<TeamStatisticsDto>(json);
        Assert.IsNotNull(dto);
        Assert.AreEqual(1.76, dto.ExpectedGoals);
        Assert.AreEqual(1.76, dto.Xg);
        Assert.AreEqual(1.76, dto.EffectiveXg);

        // Verify fallback when only expectedGoals is set
        var dto1 = new TeamStatisticsDto { ExpectedGoals = 2.40, Xg = null };
        Assert.AreEqual(2.40, dto1.EffectiveXg);

        // Verify fallback when only xg is set
        var dto2 = new TeamStatisticsDto { ExpectedGoals = null, Xg = 0.85 };
        Assert.AreEqual(0.85, dto2.EffectiveXg);
    }

    [TestMethod]
    public void Test02_MatchCenterViewModel_PopulateStatistics_Maps_ExpectedGoals_xG()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "fix_test_xg",
            HomeTeam = new TeamSummaryDto { Id = "team_home", Name = "Arsenal" },
            AwayTeam = new TeamSummaryDto { Id = "team_away", Name = "Chelsea" },
            Statistics = new List<TeamStatisticsDto>
            {
                new()
                {
                    TeamId = "team_home",
                    ExpectedGoals = 1.85,
                    PossessionPercent = 55,
                    TotalShots = 14,
                    ShotsOnGoal = 6
                },
                new()
                {
                    TeamId = "team_away",
                    ExpectedGoals = 0.92,
                    PossessionPercent = 45,
                    TotalShots = 8,
                    ShotsOnGoal = 2
                }
            }
        };

        vm.Load(match);

        Assert.IsTrue(vm.HasStatistics);
        var xgStat = vm.MatchStats.FirstOrDefault(s => s.Name == "Expected Goals (xG)");
        Assert.IsNotNull(xgStat, "Expected Goals (xG) comparison must be populated in MatchStats!");
        Assert.AreEqual("1.85", xgStat.HomeValue);
        Assert.AreEqual("0.92", xgStat.AwayValue);
        Assert.IsTrue(xgStat.HomeRatio > xgStat.AwayRatio);
    }

    [TestMethod]
    public void Test03_SmartMergeStats_Reconciles_InPlace_Preserving_Instances()
    {
        var collection = new ObservableCollection<StatComparisonViewModel>();
        var stat1 = new StatComparisonViewModel
        {
            Name = "Ball Possession",
            HomeValue = "50%",
            AwayValue = "50%",
            HomeRatio = 50.0,
            AwayRatio = 50.0
        };
        var stat2 = new StatComparisonViewModel
        {
            Name = "Total Shots",
            HomeValue = "5",
            AwayValue = "3",
            HomeRatio = 62.5,
            AwayRatio = 37.5
        };
        collection.Add(stat1);
        collection.Add(stat2);

        var propertyChangedFired = false;
        stat1.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(StatComparisonViewModel.HomeValue))
            {
                propertyChangedFired = true;
            }
        };

        var incoming = new List<StatComparisonViewModel>
        {
            new()
            {
                Name = "Ball Possession",
                HomeValue = "60%",
                AwayValue = "40%",
                HomeRatio = 60.0,
                AwayRatio = 40.0
            },
            new()
            {
                Name = "Total Shots",
                HomeValue = "8",
                AwayValue = "4",
                HomeRatio = 66.7,
                AwayRatio = 33.3
            }
        };

        MatchCenterViewModel.SmartMergeStats(collection, incoming);

        Assert.AreEqual(2, collection.Count);
        // The original instance MUST be preserved (No .Clear() called)
        Assert.AreSame(stat1, collection[0]);
        Assert.AreSame(stat2, collection[1]);
        Assert.AreEqual("60%", collection[0].HomeValue);
        Assert.AreEqual("40%", collection[0].AwayValue);
        Assert.IsTrue(propertyChangedFired, "INotifyPropertyChanged must fire on in-place stat update!");
    }

    [TestMethod]
    public void Test04_SmartMergeLineupPlayers_Matches_ByName_First_Preserving_Instances()
    {
        var collection = new ObservableCollection<LineupPlayerItemViewModel>();
        var player1 = new LineupPlayerItemViewModel
        {
            Name = "Bukayo Saka",
            Number = "7",
            Position = "Forward",
            PositionBadge = "ATT"
        };
        collection.Add(player1);

        var numberChangedFired = false;
        player1.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(LineupPlayerItemViewModel.Number))
            {
                numberChangedFired = true;
            }
        };

        // Incoming update where jersey number changed from 7 to 10 for same player Name
        var incoming = new List<LineupPlayerItemViewModel>
        {
            new()
            {
                Name = "Bukayo Saka",
                Number = "10",
                Position = "Forward",
                PositionBadge = "ATT"
            }
        };

        MatchCenterViewModel.SmartMergeLineupPlayers(collection, incoming);

        Assert.AreEqual(1, collection.Count);
        // Instance MUST be preserved by matching Name first
        Assert.AreSame(player1, collection[0], "SmartMergeLineupPlayers must match by Name first without dropping item on number update!");
        Assert.AreEqual("10", collection[0].Number);
        Assert.IsTrue(numberChangedFired, "INotifyPropertyChanged must fire when player jersey number updates in-place!");
    }

    [TestMethod]
    public void Test05_SmartMergePitchPlayers_Reconciles_InPlace()
    {
        var collection = new ObservableCollection<PitchPlayerNodeViewModel>();
        var pitchPlayer = new PitchPlayerNodeViewModel
        {
            Name = "David Raya",
            ShortDisplayName = "Raya",
            Number = "22",
            IsHomeTeam = true,
            Row = 1,
            Col = 2,
            XPercent = 0.5,
            YPercent = 0.94
        };
        collection.Add(pitchPlayer);

        var incoming = new List<PitchPlayerNodeViewModel>
        {
            new()
            {
                Name = "David Raya",
                ShortDisplayName = "Raya",
                Number = "1", // Jersey number updated to 1
                IsHomeTeam = true,
                Row = 1,
                Col = 2,
                XPercent = 0.5,
                YPercent = 0.92
            }
        };

        MatchCenterViewModel.SmartMergePitchPlayers(collection, incoming);

        Assert.AreEqual(1, collection.Count);
        Assert.AreSame(pitchPlayer, collection[0]);
        Assert.AreEqual("1", collection[0].Number);
        Assert.AreEqual(0.92, collection[0].YPercent);
    }

    [TestMethod]
    public async Task Test06_MatchCenterViewModel_RefreshAsync_ETag_304_ShortCircuit()
    {
        var matchDetail = new MatchDetailDto
        {
            Id = "fix_etag_test_10",
            State = "LIVE",
            ElapsedMinute = 55,
            Score = new ScoreDto { Home = 2, Away = 1 },
            HomeTeam = new TeamSummaryDto { Id = "team_h", Name = "Home" },
            AwayTeam = new TeamSummaryDto { Id = "team_a", Name = "Away" }
        };

        var envelope = new ApiResponseEnvelope<MatchDetailDto>
        {
            SchemaVersion = "2.0",
            Version = 2,
            Data = matchDetail
        };

        string etag = "W/\"match-fix_etag_test_10-12345-0-abc12345\"";
        int requestCount = 0;
        string? receivedIfNoneMatch = null;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                requestCount++;
                if (req.Headers.Contains("If-None-Match"))
                {
                    receivedIfNoneMatch = req.Headers.GetValues("If-None-Match").FirstOrDefault();
                    var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
                    notMod.Headers.TryAddWithoutValidation("ETag", etag);
                    return notMod;
                }

                var json = JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                resp.Headers.TryAddWithoutValidation("ETag", etag);
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:5000") };
        using var client = new FootballApiClient(httpClient);

        var vm = new MatchCenterViewModel();
        vm.Load(matchDetail);

        // 1. Initial refresh with null LastEtag -> Server returns 200 OK + ETag
        bool refreshed1 = await vm.RefreshAsync(client);
        Assert.IsTrue(refreshed1);
        Assert.AreEqual(etag, vm.LastEtag);
        Assert.AreEqual(1, requestCount);
        Assert.IsNull(receivedIfNoneMatch);

        // 2. Second refresh -> Client passes LastEtag in If-None-Match header -> Server returns 304
        bool refreshed2 = await vm.RefreshAsync(client);
        Assert.IsTrue(refreshed2);
        Assert.AreEqual(etag, vm.LastEtag);
        Assert.AreEqual(2, requestCount);
        Assert.AreEqual(etag, receivedIfNoneMatch);
    }

    [TestMethod]
    public void Test07_MatchCenterViewModel_Load_Resets_LastEtag_When_MatchId_Changes()
    {
        var vm = new MatchCenterViewModel();
        vm.Load(new MatchDetailDto { Id = "match_alpha" });
        vm.LastEtag = "W/\"match-alpha-etag\"";

        // Loading the same match retains the ETag
        vm.Load(new MatchDetailDto { Id = "match_alpha" });
        Assert.AreEqual("W/\"match-alpha-etag\"", vm.LastEtag);

        // Loading a DIFFERENT match ID resets LastEtag to null
        vm.Load(new MatchDetailDto { Id = "match_beta" });
        Assert.IsNull(vm.LastEtag, "Switching matches must reset LastEtag to prevent sending wrong ETag header!");
    }
}
