using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Taskbar;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class Strike5PolishAndPeekViewTests
{
    [TestMethod]
    public void EventMapping_Substitution_FormattedAsInAndOut()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "fix_egypt_angola",
            HomeTeam = new TeamSummaryDto { Id = "egypt", Name = "Egypt" },
            AwayTeam = new TeamSummaryDto { Id = "angola", Name = "Angola" },
            Events = new List<TrayEventDto>
            {
                new()
                {
                    Id = "sub_1",
                    Type = "substitution",
                    Minute = 65,
                    TeamId = "egypt",
                    PlayerName = "Zizo Sayed",
                    AssistPlayerName = "Marwan Attia",
                    Description = "Substitution: Zizo Sayed in for Marwan Attia"
                }
            },
            Lineups = new List<LineupDto>
            {
                new()
                {
                    TeamId = "egypt",
                    StartingXI = new List<LineupPlayerDto>
                    {
                        new() { Name = "Marwan Attia", Number = 8, Position = "MID" }
                    },
                    Substitutes = new List<LineupPlayerDto>
                    {
                        new() { Name = "Zizo Sayed", Number = 10, Position = "MID" }
                    }
                }
            }
        };

        vm.Load(match);

        Assert.AreEqual(1, vm.TimelineItems.Count);
        var subItem = vm.TimelineItems[0];
        Assert.AreEqual("🔄", subItem.IconGlyph);
        Assert.AreEqual("Out: #10 (MID) Zizo Sayed", subItem.EventTitle);
        Assert.AreEqual("In: #8 (MID) Marwan Attia", subItem.EventSubtitle);
        Assert.IsTrue(subItem.IsHomeTeam);
        Assert.IsFalse(subItem.IsAwayTeam);
    }

    [TestMethod]
    public void EventMapping_LineupCrossReference_FallsBackGracefullyWithoutLineup()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "fix_egypt_angola",
            HomeTeam = new TeamSummaryDto { Id = "egypt", Name = "Egypt" },
            AwayTeam = new TeamSummaryDto { Id = "angola", Name = "Angola" },
            Events = new List<TrayEventDto>
            {
                new()
                {
                    Id = "sub_1",
                    Type = "substitution",
                    Minute = 65,
                    TeamId = "egypt",
                    PlayerName = "Zizo Sayed",
                    AssistPlayerName = "Marwan Attia"
                }
            },
            Lineups = null! // No lineup data
        };

        vm.Load(match);

        Assert.AreEqual(1, vm.TimelineItems.Count);
        var subItem = vm.TimelineItems[0];
        Assert.AreEqual("Out: Zizo Sayed", subItem.EventTitle);
        Assert.AreEqual("In: Marwan Attia", subItem.EventSubtitle);
    }

    [TestMethod]
    public void EventMapping_MissedPenalty_UsesRedXAndExcludedFromGoals()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "fix_egypt_angola",
            HomeTeam = new TeamSummaryDto { Id = "egypt", Name = "Egypt" },
            AwayTeam = new TeamSummaryDto { Id = "angola", Name = "Angola" },
            Events = new List<TrayEventDto>
            {
                new()
                {
                    Id = "ev_scored_goal",
                    Type = "goal",
                    Minute = 15,
                    TeamId = "egypt",
                    PlayerName = "Salah"
                },
                new()
                {
                    Id = "ev_missed_pen",
                    Type = "penalty",
                    Detail = "Missed Penalty",
                    Minute = 40,
                    TeamId = "egypt",
                    PlayerName = "Trezeguet",
                    Description = "Penalty missed"
                }
            }
        };

        vm.Load(match);

        // Timeline items (Reverse chronological: minute 40 at [0], minute 15 at [1])
        Assert.AreEqual(2, vm.TimelineItems.Count);
        var missedPenItem = vm.TimelineItems[0];
        var goalItem = vm.TimelineItems[1];

        Assert.AreEqual("❌", missedPenItem.IconGlyph);
        Assert.AreEqual("Missed Penalty", missedPenItem.EventSubtitle);
        Assert.AreEqual("⚽", goalItem.IconGlyph);

        // Header incidents
        Assert.AreEqual(2, vm.HomeIncidents.Count);
        var goalIncident = vm.HomeIncidents.FirstOrDefault(i => i.Icon == "⚽");
        var missedPenIncident = vm.HomeIncidents.FirstOrDefault(i => i.Icon == "❌");

        Assert.IsNotNull(goalIncident);
        Assert.IsTrue(goalIncident.Text.Contains("Salah"));
        Assert.IsNotNull(missedPenIncident);
        Assert.IsTrue(missedPenIncident.Text.Contains("Trezeguet"));
    }

    [TestMethod]
    public void MatchCenter_FollowAndPinProperties_InitializedAndToggled()
    {
        var vm = new MatchCenterViewModel();
        Assert.AreEqual("\uE734", vm.HomeStarGlyph);
        Assert.AreEqual("\uE734", vm.AwayStarGlyph);
        Assert.AreEqual("\uE734", vm.CompetitionStarGlyph);
        Assert.AreEqual("\uE718", vm.PinGlyph);

        vm.IsHomeTeamFollowed = true;
        Assert.AreEqual("\uE735", vm.HomeStarGlyph);
        Assert.AreEqual(1.0, vm.HomeStarOpacity);

        vm.IsPinned = true;
        Assert.AreEqual("\uE840", vm.PinGlyph);
        Assert.AreEqual(1.0, vm.PinGlyphOpacity);

        Assert.IsNotNull(vm.ToggleHomeStarCommand);
        Assert.IsNotNull(vm.ToggleAwayStarCommand);
        Assert.IsNotNull(vm.ToggleCompetitionStarCommand);
        Assert.IsNotNull(vm.TogglePinCommand);
    }

    [TestMethod]
    public void PeekView_TeamDividedIncidents_ExcludeMissedPenalties()
    {
        var match = new TrayMatchDto
        {
            Id = "fix_egypt_angola",
            State = "LIVE",
            Score = new ScoreDto { Home = 2, Away = 1 },
            HomeTeam = new TeamSummaryDto { Id = "egypt", Name = "Egypt" },
            AwayTeam = new TeamSummaryDto { Id = "angola", Name = "Angola" },
            RecentEvents = new List<TrayEventDto>
            {
                new()
                {
                    Type = "goal",
                    Minute = 14,
                    TeamId = "egypt",
                    PlayerName = "Messi"
                },
                new()
                {
                    Type = "goal",
                    Minute = 89,
                    TeamId = "egypt",
                    PlayerName = "Messi"
                },
                new()
                {
                    Type = "penalty",
                    Detail = "Missed Penalty",
                    Minute = 30,
                    TeamId = "egypt",
                    PlayerName = "Salah"
                },
                new()
                {
                    Type = "goal",
                    Minute = 80,
                    TeamId = "angola",
                    PlayerName = "Ronaldo"
                }
            }
        };

        var peekData = PeekViewFormatter.Format(match);

        // Home has 2 goals by Messi, Salah's missed penalty is EXCLUDED
        Assert.IsTrue(peekData.HomeEventsText.Contains("Messi 14', 89'"));
        Assert.IsFalse(peekData.HomeEventsText.Contains("Salah"));

        // Away has 1 goal by Ronaldo
        Assert.IsTrue(peekData.AwayEventsText.Contains("Ronaldo 80'"));

        // Formatted String ToolTip matches strict format
        // [Home Team Name]\nMessi 14', 89'\n\n[Away Team Name]\nRonaldo 80'
        string expectedTip = "[Egypt]\nMessi 14', 89'\n\n[Angola]\nRonaldo 80'";
        Assert.AreEqual(expectedTip, peekData.FormattedToolTipText);
    }

    [TestMethod]
    public void PeekView_RedCardIndicators_AppendsRedSquareToTeamNameInScoreText()
    {
        var match = new TrayMatchDto
        {
            Id = "fix_egypt_angola",
            State = "LIVE",
            Score = new ScoreDto { Home = 1, Away = 0 },
            HomeTeam = new TeamSummaryDto { Id = "egypt", Name = "Egypt" },
            AwayTeam = new TeamSummaryDto { Id = "angola", Name = "Angola" },
            RecentEvents = new List<TrayEventDto>
            {
                new()
                {
                    Type = "red_card",
                    Minute = 35,
                    TeamId = "egypt",
                    PlayerName = "Hegazi"
                }
            }
        };

        var peekData = PeekViewFormatter.Format(match);

        Assert.AreEqual("Egypt 🟥", peekData.HomeScoreDisplay);
        Assert.AreEqual("Angola", peekData.AwayScoreDisplay);
        Assert.AreEqual("1 - 0", peekData.ScoreDisplay);
    }

    [TestMethod]
    public void Timeline_Events_SortedInReverseChronologicalOrder()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "fix_egypt_angola",
            HomeTeam = new TeamSummaryDto { Id = "egypt", Name = "Egypt" },
            AwayTeam = new TeamSummaryDto { Id = "angola", Name = "Angola" },
            Events = new List<TrayEventDto>
            {
                new() { Id = "ev_early", Type = "goal", Minute = 12, TeamId = "egypt", PlayerName = "Salah" },
                new() { Id = "ev_mid", Type = "card", Detail = "Yellow Card", Minute = 55, TeamId = "angola", PlayerName = "Fredy" },
                new() { Id = "ev_late", Type = "substitution", Minute = 90, ExtraMinute = 2, TeamId = "egypt", PlayerName = "Zizo", AssistPlayerName = "Trezeguet" }
            }
        };

        vm.Load(match);

        Assert.AreEqual(3, vm.TimelineItems.Count);
        Assert.AreEqual("90+2'", vm.TimelineItems[0].MinuteDisplay);
        Assert.AreEqual("55'", vm.TimelineItems[1].MinuteDisplay);
        Assert.AreEqual("12'", vm.TimelineItems[2].MinuteDisplay);
    }
}
