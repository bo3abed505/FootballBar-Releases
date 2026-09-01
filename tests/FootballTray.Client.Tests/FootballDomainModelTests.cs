using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Core.Models;
using FootballTray.Core.Providers.Mock;

namespace FootballTray.Client.Tests;

[TestClass]
public class FootballDomainModelTests
{
    [TestMethod]
    public void Score_WithGoal_IncrementsCorrectTeamScore()
    {
        var initial = Score.Empty;
        Assert.AreEqual(0, initial.Home);
        Assert.AreEqual(0, initial.Away);
        Assert.AreEqual("0 - 0", initial.DisplayScore);

        var homeScored = initial.WithGoal(isHome: true);
        Assert.AreEqual(1, homeScored.Home);
        Assert.AreEqual(0, homeScored.Away);
        Assert.AreEqual("1 - 0", homeScored.DisplayScore);

        var awayScored = homeScored.WithGoal(isHome: false);
        Assert.AreEqual(1, awayScored.Home);
        Assert.AreEqual(1, awayScored.Away);
        Assert.AreEqual("1 - 1", awayScored.DisplayScore);
    }

    [TestMethod]
    public void Score_WithHalfTimeScore_PreservesHalfTimeBreakdown()
    {
        var score = new Score(2, 1).WithHalfTimeScore(1, 0);
        Assert.AreEqual(2, score.Home);
        Assert.AreEqual(1, score.Away);
        Assert.AreEqual(1, score.HomeHalfTime);
        Assert.AreEqual(0, score.AwayHalfTime);
    }

    [TestMethod]
    public void StandingRow_GoalDifference_CalculatesCorrectly()
    {
        var row = new StandingRow(
            Position: 1,
            Team: MockMatchSeeder.Barcelona,
            Played: 10,
            Won: 8,
            Drawn: 1,
            Lost: 1,
            GoalsFor: 25,
            GoalsAgainst: 8,
            Points: 25);

        Assert.AreEqual(17, row.GoalDifference);
    }

    [TestMethod]
    public void Fixture_MatchTimeDisplay_FormatsAccuratelyForStates()
    {
        var fixture = MockMatchSeeder.CreateElClasicoFixture("fix_test");

        var preMatch = fixture.WithState(MatchState.PreMatch, 0);
        Assert.AreEqual(fixture.KickoffTime.ToLocalTime().ToString("HH:mm"), preMatch.MatchTimeDisplay);

        var live17 = fixture.WithState(MatchState.Live, 17);
        Assert.AreEqual("17'", live17.MatchTimeDisplay);

        var liveExtra = fixture.WithState(MatchState.Live, 90, 4);
        Assert.AreEqual("90+4'", liveExtra.MatchTimeDisplay);

        var ht = fixture.WithState(MatchState.HalfTime, 45);
        Assert.AreEqual("HT", ht.MatchTimeDisplay);

        var ft = fixture.WithState(MatchState.Finished, 90);
        Assert.AreEqual("FT", ft.MatchTimeDisplay);
    }
}
