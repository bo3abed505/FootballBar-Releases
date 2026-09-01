using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Core.Models;
using FootballTray.Core.Providers.Mock;

namespace FootballTray.Client.Tests;

[TestClass]
public class MockFootballProviderTests
{
    [TestMethod]
    public void InitialState_IsPreMatchWithZeroScore()
    {
        var provider = new MockFootballProvider("fix_test_init");

        Assert.AreEqual(MatchState.PreMatch, provider.CurrentState);
        Assert.AreEqual(0, provider.CurrentMinute);
        Assert.AreEqual(0, provider.CurrentFixture.Score.Home);
        Assert.AreEqual(0, provider.CurrentFixture.Score.Away);
        Assert.IsEmpty(provider.CurrentFixture.Events);
    }

    [TestMethod]
    public void AdvanceToMinute_17_TransitionsToLiveAndScoresGoal()
    {
        var provider = new MockFootballProvider("fix_test_17");

        var fixture = provider.AdvanceToMinute(17);

        Assert.AreEqual(MatchState.Live, provider.CurrentState);
        Assert.AreEqual(17, provider.CurrentMinute);
        Assert.AreEqual(1, fixture.Score.Home); // Barcelona 1 - 0 Real Madrid
        Assert.AreEqual(0, fixture.Score.Away);
        
        var goalEvent = fixture.Events.FirstOrDefault(e => e.Type == MatchEventType.Goal);
        Assert.IsNotNull(goalEvent);
        Assert.AreEqual(17, goalEvent.Minute);
        Assert.AreEqual(MockMatchSeeder.Barcelona.Id, goalEvent.Team.Id);
    }

    [TestMethod]
    public void AdvanceToMinute_45_TransitionsToHalfTimeAndRecordsHalfTimeScore()
    {
        var provider = new MockFootballProvider("fix_test_ht");

        var fixture = provider.AdvanceToMinute(45);

        Assert.AreEqual(MatchState.HalfTime, provider.CurrentState);
        Assert.AreEqual(45, provider.CurrentMinute);
        Assert.AreEqual(1, fixture.Score.Home);
        Assert.AreEqual(0, fixture.Score.Away);
        Assert.AreEqual(1, fixture.Score.HomeHalfTime);
        Assert.AreEqual(0, fixture.Score.AwayHalfTime);
    }

    [TestMethod]
    public void FullMatchSimulation_ProgressesDeterministically()
    {
        var provider = new MockFootballProvider("fix_test_full");

        // 1. Kickoff
        provider.AdvanceToMinute(1);
        Assert.AreEqual(MatchState.Live, provider.CurrentState);
        Assert.AreEqual(0, provider.CurrentFixture.Score.Home);

        // 2. Goal Barcelona (17')
        provider.AdvanceToMinute(17);
        Assert.AreEqual(1, provider.CurrentFixture.Score.Home);
        Assert.AreEqual(0, provider.CurrentFixture.Score.Away);

        // 3. Yellow card (35')
        provider.AdvanceToMinute(35);
        Assert.IsTrue(provider.CurrentFixture.Events.Any(e => e.Type == MatchEventType.YellowCard));

        // 4. Halftime (45')
        provider.AdvanceToMinute(45);
        Assert.AreEqual(MatchState.HalfTime, provider.CurrentState);
        Assert.AreEqual(1, provider.CurrentFixture.Score.Home);

        // 5. Second half kickoff (46')
        provider.AdvanceToMinute(46);
        Assert.AreEqual(MatchState.Live, provider.CurrentState);

        // 6. Real Madrid equalizer (72')
        provider.AdvanceToMinute(72);
        Assert.AreEqual(1, provider.CurrentFixture.Score.Home);
        Assert.AreEqual(1, provider.CurrentFixture.Score.Away);

        // 7. Barcelona winner (88')
        provider.AdvanceToMinute(88);
        Assert.AreEqual(2, provider.CurrentFixture.Score.Home);
        Assert.AreEqual(1, provider.CurrentFixture.Score.Away);

        // 8. Full time (90+5')
        provider.AdvanceToMinute(90, 5);
        Assert.AreEqual(MatchState.Finished, provider.CurrentState);
        Assert.AreEqual(2, provider.CurrentFixture.Score.Home);
        Assert.AreEqual(1, provider.CurrentFixture.Score.Away);
    }

    [TestMethod]
    public void StepNext_AdvancesToNextMilestoneChronologically()
    {
        var provider = new MockFootballProvider("fix_test_step");

        // Step 1: PreMatch -> Live 1'
        var f1 = provider.StepNext();
        Assert.IsNotNull(f1);
        Assert.AreEqual(1, f1.ElapsedMinute);
        Assert.AreEqual(MatchState.Live, f1.State);

        // Step 2: Live 1' -> Goal 17'
        var f2 = provider.StepNext();
        Assert.IsNotNull(f2);
        Assert.AreEqual(17, f2.ElapsedMinute);
        Assert.AreEqual(1, f2.Score.Home);

        // Step 3: Goal 17' -> YellowCard 35'
        var f3 = provider.StepNext();
        Assert.IsNotNull(f3);
        Assert.AreEqual(35, f3.ElapsedMinute);

        // Step 4: YellowCard 35' -> HalfTime 45'
        var f4 = provider.StepNext();
        Assert.IsNotNull(f4);
        Assert.AreEqual(45, f4.ElapsedMinute);
        Assert.AreEqual(MatchState.HalfTime, f4.State);
    }

    [TestMethod]
    public void Reset_RestoresSimulationToPreMatch()
    {
        var provider = new MockFootballProvider("fix_test_reset");
        provider.AdvanceToMinute(90, 5);

        Assert.AreEqual(MatchState.Finished, provider.CurrentState);

        provider.Reset();

        Assert.AreEqual(MatchState.PreMatch, provider.CurrentState);
        Assert.AreEqual(0, provider.CurrentMinute);
        Assert.AreEqual(0, provider.CurrentFixture.Score.Home);
        Assert.AreEqual(0, provider.CurrentFixture.Score.Away);
        Assert.IsEmpty(provider.CurrentFixture.Events);
    }

    [TestMethod]
    public async Task ProviderContract_QueriesReturnValidData()
    {
        var provider = new MockFootballProvider("fix_test_queries");

        var competitions = await provider.GetCompetitionsAsync();
        Assert.HasCount(1, competitions);
        Assert.AreEqual("comp_laliga", competitions[0].Id);

        var fixtures = await provider.GetFixturesAsync();
        Assert.HasCount(1, fixtures);

        var fixture = await provider.GetFixtureByIdAsync("fix_test_queries");
        Assert.IsNotNull(fixture);

        var standings = await provider.GetStandingsAsync("comp_laliga", "season_2025_2026");
        Assert.IsNotNull(standings);
        Assert.HasCount(2, standings.Rows);
        Assert.AreEqual(1, standings.Rows[0].Position);
    }
}
