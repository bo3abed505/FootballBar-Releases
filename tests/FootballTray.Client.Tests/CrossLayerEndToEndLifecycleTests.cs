using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

[TestClass]
public class CrossLayerEndToEndLifecycleTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTrayCrossLayerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private static ApiResponseEnvelope<TrayDataDto> CreateServerPayload(
        string state,
        int elapsedMinute,
        int extraMinute,
        int homeScore,
        int awayScore,
        List<TrayEventDto>? events = null,
        int version = 1,
        DateTimeOffset? kickoff = null)
    {
        return new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_cross_layer",
            Version = version,
            GeneratedAt = DateTimeOffset.UtcNow,
            ServerTime = DateTimeOffset.UtcNow,
            Stale = false,
            DataAgeSeconds = 0,
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "fix_arsenal_chelsea_001",
                        Competition = new CompetitionSummaryDto { Id = "comp_pl", Name = "Premier League", Code = "PL" },
                        HomeTeam = new TeamSummaryDto { Id = "team_ars", Name = "Arsenal FC", ShortName = "ARS" },
                        AwayTeam = new TeamSummaryDto { Id = "team_che", Name = "Chelsea FC", ShortName = "CHE" },
                        State = state,
                        ElapsedMinute = elapsedMinute,
                        ExtraMinute = extraMinute,
                        Score = new ScoreDto { Home = homeScore, Away = awayScore, Display = $"{homeScore} - {awayScore}" },
                        KickoffTime = kickoff ?? DateTimeOffset.UtcNow.AddMinutes(-elapsedMinute),
                        RecentEvents = events ?? new List<TrayEventDto>()
                    }
                }
            }
        };
    }

    private static string SerializeEnvelope(ApiResponseEnvelope<TrayDataDto> envelope) =>
        JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true });

    // ============================================================
    // 1. Cross-Layer State Propagation & Taskbar Formatting
    // ============================================================
    [TestMethod]
    public async Task Test01_PrematchState_PropagatesToClientAndFormatsWithoutLiveIndicator()
    {
        var kickoffTime = new DateTimeOffset(2026, 3, 1, 15, 0, 0, TimeSpan.Zero);
        var payload = CreateServerPayload("PREMATCH", 0, 0, 0, 0, null, 1, kickoffTime);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json"),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_prematch\"") }
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, state);
        Assert.AreEqual(1, syncService.CurrentTrayData?.Matches.Count);
        Assert.AreEqual("PREMATCH", syncService.CurrentTrayData?.Matches[0].State);

        var formatted = TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState);
        StringAssert.Contains(formatted, "ARS vs CHE");
        Assert.IsFalse(formatted.Contains('⚽'));
    }

    [TestMethod]
    public async Task Test02_LiveKickoffMinute1_PropagatesToClientAndFormatsWithElapsedMinute()
    {
        var payload = CreateServerPayload("LIVE", 1, 0, 0, 0, null, 2);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json"),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_live_1\"") }
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, state);
        var formatted = TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState);
        Assert.AreEqual("⚽ ARS 0–0 CHE 1'", formatted);
    }

    [TestMethod]
    public async Task Test03_ScoreChangeGoal1_0_ProducesNewRepresentationAndEmitsFreshUpdate()
    {
        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" }
        };
        var payload = CreateServerPayload("LIVE", 15, 0, 1, 0, events, 3);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json"),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_goal_1\"") }
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, state);
        Assert.AreEqual(1, syncService.CurrentTrayData?.Matches[0].Score.Home);
        Assert.AreEqual(0, syncService.CurrentTrayData?.Matches[0].Score.Away);

        var formatted = TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState);
        Assert.AreEqual("⚽ ARS 1–0 CHE 15'", formatted);
    }

    [TestMethod]
    public async Task Test04_NewEventArrival_AppearsInClientRecentEventsCollection()
    {
        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" },
            new() { Id = "evt_2", Type = "YellowCard", Minute = 28, TeamId = "team_che", PlayerName = "E. Fernandez" }
        };
        var payload = CreateServerPayload("LIVE", 30, 0, 1, 0, events, 4);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        await syncService.PollOnceAsync();

        var match = syncService.CurrentTrayData?.Matches[0];
        Assert.IsNotNull(match);
        Assert.AreEqual(2, match.RecentEvents.Count);
        Assert.AreEqual("B. Saka", match.RecentEvents[0].PlayerName);
        Assert.AreEqual("E. Fernandez", match.RecentEvents[1].PlayerName);
    }

    // ============================================================
    // 2. ETag / 304 Cross-Layer Protocol Negotiation
    // ============================================================
    [TestMethod]
    public async Task Test05_UnchangedData_Produces304AndNotModifiedState()
    {
        var payload = CreateServerPayload("LIVE", 30, 0, 1, 0);
        int requestCount = 0;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json"),
                        Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_match_30\"") }
                    };
                }

                // Verify client passes stored ETag in If-None-Match
                Assert.AreEqual("\"etag_match_30\"", req.Headers.IfNoneMatch.ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        // First poll: 200 OK
        var state1 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Fresh, state1);

        // Second poll: 304 Not Modified
        var state2 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.NotModified, state2);
        Assert.AreEqual(1, syncService.CurrentTrayData?.Matches[0].Score.Home);
    }

    [TestMethod]
    public async Task Test06_304Response_PreservesCachedPayloadAndETag()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var payload = CreateServerPayload("LIVE", 35, 0, 1, 0);
        await cacheService.SaveCacheAsync(payload, "\"etag_v35\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.NotModified)
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.NotModified, syncService.FreshnessState);
        Assert.AreEqual("\"etag_v35\"", syncService.LastKnownETag);
        Assert.AreEqual(1, syncService.CurrentTrayData?.Matches[0].Score.Home);
    }

    // ============================================================
    // 3. Offline & Backend Failure Recovery
    // ============================================================
    [TestMethod]
    public async Task Test07_BackendFailure_TransitionsToStaleWhilePreservingCachedMatchAndShowsOfflineBadge()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var payload = CreateServerPayload("LIVE", 40, 0, 1, 0);
        await cacheService.SaveCacheAsync(payload, "\"etag_v40\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("Backend unreachable")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Stale, syncService.FreshnessState);
        Assert.IsNotNull(syncService.CurrentTrayData);

        var formatted = TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState);
        Assert.AreEqual("⚽ ARS 1–0 CHE 40' · offline", formatted);
    }

    [TestMethod]
    public async Task Test08_BackendRecovery_TransitionsToFreshAndReplacesStaleData()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var initialPayload = CreateServerPayload("LIVE", 40, 0, 1, 0);
        await cacheService.SaveCacheAsync(initialPayload, "\"etag_old\"");

        bool backendIsUp = false;
        var recoveredPayload = CreateServerPayload("LIVE", 78, 0, 2, 1, null, 8);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                if (!backendIsUp)
                {
                    throw new HttpRequestException("Backend down");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SerializeEnvelope(recoveredPayload), Encoding.UTF8, "application/json"),
                    Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_recovered\"") }
                };
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        // 1. Startup during outage -> Stale
        await syncService.StartAsync();
        Assert.AreEqual(DataFreshnessState.Stale, syncService.FreshnessState);
        Assert.AreEqual("⚽ ARS 1–0 CHE 40' · offline", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // 2. Backend recovers -> Fresh 2-1 min 78
        backendIsUp = true;
        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, state);
        Assert.AreEqual(2, syncService.CurrentTrayData?.Matches[0].Score.Home);
        Assert.AreEqual(1, syncService.CurrentTrayData?.Matches[0].Score.Away);
        Assert.AreEqual("⚽ ARS 2–1 CHE 78'", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));
    }

    // ============================================================
    // 4. Restart Recovery & Terminal States
    // ============================================================
    [TestMethod]
    public async Task Test09_RestartRecovery_RestoresCachedPayloadAndETagAndRefreshesCleanly()
    {
        var cacheService1 = new LocalCacheService(_tempDir);
        await cacheService1.SaveCacheAsync(CreateServerPayload("LIVE", 60, 0, 1, 1, null, 6), "\"etag_restart_e2e\"");

        // Simulate new application startup
        var cacheService2 = new LocalCacheService(_tempDir);
        var updatedPayload = CreateServerPayload("LIVE", 70, 0, 2, 1, null, 7);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(updatedPayload), Encoding.UTF8, "application/json"),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_after_restart\"") }
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService2);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, syncService.FreshnessState);
        Assert.AreEqual(7, syncService.LastKnownVersion);
        Assert.AreEqual("\"etag_after_restart\"", syncService.LastKnownETag);
        Assert.AreEqual(2, syncService.CurrentTrayData?.Matches[0].Score.Home);
    }

    [TestMethod]
    public async Task Test10_FinishedState_ReachesClientAndRendersAsNonLive()
    {
        var payload = CreateServerPayload("FINISHED", 90, 4, 2, 1, null, 9);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, syncService.FreshnessState);
        Assert.AreEqual("FINISHED", syncService.CurrentTrayData?.Matches[0].State);

        var formatted = TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState);
        Assert.AreEqual("ARS 2–1 CHE FT", formatted);
        Assert.IsFalse(formatted.Contains('⚽'));
        Assert.IsFalse(formatted.Contains('\''));
    }

    [TestMethod]
    public async Task Test11_UnknownState_DoesNotRenderAsLive()
    {
        var payload = CreateServerPayload("POSTPONED", 0, 0, 0, 0);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(payload), Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        await syncService.PollOnceAsync();

        var formatted = TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState);
        Assert.IsFalse(formatted.Contains('⚽'));
        Assert.AreEqual("ARS 0–0 CHE", formatted);
    }

    // ============================================================
    // 5. Complete 8-Stage Match Lifecycle Simulation
    // ============================================================
    [TestMethod]
    public async Task Test12_FullMockLifecycle_Exercises8StagesDeterministically()
    {
        var cacheService = new LocalCacheService(_tempDir);
        ApiResponseEnvelope<TrayDataDto> currentServerPayload = CreateServerPayload("PREMATCH", 0, 0, 0, 0, null, 1);
        string currentEtag = "\"etag_1\"";

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(currentServerPayload), Encoding.UTF8, "application/json"),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(currentEtag) }
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        // Stage 1: PREMATCH (0-0)
        currentServerPayload = CreateServerPayload("PREMATCH", 0, 0, 0, 0, null, 1);
        currentEtag = "\"etag_stage1\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Fresh, syncService.FreshnessState);
        StringAssert.Contains(TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState), "ARS vs CHE");

        // Stage 2: LIVE 1' (0-0)
        currentServerPayload = CreateServerPayload("LIVE", 1, 0, 0, 0, null, 2);
        currentEtag = "\"etag_stage2\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("⚽ ARS 0–0 CHE 1'", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // Stage 3: LIVE 15' (1-0, Saka goal)
        var eventsStage3 = new List<TrayEventDto> { new() { Id = "e1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" } };
        currentServerPayload = CreateServerPayload("LIVE", 15, 0, 1, 0, eventsStage3, 3);
        currentEtag = "\"etag_stage3\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("⚽ ARS 1–0 CHE 15'", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // Stage 4: LIVE 38' (1-1, Palmer goal)
        var eventsStage4 = new List<TrayEventDto>
        {
            new() { Id = "e1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" },
            new() { Id = "e2", Type = "Goal", Minute = 38, TeamId = "team_che", PlayerName = "C. Palmer" }
        };
        currentServerPayload = CreateServerPayload("LIVE", 38, 0, 1, 1, eventsStage4, 4);
        currentEtag = "\"etag_stage4\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("⚽ ARS 1–1 CHE 38'", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // Stage 5: HALFTIME (1-1)
        currentServerPayload = CreateServerPayload("HALFTIME", 45, 0, 1, 1, eventsStage4, 5);
        currentEtag = "\"etag_stage5\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("⚽ ARS 1–1 CHE HT", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // Stage 6: LIVE 55' (1-1)
        currentServerPayload = CreateServerPayload("LIVE", 55, 0, 1, 1, eventsStage4, 6);
        currentEtag = "\"etag_stage6\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("⚽ ARS 1–1 CHE 55'", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // Stage 7: LIVE 78' (2-1, Martinelli goal)
        var eventsStage7 = new List<TrayEventDto>
        {
            new() { Id = "e1", Type = "Goal", Minute = 15, TeamId = "team_ars", PlayerName = "B. Saka" },
            new() { Id = "e2", Type = "Goal", Minute = 38, TeamId = "team_che", PlayerName = "C. Palmer" },
            new() { Id = "e3", Type = "Goal", Minute = 78, TeamId = "team_ars", PlayerName = "G. Martinelli" }
        };
        currentServerPayload = CreateServerPayload("LIVE", 78, 0, 2, 1, eventsStage7, 7);
        currentEtag = "\"etag_stage7\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("⚽ ARS 2–1 CHE 78'", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));

        // Stage 8: FINISHED (2-1, FT)
        currentServerPayload = CreateServerPayload("FINISHED", 90, 4, 2, 1, eventsStage7, 8);
        currentEtag = "\"etag_stage8\"";
        await syncService.PollOnceAsync();
        Assert.AreEqual("ARS 2–1 CHE FT", TrayTickerFormatter.FormatTicker(syncService.CurrentTrayData, syncService.FreshnessState));
    }

    [TestMethod]
    public async Task Test13_ETagCrossLayerSequence_200_304_200_304()
    {
        var cacheService = new LocalCacheService(_tempDir);
        int step = 0;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                step++;
                if (step == 1)
                {
                    // A: 200 + ETag E1
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SerializeEnvelope(CreateServerPayload("LIVE", 10, 0, 0, 0, null, 1)), Encoding.UTF8, "application/json"),
                        Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_E1\"") }
                    };
                }
                if (step == 2)
                {
                    // C: Unchanged -> 304
                    Assert.AreEqual("\"etag_E1\"", req.Headers.IfNoneMatch.ToString());
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }
                if (step == 3)
                {
                    // E: Score change -> 200 + ETag E2
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SerializeEnvelope(CreateServerPayload("LIVE", 25, 0, 1, 0, null, 2)), Encoding.UTF8, "application/json"),
                        Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_E2\"") }
                    };
                }
                // G: Unchanged -> 304 for E2
                Assert.AreEqual("\"etag_E2\"", req.Headers.IfNoneMatch.ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        // Step 1 -> Fresh (E1)
        var s1 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Fresh, s1);
        Assert.AreEqual("\"etag_E1\"", syncService.LastKnownETag);

        // Step 2 -> NotModified (E1)
        var s2 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.NotModified, s2);
        Assert.AreEqual("\"etag_E1\"", syncService.LastKnownETag);

        // Step 3 -> Fresh (E2)
        var s3 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Fresh, s3);
        Assert.AreEqual("\"etag_E2\"", syncService.LastKnownETag);

        // Step 4 -> NotModified (E2)
        var s4 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.NotModified, s4);
        Assert.AreEqual("\"etag_E2\"", syncService.LastKnownETag);
    }
}
