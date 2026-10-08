using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

public sealed class TestNotificationRenderer : INotificationRenderer
{
    public List<NotificationPayload> Rendered { get; } = new();

    public bool RenderNotification(NotificationPayload payload)
    {
        Rendered.Add(payload);
        return true;
    }
}

[TestClass]
public partial class WindowsNotificationEngineTests
{
    private string _tempDir = string.Empty;
    private TestNotificationRenderer _testRenderer = null!;
    private NotificationDeduplicationStore _store = null!;
    private NotificationEngine _engine = null!;
    private NotificationService _notificationService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTrayNotificationTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _testRenderer = new TestNotificationRenderer();
        _store = new NotificationDeduplicationStore(_tempDir);
        _engine = new NotificationEngine(_store);
        _notificationService = new NotificationService(_engine, _testRenderer, _store);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private static TrayDataDto CreateTrayData(
        string fixtureId,
        string state,
        int elapsedMinute,
        int homeScore,
        int awayScore,
        List<TrayEventDto>? events = null,
        string homeName = "Arsenal FC",
        string awayName = "Chelsea FC")
    {
        return new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = fixtureId,
                    Competition = new CompetitionSummaryDto { Id = "comp_pl", Name = "Premier League", Code = "PL" },
                    HomeTeam = new TeamSummaryDto { Id = "team_ars", Name = homeName, ShortName = "ARS" },
                    AwayTeam = new TeamSummaryDto { Id = "team_che", Name = awayName, ShortName = "CHE" },
                    State = state,
                    ElapsedMinute = elapsedMinute,
                    ExtraMinute = 0,
                    Score = new ScoreDto { Home = homeScore, Away = awayScore, Display = $"{homeScore} - {awayScore}" },
                    KickoffTime = DateTimeOffset.UtcNow,
                    RecentEvents = events ?? new List<TrayEventDto>()
                }
            }
        };
    }

    private static ApiResponseEnvelope<TrayDataDto> WrapEnvelope(TrayDataDto data, int version = 1)
    {
        return new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_test",
            Version = version,
            GeneratedAt = DateTimeOffset.UtcNow,
            ServerTime = DateTimeOffset.UtcNow,
            Data = data
        };
    }

    // ============================================================
    // Triggers & Transitions
    // ============================================================
    [TestMethod]
    public async Task Test01_PrematchToLive_ProducesMatchStartedOnce()
    {
        var prev = CreateTrayData("fix_001", "PREMATCH", 0, 0, 0);
        var curr = CreateTrayData("fix_001", "LIVE", 1, 0, 0);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.MatchStarted, alerts[0].Type);
        Assert.AreEqual("Arsenal FC vs Chelsea FC", alerts[0].Title);
        Assert.AreEqual("Match started", alerts[0].Body);
        Assert.AreEqual("match:fix_001:started", alerts[0].DeduplicationKey);
    }

    [TestMethod]
    public async Task Test02_RepeatedLivePolls_ProduceNoDuplicateMatchStarted()
    {
        var prev = CreateTrayData("fix_001", "PREMATCH", 0, 0, 0);
        var live1 = CreateTrayData("fix_001", "LIVE", 1, 0, 0);
        var live2 = CreateTrayData("fix_001", "LIVE", 2, 0, 0);

        var alerts1 = await _notificationService.ProcessTrayUpdateAsync(prev, live1);
        Assert.AreEqual(1, alerts1.Count);

        var alerts2 = await _notificationService.ProcessTrayUpdateAsync(live1, live2);
        Assert.AreEqual(0, alerts2.Count);
    }

    [TestMethod]
    public async Task Test03_NewGoalEvent_ProducesOneGoalAlert()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 14, 0, 0);
        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_saka", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" }
        };
        var curr = CreateTrayData("fix_001", "LIVE", 15, 1, 0, events);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.Goal, alerts[0].Type);
        Assert.AreEqual("Arsenal FC 1–0 Chelsea FC", alerts[0].Title);
        Assert.AreEqual("15' B. Saka", alerts[0].Body);
        Assert.AreEqual("match:fix_001:event:evt_goal_saka", alerts[0].DeduplicationKey);
        StringAssert.Contains(alerts[0].ActivationArguments, "eventUid=evt_goal_saka");
    }

    [TestMethod]
    public async Task Test04_RepeatedPollingOfSameGoalEvent_ProducesZeroDuplicates()
    {
        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_saka", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" }
        };
        var snap1 = CreateTrayData("fix_001", "LIVE", 15, 1, 0, events);
        var snap2 = CreateTrayData("fix_001", "LIVE", 16, 1, 0, events);

        var alerts1 = await _notificationService.ProcessTrayUpdateAsync(CreateTrayData("fix_001", "LIVE", 14, 0, 0), snap1);
        Assert.AreEqual(1, alerts1.Count);

        var alerts2 = await _notificationService.ProcessTrayUpdateAsync(snap1, snap2);
        Assert.AreEqual(0, alerts2.Count);
    }

    [TestMethod]
    public async Task Test05_TwoDistinctGoals_ProduceTwoDistinctNotifications()
    {
        var e1 = new TrayEventDto { Id = "evt_1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" };
        var e2 = new TrayEventDto { Id = "evt_2", Type = "Goal", Minute = 38, TeamId = "team_che", PlayerName = "C. Palmer" };

        var snap1 = CreateTrayData("fix_001", "LIVE", 15, 1, 0, new List<TrayEventDto> { e1 });
        var snap2 = CreateTrayData("fix_001", "LIVE", 38, 1, 1, new List<TrayEventDto> { e1, e2 });

        await _notificationService.ProcessTrayUpdateAsync(CreateTrayData("fix_001", "LIVE", 10, 0, 0), snap1);
        var alerts = await _notificationService.ProcessTrayUpdateAsync(snap1, snap2);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual("Arsenal FC 1–1 Chelsea FC", alerts[0].Title);
        Assert.AreEqual("38' C. Palmer", alerts[0].Body);
    }

    [TestMethod]
    public async Task Test06_LiveToHalftime_ProducesOneHalftimeAlert()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 45, 1, 1);
        var curr = CreateTrayData("fix_001", "HALFTIME", 45, 1, 1);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.HalfTime, alerts[0].Type);
        Assert.AreEqual("Arsenal FC 1–1 Chelsea FC", alerts[0].Title);
        Assert.AreEqual("Half-time", alerts[0].Body);
    }

    [TestMethod]
    public async Task Test07_RepeatedHalftimePolling_ProducesNone()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 45, 1, 1);
        var ht1 = CreateTrayData("fix_001", "HALFTIME", 45, 1, 1);
        var ht2 = CreateTrayData("fix_001", "HALFTIME", 45, 1, 1);

        await _notificationService.ProcessTrayUpdateAsync(prev, ht1);
        var alerts = await _notificationService.ProcessTrayUpdateAsync(ht1, ht2);

        Assert.AreEqual(0, alerts.Count);
    }

    [TestMethod]
    public async Task Test08_FinishedTransition_ProducesOneMatchFinishedAlert()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 90, 2, 1);
        var curr = CreateTrayData("fix_001", "FINISHED", 90, 2, 1);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.MatchFinished, alerts[0].Type);
        Assert.AreEqual("Arsenal FC 2–1 Chelsea FC", alerts[0].Title);
        Assert.AreEqual("Full-time", alerts[0].Body);
    }

    [TestMethod]
    public async Task Test09_RepeatedFinishedPolling_ProducesNone()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 90, 2, 1);
        var ft1 = CreateTrayData("fix_001", "FINISHED", 90, 2, 1);
        var ft2 = CreateTrayData("fix_001", "FINISHED", 90, 2, 1);

        await _notificationService.ProcessTrayUpdateAsync(prev, ft1);
        var alerts = await _notificationService.ProcessTrayUpdateAsync(ft1, ft2);

        Assert.AreEqual(0, alerts.Count);
    }

    [TestMethod]
    public async Task Test10_PostponedTransition_ProducesOneAlert()
    {
        var prev = CreateTrayData("fix_001", "PREMATCH", 0, 0, 0);
        var curr = CreateTrayData("fix_001", "POSTPONED", 0, 0, 0);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.MatchPostponed, alerts[0].Type);
        Assert.AreEqual("Match postponed", alerts[0].Body);
    }

    [TestMethod]
    public async Task Test11_CancelledTransition_ProducesOneAlert()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 20, 0, 0);
        var curr = CreateTrayData("fix_001", "CANCELLED", 20, 0, 0);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.MatchCancelled, alerts[0].Type);
        Assert.AreEqual("Match cancelled", alerts[0].Body);
    }

    [TestMethod]
    public async Task Test12_MinuteOnlyChange_ProducesNoAlert()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 55, 1, 1);
        var curr = CreateTrayData("fix_001", "LIVE", 56, 1, 1);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(0, alerts.Count);
    }

    [TestMethod]
    public async Task Test13_LastPolledAtOnlyChange_ProducesNoAlert()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 60, 1, 1);
        var curr = CreateTrayData("fix_001", "LIVE", 60, 1, 1);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(0, alerts.Count);
    }

    // ============================================================
    // HTTP Integration, Offline & Restart
    // ============================================================
    [TestMethod]
    public async Task Test14_Http304_ProducesNoAlert()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var payload = WrapEnvelope(CreateTrayData("fix_001", "LIVE", 20, 1, 0));
        await cacheService.SaveCacheAsync(payload, "\"etag_1\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.NotModified)
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, _notificationService);

        await syncService.StartAsync();

        Assert.AreEqual(0, _testRenderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test15_StaleCachedStartup_ProducesNoAlert()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var payload = WrapEnvelope(CreateTrayData("fix_001", "FINISHED", 90, 3, 2));
        await cacheService.SaveCacheAsync(payload, "\"etag_old\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("Offline")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, _notificationService);

        await syncService.StartAsync();

        Assert.AreEqual(0, _testRenderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test16_Restart_DoesNotReplayPriorAlerts()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" }
        };
        var liveData = CreateTrayData("fix_001", "LIVE", 15, 1, 0, events);
        await cacheService.SaveCacheAsync(WrapEnvelope(liveData), "\"etag_live\"");

        // Simulate new application instance
        var store2 = new NotificationDeduplicationStore(_tempDir);
        var renderer2 = new TestNotificationRenderer();
        var notifService2 = new NotificationService(new NotificationEngine(store2), renderer2, store2);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(WrapEnvelope(liveData, 2)), Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, notifService2);

        await syncService.StartAsync();

        Assert.AreEqual(0, renderer2.Rendered.Count);
    }

    [TestMethod]
    public async Task Test17_NotificationDeduplicationState_SurvivesRestart()
    {
        _store.MarkNotified("match:fix_001:started");
        _store.MarkNotified("match:fix_001:event:e1");
        await _store.SaveAsync();

        var store2 = new NotificationDeduplicationStore(_tempDir);
        await store2.LoadAsync();

        Assert.IsTrue(store2.IsAlreadyNotified("match:fix_001:started"));
        Assert.IsTrue(store2.IsAlreadyNotified("match:fix_001:event:e1"));
        Assert.IsFalse(store2.IsAlreadyNotified("match:fix_001:event:e2"));
    }

    [TestMethod]
    public async Task Test18_CorruptNotificationState_FailsSafely()
    {
        var filePath = Path.Combine(_tempDir, "notifications_history.json");
        await File.WriteAllTextAsync(filePath, "{ invalid corrupted json !!!");

        var store = new NotificationDeduplicationStore(_tempDir);
        await store.LoadAsync();

        Assert.AreEqual(0, store.Count);
        Assert.IsFalse(store.IsAlreadyNotified("any_key"));
    }

    [TestMethod]
    public async Task Test19_AtomicNotificationStateReplacement_PreservesPriorValidState()
    {
        _store.MarkNotified("key_1");
        await _store.SaveAsync();

        var store2 = new NotificationDeduplicationStore(_tempDir);
        await store2.LoadAsync();
        Assert.IsTrue(store2.IsAlreadyNotified("key_1"));

        _store.MarkNotified("key_2");
        await _store.SaveAsync();

        var store3 = new NotificationDeduplicationStore(_tempDir);
        await store3.LoadAsync();
        Assert.IsTrue(store3.IsAlreadyNotified("key_1"));
        Assert.IsTrue(store3.IsAlreadyNotified("key_2"));
    }

    [TestMethod]
    public async Task Test20_NewlyConfirmedPostOfflineGoal_ProducesExactlyOneAlert()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var initialData = CreateTrayData("fix_001", "LIVE", 10, 0, 0);
        await cacheService.SaveCacheAsync(WrapEnvelope(initialData), "\"etag_0\"");

        bool isOnline = false;
        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_78", Type = "Goal", Minute = 78, TeamId = "team_ars", PlayerName = "G. Martinelli" }
        };
        var updatedData = CreateTrayData("fix_001", "LIVE", 78, 1, 0, events);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                if (!isOnline) throw new HttpRequestException("Backend offline");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(WrapEnvelope(updatedData, 3)), Encoding.UTF8, "application/json"),
                    Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_recovered\"") }
                };
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, _notificationService);

        // 1. Start during offline period
        await syncService.StartAsync();
        Assert.AreEqual(0, _testRenderer.Rendered.Count);

        // 2. Recovery: goal scored while offline
        isOnline = true;
        await syncService.PollOnceAsync();

        Assert.AreEqual(1, _testRenderer.Rendered.Count);
        Assert.AreEqual(NotificationType.Goal, _testRenderer.Rendered[0].Type);
        Assert.AreEqual("Arsenal FC 1–0 Chelsea FC", _testRenderer.Rendered[0].Title);
        Assert.AreEqual("78' G. Martinelli", _testRenderer.Rendered[0].Body);
    }

    [TestMethod]
    public async Task Test21a_MultipleConcurrentEvents_ProcessesAllLegitimateCandidates()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 10, 0, 0);
        var fifteenEvents = Enumerable.Range(1, 15).Select(i => new TrayEventDto
        {
            Id = $"evt_legit_{i}",
            Type = "Goal",
            Minute = i + 10,
            TeamId = "team_ars",
            PlayerName = $"Player {i}"
        }).ToList();

        var curr = CreateTrayData("fix_001", "LIVE", 30, 15, 0, fifteenEvents);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        // Normal operating changes are all processed (not truncated at 10)
        Assert.AreEqual(15, alerts.Count);
    }

    [TestMethod]
    public async Task Test21b_PathologicalEventStorm_EnforcesDefensiveCapAndDeduplicatesExcess()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 10, 0, 0);
        var stormEvents = Enumerable.Range(1, 70).Select(i => new TrayEventDto
        {
            Id = $"evt_storm_{i}",
            Type = "Goal",
            Minute = i,
            TeamId = "team_ars",
            PlayerName = $"Player {i}"
        }).ToList();

        var curr = CreateTrayData("fix_001", "LIVE", 90, 70, 0, stormEvents);

        var alerts = await _notificationService.ProcessTrayUpdateAsync(prev, curr);

        // Defensive threshold limits immediate dispatch to 50
        Assert.AreEqual(50, alerts.Count);

        // All 70 are marked in the deduplication store so none are replayed
        Assert.IsTrue(_store.IsAlreadyNotified("match:fix_001:event:evt_storm_1"));
        Assert.IsTrue(_store.IsAlreadyNotified("match:fix_001:event:evt_storm_60"));
        Assert.IsTrue(_store.IsAlreadyNotified("match:fix_001:event:evt_storm_70"));
    }

    [TestMethod]
    public async Task Test22_DistinctFixtures_ProduceIndependentNotificationIds()
    {
        var prev1 = CreateTrayData("fix_ars_che", "PREMATCH", 0, 0, 0, null, "Arsenal", "Chelsea");
        var curr1 = CreateTrayData("fix_ars_che", "LIVE", 1, 0, 0, null, "Arsenal", "Chelsea");

        var prev2 = CreateTrayData("fix_liv_mci", "PREMATCH", 0, 0, 0, null, "Liverpool", "Man City");
        var curr2 = CreateTrayData("fix_liv_mci", "LIVE", 1, 0, 0, null, "Liverpool", "Man City");

        var combinedPrev = new TrayDataDto { Matches = new List<TrayMatchDto> { prev1.Matches[0], prev2.Matches[0] } };
        var combinedCurr = new TrayDataDto { Matches = new List<TrayMatchDto> { curr1.Matches[0], curr2.Matches[0] } };

        var alerts = await _notificationService.ProcessTrayUpdateAsync(combinedPrev, combinedCurr);

        Assert.AreEqual(2, alerts.Count);
        Assert.AreEqual("match:fix_ars_che:started", alerts[0].DeduplicationKey);
        Assert.AreEqual("match:fix_liv_mci:started", alerts[1].DeduplicationKey);
    }

    [TestMethod]
    public async Task Test23_GoalAlert_UsesEventUidRatherThanScoreOnlyDetection()
    {
        var prev = CreateTrayData("fix_001", "LIVE", 10, 0, 0);

        // Score changed to 1-0 but NO new event provided (e.g. score correction)
        var scoreOnlyChange = CreateTrayData("fix_001", "LIVE", 11, 1, 0, new List<TrayEventDto>());
        var alerts1 = await _notificationService.ProcessTrayUpdateAsync(prev, scoreOnlyChange);

        Assert.AreEqual(0, alerts1.Count);

        // Now event arrives
        var eventArrival = CreateTrayData("fix_001", "LIVE", 12, 1, 0, new List<TrayEventDto>
        {
            new() { Id = "evt_saka_real", Type = "Goal", Minute = 10, PlayerName = "B. Saka" }
        });
        var alerts2 = await _notificationService.ProcessTrayUpdateAsync(scoreOnlyChange, eventArrival);

        Assert.AreEqual(1, alerts2.Count);
        Assert.AreEqual("match:fix_001:event:evt_saka_real", alerts2[0].DeduplicationKey);
    }

    [TestMethod]
    public void Test24_WindowsNotificationRendering_ReceivesCorrectNormalizedPayload()
    {
        var candidate = new NotificationCandidate
        {
            FixtureId = "fix_999",
            Type = NotificationType.Goal,
            DeduplicationKey = "match:fix_999:event:e99",
            EventUid = "e99",
            NewState = "LIVE",
            HomeTeamName = "Arsenal FC",
            AwayTeamName = "Chelsea FC",
            HomeScore = 2,
            AwayScore = 1,
            ElapsedMinute = 78,
            ExtraMinute = 0,
            EventPlayerName = "G. Martinelli"
        };

        var payload = NotificationPayloadFormatter.Format(candidate);

        Assert.AreEqual("Arsenal FC 2–1 Chelsea FC", payload.Title);
        Assert.AreEqual("78' G. Martinelli", payload.Body);
        Assert.AreEqual("action=viewMatch&fixtureId=fix_999&eventUid=e99", payload.ActivationArguments);
    }

    [TestMethod]
    public void Test25_MalformedNotificationActivationInput_FailsSafely()
    {
        var res1 = NotificationActivationHandler.ParseQueryString(null);
        Assert.IsFalse(res1.IsValid);

        var res2 = NotificationActivationHandler.ParseQueryString("");
        Assert.IsFalse(res2.IsValid);

        var res3 = NotificationActivationHandler.ParseQueryString("corrupted&&&==invalid");
        Assert.IsFalse(res3.IsValid);

        var res4 = NotificationActivationHandler.ParseQueryString("action=viewMatch&fixtureId=fix_123&eventUid=evt_456");
        Assert.IsTrue(res4.IsValid);
        Assert.AreEqual("viewMatch", res4.Action);
        Assert.AreEqual("fix_123", res4.FixtureId);
        Assert.AreEqual("evt_456", res4.EventUid);

        // Test IDictionary activation overload as used by Windows App SDK
        var dict1 = new Dictionary<string, string>
        {
            ["action"] = "viewMatch",
            ["fixtureId"] = "fix_789",
            ["eventUid"] = "evt_999"
        };
        var res5 = NotificationActivationHandler.ParseDictionary(dict1);
        Assert.IsTrue(res5.IsValid);
        Assert.AreEqual("viewMatch", res5.Action);
        Assert.AreEqual("fix_789", res5.FixtureId);
        Assert.AreEqual("evt_999", res5.EventUid);

        var res6 = NotificationActivationHandler.ParseDictionary(null);
        Assert.IsFalse(res6.IsValid);
    }

    [TestMethod]
    public void Test26_WindowsAppNotificationRenderer_ConstructsPayloadSafely()
    {
        var renderer = new WindowsAppNotificationRenderer(() => null!);
        var payload = new NotificationPayload
        {
            Title = "Arsenal FC 1–0 Chelsea FC",
            Body = "15' B. Saka",
            ActivationArguments = "action=viewMatch&fixtureId=fix_001",
            DeduplicationKey = "match:fix_001:event:e1",
            Type = NotificationType.Goal,
            FixtureId = "fix_001"
        };

        // In test runner with isolated null managerProvider, RenderNotification catches and safely returns false without invoking real Windows toast
        bool rendered = renderer.RenderNotification(payload);
        Assert.IsFalse(rendered);
        Assert.IsNotNull(payload.Title);
        Assert.IsNotNull(payload.Body);
    }

    // ============================================================
    // Registration Lifecycle Tests
    // ============================================================
    [TestMethod]
    public void Test27_AppNotificationRegistration_SuccessfulRegistrationSetsStateAndAttachesHandler()
    {
        var bridge = new TestAppNotificationManagerBridge();
        var regManager = new AppNotificationRegistrationManager(bridge);

        bool success = regManager.Initialize(_ => { });

        Assert.IsTrue(success);
        Assert.IsTrue(regManager.IsRegistered);
        Assert.IsTrue(regManager.IsHandlerAttached);
        Assert.AreEqual(1, bridge.RegisterCallCount);
    }

    [TestMethod]
    public void Test28_AppNotificationRegistration_DuplicateInitializationDoesNotDoubleRegister()
    {
        var bridge = new TestAppNotificationManagerBridge();
        var regManager = new AppNotificationRegistrationManager(bridge);

        regManager.Initialize(_ => { });
        regManager.Initialize(_ => { }); // Second call

        Assert.IsTrue(regManager.IsRegistered);
        Assert.AreEqual(1, bridge.RegisterCallCount); // Did not double-register
    }

    [TestMethod]
    public void Test29_AppNotificationRegistration_RegistrationFailureDoesNotMarkRegistered()
    {
        var bridge = new TestAppNotificationManagerBridge { ShouldThrowOnRegister = true };
        var regManager = new AppNotificationRegistrationManager(bridge);

        bool success = regManager.Initialize(_ => { });

        Assert.IsFalse(success);
        Assert.IsFalse(regManager.IsRegistered);
        Assert.IsFalse(regManager.IsHandlerAttached);
    }

    [TestMethod]
    public void Test30_AppNotificationRegistration_RegistrationFailureCleansUpHandlerSafely()
    {
        var bridge = new TestAppNotificationManagerBridge { ShouldThrowOnRegister = true };
        var regManager = new AppNotificationRegistrationManager(bridge);

        regManager.Initialize(_ => { });

        // Handler was detached during rollback
        Assert.IsFalse(regManager.IsHandlerAttached);
        Assert.AreEqual(0, bridge.ActiveSubscribersCount);
    }

    [TestMethod]
    public void Test31_AppNotificationRegistration_SuccessfulUnregisterSetsStateAndDetachesHandler()
    {
        var bridge = new TestAppNotificationManagerBridge();
        var regManager = new AppNotificationRegistrationManager(bridge);

        regManager.Initialize(_ => { });
        Assert.IsTrue(regManager.IsRegistered);

        bool unregSuccess = regManager.Unregister();

        Assert.IsTrue(unregSuccess);
        Assert.IsFalse(regManager.IsRegistered);
        Assert.IsFalse(regManager.IsHandlerAttached);
        Assert.AreEqual(1, bridge.UnregisterCallCount);
        Assert.AreEqual(0, bridge.ActiveSubscribersCount);
    }

    [TestMethod]
    public void Test32_AppNotificationRegistration_DuplicateUnregisterIsSafe()
    {
        var bridge = new TestAppNotificationManagerBridge();
        var regManager = new AppNotificationRegistrationManager(bridge);

        regManager.Initialize(_ => { });
        regManager.Unregister();
        bool secondUnreg = regManager.Unregister(); // Duplicate

        Assert.IsTrue(secondUnreg);
        Assert.IsFalse(regManager.IsRegistered);
        Assert.AreEqual(1, bridge.UnregisterCallCount);
    }

    [TestMethod]
    public void Test33_AppNotificationRegistration_NotificationInvokedRoutesToActivationCallback()
    {
        var bridge = new TestAppNotificationManagerBridge();
        var regManager = new AppNotificationRegistrationManager(bridge);

        NotificationActivationResult? receivedResult = null;
        regManager.Initialize(res => { receivedResult = res; });

        // Simulate Windows App SDK notification invocation
        bridge.SimulateInvoked("action=viewMatch&fixtureId=fix_live_99&eventUid=evt_goal_99");

        Assert.IsNotNull(receivedResult);
        Assert.IsTrue(receivedResult.IsValid);
        Assert.AreEqual("viewMatch", receivedResult.Action);
        Assert.AreEqual("fix_live_99", receivedResult.FixtureId);
        Assert.AreEqual("evt_goal_99", receivedResult.EventUid);
    }
}

