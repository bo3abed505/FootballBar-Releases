using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class TrayTickerFormatterTests
{
    [TestMethod]
    public void GetActiveMatch_LiveMatchBeatsPrematchEvenIfOrderedSecond()
    {
        // Scenario from user issue: Spain vs Egypt is at Index 0 (PREMATCH), El Clasico is at Index 1 (LIVE)
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_spain_egypt",
                    HomeTeam = new TeamSummaryDto { Name = "Spain", ShortName = "ESP" },
                    AwayTeam = new TeamSummaryDto { Name = "Egypt", ShortName = "EGY" },
                    State = "PREMATCH",
                    KickoffTime = DateTimeOffset.UtcNow.AddHours(4),
                    Score = new ScoreDto { Home = 0, Away = 0 },
                },
                new()
                {
                    Id = "fix_elclasico",
                    HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                    State = "LIVE",
                    ElapsedMinute = 72,
                    ExtraMinute = 0,
                    Score = new ScoreDto { Home = 1, Away = 1 },
                },
            }
        };

        var activeMatch = TrayTickerFormatter.GetActiveMatch(trayData);
        Assert.IsNotNull(activeMatch);
        Assert.AreEqual("fix_elclasico", activeMatch.Id);
        Assert.AreEqual("BAR", activeMatch.HomeTeam.ShortName);

        var tickerText = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual("⚽ BAR 1–1 RMA 72'", tickerText);
    }

    [TestMethod]
    public void GetActiveMatch_MultipleUpcomingMatches_SelectsNearestKickoff()
    {
        var now = DateTimeOffset.UtcNow;
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_match_later",
                    HomeTeam = new TeamSummaryDto { ShortName = "LIV" },
                    AwayTeam = new TeamSummaryDto { ShortName = "MCI" },
                    State = "PREMATCH",
                    KickoffTime = now.AddHours(5),
                },
                new()
                {
                    Id = "fix_match_sooner",
                    HomeTeam = new TeamSummaryDto { ShortName = "ARS" },
                    AwayTeam = new TeamSummaryDto { ShortName = "CHE" },
                    State = "PREMATCH",
                    KickoffTime = now.AddHours(1),
                },
            }
        };

        var activeMatch = TrayTickerFormatter.GetActiveMatch(trayData);
        Assert.IsNotNull(activeMatch);
        Assert.AreEqual("fix_match_sooner", activeMatch.Id);
    }

    [TestMethod]
    public void GetActiveMatch_FinishedOnlyMatches_FallsBackToFirstAvailable()
    {
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_finished_1",
                    HomeTeam = new TeamSummaryDto { ShortName = "AHL" },
                    AwayTeam = new TeamSummaryDto { ShortName = "ZAM" },
                    State = "FINISHED",
                    Score = new ScoreDto { Home = 2, Away = 1 },
                },
                new()
                {
                    Id = "fix_finished_2",
                    HomeTeam = new TeamSummaryDto { ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { ShortName = "RMA" },
                    State = "FINISHED",
                    Score = new ScoreDto { Home = 3, Away = 0 },
                },
            }
        };

        var activeMatch = TrayTickerFormatter.GetActiveMatch(trayData);
        Assert.IsNotNull(activeMatch);
        Assert.AreEqual("fix_finished_1", activeMatch.Id);
    }

    [TestMethod]
    public void GetActiveMatch_NullOrEmptyMatches_ReturnsNull()
    {
        Assert.IsNull(TrayTickerFormatter.GetActiveMatch(null));
        Assert.IsNull(TrayTickerFormatter.GetActiveMatch(new TrayDataDto { Matches = new() }));
    }

    [TestMethod]
    public void FormatTicker_LiveMatch_FormatsCorrectly()
    {
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_elclasico",
                    HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                    State = "LIVE",
                    ElapsedMinute = 72,
                    ExtraMinute = 0,
                    Score = new ScoreDto { Home = 1, Away = 1 },
                }
            }
        };

        var result = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual("⚽ BAR 1–1 RMA 72'", result);
    }

    [TestMethod]
    public void FormatTicker_LiveMatch_WithExtraMinute_FormatsCorrectly()
    {
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_elclasico",
                    HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                    State = "LIVE",
                    ElapsedMinute = 90,
                    ExtraMinute = 4,
                    Score = new ScoreDto { Home = 2, Away = 1 },
                }
            }
        };

        var result = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual("⚽ BAR 2–1 RMA 90+4'", result);
    }

    [TestMethod]
    public void FormatTicker_Halftime_FormatsCorrectly()
    {
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_elclasico",
                    HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                    State = "HALFTIME",
                    ElapsedMinute = 45,
                    Score = new ScoreDto { Home = 1, Away = 0 },
                }
            }
        };

        var result = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual("⚽ BAR 1–0 RMA HT", result);
    }

    [TestMethod]
    public void FormatTicker_UpcomingMatch_FormatsCorrectly()
    {
        var kickoff = new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero);
        var expectedTime = kickoff.ToLocalTime().ToString("HH:mm");

        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_spain_egypt",
                    HomeTeam = new TeamSummaryDto { Name = "Spain", ShortName = "ESP" },
                    AwayTeam = new TeamSummaryDto { Name = "Egypt", ShortName = "EGY" },
                    State = "PREMATCH",
                    KickoffTime = kickoff,
                    Score = new ScoreDto { Home = 0, Away = 0 },
                }
            }
        };

        var result = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual($"ESP vs EGY · {expectedTime}", result);
    }

    [TestMethod]
    public void FormatTicker_OfflineAndCached_AppendsSuffix()
    {
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "fix_elclasico",
                    HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                    State = "LIVE",
                    ElapsedMinute = 72,
                    Score = new ScoreDto { Home = 1, Away = 1 },
                }
            }
        };

        var offlineResult = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OfflineCached);
        Assert.AreEqual("⚽ BAR 1–1 RMA 72' · offline", offlineResult);

        var staleResult = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineStale);
        Assert.AreEqual("⚽ BAR 1–1 RMA 72' · cached", staleResult);
    }

    [TestMethod]
    public void FormatTicker_NoCacheAndOffline_ShowsOfflineFallback()
    {
        var result = TrayTickerFormatter.FormatTicker(null, TickerConnectionState.NoCacheOffline);
        Assert.AreEqual("FootballTray · offline", result);
    }

    [TestMethod]
    public void FormatTicker_EmptyMatches_ShowsDefaultTitle()
    {
        var trayData = new TrayDataDto { Matches = new List<TrayMatchDto>() };
        var result = TrayTickerFormatter.FormatTicker(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual("FootballTray", result);
    }

    [TestMethod]
    public void TestA_GetTickerCandidates_NoMatches_ReturnsEmptyList()
    {
        Assert.AreEqual(0, TrayTickerFormatter.GetTickerCandidates(null).Count);
        Assert.AreEqual(0, TrayTickerFormatter.GetTickerCandidates(new TrayDataDto { Matches = new() }).Count);
    }

    [TestMethod]
    public void TestB_GetTickerCandidates_NoLiveMatches_ReturnsSingleTopPriorityCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "later",
                    HomeTeam = new TeamSummaryDto { ShortName = "LIV" },
                    AwayTeam = new TeamSummaryDto { ShortName = "MCI" },
                    State = "PREMATCH",
                    KickoffTime = now.AddHours(5),
                },
                new()
                {
                    Id = "sooner",
                    HomeTeam = new TeamSummaryDto { ShortName = "ARS" },
                    AwayTeam = new TeamSummaryDto { ShortName = "CHE" },
                    State = "PREMATCH",
                    KickoffTime = now.AddHours(1),
                },
            }
        };

        var candidates = TrayTickerFormatter.GetTickerCandidates(trayData);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("sooner", candidates[0].Id);
    }

    [TestMethod]
    public void TestC_GetTickerCandidates_OneLiveAndSeveralUpcomingOrFinished_ReturnsOnlyTheLiveMatch()
    {
        var now = DateTimeOffset.UtcNow;
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "upcoming",
                    HomeTeam = new TeamSummaryDto { ShortName = "LIV" },
                    AwayTeam = new TeamSummaryDto { ShortName = "MCI" },
                    State = "PREMATCH",
                    KickoffTime = now.AddHours(1),
                },
                new()
                {
                    Id = "live1",
                    HomeTeam = new TeamSummaryDto { ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { ShortName = "RMA" },
                    State = "LIVE",
                    ElapsedMinute = 55,
                    Score = new ScoreDto { Home = 1, Away = 0 },
                },
                new()
                {
                    Id = "finished",
                    HomeTeam = new TeamSummaryDto { ShortName = "ARS" },
                    AwayTeam = new TeamSummaryDto { ShortName = "CHE" },
                    State = "FINISHED",
                    Score = new ScoreDto { Home = 2, Away = 2 },
                },
            }
        };

        var candidates = TrayTickerFormatter.GetTickerCandidates(trayData);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("live1", candidates[0].Id);
    }

    [TestMethod]
    public void TestD_GetTickerCandidates_MultipleLiveAndUpcomingOrFinished_ReturnsAllAndOnlyLiveMatches()
    {
        var now = DateTimeOffset.UtcNow;
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "live1",
                    HomeTeam = new TeamSummaryDto { ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { ShortName = "RMA" },
                    State = "LIVE",
                    ElapsedMinute = 40,
                    Score = new ScoreDto { Home = 0, Away = 0 },
                },
                new()
                {
                    Id = "upcoming",
                    HomeTeam = new TeamSummaryDto { ShortName = "LIV" },
                    AwayTeam = new TeamSummaryDto { ShortName = "MCI" },
                    State = "PREMATCH",
                    KickoffTime = now.AddHours(2),
                },
                new()
                {
                    Id = "live2",
                    HomeTeam = new TeamSummaryDto { ShortName = "BAY" },
                    AwayTeam = new TeamSummaryDto { ShortName = "DOR" },
                    State = "LIVE",
                    ElapsedMinute = 75,
                    Score = new ScoreDto { Home = 3, Away = 1 },
                },
                new()
                {
                    Id = "finished",
                    HomeTeam = new TeamSummaryDto { ShortName = "JUV" },
                    AwayTeam = new TeamSummaryDto { ShortName = "MIL" },
                    State = "FINISHED",
                    Score = new ScoreDto { Home = 1, Away = 0 },
                },
            }
        };

        var candidates = TrayTickerFormatter.GetTickerCandidates(trayData);
        Assert.AreEqual(2, candidates.Count);
        Assert.IsTrue(candidates.Any(m => m.Id == "live1"));
        Assert.IsTrue(candidates.Any(m => m.Id == "live2"));
        Assert.IsFalse(candidates.Any(m => m.Id == "upcoming"));
        Assert.IsFalse(candidates.Any(m => m.Id == "finished"));
    }

    [TestMethod]
    public void TestE_GetTickerCandidates_MultipleLiveMatches_PreservesFixtureRelevanceEnginePriorityOrder()
    {
        // Neutral matches: FixtureRelevanceEngine prioritizes by elapsed minute descending
        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "live_earlier_min",
                    HomeTeam = new TeamSummaryDto { ShortName = "A" },
                    AwayTeam = new TeamSummaryDto { ShortName = "B" },
                    State = "LIVE",
                    ElapsedMinute = 25,
                    Score = new ScoreDto { Home = 0, Away = 0 },
                },
                new()
                {
                    Id = "live_later_min",
                    HomeTeam = new TeamSummaryDto { ShortName = "C" },
                    AwayTeam = new TeamSummaryDto { ShortName = "D" },
                    State = "LIVE",
                    ElapsedMinute = 80,
                    Score = new ScoreDto { Home = 1, Away = 1 },
                },
            }
        };

        var candidates = TrayTickerFormatter.GetTickerCandidates(trayData);
        Assert.AreEqual(2, candidates.Count);
        Assert.AreEqual("live_later_min", candidates[0].Id);
        Assert.AreEqual("live_earlier_min", candidates[1].Id);
    }

    [TestMethod]
    public void TestF_GetTickerCandidates_FollowedTeamLive_OutranksFollowedCompetitionAndGenericLive()
    {
        var prefs = new UserPreferences
        {
            FollowedTeams = new List<FollowedTeam>
            {
                new() { Id = "team_ars", Name = "Arsenal" }
            },
            FollowedCompetitions = new List<FollowedCompetition>
            {
                new() { Id = "comp_pl", Name = "Premier League" }
            }
        };

        var genericLive = new TrayMatchDto
        {
            Id = "m_generic",
            HomeTeam = new TeamSummaryDto { Id = "team_gen1", ShortName = "GEN1" },
            AwayTeam = new TeamSummaryDto { Id = "team_gen2", ShortName = "GEN2" },
            Competition = new CompetitionSummaryDto { Id = "comp_other", Name = "Other" },
            State = "LIVE",
            ElapsedMinute = 88,
            Score = new ScoreDto { Home = 1, Away = 0 },
        };

        var followedCompLive = new TrayMatchDto
        {
            Id = "m_followed_comp",
            HomeTeam = new TeamSummaryDto { Id = "team_che", ShortName = "CHE" },
            AwayTeam = new TeamSummaryDto { Id = "team_tot", ShortName = "TOT" },
            Competition = new CompetitionSummaryDto { Id = "comp_pl", Name = "Premier League" },
            State = "LIVE",
            ElapsedMinute = 50,
            Score = new ScoreDto { Home = 0, Away = 0 },
        };

        var followedTeamLive = new TrayMatchDto
        {
            Id = "m_followed_team",
            HomeTeam = new TeamSummaryDto { Id = "team_ars", ShortName = "ARS" },
            AwayTeam = new TeamSummaryDto { Id = "team_wol", ShortName = "WOL" },
            Competition = new CompetitionSummaryDto { Id = "comp_pl", Name = "Premier League" },
            State = "LIVE",
            ElapsedMinute = 15,
            Score = new ScoreDto { Home = 1, Away = 0 },
        };

        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto> { genericLive, followedCompLive, followedTeamLive }
        };

        var candidates = TrayTickerFormatter.GetTickerCandidates(trayData, prefs);
        Assert.AreEqual(3, candidates.Count);
        Assert.AreEqual("m_followed_team", candidates[0].Id);
        Assert.AreEqual("m_followed_comp", candidates[1].Id);
        Assert.AreEqual("m_generic", candidates[2].Id);
    }

    [TestMethod]
    public void TestG_FormatMatchTicker_OfflineAndFreshness_ConsistentWithFormatTicker()
    {
        var match = new TrayMatchDto
        {
            Id = "match_1",
            HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
            AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
            State = "LIVE",
            ElapsedMinute = 72,
            Score = new ScoreDto { Home = 1, Away = 1 },
        };
        var trayData = new TrayDataDto { Matches = new List<TrayMatchDto> { match } };

        // Test across all connection states
        foreach (var connState in Enum.GetValues<TickerConnectionState>())
        {
            var matchResult = TrayTickerFormatter.FormatMatchTicker(match, connState);
            var tickerResult = TrayTickerFormatter.FormatTicker(trayData, connState);
            Assert.AreEqual(tickerResult, matchResult, $"State {connState} must match FormatTicker output.");
        }

        // Test with DataFreshnessState mapping
        foreach (var freshness in Enum.GetValues<DataFreshnessState>())
        {
            var matchResult = TrayTickerFormatter.FormatMatchTicker(match, freshness);
            var tickerResult = TrayTickerFormatter.FormatTicker(trayData, freshness);
            Assert.AreEqual(tickerResult, matchResult, $"Freshness {freshness} must match FormatTicker output.");
        }

        // Test null match
        Assert.AreEqual("FootballTray · offline", TrayTickerFormatter.FormatMatchTicker(null, TickerConnectionState.OfflineCached));
        Assert.AreEqual("FootballTray · offline", TrayTickerFormatter.FormatMatchTicker(null, TickerConnectionState.NoCacheOffline));
        Assert.AreEqual("FootballTray", TrayTickerFormatter.FormatMatchTicker(null, TickerConnectionState.OnlineFresh));
    }
}
