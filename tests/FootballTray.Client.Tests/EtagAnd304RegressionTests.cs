using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

[TestClass]
public class EtagAnd304RegressionTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTray304Test_" + Guid.NewGuid().ToString("N"));
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

    private static ApiResponseEnvelope<TrayDataDto> CreateFourMatchEnvelope(int version = 1)
    {
        return new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_304_test",
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
                        Id = "fix_1",
                        Competition = new CompetitionSummaryDto { Id = "comp_laliga", Name = "La Liga", Code = "PD" },
                        HomeTeam = new TeamSummaryDto { Id = "team_rso", Name = "Real Sociedad", ShortName = "RSO" },
                        AwayTeam = new TeamSummaryDto { Id = "team_cel", Name = "Celta Vigo", ShortName = "CEL" },
                        State = "LIVE",
                        ElapsedMinute = 45,
                        Score = new ScoreDto { Home = 1, Away = 0 }
                    },
                    new()
                    {
                        Id = "fix_2",
                        Competition = new CompetitionSummaryDto { Id = "comp_pl", Name = "Premier League", Code = "PL" },
                        HomeTeam = new TeamSummaryDto { Id = "team_ars", Name = "Arsenal", ShortName = "ARS" },
                        AwayTeam = new TeamSummaryDto { Id = "team_che", Name = "Chelsea", ShortName = "CHE" },
                        State = "PREMATCH",
                        KickoffTime = DateTimeOffset.UtcNow.AddHours(2),
                        Score = new ScoreDto { Home = 0, Away = 0 }
                    },
                    new()
                    {
                        Id = "fix_3",
                        Competition = new CompetitionSummaryDto { Id = "comp_seriea", Name = "Serie A", Code = "SA" },
                        HomeTeam = new TeamSummaryDto { Id = "team_int", Name = "Inter", ShortName = "INT" },
                        AwayTeam = new TeamSummaryDto { Id = "team_mil", Name = "Milan", ShortName = "MIL" },
                        State = "FINISHED",
                        Score = new ScoreDto { Home = 2, Away = 1 }
                    },
                    new()
                    {
                        Id = "fix_4",
                        Competition = new CompetitionSummaryDto { Id = "comp_ucl", Name = "Champions League", Code = "CL" },
                        HomeTeam = new TeamSummaryDto { Id = "team_rma", Name = "Real Madrid", ShortName = "RMA" },
                        AwayTeam = new TeamSummaryDto { Id = "team_bay", Name = "Bayern", ShortName = "BAY" },
                        State = "LIVE",
                        ElapsedMinute = 72,
                        Score = new ScoreDto { Home = 2, Away = 2 }
                    }
                }
            }
        };
    }

    private static string SerializeEnvelope(ApiResponseEnvelope<TrayDataDto> envelope) =>
        JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true });

    [TestMethod]
    public async Task Test01_Sequence_200_304_304_RemainsOnlineAndHealthy()
    {
        var envelope = CreateFourMatchEnvelope(version: 1);
        string initialEtag = "\"etag_v1_initial\"";
        int requestCount = 0;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    // 1st request: HTTP 200 OK with initial payload and ETag
                    var resp = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SerializeEnvelope(envelope), Encoding.UTF8, "application/json")
                    };
                    resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(initialEtag);
                    return resp;
                }

                // Subsequent requests: Verify If-None-Match header was sent by client
                Assert.IsNotNull(req.Headers.IfNoneMatch, "Client must send If-None-Match header on subsequent polls.");
                Assert.IsTrue(req.Headers.IfNoneMatch.ToString().Contains("etag_v1_initial"));

                // Return 304 Not Modified
                var notModResp = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModResp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(initialEtag);
                return notModResp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        var prefService = new PreferencesService(_tempDir);
        using var syncService = new SyncService(client, cacheService, preferencesService: prefService);
        var diagService = new DiagnosticsService(syncService, prefService, cacheService);
        diagService.RecordTaskbarStatus("Attached");

        // 1. Initial 200 sync
        var state1 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Fresh, state1);
        Assert.AreEqual(TickerConnectionState.OnlineFresh, syncService.CurrentState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(4, syncService.CurrentTrayData.Matches.Count);
        Assert.AreEqual(initialEtag, syncService.LastKnownETag);
        Assert.IsNotNull(syncService.LastSuccessfulServerResponseTime);

        // Verify initial diagnostics snapshot
        var snap1 = diagService.GetCurrentSnapshot();
        Assert.AreEqual(ApplicationHealthState.Healthy, snap1.HealthState);
        Assert.AreEqual(DataFreshnessState.Fresh, snap1.FreshnessState);

        // 2. Next poll: HTTP 304 Not Modified
        var beforePoll2 = DateTimeOffset.UtcNow.AddSeconds(-1);
        var state2 = await syncService.PollOnceAsync();
        var afterPoll2 = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.AreEqual(DataFreshnessState.NotModified, state2);
        Assert.AreEqual(TickerConnectionState.OnlineFresh, syncService.CurrentState, "304 response must preserve OnlineFresh ticker state.");
        Assert.IsNotNull(syncService.CurrentTrayData, "Cached payload must remain authoritative after 304.");
        Assert.AreEqual(4, syncService.CurrentTrayData.Matches.Count);
        Assert.AreEqual(initialEtag, syncService.LastKnownETag, "ETag must be preserved after 304.");
        Assert.IsNotNull(syncService.LastValidatedAt);
        Assert.IsTrue(syncService.LastValidatedAt >= beforePoll2 && syncService.LastValidatedAt <= afterPoll2);
        Assert.IsNotNull(syncService.LastSuccessfulServerResponseTime);
        Assert.IsTrue(syncService.LastSuccessfulServerResponseTime >= beforePoll2 && syncService.LastSuccessfulServerResponseTime <= afterPoll2);
        Assert.AreNotEqual(DataFreshnessState.Stale, syncService.FreshnessState, "Client must NOT enter Stale state after 304.");

        // Verify diagnostics snapshot remains Healthy
        var snap2 = diagService.GetCurrentSnapshot();
        Assert.AreEqual(ApplicationHealthState.Healthy, snap2.HealthState);
        Assert.AreEqual(DataFreshnessState.NotModified, snap2.FreshnessState);

        // 3. Subsequent poll: HTTP 304 Not Modified again
        var state3 = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.NotModified, state3);
        Assert.AreEqual(TickerConnectionState.OnlineFresh, syncService.CurrentState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(4, syncService.CurrentTrayData.Matches.Count);
        Assert.AreEqual(initialEtag, syncService.LastKnownETag);
        Assert.AreNotEqual(DataFreshnessState.Stale, syncService.FreshnessState);

        var snap3 = diagService.GetCurrentSnapshot();
        Assert.AreEqual(ApplicationHealthState.Healthy, snap3.HealthState);
        Assert.AreEqual(DataFreshnessState.NotModified, snap3.FreshnessState);
    }

    [TestMethod]
    public async Task Test02_SyncService_TrayUpdatedSubscriberFailure_IsIsolatedAndDoesNotTriggerOfflineFallback()
    {
        var envelope = CreateFourMatchEnvelope();
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var notModResp = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModResp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_test\"");
                return notModResp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(envelope, "\"etag_test\"");

        using var syncService = new SyncService(client, cacheService);
        await syncService.StartAsync(); // loads cache

        bool secondSubscriberCalled = false;

        // 1st subscriber intentionally throws an exception simulating UI thread marshaling failure
        syncService.TrayUpdated += (s, e) =>
        {
            throw new COMException("Simulated thread-affinity error", unchecked((int)0x8001010E));
        };

        // 2nd subscriber should still be called because PublishUpdate isolates each subscriber
        syncService.TrayUpdated += (s, e) =>
        {
            secondSubscriberCalled = true;
        };

        // Poll with 304 response
        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.NotModified, state, "Subscriber error must not cause SyncService to fall back to Stale.");
        Assert.AreNotEqual(DataFreshnessState.Stale, syncService.FreshnessState, "Client freshness state must remain NotModified.");
        Assert.AreEqual(TickerConnectionState.OnlineFresh, syncService.CurrentState);
        Assert.IsTrue(secondSubscriberCalled, "Secondary subscribers must still execute even if an earlier subscriber throws.");
    }

    [TestMethod]
    public async Task Test03_SecondaryCacheTouchFailure_On304_DoesNotMarkBackendOffline()
    {
        var envelope = CreateFourMatchEnvelope();
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
                notMod.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_v1\"");
                return notMod;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        // Cache service initialized in a read-only or invalid directory to simulate disk write failure
        var failingCache = new FailingTouchLocalCacheService(_tempDir);
        await failingCache.SaveCacheAsync(envelope, "\"etag_v1\"");

        using var syncService = new SyncService(client, failingCache);
        await syncService.StartAsync();

        // When 304 arrives, TouchLastValidatedAsync will throw an exception
        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.NotModified, state, "Cache persistence failure after 304 must not turn into network failure.");
        Assert.AreEqual(TickerConnectionState.OnlineFresh, syncService.CurrentState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(4, syncService.CurrentTrayData.Matches.Count);
    }

    [TestMethod]
    public void Test04_DiagnosticLogging_EmptyExceptionMessage_PreservesExceptionTypeAndHResult()
    {
        var comEx = new COMException(string.Empty, unchecked((int)0x8001010E));
        AppLogger.Warn("TestSubsystem", "TestComError", "WinUI cross-thread call failed", ex: comEx);

        var recent = AppLogger.GetRecentEntries();
        Assert.IsTrue(recent.Count > 0);

        var entry = recent[^1];
        Assert.AreEqual("COMException", entry.ExceptionType);
        Assert.IsNotNull(entry.ExceptionMessage);
        Assert.IsTrue(entry.ExceptionMessage.Contains("0x8001010E"), "ExceptionMessage must contain the hex HResult.");

        string formatted = entry.ToString();
        Assert.IsTrue(formatted.Contains("COMException"), "Formatted log line must display ExceptionType even when original ex.Message is blank.");
        Assert.IsTrue(formatted.Contains("0x8001010E"), "Formatted log line must display hex HResult.");
    }

    [TestMethod]
    public async Task Test05_MainViewModel_OnTrayUpdated_DispatchesSafelyAndUpdatesCollections()
    {
        var envelope = CreateFourMatchEnvelope();
        var prefService = new PreferencesService(_tempDir);
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SerializeEnvelope(envelope), Encoding.UTF8, "application/json")
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_vm_test\"");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService, preferencesService: prefService);

        using var vm = new MainViewModel(prefService, syncService);
        await vm.InitializeAsync();

        // 1. Initial 200 poll
        await syncService.PollOnceAsync();

        Assert.IsFalse(vm.IsOffline, "ViewModel must report online after successful initial sync.");
        Assert.AreEqual("Online (Live)", vm.ConnectionStatusText);
        Assert.AreEqual(4, vm.FilteredMatches.Count, "All 4 matches must be present in FilteredMatches.");
        Assert.IsTrue(vm.HasMatches);

        // 2. Next poll with 304 response
        mockHandler.ResponseFactory = (req) =>
        {
            var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
            notMod.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_vm_test\"");
            return notMod;
        };

        await syncService.PollOnceAsync();

        Assert.IsFalse(vm.IsOffline, "ViewModel must remain online after 304 revalidation.");
        Assert.AreEqual("Online (Live)", vm.ConnectionStatusText);
        Assert.AreEqual(4, vm.FilteredMatches.Count, "All 4 matches must remain present after 304.");
        Assert.IsTrue(vm.HasMatches);
    }

    private class FailingTouchLocalCacheService : LocalCacheService
    {
        public FailingTouchLocalCacheService(string customDirectory) : base(customDirectory) { }

        public override Task<bool> TouchLastValidatedAsync(string? etag, CancellationToken cancellationToken = default)
        {
            throw new IOException("Simulated disk I/O lock on cache metadata file.");
        }
    }
}
