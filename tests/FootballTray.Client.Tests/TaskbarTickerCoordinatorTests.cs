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

    [TestMethod]
    public void Test18_PinMatch_PausesCarousel_AndStaysOnPinnedMatchAcrossTicks()
    {
        var timer = new TestTickerTimer();
        var emittedStates = new List<(string? Id, string Text, bool IsPinned)>();

        using var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => { },
            null,
            (id, text, home, away, match, isPinned) => emittedStates.Add((id, text, isPinned)));

        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                CreateMatch("m1", "ARS", "CHE"),
                CreateMatch("m2", "LIV", "MCI"),
                CreateMatch("m3", "MUN", "NEW")
            }
        };

        coordinator.ApplySnapshot(trayData, TickerConnectionState.OnlineFresh);
        Assert.AreEqual("m1", coordinator.State.CurrentCandidate?.Id);
        Assert.IsFalse(coordinator.IsPinned);

        // Pin m2
        bool pinned = coordinator.PinMatch("m2");
        Assert.IsTrue(pinned);
        Assert.IsTrue(coordinator.IsPinned);
        Assert.AreEqual("m2", coordinator.PinnedMatchId);
        Assert.AreEqual("m2", coordinator.State.CurrentCandidate?.Id);

        int emissionsCountBeforeTicks = emittedStates.Count;

        // Fire multiple ticks - should NOT advance because match is pinned
        timer.FireTick();
        Assert.AreEqual("m2", coordinator.State.CurrentCandidate?.Id);

        timer.FireTick();
        Assert.AreEqual("m2", coordinator.State.CurrentCandidate?.Id);

        // Emission count should NOT increase from ticks while pinned
        Assert.AreEqual(emissionsCountBeforeTicks, emittedStates.Count);
    }

    [TestMethod]
    public void Test19_UnpinMatch_ResumesCarouselRotation()
    {
        var timer = new TestTickerTimer();
        var emittedStates = new List<(string? Id, string Text, bool IsPinned)>();

        using var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => { },
            null,
            (id, text, home, away, match, isPinned) => emittedStates.Add((id, text, isPinned)));

        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                CreateMatch("m1", "ARS", "CHE"),
                CreateMatch("m2", "LIV", "MCI")
            }
        };

        coordinator.ApplySnapshot(trayData, TickerConnectionState.OnlineFresh);
        coordinator.PinMatch("m1");
        Assert.IsTrue(coordinator.IsPinned);

        // Timer tick does not advance while pinned
        timer.FireTick();
        Assert.AreEqual("m1", coordinator.State.CurrentCandidate?.Id);

        // Unpin
        coordinator.UnpinMatch();
        Assert.IsFalse(coordinator.IsPinned);
        Assert.IsNull(coordinator.PinnedMatchId);

        // Next timer tick should advance to m2
        timer.FireTick();
        Assert.AreEqual("m2", coordinator.State.CurrentCandidate?.Id);

        // Next timer tick should rotate back to m1
        timer.FireTick();
        Assert.AreEqual("m1", coordinator.State.CurrentCandidate?.Id);
    }

    [TestMethod]
    public void Test20_TogglePin_TogglesStateCorrectly()
    {
        var timer = new TestTickerTimer();
        using var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => { });

        var trayData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                CreateMatch("m1", "ARS", "CHE"),
                CreateMatch("m2", "LIV", "MCI")
            }
        };

        coordinator.ApplySnapshot(trayData, TickerConnectionState.OnlineFresh);

        // Toggle on current candidate (m1)
        bool pinResult = coordinator.TogglePin();
        Assert.IsTrue(pinResult);
        Assert.IsTrue(coordinator.IsPinned);
        Assert.AreEqual("m1", coordinator.PinnedMatchId);

        // Toggle again without arguments should unpin
        bool unpinResult = coordinator.TogglePin();
        Assert.IsFalse(unpinResult);
        Assert.IsFalse(coordinator.IsPinned);
        Assert.IsNull(coordinator.PinnedMatchId);

        // Toggle specifying m2
        bool pinM2Result = coordinator.TogglePin("m2");
        Assert.IsTrue(pinM2Result);
        Assert.IsTrue(coordinator.IsPinned);
        Assert.AreEqual("m2", coordinator.PinnedMatchId);

        // Toggle switching to m1
        bool pinM1Result = coordinator.TogglePin("m1");
        Assert.IsTrue(pinM1Result);
        Assert.IsTrue(coordinator.IsPinned);
        Assert.AreEqual("m1", coordinator.PinnedMatchId);
    }

    [TestMethod]
    public void Test21_ApplySnapshot_WhenPinnedMatchRemoved_ClearsPinnedStateGracefully()
    {
        var timer = new TestTickerTimer();
        using var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => { });

        var initialData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                CreateMatch("m1", "ARS", "CHE"),
                CreateMatch("m2", "LIV", "MCI")
            }
        };

        coordinator.ApplySnapshot(initialData, TickerConnectionState.OnlineFresh);
        coordinator.PinMatch("m2");
        Assert.IsTrue(coordinator.IsPinned);
        Assert.AreEqual("m2", coordinator.PinnedMatchId);

        // Next snapshot without m2 (e.g. match concluded or expired)
        var updatedData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                CreateMatch("m1", "ARS", "CHE"),
                CreateMatch("m3", "TOT", "AVL")
            }
        };

        coordinator.ApplySnapshot(updatedData, TickerConnectionState.OnlineFresh);

        // Pin must be dropped gracefully
        Assert.IsFalse(coordinator.IsPinned);
        Assert.IsNull(coordinator.PinnedMatchId);
        Assert.AreEqual("m1", coordinator.State.CurrentCandidate?.Id);

        // Ticker rotation resumes
        timer.FireTick();
        Assert.AreEqual("m3", coordinator.State.CurrentCandidate?.Id);
    }

    [TestMethod]
    public void Test22_GoalscorerDtoVerification_TrayMatchSurfacesFormattedGoalscorers()
    {
        var match = CreateMatch("m1", "ARS", "CHE", "LIVE", 55, 2, 1);
        match.RecentEvents = new List<TrayEventDto>
        {
            new TrayEventDto { Id = "e1", Type = "Goal", Minute = 14, PlayerName = "B. Saka" },
            new TrayEventDto { Id = "e2", Type = "Card", Minute = 22, PlayerName = "E. Fernandez" },
            new TrayEventDto { Id = "e3", Type = "Goal", Minute = 45, ExtraMinute = 2, PlayerName = "G. Martinelli" },
            new TrayEventDto { Id = "e4", Type = "Goal", Minute = 53, PlayerName = "C. Palmer" }
        };

        var goalscorers = match.FormattedGoalscorers;
        Assert.IsNotNull(goalscorers);
        Assert.AreEqual(3, goalscorers.Count);
        Assert.IsTrue(goalscorers[0].Contains("B. Saka 14'"));
        Assert.IsTrue(goalscorers[1].Contains("G. Martinelli 45+2'"));
        Assert.IsTrue(goalscorers[2].Contains("C. Palmer 53'"));
    }

    [TestMethod]
    public void Test23_TaskbarTickerPinButtonEventInterception_StructuralContractGuards()
    {
        string root = FindRepoRoot();
        string xamlPath = Path.Combine(root, "src", "client", "FootballTray.Client", "Taskbar", "TaskbarTicker.xaml");
        string csPath = Path.Combine(root, "src", "client", "FootballTray.Client", "Taskbar", "TaskbarTicker.xaml.cs");

        Assert.IsTrue(File.Exists(xamlPath), "TaskbarTicker.xaml must exist.");
        Assert.IsTrue(File.Exists(csPath), "TaskbarTicker.xaml.cs must exist.");

        string xaml = File.ReadAllText(xamlPath);
        string cs = File.ReadAllText(csPath);

        // 1. ToolTip must have ShowDuration="30000"
        Assert.IsTrue(xaml.Contains("ShowDuration=\"30000\""), "TaskbarTicker.xaml must specify ShowDuration=\"30000\" on ToolTip.");

        // 2. PinButton must be declared with click and tap handlers
        Assert.IsTrue(xaml.Contains("x:Name=\"PinButton\""), "TaskbarTicker.xaml must declare PinButton.");
        Assert.IsTrue(xaml.Contains("Click=\"PinButton_Click\""), "PinButton must wire Click to PinButton_Click.");
        Assert.IsTrue(xaml.Contains("Tapped=\"PinButton_Tapped\""), "PinButton must wire Tapped to PinButton_Tapped.");

        // 3. PinButton_Click and PinButton_Tapped must mark e.Handled = true
        Assert.IsTrue(cs.Contains("PinButton_Click"), "TaskbarTicker.xaml.cs must implement PinButton_Click.");
        Assert.IsTrue(cs.Contains("PinButton_Tapped"), "TaskbarTicker.xaml.cs must implement PinButton_Tapped.");
        Assert.IsTrue(cs.Contains("e.Handled = true;"), "Event handlers must set e.Handled = true to stop event bubbling.");

        // 4. PinToggled event must be declared
        Assert.IsTrue(cs.Contains("public event EventHandler<string?>? PinToggled;"), "TaskbarTicker must declare PinToggled event.");

        // 5. RootBorder_Tapped must verify not handled and guard against PinButton descendants
        Assert.IsTrue(cs.Contains("IsDescendantOf(orig, PinButton)"), "RootBorder_Tapped must intercept events originating from PinButton.");
    }
}
