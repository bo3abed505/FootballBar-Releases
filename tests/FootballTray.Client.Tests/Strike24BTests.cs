using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Taskbar;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

[TestClass]
public class Strike24BTests
{
    private sealed class MockTickerTimer : ITickerTimer
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(7);
        public bool IsRunning { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public event EventHandler? Tick;

        public void Start()
        {
            IsRunning = true;
            StartCount++;
        }

        public void Stop()
        {
            IsRunning = false;
            StopCount++;
        }

        public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Stop();
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private static TrayMatchDto CreateMatch(
        string id,
        string home,
        string away,
        string state = "LIVE",
        int minute = 15,
        int scoreHome = 0,
        int scoreAway = 0,
        string? homeTeamId = null,
        string? awayTeamId = null)
    {
        return new TrayMatchDto
        {
            Id = id,
            HomeTeam = new TeamSummaryDto
            {
                Id = homeTeamId ?? $"team_{home.ToLowerInvariant()}",
                Name = home,
                ShortName = home
            },
            AwayTeam = new TeamSummaryDto
            {
                Id = awayTeamId ?? $"team_{away.ToLowerInvariant()}",
                Name = away,
                ShortName = away
            },
            State = state,
            ElapsedMinute = minute,
            Score = new ScoreDto
            {
                Home = scoreHome,
                Away = scoreAway
            }
        };
    }

    private static NotificationCandidate CreateCandidate(NotificationType type, string fixtureId = "m1")
    {
        return new NotificationCandidate
        {
            FixtureId = fixtureId,
            Type = type,
            DeduplicationKey = $"match:{fixtureId}:{type}:{Guid.NewGuid():N}",
            NewState = "LIVE",
            HomeTeamName = "Arsenal",
            AwayTeamName = "Chelsea",
            HomeScore = 1,
            AwayScore = 0
        };
    }

    // =========================================================================
    // PHASE 1: Carousel Goal-Triggered Jump Tests
    // =========================================================================

    [TestMethod]
    public void Phase1_TickerRotationState_PromoteToTop_MovesCandidateToHead()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "Arsenal", "Chelsea");
        var m2 = CreateMatch("m2", "Liverpool", "Everton");
        var m3 = CreateMatch("m3", "Man City", "Man United");

