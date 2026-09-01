using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Core.Models;
using FootballTray.Core.Providers.Mock;

namespace FootballTray.Client.Tests;

[TestClass]
public class MatchEventAndDeduplicationTests
{
    [TestMethod]
    public void GenerateEventUid_SameProperties_ProducesSameUid()
    {
        string uid1 = MatchEvent.GenerateEventUid(
            fixtureId: "fix_123",
            type: MatchEventType.Goal,
            minute: 17,
            extraMinute: 0,
            teamId: "team_barcelona",
            playerId: "ply_10",
            assistPlayerId: "ply_8",
            description: "Goal from penalty box");

        string uid2 = MatchEvent.GenerateEventUid(
            fixtureId: "fix_123",
            type: MatchEventType.Goal,
            minute: 17,
            extraMinute: 0,
            teamId: "team_barcelona",
            playerId: "ply_10",
            assistPlayerId: "ply_8",
            description: "Goal from penalty box");

        Assert.AreEqual(uid1, uid2);
        StringAssert.StartsWith(uid1, "evt_fix_123_17_0_Goal_");
    }

    [TestMethod]
    public void GenerateEventUid_DifferentMinute_ProducesDifferentUid()
    {
        string uid1 = MatchEvent.GenerateEventUid("fix_123", MatchEventType.Goal, 17, 0, "team_barcelona", "ply_10");
        string uid2 = MatchEvent.GenerateEventUid("fix_123", MatchEventType.Goal, 18, 0, "team_barcelona", "ply_10");

        Assert.AreNotEqual(uid1, uid2);
    }

    [TestMethod]
    public void Fixture_WithEvent_DeduplicatesEventsWithSameUid()
    {
        var fixture = MockMatchSeeder.CreateElClasicoFixture("fix_test_dedup");
        var goalEvent = MatchEvent.Create(
            fixtureId: "fix_test_dedup",
            type: MatchEventType.Goal,
            minute: 17,
            extraMinute: 0,
            team: MockMatchSeeder.Barcelona,
            player: MockMatchSeeder.PlayerLMartinez);

        // Add once
        var updatedFixture1 = fixture.WithEvent(goalEvent);
        Assert.HasCount(1, updatedFixture1.Events);
        Assert.AreEqual(1, updatedFixture1.Score.Home);

        // Add duplicate event
        var updatedFixture2 = updatedFixture1.WithEvent(goalEvent);
        Assert.HasCount(1, updatedFixture2.Events);
        Assert.AreEqual(1, updatedFixture2.Score.Home); // Score must not increment twice!
    }

    [TestMethod]
    public void Fixture_WithEvent_OwnGoal_CreditsOpponentScore()
    {
        var fixture = MockMatchSeeder.CreateElClasicoFixture("fix_og_test");
        var ownGoalByBarca = MatchEvent.Create(
            fixtureId: "fix_og_test",
            type: MatchEventType.OwnGoal,
            minute: 22,
            extraMinute: 0,
            team: MockMatchSeeder.Barcelona,
            player: MockMatchSeeder.PlayerVSilva);

        var updated = fixture.WithEvent(ownGoalByBarca);

        Assert.AreEqual(0, updated.Score.Home);
        Assert.AreEqual(1, updated.Score.Away); // Away team (Real Madrid) credited
    }

    [TestMethod]
    public void Fixture_WithEvent_YellowCard_DoesNotChangeScore()
    {
        var fixture = MockMatchSeeder.CreateElClasicoFixture("fix_card_test");
        var yellowCard = MatchEvent.Create(
            fixtureId: "fix_card_test",
            type: MatchEventType.YellowCard,
            minute: 35,
            extraMinute: 0,
            team: MockMatchSeeder.RealMadrid,
            player: MockMatchSeeder.PlayerVSilva);

        var updated = fixture.WithEvent(yellowCard);

        Assert.HasCount(1, updated.Events);
        Assert.AreEqual(0, updated.Score.Home);
        Assert.AreEqual(0, updated.Score.Away);
    }
}
