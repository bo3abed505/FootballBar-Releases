using System;
using System.Collections.Generic;
using FootballTray.Core.Models;

namespace FootballTray.Core.Providers.Mock;

public sealed record TimelineEntry(
    int Minute,
    int ExtraMinute,
    MatchState? TransitionToState,
    MatchEvent? Event);

/// <summary>
/// Pre-configured deterministic timeline for mock match simulation.
/// </summary>
public static class MockMatchTimeline
{
    public static IReadOnlyList<TimelineEntry> CreateDefaultElClasicoTimeline(string fixtureId)
    {
        var bar = MockMatchSeeder.Barcelona;
        var rma = MockMatchSeeder.RealMadrid;

        return new List<TimelineEntry>
        {
            // 00:00 PreMatch
            new(0, 0, MatchState.PreMatch, null),

            // 00:01 Match kicks off
            new(1, 0, MatchState.Live, MatchEvent.Create(fixtureId, MatchEventType.PeriodStart, 1, 0, bar, description: "First half kicks off")),

            // 00:17 Goal: Barcelona 1-0
            new(17, 0, MatchState.Live, MatchEvent.Create(fixtureId, MatchEventType.Goal, 17, 0, bar, MockMatchSeeder.PlayerLMartinez, MockMatchSeeder.PlayerPGonzalez, "Goal! L. Martinez slots it into the bottom corner.")),

            // 00:35 Yellow Card: Real Madrid
            new(35, 0, MatchState.Live, MatchEvent.Create(fixtureId, MatchEventType.YellowCard, 35, 0, rma, MockMatchSeeder.PlayerVSilva, description: "Yellow card for tactical foul")),

            // 00:45 Half Time
            new(45, 0, MatchState.HalfTime, MatchEvent.Create(fixtureId, MatchEventType.PeriodEnd, 45, 0, bar, description: "Half-time whistle")),

            // 00:46 Second Half kicks off
            new(46, 0, MatchState.Live, MatchEvent.Create(fixtureId, MatchEventType.PeriodStart, 46, 0, rma, description: "Second half kicks off")),

            // 00:72 Goal: Real Madrid 1-1
            new(72, 0, MatchState.Live, MatchEvent.Create(fixtureId, MatchEventType.Goal, 72, 0, rma, MockMatchSeeder.PlayerKBecker, MockMatchSeeder.PlayerRHernandez, "Goal! K. Becker powers a header past the keeper.")),

            // 00:88 Goal: Barcelona 2-1
            new(88, 0, MatchState.Live, MatchEvent.Create(fixtureId, MatchEventType.Goal, 88, 0, bar, MockMatchSeeder.PlayerFDeJong, MockMatchSeeder.PlayerLMartinez, "Goal! F. De Jong curls a stunning strike from distance.")),

            // 00:90+5 Full Time
            new(90, 5, MatchState.Finished, MatchEvent.Create(fixtureId, MatchEventType.PeriodEnd, 90, 5, bar, description: "Full-time whistle. Barcelona wins 2-1."))
        };
    }
}
