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
public class ProductionClientDeliveryAndOfflineConsistencyTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTrayWP4Test_" + Guid.NewGuid().ToString("N"));
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

    private static ApiResponseEnvelope<TrayDataDto> CreateSampleEnvelope(int version = 1, string homeScore = "1", string awayScore = "0")
    {
        return new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_wp4_test",
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
                        Id = "fix_wp4_arsenal_chelsea",
                        Competition = new CompetitionSummaryDto { Id = "comp_pl", Name = "Premier League", Code = "PL" },
                        HomeTeam = new TeamSummaryDto { Id = "team_ars", Name = "Arsenal FC", ShortName = "ARS" },
                        AwayTeam = new TeamSummaryDto { Id = "team_che", Name = "Chelsea FC", ShortName = "CHE" },
                        State = "LIVE",
                        ElapsedMinute = 35,
                        ExtraMinute = 0,
                        Score = new ScoreDto { Home = int.Parse(homeScore), Away = int.Parse(awayScore), Display = $"{homeScore} - {awayScore}" },
                        KickoffTime = DateTimeOffset.UtcNow.AddMinutes(-35),
                        RecentEvents = new List<TrayEventDto>
                        {
                            new() { Id = "evt_1", Type = "Goal", Minute = 18, TeamId = "team_ars", PlayerName = "B. Saka" }
                        }
                    }
                }
            }
        };
    }

    private static string SerializeEnvelope(ApiResponseEnvelope<TrayDataDto> envelope) =>
        JsonSerializer.Serialize(envelope, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true });

    // ============================================================
    // 1. Successful 200 response & ETag / Freshness Storage
    // ============================================================
    [TestMethod]
    public async Task Test01_Successful200Response_StoresPayload()
    {
        var envelope = CreateSampleEnvelope(version: 2);
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SerializeEnvelope(envelope), Encoding.UTF8, "application/json")
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_v2\"");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        var state = await syncService.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, state);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.IsNotNull(syncService.CurrentTrayData.Matches);
        Assert.AreEqual(1, syncService.CurrentTrayData.Matches.Count);
        Assert.AreEqual("fix_wp4_arsenal_chelsea", syncService.CurrentTrayData.Matches[0].Id);
    }

    [TestMethod]
    public async Task Test02_Successful200Response_StoresETag()
    {
        var envelope = CreateSampleEnvelope();
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SerializeEnvelope(envelope), Encoding.UTF8, "application/json")
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_abc_123\"");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        await syncService.PollOnceAsync();

        Assert.AreEqual("\"etag_abc_123\"", syncService.LastKnownETag);
        var cached = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(cached);
        Assert.AreEqual("\"etag_abc_123\"", cached.ETag);
    }

    [TestMethod]
    public async Task Test03_Successful200Response_StoresFreshnessTimestamp()
    {
        var envelope = CreateSampleEnvelope();
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(envelope), Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await syncService.PollOnceAsync();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.IsNotNull(syncService.LastValidatedAt);
        Assert.IsTrue(syncService.LastValidatedAt >= before && syncService.LastValidatedAt <= after);
    }

    // ============================================================
    // 2. Conditional Requests & 304 Handling
    // ============================================================
    [TestMethod]
    public async Task Test04_ConditionalRequest_SendsIfNoneMatchHeader()
    {
        string? sentIfNoneMatch = null;
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                sentIfNoneMatch = req.Headers.IfNoneMatch.ToString();
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);

        var response = await client.GetTrayAsync("\"etag_saved_123\"");

        Assert.IsTrue(response.IsNotModified);
        Assert.AreEqual("\"etag_saved_123\"", sentIfNoneMatch);
    }

    [TestMethod]
    public async Task Test05_304Response_PreservesPayload()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var initialEnvelope = CreateSampleEnvelope(version: 1, homeScore: "2", awayScore: "1");
        await cacheService.SaveCacheAsync(initialEnvelope, "\"etag_v1\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.NotModified)
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.NotModified, syncService.FreshnessState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(2, syncService.CurrentTrayData.Matches[0].Score.Home);
        Assert.AreEqual(1, syncService.CurrentTrayData.Matches[0].Score.Away);
    }

    [TestMethod]
    public async Task Test06_304Response_PreservesETag()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(CreateSampleEnvelope(), "\"etag_preserved_xyz\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.NotModified)
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual("\"etag_preserved_xyz\"", syncService.LastKnownETag);
    }

    [TestMethod]
    public async Task Test07_304Response_UpdatesFreshnessMetadata()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(CreateSampleEnvelope(), "\"etag_v1\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.NotModified)
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await syncService.StartAsync();
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.IsNotNull(syncService.LastValidatedAt);
        Assert.IsTrue(syncService.LastValidatedAt >= before && syncService.LastValidatedAt <= after);
        Assert.AreEqual(DataFreshnessState.NotModified, syncService.FreshnessState);
    }

    // ============================================================
    // 3. Failure Fallback & Cache Preservation
    // ============================================================
    [TestMethod]
    public async Task Test08_NetworkFailure_FallsBackToValidCache()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(CreateSampleEnvelope(homeScore: "3", awayScore: "0"), "\"etag_cached\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("Network unreachable")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Stale, syncService.FreshnessState);
        Assert.AreEqual(TickerConnectionState.OfflineCached, syncService.CurrentState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(3, syncService.CurrentTrayData.Matches[0].Score.Home);
    }

    [TestMethod]
    public async Task Test09_NetworkFailure_WithNoCache_ReturnsEmpty()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("Connection refused")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Empty, syncService.FreshnessState);
        Assert.AreEqual(TickerConnectionState.NoCacheOffline, syncService.CurrentState);
        Assert.IsNull(syncService.CurrentTrayData);
    }

    [TestMethod]
    public async Task Test10_Http5xxServerFailure_FallsBackToCache()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(CreateSampleEnvelope(homeScore: "1", awayScore: "1"), "\"etag_good\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"Database connection pool exhausted\"}", Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Stale, syncService.FreshnessState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual("1 - 1", syncService.CurrentTrayData.Matches[0].Score.Display);
    }

    // ============================================================
    // 4. Payload Validation & Malformed Payload Rejection
    // ============================================================
    [TestMethod]
    public async Task Test11_MalformedPayload_IsRejectedByValidator()
    {
        // 1. Null envelope
        Assert.IsFalse(TrayPayloadValidator.IsValid(null, out var err1));
        Assert.IsNotNull(err1);

        // 2. Missing matches
        var envNoMatches = new ApiResponseEnvelope<TrayDataDto> { SchemaVersion = "1.0", Data = new TrayDataDto { Matches = null! } };
        Assert.IsFalse(TrayPayloadValidator.IsValid(envNoMatches, out var err2));
        Assert.IsNotNull(err2);

        // 3. Match missing ID
        var envEmptyMatchId = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            Data = new TrayDataDto { Matches = new List<TrayMatchDto> { new() { Id = "", HomeTeam = new() { Name = "A" }, AwayTeam = new() { Name = "B" } } } }
        };
        Assert.IsFalse(TrayPayloadValidator.IsValid(envEmptyMatchId, out var err3));
        Assert.IsNotNull(err3);

        // 4. Match missing team identity
        var envMissingTeam = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            Data = new TrayDataDto { Matches = new List<TrayMatchDto> { new() { Id = "fix_1", HomeTeam = null!, AwayTeam = new() { Name = "B" } } } }
        };
        Assert.IsFalse(TrayPayloadValidator.IsValid(envMissingTeam, out var err4));
        Assert.IsNotNull(err4);
    }

    [TestMethod]
    public async Task Test12_MalformedPayload_DoesNotReplaceValidCache()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var validEnvelope = CreateSampleEnvelope(homeScore: "4", awayScore: "2");
        await cacheService.SaveCacheAsync(validEnvelope, "\"etag_valid\"");

        // Server sends a 200 with malformed/missing match structure
        var malformedJson = "{\"schemaVersion\":\"1.0\",\"data\":{\"matches\":[{\"id\":\"\",\"homeTeam\":null}]}}";
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(malformedJson, Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        // Must remain in Stale state with valid cached data preserved
        Assert.AreEqual(DataFreshnessState.Stale, syncService.FreshnessState);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(4, syncService.CurrentTrayData.Matches[0].Score.Home);
    }

    // ============================================================
    // 5. Cache Corruption & Atomic Replacement
    // ============================================================
    [TestMethod]
    public async Task Test13_CacheCorruptionAtStartup_IsHandledSafely()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await File.WriteAllTextAsync(cacheService.CacheFilePath, "{ broken json ... !!! not valid");

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNull(loaded);

        // Ensure SyncService starts cleanly with Empty state without throwing
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("offline")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();
        Assert.AreEqual(DataFreshnessState.Empty, syncService.FreshnessState);
    }

    [TestMethod]
    public async Task Test14_AtomicCacheReplacement_PreservesValidState()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var envelope1 = CreateSampleEnvelope(version: 1);
        var envelope2 = CreateSampleEnvelope(version: 2);

        var saved1 = await cacheService.SaveCacheAsync(envelope1, "\"etag_1\"");
        Assert.IsTrue(saved1);

        var saved2 = await cacheService.SaveCacheAsync(envelope2, "\"etag_2\"");
        Assert.IsTrue(saved2);

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded.Envelope?.Version);
        Assert.AreEqual("\"etag_2\"", loaded.ETag);
    }

    [TestMethod]
    public async Task Test15_Restart_CanRestoreCachedPayloadAndETag()
    {
        var cacheService1 = new LocalCacheService(_tempDir);
        await cacheService1.SaveCacheAsync(CreateSampleEnvelope(version: 4, homeScore: "2", awayScore: "2"), "\"etag_restart_test\"");

        // Simulate app restart by instantiating new LocalCacheService and SyncService instances
        var cacheService2 = new LocalCacheService(_tempDir);
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("Offline at startup")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService2);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Stale, syncService.FreshnessState);
        Assert.AreEqual(4, syncService.LastKnownVersion);
        Assert.AreEqual("\"etag_restart_test\"", syncService.LastKnownETag);
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual("2 - 2", syncService.CurrentTrayData.Matches[0].Score.Display);
    }

    // ============================================================
    // 6. Concurrency Protection & State Transitions
    // ============================================================
    [TestMethod]
    public async Task Test16_ConcurrentRefreshes_DoNotOverlap()
    {
        int networkCallCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                Interlocked.Increment(ref networkCallCount);
                Thread.Sleep(50); // Simulate network latency
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SerializeEnvelope(CreateSampleEnvelope()), Encoding.UTF8, "application/json")
                };
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService);

        // Fire 5 concurrent poll operations
        var task1 = syncService.PollOnceAsync();
        var task2 = syncService.PollOnceAsync();
        var task3 = syncService.PollOnceAsync();
        var task4 = syncService.PollOnceAsync();
        var task5 = syncService.PollOnceAsync();

        await Task.WhenAll(task1, task2, task3, task4, task5);

        // All 5 callers should share the exact same single in-flight network operation
        Assert.AreEqual(1, networkCallCount);
    }

    [TestMethod]
    public async Task Test17_SuccessfulNew200_ReplacesOldPayloadCorrectly()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(CreateSampleEnvelope(version: 1, homeScore: "0", awayScore: "0"), "\"etag_1\"");

        var updatedEnvelope = CreateSampleEnvelope(version: 2, homeScore: "1", awayScore: "0");
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SerializeEnvelope(updatedEnvelope), Encoding.UTF8, "application/json"),
                Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_2\"") }
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();

        Assert.AreEqual(DataFreshnessState.Fresh, syncService.FreshnessState);
        Assert.AreEqual(2, syncService.LastKnownVersion);
        Assert.AreEqual("\"etag_2\"", syncService.LastKnownETag);
        Assert.AreEqual(1, syncService.CurrentTrayData?.Matches[0].Score.Home);
    }

    [TestMethod]
    public async Task Test18_FailedRefresh_DoesNotDestroyPreviousCache()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(CreateSampleEnvelope(version: 5, homeScore: "3", awayScore: "2"), "\"etag_v5\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        await syncService.StartAsync();
        await syncService.PollOnceAsync();

        // Disk cache file must still exist and be readable
        var diskCache = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(diskCache);
        Assert.AreEqual(5, diskCache.Envelope?.Version);
        Assert.AreEqual("\"etag_v5\"", diskCache.ETag);
    }

    [TestMethod]
    public async Task Test19_FreshnessStateTransitions_AreCorrect()
    {
        var cacheService = new LocalCacheService(_tempDir);
        int callNumber = 0;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                callNumber++;
                if (callNumber == 1)
                {
                    // 1. Initial 200 OK -> Fresh
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SerializeEnvelope(CreateSampleEnvelope(1)), Encoding.UTF8, "application/json"),
                        Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_1\"") }
                    };
                }
                if (callNumber == 2)
                {
                    // 2. Unchanged -> NotModified
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }
                // 3. Network Failure -> Stale
                throw new HttpRequestException("Server offline");
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService);

        // 1. First poll -> Fresh
        var state1 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Fresh, state1);

        // 2. Second poll -> NotModified
        var state2 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.NotModified, state2);

        // 3. Third poll -> Stale
        var state3 = await syncService.PollOnceAsync();
        Assert.AreEqual(DataFreshnessState.Stale, state3);
    }

    [TestMethod]
    public void Test20_TrayTickerFormatter_FormatsAllFreshnessStatesCorrectly()
    {
        var data = CreateSampleEnvelope().Data;

        // Fresh
        var textFresh = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Fresh);
        Assert.AreEqual("⚽ ARS 1–0 CHE 35'", textFresh);

        // NotModified
        var textNotMod = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.NotModified);
        Assert.AreEqual("⚽ ARS 1–0 CHE 35'", textNotMod);

        // Stale
        var textStale = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Stale);
        Assert.AreEqual("⚽ ARS 1–0 CHE 35' · offline", textStale);

        // Empty
        var textEmpty = TrayTickerFormatter.FormatTicker(null, DataFreshnessState.Empty);
        Assert.AreEqual("FootballTray · offline", textEmpty);
    }

    // ============================================================
    // 7. Cache Corruption Granular Scenarios (Step 13)
    // ============================================================
    [TestMethod]
    public async Task Test21_CorruptionScenarioA_EmptyCacheFile_ReturnsNull()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await File.WriteAllTextAsync(cacheService.CacheFilePath, "");

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task Test22_CorruptionScenarioB_TruncatedJson_ReturnsNull()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await File.WriteAllTextAsync(cacheService.CacheFilePath, "{\"schemaVersion\":\"1.0\",\"envelope\":{\"version\":");

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task Test23_CorruptionScenarioC_MissingRequiredFields_ReturnsNull()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var jsonWithoutEnvelope = "{\"cachedAt\":\"2026-03-01T12:00:00Z\",\"etag\":\"test\"}";
        await File.WriteAllTextAsync(cacheService.CacheFilePath, jsonWithoutEnvelope);

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task Test24_CorruptionScenarioD_UnknownAdditiveFields_DeserializesCleanly()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var envelope = CreateSampleEnvelope(version: 9);
        var jsonWithExtraField = $$"""
        {
            "envelope": {{SerializeEnvelope(envelope)}},
            "etag": "\"etag_future\"",
            "cachedAt": "2026-03-01T12:00:00Z",
            "futureExtensionField": { "extraProperty": 12345 }
        }
        """;
        await File.WriteAllTextAsync(cacheService.CacheFilePath, jsonWithExtraField);

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(9, loaded.Envelope?.Version);
        Assert.AreEqual("\"etag_future\"", loaded.ETag);
    }

    [TestMethod]
    public async Task Test25_CorruptionScenarioE_ValidCacheWithTimestampAndETag_LoadsSuccessfully()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var envelope = CreateSampleEnvelope(version: 3);
        await cacheService.SaveCacheAsync(envelope, "\"etag_v3\"");

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(loaded);
        Assert.AreEqual("\"etag_v3\"", loaded.ETag);
        Assert.IsTrue(loaded.CachedAt > DateTimeOffset.MinValue);
        Assert.IsTrue(loaded.LastValidatedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public async Task Test26_CorruptionScenarioF_LeftoverTempFiles_DoNotInterfereWithCache()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var leftoverTmp = Path.Combine(_tempDir, "tray_deadbeef.tmp");
        await File.WriteAllTextAsync(leftoverTmp, "incomplete write");

        await cacheService.SaveCacheAsync(CreateSampleEnvelope(version: 7), "\"etag_clean\"");

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(loaded);
        Assert.AreEqual(7, loaded.Envelope?.Version);
    }
}
