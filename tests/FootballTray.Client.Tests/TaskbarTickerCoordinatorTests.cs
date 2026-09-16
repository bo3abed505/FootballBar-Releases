using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Client.Taskbar;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class TaskbarTickerCoordinatorTests
{
    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "FootballTray.sln")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static TrayMatchDto CreateMatch(
        string id,
        string home,
        string away,
        string state = "LIVE",
        int minute = 10,
        int scoreH = 0,
        int scoreA = 0)
    {
        return new TrayMatchDto
        {
            Id = id,
            HomeTeam = new TeamSummaryDto { Name = home, ShortName = home },
            AwayTeam = new TeamSummaryDto { Name = away, ShortName = away },
            State = state,
            ElapsedMinute = minute,
            Score = new ScoreDto { Home = scoreH, Away = scoreA }
        };
    }

    private sealed class TestTickerTimer : ITickerTimer
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(7);
        public bool IsRunning { get; private set; }
        public event EventHandler? Tick;

        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Stop();
    }

    [TestMethod]
    public void Test01_ZeroCandidates_HasNoCurrentCandidate_AndCannotCycle()
    {
        var state = new TickerRotationState();
        state.ApplyCandidates(Array.Empty<TrayMatchDto>());

        Assert.IsNull(state.CurrentCandidate);
        Assert.AreEqual(-1, state.CurrentIndex);
        Assert.IsFalse(state.CanCycle);
        Assert.IsNull(state.Advance());
    }

    [TestMethod]
    public void Test02_SingleCandidate_SelectsCandidate_AndCannotCycle()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "ARS", "CHE");

        state.ApplyCandidates(new[] { m1 });

        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
        Assert.IsFalse(state.CanCycle);
        Assert.AreEqual("m1", state.Advance()?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
    }

    [TestMethod]
    public void Test03_TwoLiveCandidates_AdvanceRotatesToNext()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "ARS", "CHE");
        var m2 = CreateMatch("m2", "LIV", "MCI");

        state.ApplyCandidates(new[] { m1, m2 });

        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
        Assert.IsTrue(state.CanCycle);

        var advanced = state.Advance();
        Assert.AreEqual("m2", advanced?.Id);
        Assert.AreEqual(1, state.CurrentIndex);
    }

    [TestMethod]
    public void Test04_ThreeCandidates_AdvancePreservesPriorityOrderAndWraps()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");
        var m2 = CreateMatch("m2", "C", "D");
        var m3 = CreateMatch("m3", "E", "F");

        state.ApplyCandidates(new[] { m1, m2, m3 });

        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.AreEqual("m2", state.Advance()?.Id);
        Assert.AreEqual(1, state.CurrentIndex);
        Assert.AreEqual("m3", state.Advance()?.Id);
        Assert.AreEqual(2, state.CurrentIndex);
        Assert.AreEqual("m1", state.Advance()?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
    }

    [TestMethod]
    public void Test05_Refresh_CurrentFixtureStillPresent_PreservesCurrentIdentity()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");
        var m2 = CreateMatch("m2", "C", "D");

        state.ApplyCandidates(new[] { m1, m2 });
        state.Advance(); // now at m2
        Assert.AreEqual("m2", state.CurrentCandidate?.Id);

        var m1Fresh = CreateMatch("m1", "A", "B", minute: 15);
        var m2Fresh = CreateMatch("m2", "C", "D", minute: 15);

        state.ApplyCandidates(new[] { m1Fresh, m2Fresh });

        Assert.AreEqual("m2", state.CurrentCandidate?.Id);
        Assert.AreEqual(1, state.CurrentIndex);
    }

    [TestMethod]
    public void Test06_Refresh_CurrentFixtureStillPresent_UpdatesToFreshDtoData()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B", minute: 10, scoreH: 0);

        state.ApplyCandidates(new[] { m1 });

        var m1Fresh = CreateMatch("m1", "A", "B", minute: 25, scoreH: 1);
        state.ApplyCandidates(new[] { m1Fresh });

        Assert.AreSame(m1Fresh, state.CurrentCandidate);
        Assert.IsNotNull(state.CurrentCandidate);
        Assert.AreEqual(25, state.CurrentCandidate.ElapsedMinute);
        Assert.AreEqual(1, state.CurrentCandidate.Score.Home);
    }

    [TestMethod]
    public void Test07_Refresh_CurrentFixtureRemoved_SelectsNewCandidateZero()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");
        var m2 = CreateMatch("m2", "C", "D");

        state.ApplyCandidates(new[] { m1, m2 });
        state.Advance(); // now at m2
        Assert.AreEqual("m2", state.CurrentCandidate?.Id);

        var m3 = CreateMatch("m3", "E", "F");
        var m4 = CreateMatch("m4", "G", "H");

        state.ApplyCandidates(new[] { m3, m4 }); // m2 is gone

        Assert.AreEqual("m3", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
    }

    [TestMethod]
    public void Test08_Refresh_MultipleToOne_DisablesCycling()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");
        var m2 = CreateMatch("m2", "C", "D");

        state.ApplyCandidates(new[] { m1, m2 });
        Assert.IsTrue(state.CanCycle);

        var m1Fresh = CreateMatch("m1", "A", "B");
        state.ApplyCandidates(new[] { m1Fresh });

        Assert.IsFalse(state.CanCycle);
        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
    }

    [TestMethod]
    public void Test09_Refresh_ToEmpty_ClearsCurrentCandidate()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");

        state.ApplyCandidates(new[] { m1 });
        Assert.IsNotNull(state.CurrentCandidate);

        state.ApplyCandidates(Array.Empty<TrayMatchDto>());

        Assert.IsNull(state.CurrentCandidate);
        Assert.AreEqual(-1, state.CurrentIndex);
        Assert.IsFalse(state.CanCycle);
    }

    [TestMethod]
    public void Test10_Refresh_OneToMultiple_PreservesCurrentWhenStillEligible()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");

        state.ApplyCandidates(new[] { m1 });
        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.IsFalse(state.CanCycle);

        var m1Fresh = CreateMatch("m1", "A", "B", minute: 15);
        var m2 = CreateMatch("m2", "C", "D");

        state.ApplyCandidates(new[] { m1Fresh, m2 });

        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
        Assert.IsTrue(state.CanCycle);
    }

    [TestMethod]
    public void Test11_Refresh_OneToMultiple_SelectsCandidateZeroWhenOldCurrentAbsent()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");

        state.ApplyCandidates(new[] { m1 });

        var m2 = CreateMatch("m2", "C", "D");
        var m3 = CreateMatch("m3", "E", "F");

        state.ApplyCandidates(new[] { m2, m3 }); // m1 gone

        Assert.AreEqual("m2", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
        Assert.IsTrue(state.CanCycle);
    }

    [TestMethod]
    public void Test12_PriorityReordering_CurrentStillPresent_PreservesIdentityAndUpdatesIndex()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");
        var m2 = CreateMatch("m2", "C", "D");
        var m3 = CreateMatch("m3", "E", "F");

        state.ApplyCandidates(new[] { m1, m2, m3 });
        state.Advance(); // m2
        state.Advance(); // m3 (index 2)
        Assert.AreEqual("m3", state.CurrentCandidate?.Id);
        Assert.AreEqual(2, state.CurrentIndex);

        // Reordered candidates with m3 at index 0
        var m3Fresh = CreateMatch("m3", "E", "F");
        var m1Fresh = CreateMatch("m1", "A", "B");
        var m2Fresh = CreateMatch("m2", "C", "D");

        state.ApplyCandidates(new[] { m3Fresh, m1Fresh, m2Fresh });

        Assert.AreEqual("m3", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
    }

    [TestMethod]
    public void Test13_Advance_AfterRefresh_ContinuesFromUpdatedCurrentIndex()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "A", "B");
        var m2 = CreateMatch("m2", "C", "D");
        var m3 = CreateMatch("m3", "E", "F");

        state.ApplyCandidates(new[] { m1, m2, m3 });
        state.Advance(); // m2
        state.Advance(); // m3 (index 2)

        // Reordered: m3 is index 0
        var m3Fresh = CreateMatch("m3", "E", "F");
        var m1Fresh = CreateMatch("m1", "A", "B");
        var m2Fresh = CreateMatch("m2", "C", "D");

        state.ApplyCandidates(new[] { m3Fresh, m1Fresh, m2Fresh });
        Assert.AreEqual(0, state.CurrentIndex);

        // Next advance moves to index 1 (m1Fresh)
        var next = state.Advance();
        Assert.AreEqual("m1", next?.Id);
        Assert.AreEqual(1, state.CurrentIndex);
    }

    [TestMethod]
    public void Test14_DisplayedFixtureIdAndDisplayText_AreProducedAsOneCoherentState()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "ARS", "CHE", minute: 30, scoreH: 2, scoreA: 1);

        state.ApplyCandidates(new[] { m1 });
        var displayState = state.GetDisplayState(TickerConnectionState.OnlineFresh);

        Assert.AreEqual("m1", displayState.FixtureId);
        Assert.AreEqual("⚽ ARS 2–1 CHE 30'", displayState.FormattedText);
    }

    [TestMethod]
    public void Test15_Dispose_TimerControllerContract_DoesNotAllowFurtherScheduledRotation()
    {
        var timer = new TestTickerTimer();
        var emittedStates = new List<(string? Id, string Text)>();

        using var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => emittedStates.Add((id, text)));

        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                CreateMatch("m1", "ARS", "CHE"),
                CreateMatch("m2", "LIV", "MCI")
            }
        };

        coordinator.ApplySnapshot(trayData, TickerConnectionState.OnlineFresh);

        Assert.IsTrue(coordinator.IsCycling);
        Assert.IsTrue(timer.IsRunning);
        Assert.AreEqual(1, emittedStates.Count);
        Assert.AreEqual("m1", emittedStates[0].Id);

        // Timer tick advances to m2
        timer.FireTick();
        Assert.AreEqual(2, emittedStates.Count);
        Assert.AreEqual("m2", emittedStates[1].Id);

        // Dispose coordinator
        coordinator.Dispose();
        Assert.IsFalse(timer.IsRunning);

        // Firing tick after dispose must not emit any new states
        int countBeforeTick = emittedStates.Count;
        timer.FireTick();
        Assert.AreEqual(countBeforeTick, emittedStates.Count);

        // Applying snapshot after dispose must be a no-op
        coordinator.ApplySnapshot(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual(countBeforeTick, emittedStates.Count);
    }

    [TestMethod]
    public void Test16_TaskbarTickerClickRouting_StructuralContractGuards()
    {
        string root = FindRepoRoot();
        string tickerSourcePath = Path.Combine(root, "src", "client", "FootballTray.Client", "Taskbar", "TaskbarTicker.xaml.cs");
        Assert.IsTrue(File.Exists(tickerSourcePath), "TaskbarTicker.xaml.cs must exist.");

        string content = File.ReadAllText(tickerSourcePath);

        // 1. Must store displayed match ID
        Assert.IsTrue(
            content.Contains("_currentDisplayedMatchId"),
            "TaskbarTicker must declare _currentDisplayedMatchId.");

        // 2. Must expose UpdateDisplay(string? fixtureId, string formattedText)
        Assert.IsTrue(
            content.Contains("UpdateDisplay(string? fixtureId, string formattedText)"),
            "TaskbarTicker must implement UpdateDisplay with fixtureId and formattedText.");

        // 3. RootBorder_Tapped must snapshot matchId before any await
        Assert.IsTrue(
            content.Contains("var matchId = _currentDisplayedMatchId;"),
            "RootBorder_Tapped must snapshot _currentDisplayedMatchId into a local variable before await.");

        // 4. RootBorder_Tapped must pass snapshotted matchId to GetMatchDetailsAsync
        Assert.IsTrue(
            content.Contains("apiClient.GetMatchDetailsAsync(matchId)"),
            "RootBorder_Tapped must request details for the snapshotted matchId.");

        // 5. RootBorder_Tapped must NOT invoke GetActiveMatch
        Assert.IsFalse(
            content.Contains("GetActiveMatch"),
            "TaskbarTicker.xaml.cs must not call GetActiveMatch upon click.");
    }

    [TestMethod]
    public void Test17_AppTrayUpdatedDispatch_CapturesCoordinatorLocallyAndAvoidsFieldDereference()
    {
        string root = FindRepoRoot();
        string appSourcePath = Path.Combine(root, "src", "client", "FootballTray.Client", "App.xaml.cs");
        Assert.IsTrue(File.Exists(appSourcePath), "App.xaml.cs must exist.");

        string content = File.ReadAllText(appSourcePath);

        // 1. Must capture coordinator into a local variable in TrayUpdated handler
        Assert.IsTrue(
            content.Contains("var coordinator = _tickerCoordinator;"),
            "App.xaml.cs must snapshot _tickerCoordinator into local variable coordinator.");

        // 2. Must NOT dereference mutable field _tickerCoordinator.ApplySnapshot in TrayUpdated
        Assert.IsFalse(
            content.Contains("_tickerCoordinator.ApplySnapshot"),
            "App.xaml.cs must not dereference _tickerCoordinator directly inside ApplySnapshot call.");

        // 3. Must use coordinator.ApplySnapshot
        Assert.IsTrue(
            content.Contains("coordinator.ApplySnapshot"),
            "App.xaml.cs must invoke ApplySnapshot on the captured coordinator reference.");
    }
}
