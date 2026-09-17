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

namespace FootballTray.Client.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var response = ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}

[TestClass]
public class FootballApiClientAndSyncServiceTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTraySyncTest_" + Guid.NewGuid().ToString("N"));
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

    [TestMethod]
    public async Task GetTrayAsync_200OK_DeserializesEnvelopeAndReadsETag()
    {
        var jsonResponse = """
        {
            "schemaVersion": "1.0",
            "requestId": "req_1",
            "version": 2,
            "generatedAt": "2026-03-01T12:00:00Z",
            "serverTime": "2026-03-01T12:00:00Z",
            "stale": false,
            "dataAgeSeconds": 0,
            "data": {
                "matches": [
                    {
                        "id": "fix_elclasico",
                        "homeTeam": { "name": "FC Barcelona", "shortName": "BAR" },
                        "awayTeam": { "name": "Real Madrid", "shortName": "RMA" },
                        "state": "LIVE",
                        "elapsedMinute": 72,
                        "score": { "home": 1, "away": 1, "display": "1 - 1" }
                    }
                ]
            }
        }
        """;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_test_123\"");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);

        var result = await client.GetTrayAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.IsNotModified);
        Assert.AreEqual("\"etag_test_123\"", result.ETag);
        Assert.AreEqual(2, result.Envelope?.Version);
        Assert.AreEqual(1, result.Envelope?.Data?.Matches.Count);
        Assert.AreEqual("BAR", result.Envelope?.Data?.Matches[0].HomeTeam.ShortName);
    }

    [TestMethod]
    public async Task GetTrayAsync_304NotModified_ReturnsNotModified()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                Assert.AreEqual("\"etag_current\"", req.Headers.IfNoneMatch.ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);

        var result = await client.GetTrayAsync("etag_current");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.IsNotModified);
    }

    [TestMethod]
    public async Task SyncService_FullCycle_LoadsCache_PollsAPI_EmitsEvents()
    {
        var jsonResponse = """
        {
            "schemaVersion": "1.0",
            "requestId": "req_1",
            "version": 5,
            "data": {
                "matches": [
                    {
                        "id": "fix_elclasico",
                        "homeTeam": { "name": "FC Barcelona", "shortName": "BAR" },
                        "awayTeam": { "name": "Real Madrid", "shortName": "RMA" },
                        "state": "LIVE",
                        "elapsedMinute": 72,
                        "score": { "home": 1, "away": 1, "display": "1 - 1" }
                    }
                ]
            }
        }
        """;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_v5\"");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var apiClient = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(apiClient, cacheService, TimeSpan.FromSeconds(60));

        var receivedUpdates = new List<TrayUpdateEventArgs>();
        syncService.TrayUpdated += (s, e) => receivedUpdates.Add(e);

        await syncService.StartAsync();

        Assert.IsGreaterThanOrEqualTo(1, receivedUpdates.Count);
        var latest = receivedUpdates[^1];
        Assert.AreEqual(TickerConnectionState.OnlineFresh, latest.ConnectionState);
        Assert.AreEqual("⚽ BAR 1–1 RMA 72'", latest.FormattedText);
        Assert.AreEqual(5, latest.Version);

        // Verify cache file was written to disk
        var cachedOnDisk = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(cachedOnDisk);
        Assert.AreEqual(5, cachedOnDisk.Envelope?.Version);
    }

    [TestMethod]
    public async Task SyncService_OfflineHandling_TransitionsToOfflineCached()
    {
        // First establish cached state
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(new ApiResponseEnvelope<TrayDataDto>
        {
            Version = 1,
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "fix_1",
                        HomeTeam = new TeamSummaryDto { ShortName = "BAR" },
                        AwayTeam = new TeamSummaryDto { ShortName = "RMA" },
                        State = "LIVE",
                        ElapsedMinute = 40,
                        Score = new ScoreDto { Home = 1, Away = 0 }
                    }
                }
            }
        }, "\"etag_1\"");

        // Mock network error
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) => throw new HttpRequestException("Connection refused")
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var apiClient = new FootballApiClient(httpClient);
        using var syncService = new SyncService(apiClient, cacheService);

        var receivedUpdates = new List<TrayUpdateEventArgs>();
        syncService.TrayUpdated += (s, e) => receivedUpdates.Add(e);

        await syncService.StartAsync();

        Assert.IsGreaterThanOrEqualTo(1, receivedUpdates.Count);
        var latest = receivedUpdates[^1];
        Assert.AreEqual(TickerConnectionState.OfflineCached, latest.ConnectionState);
        Assert.AreEqual("⚽ BAR 1–0 RMA 40' · offline", latest.FormattedText);
    }

    [TestMethod]
    public async Task SyncService_Poll_WithLowerServerVersionThanCache_AcceptsAuthoritativeServerPayload()
    {
        // 1. Given: Persisted cache exists with Version = 5 and stale match data
        var cacheService = new LocalCacheService(_tempDir);
        await cacheService.SaveCacheAsync(new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            Version = 5,
            GeneratedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ServerTime = DateTimeOffset.UtcNow.AddHours(-2),
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "mock_stale_1",
                        HomeTeam = new TeamSummaryDto { ShortName = "AAA" },
                        AwayTeam = new TeamSummaryDto { ShortName = "BBB" },
                        State = "LIVE",
                        ElapsedMinute = 81,
                        Score = new ScoreDto { Home = 1, Away = 0 }
                    }
                }
            }
        }, "\"mock-etag-v5\"");

        // 2. Given: Backend responds HTTP 200 with Version = 1 and new production matches
        var prodJsonResponse = """
        {
            "schemaVersion": "1.0",
            "requestId": "req_prod_1",
            "version": 1,
            "generatedAt": "2026-09-17T20:00:00Z",
            "serverTime": "2026-09-17T20:00:00Z",
            "data": {
                "matches": [
                    {
                        "id": "fix_prod_live",
                        "homeTeam": { "name": "Arsenal", "shortName": "ARS" },
                        "awayTeam": { "name": "Chelsea", "shortName": "CHE" },
                        "state": "LIVE",
                        "elapsedMinute": 25,
                        "score": { "home": 2, "away": 1, "display": "2 - 1" }
                    }
                ]
            }
        }
        """;

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(prodJsonResponse, Encoding.UTF8, "application/json"),
                };
                resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_prod_v1\"");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var apiClient = new FootballApiClient(httpClient);
        using var syncService = new SyncService(apiClient, cacheService);

        var receivedUpdates = new List<TrayUpdateEventArgs>();
        syncService.TrayUpdated += (s, e) => receivedUpdates.Add(e);

        // When: SyncService performs start and sync
        await syncService.StartAsync();

        // Then:
        // 1. Freshness becomes normal Fresh/Online state (not Stale / OfflineCached)
        Assert.AreEqual(DataFreshnessState.Fresh, syncService.FreshnessState);
        Assert.AreEqual(TickerConnectionState.OnlineFresh, syncService.CurrentState);

        // 2. Current tray data equals the server payload
        Assert.IsNotNull(syncService.CurrentTrayData);
        Assert.AreEqual(1, syncService.CurrentTrayData.Matches.Count);
        Assert.AreEqual("fix_prod_live", syncService.CurrentTrayData.Matches[0].Id);
        Assert.AreEqual("ARS", syncService.CurrentTrayData.Matches[0].HomeTeam.ShortName);

        // 3. Stored last-known version becomes 1
        Assert.AreEqual(1, syncService.LastKnownVersion);

        // 4. Stored ETag becomes the server ETag
        Assert.AreEqual("\"etag_prod_v1\"", syncService.LastKnownETag);

        // 5. Persisted cache on disk contains server payload and version 1
        var diskCache = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(diskCache);
        Assert.AreEqual(1, diskCache.Envelope?.Version);
        Assert.AreEqual("\"etag_prod_v1\"", diskCache.ETag);
        Assert.AreEqual("fix_prod_live", diskCache.Envelope?.Data?.Matches[0].Id);

        // 6. Ticker / UI event published reflects OnlineFresh with production score
        Assert.IsTrue(receivedUpdates.Count >= 2); // 1st is Stale from cache load, 2nd is OnlineFresh from poll
        var latestUpdate = receivedUpdates[^1];
        Assert.AreEqual(TickerConnectionState.OnlineFresh, latestUpdate.ConnectionState);
        Assert.AreEqual("⚽ ARS 2–1 CHE 25'", latestUpdate.FormattedText);
        Assert.AreEqual(1, latestUpdate.Version);
    }
}