public sealed class TestAppNotificationManagerBridge : IAppNotificationManagerBridge
{
    public event EventHandler<string>? NotificationInvoked;
    public int RegisterCallCount { get; private set; }
    public int UnregisterCallCount { get; private set; }
    public bool ShouldThrowOnRegister { get; set; }
    public bool ShouldThrowOnUnregister { get; set; }
    public bool IsSupported => true;

    public int ActiveSubscribersCount => NotificationInvoked?.GetInvocationList().Length ?? 0;

    public void Register()
    {
        RegisterCallCount++;
        if (ShouldThrowOnRegister)
        {
            throw new InvalidOperationException("Simulated AppNotificationManager registration failure.");
        }
    }

    public void Unregister()
    {
        UnregisterCallCount++;
        if (ShouldThrowOnUnregister)
        {
            throw new InvalidOperationException("Simulated AppNotificationManager unregistration failure.");
        }
    }

    public void SimulateInvoked(string rawArgs)
    {
        NotificationInvoked?.Invoke(this, rawArgs);
    }
}

public partial class WindowsNotificationEngineTests
{
    [TestMethod]
    public void Test_EvaluateMatch_WhenPrevIsNullAndMatchIsFinished_SuppressesGhostNotifications()
    {
        var finishedMatch = new TrayMatchDto
        {
            Id = "fix_moawad_finished",
            State = "FT",
            Score = new ScoreDto { Home = 2, Away = 1 },
            RecentEvents = new List<TrayEventDto>
            {
                new() { Id = "evt_goal_1", Type = "Goal", Minute = 86, PlayerName = "O. Moawad" }
            }
        };

        var candidates = _engine.EvaluateMatch(null, finishedMatch);
        Assert.AreEqual(0, candidates.Count, "Should completely suppress ghost notifications when prev is null and match state is FT/FINISHED");
    }

