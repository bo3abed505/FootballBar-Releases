using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
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
}
