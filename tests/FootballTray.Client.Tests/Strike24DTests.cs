using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

[TestClass]
public class Strike24DTests
{
    [TestMethod]
    public void Strike24D6_WinnerCalculation_HomeWinner_IdentifiedCorrectly()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_home_win",
            State = "FT",
            HomeTeam = new TeamSummaryDto { Name = "Real Madrid" },
            AwayTeam = new TeamSummaryDto { Name = "Barcelona" },
            Score = new ScoreDto { Home = 3, Away = 1 }
        };

        vm.Load(match);

        Assert.IsTrue(vm.IsHomeWinner, "Home team should be marked as winner when finished and home score > away score.");
        Assert.IsFalse(vm.IsAwayWinner, "Away team should not be marked as winner when away score < home score.");
    }

    [TestMethod]
    public void Strike24D6_WinnerCalculation_AwayWinner_IdentifiedCorrectly()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_away_win",
            State = "FT",
            HomeTeam = new TeamSummaryDto { Name = "Liverpool" },
            AwayTeam = new TeamSummaryDto { Name = "Arsenal" },
            Score = new ScoreDto { Home = 0, Away = 2 }
        };

        vm.Load(match);

        Assert.IsFalse(vm.IsHomeWinner, "Home team should not be winner when home score < away score.");
        Assert.IsTrue(vm.IsAwayWinner, "Away team should be marked as winner when away score > home score.");
    }

    [TestMethod]
    public void Strike24D6_WinnerCalculation_DrawnMatch_NeitherIsWinner()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_draw",
            State = "FT",
            HomeTeam = new TeamSummaryDto { Name = "Inter" },
            AwayTeam = new TeamSummaryDto { Name = "Milan" },
            Score = new ScoreDto { Home = 2, Away = 2 }
        };

        vm.Load(match);

        Assert.IsFalse(vm.IsHomeWinner, "Home team should not be winner in a drawn match.");
        Assert.IsFalse(vm.IsAwayWinner, "Away team should not be winner in a drawn match.");
    }

    [TestMethod]
    public void Strike24D6_WinnerCalculation_LiveMatch_NeitherIsWinnerYet()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_live",
            State = "LIVE",
            ElapsedMinute = 75,
            HomeTeam = new TeamSummaryDto { Name = "Bayern" },
            AwayTeam = new TeamSummaryDto { Name = "Dortmund" },
            Score = new ScoreDto { Home = 4, Away = 1 }
        };

        vm.Load(match);

        Assert.IsFalse(vm.IsHomeWinner, "Home team should not be marked as final winner while match is still LIVE.");
        Assert.IsFalse(vm.IsAwayWinner, "Away team should not be marked as final winner while match is still LIVE.");
    }

    [TestMethod]
    public void Strike24D6_WinnerCalculation_PenaltyShootout_DeterminesWinner()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_penalties",
            State = "PEN",
            HomeTeam = new TeamSummaryDto { Name = "Argentina" },
            AwayTeam = new TeamSummaryDto { Name = "France" },
            Score = new ScoreDto
            {
                Home = 3,
                Away = 3,
                Penalty = new ScorePeriodDto { Home = 4, Away = 2 }
            }
        };

        vm.Load(match);

        Assert.IsTrue(vm.IsHomeWinner, "Argentina won 4-2 on penalties so Home must be winner.");
        Assert.IsFalse(vm.IsAwayWinner, "France lost penalties so Away must not be winner.");
    }

    [TestMethod]
    public void Strike24D6_UnifiedTimeline_SynthesizesAllWhistleMarkers_InReverseChronologicalOrder()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_full_journey",
            State = "PEN",
            ElapsedMinute = 120,
            HomeTeam = new TeamSummaryDto { Id = "team_h", Name = "Home FC" },
            AwayTeam = new TeamSummaryDto { Id = "team_a", Name = "Away FC" },
            Score = new ScoreDto
            {
                Home = 2,
                Away = 2,
                HalfTime = new ScorePeriodDto { Home = 1, Away = 0 },
                ExtraTime = new ScorePeriodDto { Home = 2, Away = 2 },
                Penalty = new ScorePeriodDto { Home = 5, Away = 4 }
            },
            Events = new List<TrayEventDto>
            {
                new() { Id = "ev_1", Minute = 23, Type = "goal", TeamId = "team_h", PlayerName = "Striker 1", Description = "Goal" },
                new() { Id = "ev_2", Minute = 68, Type = "goal", TeamId = "team_a", PlayerName = "Striker 2", Description = "Goal" }
            }
        };

        vm.Load(match);

        // Verify presence of all key transition whistle markers
        var kickoff = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Kickoff");
        Assert.IsNotNull(kickoff, "0' Kickoff marker must be present.");
        Assert.AreEqual("0'", kickoff.MinuteDisplay);

        var ht = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Half-Time");
        Assert.IsNotNull(ht, "HT marker must be present.");
        Assert.AreEqual("HT", ht.MinuteDisplay);

        var start2H = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Start 2nd Half");
        Assert.IsNotNull(start2H, "Start 2nd Half marker must be present.");
        Assert.AreEqual("45'", start2H.MinuteDisplay);

        var end90 = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "End of 90 Min");
        Assert.IsNotNull(end90, "End of 90 Min marker must be present for extra time game.");
        Assert.AreEqual("90'", end90.MinuteDisplay);

        var etStart = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Extra Time Kickoff");
        Assert.IsNotNull(etStart, "Extra Time Kickoff marker must be present.");
        Assert.AreEqual("90'", etStart.MinuteDisplay);

        var etHt = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Extra Time Half-Time");
        Assert.IsNotNull(etHt, "Extra Time Half-Time marker must be present.");
        Assert.AreEqual("105'", etHt.MinuteDisplay);

        var et2h = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Start Extra Time 2nd Half");
        Assert.IsNotNull(et2h, "Start Extra Time 2nd Half marker must be present.");
        Assert.AreEqual("105'", et2h.MinuteDisplay);

        var penStart = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Penalty Shootout");
        Assert.IsNotNull(penStart, "Penalty Shootout marker must be present.");
        Assert.AreEqual("PEN", penStart.MinuteDisplay);

        var ftPen = vm.TimelineItems.FirstOrDefault(i => i.EventTitle == "Full-Time (PEN)");
        Assert.IsNotNull(ftPen, "Full-Time (PEN) final whistle must be present.");
        Assert.AreEqual("PEN", ftPen.MinuteDisplay);

        // Reverse-chronological order: Top event must be final whistle, bottom event must be Kickoff
        Assert.AreEqual("Full-Time (PEN)", vm.TimelineItems.First().EventTitle, "Top item must be final whistle.");
        Assert.AreEqual("Kickoff", vm.TimelineItems.Last().EventTitle, "Bottom item must be Kickoff.");
    }
}
