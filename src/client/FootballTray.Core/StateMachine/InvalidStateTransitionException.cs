using System;
using FootballTray.Core.Models;

namespace FootballTray.Core.StateMachine;

/// <summary>
/// Exception thrown when an illegal or unsupported match state transition is attempted.
/// </summary>
public sealed class InvalidStateTransitionException : InvalidOperationException
{
    public MatchState FromState { get; }
    public MatchState ToState { get; }

    public InvalidStateTransitionException(MatchState fromState, MatchState toState, string? message = null)
        : base(message ?? $"Invalid match state transition from '{fromState}' to '{toState}'.")
    {
        FromState = fromState;
        ToState = toState;
    }
}
