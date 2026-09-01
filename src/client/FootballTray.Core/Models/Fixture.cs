using System;
using System.Collections.Generic;
using System.Linq;

namespace FootballTray.Core.Models;

/// <summary>
/// Domain model representing a scheduled, live, or completed football match fixture.
/// </summary>
public sealed record Fixture
{
    public string Id { get; init; }
    public Competition Competition { get; init; }
    public Season Season { get; init; }
    public Team HomeTeam { get; init; }
    public Team AwayTeam { get; init; }
    public DateTime KickoffTime { get; init; }
    public MatchState State { get; init; }
    public int ElapsedMinute { get; init; }
    public int ExtraMinute { get; init; }
    public Score Score { get; init; }
    public IReadOnlyList<MatchEvent> Events { get; init; }
    public Lineup? HomeLineup { get; init; }
    public Lineup? AwayLineup { get; init; }

    public Fixture(
        string id,
        Competition competition,
        Season season,
        Team homeTeam,
        Team awayTeam,
        DateTime kickoffTime,
        MatchState state = MatchState.PreMatch,
        int elapsedMinute = 0,
        int extraMinute = 0,
        Score? score = null,
        IReadOnlyList<MatchEvent>? events = null,
        Lineup? homeLineup = null,
        Lineup? awayLineup = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Competition = competition ?? throw new ArgumentNullException(nameof(competition));
        Season = season ?? throw new ArgumentNullException(nameof(season));
        HomeTeam = homeTeam ?? throw new ArgumentNullException(nameof(homeTeam));
        AwayTeam = awayTeam ?? throw new ArgumentNullException(nameof(awayTeam));
        KickoffTime = kickoffTime;
        State = state;
        ElapsedMinute = elapsedMinute;
        ExtraMinute = extraMinute;
        Score = score ?? Score.Empty;
        Events = events ?? Array.Empty<MatchEvent>();
        HomeLineup = homeLineup;
        AwayLineup = awayLineup;
    }

    /// <summary>
    /// Returns a new Fixture with the appended MatchEvent (deduplicating by EventUid) and updated score if applicable.
    /// </summary>
    public Fixture WithEvent(MatchEvent matchEvent)
    {
        if (matchEvent == null) throw new ArgumentNullException(nameof(matchEvent));

        // Deduplicate
        if (Events.Any(e => e.EventUid == matchEvent.EventUid))
        {
            return this;
        }

        var updatedEvents = Events.Append(matchEvent).OrderBy(e => e.Minute).ThenBy(e => e.ExtraMinute).ToList();
        Score newScore = Score;

        if (matchEvent.Type is MatchEventType.Goal or MatchEventType.PenaltyGoal)
        {
            bool isHome = matchEvent.Team.Id == HomeTeam.Id;
            newScore = Score.WithGoal(isHome);
        }
        else if (matchEvent.Type is MatchEventType.OwnGoal)
        {
            // Own goal awarded to opposing team
            bool isHome = matchEvent.Team.Id != HomeTeam.Id;
            newScore = Score.WithGoal(isHome);
        }

        return this with
        {
            Score = newScore,
            Events = updatedEvents
        };
    }

    /// <summary>
    /// Returns a new Fixture with updated match state and elapsed minute.
    /// </summary>
    public Fixture WithState(MatchState newState, int elapsedMinute, int extraMinute = 0)
    {
        Score newScore = Score;
        if (newState == MatchState.HalfTime && Score.HomeHalfTime == null)
        {
            newScore = Score.WithHalfTimeScore(Score.Home, Score.Away);
        }

        return this with
        {
            State = newState,
            ElapsedMinute = elapsedMinute,
            ExtraMinute = extraMinute,
            Score = newScore
        };
    }

    public string MatchSummary => $"{HomeTeam.Name} {Score.DisplayScore} {AwayTeam.Name}";
    public string MatchTimeDisplay => State switch
    {
        MatchState.PreMatch => KickoffTime.ToLocalTime().ToString("HH:mm"),
        MatchState.Live => ExtraMinute > 0 ? $"{ElapsedMinute}+{ExtraMinute}'" : $"{ElapsedMinute}'",
        MatchState.HalfTime => "HT",
        MatchState.Finished => "FT",
        MatchState.PostMatch => "AET/PEN",
        MatchState.Postponed => "PSTP",
        MatchState.Cancelled => "CANC",
        _ => ""
    };
}
