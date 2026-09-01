using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Core.Models;
using FootballTray.Core.StateMachine;

namespace FootballTray.Client.Tests;

[TestClass]
public class MatchStateMachineTests
{
    [TestMethod]
    public void InitialState_DefaultsToPreMatch()
    {
        var fsm = new MatchStateMachine();
        Assert.AreEqual(MatchState.PreMatch, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_PreMatchToLive_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.PreMatch);
        fsm.TransitionTo(MatchState.Live);
        Assert.AreEqual(MatchState.Live, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_LiveToHalfTime_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.Live);
        fsm.TransitionTo(MatchState.HalfTime);
        Assert.AreEqual(MatchState.HalfTime, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_HalfTimeToLive_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.HalfTime);
        fsm.TransitionTo(MatchState.Live);
        Assert.AreEqual(MatchState.Live, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_LiveToFinished_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.Live);
        fsm.TransitionTo(MatchState.Finished);
        Assert.AreEqual(MatchState.Finished, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_FinishedToPostMatch_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.Finished);
        fsm.TransitionTo(MatchState.PostMatch);
        Assert.AreEqual(MatchState.PostMatch, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_PreMatchToPostponed_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.PreMatch);
        fsm.TransitionTo(MatchState.Postponed);
        Assert.AreEqual(MatchState.Postponed, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_PreMatchToCancelled_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.PreMatch);
        fsm.TransitionTo(MatchState.Cancelled);
        Assert.AreEqual(MatchState.Cancelled, fsm.CurrentState);
    }

    [TestMethod]
    public void ValidTransitions_PostponedToPreMatch_Succeeds()
    {
        var fsm = new MatchStateMachine(MatchState.Postponed);
        fsm.TransitionTo(MatchState.PreMatch);
        Assert.AreEqual(MatchState.PreMatch, fsm.CurrentState);
    }

    [TestMethod]
    public void InvalidTransition_FinishedToLive_ThrowsException()
    {
        var fsm = new MatchStateMachine(MatchState.Finished);
        Assert.ThrowsExactly<InvalidStateTransitionException>(() => fsm.TransitionTo(MatchState.Live));
    }

    [TestMethod]
    public void InvalidTransition_CancelledToLive_ThrowsException()
    {
        var fsm = new MatchStateMachine(MatchState.Cancelled);
        Assert.ThrowsExactly<InvalidStateTransitionException>(() => fsm.TransitionTo(MatchState.Live));
    }

    [TestMethod]
    public void InvalidTransition_PreMatchToFinished_ThrowsException()
    {
        var fsm = new MatchStateMachine(MatchState.PreMatch);
        Assert.ThrowsExactly<InvalidStateTransitionException>(() => fsm.TransitionTo(MatchState.Finished));
    }

    [TestMethod]
    public void TryTransitionTo_InvalidTransition_ReturnsFalseWithError()
    {
        var fsm = new MatchStateMachine(MatchState.PostMatch);
        bool success = fsm.TryTransitionTo(MatchState.Live, out string? error);

        Assert.IsFalse(success);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "Cannot transition match state from 'PostMatch' to 'Live'");
    }

    [TestMethod]
    public void StateChangedEvent_FiresOnTransition()
    {
        var fsm = new MatchStateMachine(MatchState.PreMatch);
        MatchState? from = null;
        MatchState? to = null;

        fsm.StateChanged += (prev, next) =>
        {
            from = prev;
            to = next;
        };

        fsm.TransitionTo(MatchState.Live);

        Assert.AreEqual(MatchState.PreMatch, from);
        Assert.AreEqual(MatchState.Live, to);
    }
}