    [TestMethod]
    public void Test_EvaluateMatch_WhenPrevIsNullAndMatchIsPostponedPST_SuppressesGhostNotifications()
    {
        var pstMatch = new TrayMatchDto
        {
            Id = "fix_pst_match",
            State = "PST",
            Score = new ScoreDto { Home = 0, Away = 0 },
            RecentEvents = new List<TrayEventDto>
            {
                new() { Id = "evt_card_1", Type = "Card", Detail = "Red Card", Minute = 12, PlayerName = "J. Smith" }
            }
        };

        var candidates = _engine.EvaluateMatch(null, pstMatch);
        Assert.AreEqual(0, candidates.Count, "Should completely suppress ghost notifications when prev is null and match state is PST");
    }
    [TestMethod]
    public void Test34_RealWindowsAppNotificationSmoke_AttemptShow()
    {
        // Strictly isolated mock bridge and renderer to prevent real Windows toast notifications from firing during dotnet test
        var bridge = new TestAppNotificationManagerBridge();
        Assert.IsTrue(bridge.IsSupported);

        var regManager = new AppNotificationRegistrationManager(bridge);
        bool regSuccess = regManager.Initialize(activation =>
        {
            Console.WriteLine($"[TEST34] Notification invoked callback received: {activation.Action}");
        });
        Assert.IsTrue(regSuccess);

        var renderer = new WindowsAppNotificationRenderer(() => null!);
        var payload = new NotificationPayload
        {
            Title = "Arsenal FC 1–0 Chelsea FC",
            Body = "15' B. Saka",
            ActivationArguments = "action=viewMatch&fixtureId=fix_smoke_001&eventUid=evt_smoke_1",
            DeduplicationKey = "match:fix_smoke_001:event:evt_smoke_1",
            Type = NotificationType.Goal,
            FixtureId = "fix_smoke_001"
        };

        bool showResult = renderer.RenderNotification(payload);
        Assert.IsFalse(showResult);

        regManager.Unregister();
    }
}