        state.ApplyCandidates(new[] { m1, m2, m3 });
        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);

        // Promote m3 to top
        bool promoted = state.PromoteToTop("m3");
        Assert.IsTrue(promoted);
        Assert.AreEqual("m3", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);
        Assert.AreEqual("m3", state.Candidates[0].Id);
        Assert.AreEqual("m1", state.Candidates[1].Id);
        Assert.AreEqual("m2", state.Candidates[2].Id);

        // Next natural advance should move to m1 (index 1)
        var next = state.Advance();
        Assert.AreEqual("m1", next?.Id);
        Assert.AreEqual(1, state.CurrentIndex);
    }

    [TestMethod]
    public void Phase1_TickerRotationState_PromoteToTop_ReturnsFalseIfNotFound()
    {
        var state = new TickerRotationState();
        var m1 = CreateMatch("m1", "Arsenal", "Chelsea");
        state.ApplyCandidates(new[] { m1 });

        bool promoted = state.PromoteToTop("unknown_id");
        Assert.IsFalse(promoted);
        Assert.AreEqual("m1", state.CurrentCandidate?.Id);
    }

    [TestMethod]
    public void Phase1_GoalTriggeredJump_InterruptsTimerAndPromotesMatch()
    {
        var timer = new MockTickerTimer();
        string currentText = string.Empty;
        string? currentMatchId = null;

        var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) =>
            {
                currentMatchId = id;
                currentText = text;
            });

        var prefs = new UserPreferences
        {
            EnableTickerCarousel = true,
            FollowedTeams = new List<FollowedTeam>
            {
                new FollowedTeam { Id = "team_arsenal", Name = "Arsenal" },
                new FollowedTeam { Id = "team_liverpool", Name = "Liverpool" }
            }
        };

        // Poll 1: Initial state 0-0 for Arsenal vs Chelsea (m1, 25'), 0-0 for Liverpool vs Everton (m2, 20')
        var m1 = CreateMatch("m1", "Arsenal", "Chelsea", "LIVE", 25, 0, 0);
        var m2 = CreateMatch("m2", "Liverpool", "Everton", "LIVE", 20, 0, 0);

        var trayData1 = new TrayDataDto { Matches = new List<TrayMatchDto> { m1, m2 } };
        coordinator.ApplySnapshot(trayData1, TickerConnectionState.OnlineFresh, prefs);

        Assert.AreEqual("m1", currentMatchId);
        int initialStarts = timer.StartCount;
        int initialStops = timer.StopCount;

        // Poll 2: Liverpool (m2) scores! Score becomes 1-0.
        var m1Still00 = CreateMatch("m1", "Arsenal", "Chelsea", "LIVE", 26, 0, 0);
        var m2Goal = CreateMatch("m2", "Liverpool", "Everton", "LIVE", 21, 1, 0);

        var trayData2 = new TrayDataDto { Matches = new List<TrayMatchDto> { m1Still00, m2Goal } };
        coordinator.ApplySnapshot(trayData2, TickerConnectionState.OnlineFresh, prefs);

        // Verification:
        // 1. Goal jump match ID captured
        Assert.AreEqual("m2", coordinator.LastGoalJumpMatchId);

        // 2. Head of carousel promoted immediately to m2
        Assert.AreEqual("m2", currentMatchId);
        Assert.IsTrue(currentText.Contains("1–0") || currentText.Contains("1-0"), $"Expected score 1–0 in text: {currentText}");

        // 3. Timer was interrupted (stopped and restarted) to grant full display cycle
        Assert.IsTrue(timer.StopCount > initialStops, "Timer should have been stopped to interrupt cycle.");
        Assert.IsTrue(timer.StartCount > initialStarts, "Timer should have been restarted.");

        // 4. Next timer tick advances naturally to m1
        timer.FireTick();
        Assert.AreEqual("m1", currentMatchId);
    }

    [TestMethod]
    public void Phase1_UnfollowedMatchScoreChange_DoesNotTriggerGoalJump()
    {
        var timer = new MockTickerTimer();
        string? currentMatchId = null;
        var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => { currentMatchId = id; });

        var prefs = new UserPreferences
        {
            EnableTickerCarousel = true,
            FollowedTeams = new List<FollowedTeam>
            {
                new FollowedTeam { Id = "team_arsenal", Name = "Arsenal" }
            }
        };

        var m1Followed = CreateMatch("m1", "Arsenal", "Chelsea", "LIVE", 10, 0, 0);
        var m2Unfollowed = CreateMatch("m2", "PSG", "Monaco", "LIVE", 10, 0, 0);

        var tray1 = new TrayDataDto { Matches = new List<TrayMatchDto> { m1Followed, m2Unfollowed } };
        coordinator.ApplySnapshot(tray1, TickerConnectionState.OnlineFresh, prefs);

        // Unfollowed match PSG scores
        var m1Same = CreateMatch("m1", "Arsenal", "Chelsea", "LIVE", 11, 0, 0);
        var m2Scored = CreateMatch("m2", "PSG", "Monaco", "LIVE", 11, 1, 0);

        var tray2 = new TrayDataDto { Matches = new List<TrayMatchDto> { m1Same, m2Scored } };
        coordinator.ApplySnapshot(tray2, TickerConnectionState.OnlineFresh, prefs);

        Assert.IsNull(coordinator.LastGoalJumpMatchId, "Unfollowed match score change must not trigger goal jump.");
        Assert.AreEqual("m1", currentMatchId);
    }

    // =========================================================================
    // PHASE 2: Match Center Window Live Refresh Tests
    // =========================================================================

    [TestMethod]
    public async Task Phase2_MatchCenterViewModel_RefreshAsync_UpdatesScoreAndMinute()
    {
        var vm = new MatchCenterViewModel();
        var initialMatch = new MatchDetailDto
        {
            Id = "match_42",
            State = "LIVE",
            ElapsedMinute = 30,
            HomeTeam = new TeamSummaryDto { Name = "Real Madrid" },
            AwayTeam = new TeamSummaryDto { Name = "Barcelona" },
            Score = new ScoreDto { Home = 0, Away = 0 }
        };

        vm.Load(initialMatch);
        Assert.AreEqual("0 - 0", vm.ScoreText);
        Assert.AreEqual("LIVE 30'", vm.StatusText);

        // Mock API response with 1-0 at 35'
        var updatedMatch = new MatchDetailDto
        {
            Id = "match_42",
            State = "LIVE",
            ElapsedMinute = 35,
            HomeTeam = new TeamSummaryDto { Name = "Real Madrid" },
            AwayTeam = new TeamSummaryDto { Name = "Barcelona" },
            Score = new ScoreDto { Home = 1, Away = 0 }
        };

        var envelope = new ApiResponseEnvelope<MatchDetailDto> { Data = updatedMatch };
        string json = JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        using var client = new FootballApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        bool refreshed = await vm.RefreshAsync(client);

        Assert.IsTrue(refreshed);
        Assert.AreEqual("1 - 0", vm.ScoreText);
        Assert.AreEqual("LIVE 35'", vm.StatusText);
    }

    [TestMethod]
    public async Task Phase2_MatchCenterViewModel_RefreshAsync_HandlesNetworkFailureGracefully()
    {
        var vm = new MatchCenterViewModel();
        var initialMatch = new MatchDetailDto
        {
            Id = "match_42",
            State = "LIVE",
            ElapsedMinute = 30,
            HomeTeam = new TeamSummaryDto { Name = "Real Madrid" },
            AwayTeam = new TeamSummaryDto { Name = "Barcelona" },
            Score = new ScoreDto { Home = 0, Away = 0 }
        };

        vm.Load(initialMatch);

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new FootballApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4000") });

        bool refreshed = await vm.RefreshAsync(client);

        Assert.IsFalse(refreshed);
        Assert.AreEqual("0 - 0", vm.ScoreText, "Score should remain intact on refresh failure.");
    }

    // =========================================================================
    // PHASE 3: Granular Notification Settings Restoration Tests
    // =========================================================================

    [TestMethod]
    public void Phase3_NotificationPreferences_GranularProperties_DefaultToTrue()
    {
        var prefs = new NotificationPreferences();
        Assert.IsTrue(prefs.GlobalEnabled);
        Assert.IsTrue(prefs.GoalsEnabled);
        Assert.IsTrue(prefs.CardsEnabled);
        Assert.IsTrue(prefs.MatchStartEnabled);
        Assert.IsTrue(prefs.HalfTimeEnabled);
        Assert.IsTrue(prefs.FullTimeEnabled);
        Assert.IsTrue(prefs.PostponedEnabled);
        Assert.IsTrue(prefs.CancelledEnabled);
    }

    [TestMethod]
    public void Phase3_NotificationPreferences_WithTypeEnabled_ProducesUpdatedCopy()
    {
        var prefs = new NotificationPreferences();
        var updated = prefs.WithTypeEnabled(NotificationType.Goal, false);

        Assert.IsFalse(updated.GoalsEnabled);
        Assert.IsTrue(prefs.GoalsEnabled, "Original preferences object must remain immutable.");

        var cardsDisabled = updated.WithTypeEnabled(NotificationType.RedCard, false);
        Assert.IsFalse(cardsDisabled.CardsEnabled);
        Assert.IsFalse(cardsDisabled.GoalsEnabled);
    }

    [TestMethod]
    public void Phase3_NotificationsWatcher_StrictlyHonorsGranularChoices()
    {
        var basePrefs = new NotificationPreferences();

        var goalCandidate = CreateCandidate(NotificationType.Goal);
        var redCardCandidate = CreateCandidate(NotificationType.RedCard);
        var startCandidate = CreateCandidate(NotificationType.MatchStarted);
        var htCandidate = CreateCandidate(NotificationType.HalfTime);
        var ftCandidate = CreateCandidate(NotificationType.MatchFinished);

        // 1. All enabled by default
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(goalCandidate, basePrefs));
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(redCardCandidate, basePrefs));
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(startCandidate, basePrefs));
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(htCandidate, basePrefs));
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(ftCandidate, basePrefs));

        // 2. Disable Goals only
        var noGoals = basePrefs.WithTypeEnabled(NotificationType.Goal, false);
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(goalCandidate, noGoals), "Goals should be suppressed when disabled.");
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(redCardCandidate, noGoals), "Cards should still fire.");

        // 3. Disable Cards only
        var noCards = basePrefs.WithTypeEnabled(NotificationType.RedCard, false);
        Assert.IsTrue(NotificationsWatcher.ShouldFireNotification(goalCandidate, noCards));
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(redCardCandidate, noCards), "Cards should be suppressed when disabled.");

        // 4. Disable Match Start only
        var noStart = basePrefs.WithTypeEnabled(NotificationType.MatchStarted, false);
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(startCandidate, noStart));

        // 5. Disable HalfTime only
        var noHt = basePrefs.WithTypeEnabled(NotificationType.HalfTime, false);
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(htCandidate, noHt));

        // 6. Disable FullTime only
        var noFt = basePrefs.WithTypeEnabled(NotificationType.MatchFinished, false);
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(ftCandidate, noFt));

        // 7. Master toggle overrides everything
        var masterDisabled = basePrefs with { GlobalEnabled = false };
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(goalCandidate, masterDisabled));
        Assert.IsFalse(NotificationsWatcher.ShouldFireNotification(redCardCandidate, masterDisabled));
    }

    [TestMethod]
    public void Phase3_NotificationEngine_DetectsRedCards()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ft_test_red_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new NotificationDeduplicationStore(tempDir);
            var engine = new NotificationEngine(store);

            var previousMatch = CreateMatch("m1", "Arsenal", "Chelsea", "LIVE", 20, 0, 0);
            previousMatch.RecentEvents = new List<TrayEventDto>();

            var currentMatch = CreateMatch("m1", "Arsenal", "Chelsea", "LIVE", 25, 0, 0);
            currentMatch.RecentEvents = new List<TrayEventDto>
            {
                new TrayEventDto
                {
                    Type = "red_card",
                    PlayerName = "Saliba",
                    Minute = 24,
                    Description = "Direct red card for foul"
                }
            };

            var trayPrev = new TrayDataDto { Matches = new List<TrayMatchDto> { previousMatch } };
            var trayCurr = new TrayDataDto { Matches = new List<TrayMatchDto> { currentMatch } };

            var candidates = engine.EvaluateUpdates(trayPrev, trayCurr);

            Assert.IsTrue(candidates.Any(c => c.Type == NotificationType.RedCard), "Red card candidate must be generated.");
            var redCandidate = candidates.First(c => c.Type == NotificationType.RedCard);
            Assert.AreEqual("Saliba", redCandidate.EventPlayerName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    // =========================================================================
    // PHASE 4: Match Events Split ("Key Events" vs. "All") Tests
    // =========================================================================

    [TestMethod]
    public void Phase4_MatchCenter_SplitsKeyEventsFromAllEvents_AndSynthesizesMarkers()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_derby",
            State = "FINISHED",
            ElapsedMinute = 90,
            HomeTeam = new TeamSummaryDto { Name = "Arsenal" },
            AwayTeam = new TeamSummaryDto { Name = "Chelsea" },
            Score = new ScoreDto
            {
                Home = 2,
                Away = 1,
                HalfTime = new ScorePeriodDto { Home = 1, Away = 0 }
            },
            Events = new List<TrayEventDto>
            {
                new TrayEventDto { Minute = 12, Type = "foul", Description = "Foul by Enzo" },
                new TrayEventDto { Minute = 18, Type = "corner", Description = "Corner Arsenal" },
                new TrayEventDto { Minute = 23, Type = "goal", PlayerName = "Saka", Description = "Goal by Saka" },
                new TrayEventDto { Minute = 35, Type = "yellow_card", PlayerName = "Caicedo", Description = "Yellow card" },
                new TrayEventDto { Minute = 42, Type = "shot", Description = "Shot saved" },
                new TrayEventDto { Minute = 55, Type = "substitution", PlayerName = "Sterling", AssistPlayerName = "Mudryk", Description = "Sub" },
                new TrayEventDto { Minute = 70, Type = "red_card", PlayerName = "Jackson", Description = "Red card" },
                new TrayEventDto { Minute = 82, Type = "goal", PlayerName = "Havertz", Description = "Goal by Havertz" },
                new TrayEventDto { Minute = 88, Type = "missed_penalty", PlayerName = "Palmer", Description = "Penalty missed" }
            }
        };

        vm.Load(match);

        // 1. All Timeline items contains commentary, fouls, corners, shots, etc.
        Assert.IsTrue(vm.AllTimelineItems.Any(i => i.IconGlyph == "⚠️"), "All items must include fouls.");
        Assert.IsTrue(vm.AllTimelineItems.Any(i => i.IconGlyph == "🚩"), "All items must include corners.");
        Assert.IsTrue(vm.AllTimelineItems.Any(i => i.IconGlyph == "🎯"), "All items must include shots.");

        // 2. Key Timeline items strictly excludes minor fouls, corners, shots
        Assert.IsFalse(vm.KeyTimelineItems.Any(i => i.IconGlyph == "⚠️"), "Key items must NOT include fouls.");
        Assert.IsFalse(vm.KeyTimelineItems.Any(i => i.IconGlyph == "🚩"), "Key items must NOT include corners.");
        Assert.IsFalse(vm.KeyTimelineItems.Any(i => i.IconGlyph == "🎯"), "Key items must NOT include shots.");

        // 3. Key Timeline items includes core events: goals, cards, subs, missed pens
        Assert.IsTrue(vm.KeyTimelineItems.Any(i => i.IconGlyph == "⚽" && i.EventTitle.Contains("Saka")));
        Assert.IsTrue(vm.KeyTimelineItems.Any(i => i.IconGlyph == "🟨" && i.EventTitle.Contains("Caicedo")));
        Assert.IsTrue(vm.KeyTimelineItems.Any(i => i.IconGlyph == "🔄" && i.EventTitle.Contains("Sterling")));
        Assert.IsTrue(vm.KeyTimelineItems.Any(i => i.IconGlyph == "🟥" && i.EventTitle.Contains("Jackson")));
        Assert.IsTrue(vm.KeyTimelineItems.Any(i => i.IconGlyph == "❌" && i.EventTitle.Contains("Palmer")));

        // 4. Synthesized Match-State Transition Markers present in Key Events:
        // 0' Kickoff, HT, 45' Start 2nd Half, FT
        var kickoff = vm.KeyTimelineItems.FirstOrDefault(i => i.EventTitle == "Kickoff");
        Assert.IsNotNull(kickoff, "0' Kickoff marker must be synthesized.");
        Assert.AreEqual("0'", kickoff.MinuteDisplay);

        var ht = vm.KeyTimelineItems.FirstOrDefault(i => i.EventTitle == "Half-Time");
        Assert.IsNotNull(ht, "HT Half-Time marker must be synthesized.");
        Assert.AreEqual("HT", ht.MinuteDisplay);

        var start2H = vm.KeyTimelineItems.FirstOrDefault(i => i.EventTitle == "Start 2nd Half");
        Assert.IsNotNull(start2H, "45' Start 2nd Half marker must be synthesized.");
        Assert.AreEqual("45'", start2H.MinuteDisplay);

        var ft = vm.KeyTimelineItems.FirstOrDefault(i => i.EventTitle == "Full-Time");
        Assert.IsNotNull(ft, "FT Full-Time marker must be synthesized for concluded match.");
        Assert.AreEqual("FT", ft.MinuteDisplay);

        // 5. Reverse Chronological Sorting Verification:
        // Top should be FT (concluded), bottom should be Kickoff (0')
        Assert.AreEqual("Full-Time", vm.KeyTimelineItems.First().EventTitle, "Top event in Key Events must be FT.");
        Assert.AreEqual("Kickoff", vm.KeyTimelineItems.Last().EventTitle, "Bottom event in Key Events must be Kickoff.");
    }

    [TestMethod]
    public void Phase4_MatchCenter_TabSwitching_TogglesVisibleTimelineItems()
    {
        var vm = new MatchCenterViewModel();
        var match = new MatchDetailDto
        {
            Id = "m_test",
            State = "LIVE",
            ElapsedMinute = 25,
            HomeTeam = new TeamSummaryDto { Name = "Arsenal" },
            AwayTeam = new TeamSummaryDto { Name = "Chelsea" },
            Score = new ScoreDto { Home = 1, Away = 0 },
            Events = new List<TrayEventDto>
            {
                new TrayEventDto { Minute = 10, Type = "foul", Description = "Foul" },
                new TrayEventDto { Minute = 20, Type = "goal", PlayerName = "Saka", Description = "Goal" }
            }
        };

        vm.Load(match);

        // Default view: Key Events
        Assert.IsTrue(vm.IsKeyEventsViewSelected);
        Assert.IsFalse(vm.TimelineItems.Any(i => i.Description == "Foul"));
        Assert.IsTrue(vm.TimelineItems.Any(i => i.EventTitle.Contains("Saka")));

        // Switch to All Events
        vm.SetTimelineFilter(isKeyEvents: false);
        Assert.IsFalse(vm.IsKeyEventsViewSelected);
        Assert.IsTrue(vm.IsAllEventsViewSelected);
        Assert.IsTrue(vm.TimelineItems.Any(i => i.Description == "Foul"), "Fouls should be visible in All Events view.");

        // Switch back to Key Events
        vm.SetTimelineFilter(isKeyEvents: true);
        Assert.IsTrue(vm.IsKeyEventsViewSelected);
        Assert.IsFalse(vm.TimelineItems.Any(i => i.Description == "Foul"));
    }
}
