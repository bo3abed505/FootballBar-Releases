using System;
using System.Collections.Generic;
using FootballTray.Core.Models;

namespace FootballTray.Core.StateMachine;

/// <summary>
/// Deterministic finite state machine governing match lifecycle transitions.
/// </summary>
public sealed class MatchStateMachine
{
    private static readonly IReadOnlyDictionary<MatchState, HashSet<MatchState>> ValidTransitions =
        new Dictionary<MatchState, HashSet<MatchState>>
        {
            [MatchState.PreMatch] = new() { MatchState.Live, MatchState.Postponed, MatchState.Cancelled },
            [MatchState.Live] = new() { MatchState.HalfTime, MatchState.Finished, MatchState.Postponed, MatchState.Cancelled },
            [MatchState.HalfTime] = new() { MatchState.Live, MatchState.Postponed, MatchState.Cancelled },
            [MatchState.Finished] = new() { MatchState.PostMatch },
            [MatchState.PostMatch] = new() { },
            [MatchState.Postponed] = new() { MatchState.PreMatch, MatchState.Cancelled },
            [MatchState.Cancelled] = new() { }
        };

    public MatchState CurrentState { get; private set; }

    public event Action<MatchState, MatchState>? StateChanged;

    public MatchStateMachine(MatchState initialState = MatchState.PreMatch)
    {
        CurrentState = initialState;
    }

    /// <summary>
    /// Checks whether transitioning from the current state to the target state is permissible.
    /// </summary>
    public bool CanTransitionTo(MatchState targetState)
    {
        if (CurrentState == targetState) return true;
        return ValidTransitions.TryGetValue(CurrentState, out var allowed) && allowed.Contains(targetState);
    }

    /// <summary>
    /// Attempts to transition to the target state, returning success or failure with an error message.
    /// </summary>
    public bool TryTransitionTo(MatchState targetState, out string? errorMessage)
    {
        if (!CanTransitionTo(targetState))
        {
            errorMessage = $"Cannot transition match state from '{CurrentState}' to '{targetState}'.";
            return false;
        }

        if (CurrentState != targetState)
        {
            MatchState previous = CurrentState;
            CurrentState = targetState;
            errorMessage = null;
            StateChanged?.Invoke(previous, targetState);
        }
        else
        {
            errorMessage = null;
        }

        return true;
    }

    /// <summary>
    /// Transitions to the target state or throws an InvalidStateTransitionException if the transition is illegal.
    /// </summary>
    public void TransitionTo(MatchState targetState)
    {
        if (!TryTransitionTo(targetState, out string? error))
        {
            throw new InvalidStateTransitionException(CurrentState, targetState, error);
        }
    }

    /// <summary>
    /// Resets the state machine back to a specified state (default: PreMatch).
    /// </summary>
    public void Reset(MatchState state = MatchState.PreMatch)
    {
        MatchState previous = CurrentState;
        CurrentState = state;
        if (previous != state)
        {
            StateChanged?.Invoke(previous, state);
        }
    }
}
