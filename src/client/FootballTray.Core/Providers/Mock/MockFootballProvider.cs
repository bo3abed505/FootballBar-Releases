using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FootballTray.Core.Models;
using FootballTray.Core.StateMachine;

namespace FootballTray.Core.Providers.Mock;

/// <summary>
/// Deterministic in-memory football provider capable of simulating a match timeline without real waiting.
/// </summary>
public sealed class MockFootballProvider : IFootballProvider
{
    private readonly string _fixtureId;
    private readonly IReadOnlyList<TimelineEntry> _timeline;
    private readonly MatchStateMachine _stateMachine;
    private readonly HashSet<string> _emittedEventUids = new();

    private Fixture _currentFixture;

    public string ProviderName => "MockFootballProvider";

    public Fixture CurrentFixture => _currentFixture;
    public MatchState CurrentState => _stateMachine.CurrentState;
    public int CurrentMinute => _currentFixture.ElapsedMinute;
    public int CurrentExtraMinute => _currentFixture.ExtraMinute;

    public event Action<Fixture>? FixtureUpdated;
    public event Action<MatchEvent>? MatchEventOccurred;

    public MockFootballProvider(string fixtureId = "fix_elclasico_2026")
    {
        _fixtureId = fixtureId;
        _timeline = MockMatchTimeline.CreateDefaultElClasicoTimeline(_fixtureId);
        _stateMachine = new MatchStateMachine(MatchState.PreMatch);
        _currentFixture = MockMatchSeeder.CreateElClasicoFixture(_fixtureId);
    }

    /// <summary>
    /// Advances simulated match time to the target minute and extra minute,
    /// triggering any state transitions, score changes, and events up to that timestamp.
    /// </summary>
    public Fixture AdvanceToMinute(int targetMinute, int targetExtraMinute = 0)
    {
        // Filter timeline events that have occurred up to the target time
        var eligibleEntries = _timeline.Where(e =>
            e.Minute < targetMinute || (e.Minute == targetMinute && e.ExtraMinute <= targetExtraMinute))
            .OrderBy(e => e.Minute)
            .ThenBy(e => e.ExtraMinute);

        foreach (var entry in eligibleEntries)
        {
            // Apply State Transition if needed
            if (entry.TransitionToState.HasValue && entry.TransitionToState.Value != _stateMachine.CurrentState)
            {
                if (_stateMachine.CanTransitionTo(entry.TransitionToState.Value))
                {
                    _stateMachine.TransitionTo(entry.TransitionToState.Value);
                }
            }

            // Apply Event if not already emitted
            if (entry.Event != null && _emittedEventUids.Add(entry.Event.EventUid))
            {
                _currentFixture = _currentFixture.WithEvent(entry.Event);
                MatchEventOccurred?.Invoke(entry.Event);
            }
        }

        _currentFixture = _currentFixture.WithState(_stateMachine.CurrentState, targetMinute, targetExtraMinute);
        FixtureUpdated?.Invoke(_currentFixture);

        return _currentFixture;
    }

    /// <summary>
    /// Advances to the next timeline milestone entry.
    /// </summary>
    public Fixture? StepNext()
    {
        var nextEntry = _timeline
            .Where(e => e.Minute > CurrentMinute || (e.Minute == CurrentMinute && e.ExtraMinute > CurrentExtraMinute))
            .OrderBy(e => e.Minute)
            .ThenBy(e => e.ExtraMinute)
            .FirstOrDefault();

        if (nextEntry != null)
        {
            return AdvanceToMinute(nextEntry.Minute, nextEntry.ExtraMinute);
        }

        return null;
    }

    /// <summary>
    /// Resets the simulation back to initial pre-match state.
    /// </summary>
    public void Reset()
    {
        _stateMachine.Reset(MatchState.PreMatch);
        _emittedEventUids.Clear();
        _currentFixture = MockMatchSeeder.CreateElClasicoFixture(_fixtureId);
        FixtureUpdated?.Invoke(_currentFixture);
    }

    public Task<IReadOnlyList<Competition>> GetCompetitionsAsync()
    {
        IReadOnlyList<Competition> list = new[] { MockMatchSeeder.LaLiga };
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<Fixture>> GetFixturesAsync(string? competitionId = null, DateTime? date = null)
    {
        IReadOnlyList<Fixture> list = new[] { _currentFixture };
        return Task.FromResult(list);
    }

    public Task<Fixture?> GetFixtureByIdAsync(string fixtureId)
    {
        Fixture? match = fixtureId == _fixtureId ? _currentFixture : null;
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<MatchEvent>> GetMatchEventsAsync(string fixtureId)
    {
        IReadOnlyList<MatchEvent> events = fixtureId == _fixtureId ? _currentFixture.Events : Array.Empty<MatchEvent>();
        return Task.FromResult(events);
    }

    public Task<Standing?> GetStandingsAsync(string competitionId, string seasonId)
    {
        var rows = new List<StandingRow>
        {
            new(1, MockMatchSeeder.Barcelona, 24, 18, 4, 2, 58, 20, 58, "WWWDW"),
            new(2, MockMatchSeeder.RealMadrid, 24, 17, 5, 2, 54, 18, 56, "WWLWW")
        };

        var standings = new Standing(MockMatchSeeder.LaLiga.Id, MockMatchSeeder.CurrentSeason.Id, rows);
        return Task.FromResult<Standing?>(standings);
    }
}
