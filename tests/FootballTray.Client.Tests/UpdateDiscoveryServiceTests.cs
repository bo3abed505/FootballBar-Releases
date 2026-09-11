using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Updates;
using FootballTray.Core;
using FootballTray.Core.Dto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class UpdateDiscoveryServiceTests
{
    private const string SampleManifestJsonV110 = """
    {
      "schemaVersion": "1.0",
      "version": "1.1.0",
      "publishedAt": "2026-09-15T12:00:00Z",
      "architecture": "win-x64",
      "downloadUrl": "https://github.com/bo3abed505/FootballTray-Releases/releases/download/v1.1.0/FootballTray-v1.1.0-win-x64.zip",
      "sha256": "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54",
      "sizeBytes": 65327531,
      "releaseNotes": "• New ticker improvements\n• Bug fixes"
    }
    """;

    private const string SampleManifestJsonV100 = """
    {
      "schemaVersion": "1.0",
      "version": "1.0.0",
      "publishedAt": "2026-09-10T12:00:00Z",
      "architecture": "win-x64",
      "downloadUrl": "https://github.com/bo3abed505/FootballTray-Releases/releases/download/v1.0.0/FootballTray-v1.0.0-win-x64.zip",
      "sha256": "fff330cf8b14038dead109efc5186478f53d95848c5fb4fb3ebc186262bd26dd",
      "sizeBytes": 65514768
    }
    """;

    private const string SampleManifestJsonV090 = """
    {
      "schemaVersion": "1.0",
      "version": "0.9.0",
      "publishedAt": "2026-08-10T12:00:00Z",
      "architecture": "win-x64",
      "downloadUrl": "https://github.com/bo3abed505/FootballTray-Releases/releases/download/v0.9.0/FootballTray-v0.9.0-win-x64.zip",
      "sha256": "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
      "sizeBytes": 50000000
    }
    """;

    private const string SampleManifestJsonV110Beta = """
    {
      "schemaVersion": "1.0",
      "version": "1.1.0-beta.1",
      "publishedAt": "2026-09-14T12:00:00Z",
      "architecture": "win-x64",
      "downloadUrl": "https://github.com/bo3abed505/FootballTray-Releases/releases/download/v1.1.0-beta.1/FootballTray-v1.1.0-beta.1-win-x64.zip",
      "sha256": "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54",
      "sizeBytes": 65327531
    }
    """;

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? SyncHandler { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncHandler { get; set; }
        public int RequestCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;

            if (AsyncHandler != null)
            {
                return await AsyncHandler(request, cancellationToken);
            }

            if (SyncHandler != null)
            {
                return SyncHandler(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    #region Required Tests 1-13: Core Lifecycle, Results & Error Boundaries

    [TestMethod]
    public void Test01_InitialState_IsNotChecked()
    {
        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(new TestHttpMessageHandler()),
            currentVersion: "1.0.0");

        Assert.AreEqual(UpdateCheckState.NotChecked, service.CurrentState);
        Assert.IsNull(service.CurrentOfferedManifest);
        Assert.IsNull(service.LastCheckAttempt);
        Assert.IsNull(service.LastSuccessfulCheck);
        Assert.IsNull(service.LastKnownETag);
        Assert.IsNull(service.LastFailureReason);
        Assert.AreEqual("1.0.0", service.CurrentVersion.ToString());
    }

    [TestMethod]
    public async Task Test02_ValidNewerStableManifest_ReturnsUpdateAvailable()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result.State);
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.IsNotNull(result.OfferedManifest);
        Assert.AreEqual("1.1.0", result.OfferedManifest.Version);
        Assert.AreEqual("1.0.0", result.CurrentVersion?.ToString());
        Assert.AreEqual("1.1.0", result.TargetVersion?.ToString());
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, service.CurrentState);
        Assert.IsNotNull(service.CurrentOfferedManifest);
        Assert.IsNotNull(service.LastSuccessfulCheck);
    }

    [TestMethod]
    public async Task Test03_SameVersion_ReturnsUpToDate()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.UpToDate, result.State);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsNull(result.OfferedManifest);
        Assert.AreEqual(UpdateCheckState.UpToDate, service.CurrentState);
        Assert.IsNull(service.CurrentOfferedManifest);
    }

    [TestMethod]
    public async Task Test04_OlderVersion_ReturnsUpToDate()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleManifestJsonV090, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.UpToDate, result.State);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsNull(result.OfferedManifest);
        Assert.AreEqual(UpdateCheckState.UpToDate, service.CurrentState);
    }

    [TestMethod]
    public async Task Test05_PrereleaseTargetOfferedToStableClient_ReturnsUpToDate()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleManifestJsonV110Beta, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.UpToDate, result.State);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsNull(result.OfferedManifest);
        Assert.AreEqual(UpdateCheckState.UpToDate, service.CurrentState);
    }

    [TestMethod]
    public async Task Test06_MalformedJson_ReturnsCheckFailed()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ not valid json at all", Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.IsTrue(result.Message?.Contains("Malformed JSON") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
        Assert.IsNotNull(service.LastFailureReason);
    }

    [TestMethod]
    public async Task Test07_UnsupportedSchema_ReturnsCheckFailed()
    {
        string unsupportedJson = SampleManifestJsonV110.Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"2.0\"");
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(unsupportedJson, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("Unsupported manifest schemaVersion '2.0'") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test08_InvalidManifest_NonHttpsDownloadUrl_ReturnsCheckFailed()
    {
        string insecureJson = SampleManifestJsonV110.Replace("https://github.com", "http://github.com");
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(insecureJson, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("must use HTTPS") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test09_Http404_ReturnsCheckFailed()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                ReasonPhrase = "Not Found"
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("404") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test10_Http500_ReturnsCheckFailed()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "Internal Server Error"
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("500") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test11_NetworkException_ReturnsCheckFailed()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => throw new HttpRequestException("Connection refused by target host")
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.AreEqual("Network error during update check.", result.Message);
        Assert.AreEqual("Network error during update check.", service.LastFailureReason);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test12_Timeout_ReturnsCheckFailed()
    {
        var handler = new TestHttpMessageHandler
        {
            // Simulates HttpClient timeout: OperationCanceledException thrown while caller token is NOT cancelled
            AsyncHandler = async (_, token) =>
            {
                await Task.Delay(50, token);
                throw new OperationCanceledException("The operation was canceled due to timeout.");
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("timed out") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test13_CallerCancellation_ThrowsOperationCanceledException_PreservesState()
    {
        var handler = new TestHttpMessageHandler
        {
            AsyncHandler = async (_, token) =>
            {
                await Task.Delay(500, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await service.CheckForUpdatesAsync(cts.Token);
        });

        // State remains NotChecked after caller cancellation; zero network request performed
        Assert.AreEqual(UpdateCheckState.NotChecked, service.CurrentState);
        Assert.AreEqual(0, handler.RequestCount);
    }

    #endregion

    #region Required Tests 14-18: ETag and Conditional GET Lifecycle

    [TestMethod]
    public async Task Test14_Successful200WithETag_StoresETag()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-etag-123\"");
                return response;
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result.State);
        Assert.AreEqual("\"release-etag-123\"", service.LastKnownETag);
        Assert.AreEqual("\"release-etag-123\"", result.ETag);
    }

    [TestMethod]
    public async Task Test15_NextCheck_SendsIfNoneMatch()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = req =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-abc-999\"");
                return response;
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // First check: stores ETag
        await service.CheckForUpdatesAsync();
        Assert.AreEqual("\"etag-abc-999\"", service.LastKnownETag);

        // Second check: inspect outgoing request headers
        await service.CheckForUpdatesAsync();

        Assert.IsNotNull(handler.LastRequest);
        Assert.IsTrue(handler.LastRequest.Headers.Contains("If-None-Match"));
        Assert.AreEqual("\"etag-abc-999\"", handler.LastRequest.Headers.IfNoneMatch.ToString());
    }

    [TestMethod]
    public async Task Test16_Http304AfterSuccessfulUpToDate_PreservesUpToDate()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var res = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
                    };
                    res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-100\"");
                    return res;
                }
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Call 1: 200 OK -> UpToDate
        var result1 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpToDate, result1.State);
        var timestamp1 = service.LastSuccessfulCheck;

        // Call 2: 304 Not Modified -> preserves UpToDate
        var result2 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpToDate, result2.State);
        Assert.IsFalse(result2.IsUpdateAvailable);
        Assert.AreEqual("Manifest not modified (HTTP 304).", result2.Message);
        Assert.AreEqual(UpdateCheckState.UpToDate, service.CurrentState);
        Assert.IsTrue(service.LastSuccessfulCheck >= timestamp1);
    }

    [TestMethod]
    public async Task Test17_Http304AfterSuccessfulUpdateAvailable_PreservesUpdateAvailableAndManifest()
    {
        int callCount = 0;
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var res = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
                    };
                    res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-v110\"");
                    return res;
                }
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Call 1: 200 OK -> UpdateAvailable
        var result1 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result1.State);
        Assert.IsNotNull(result1.OfferedManifest);

        // Call 2: 304 Not Modified -> preserves UpdateAvailable with manifest
        var result2 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result2.State);
        Assert.IsTrue(result2.IsUpdateAvailable);
        Assert.IsNotNull(result2.OfferedManifest);
        Assert.AreEqual("1.1.0", result2.OfferedManifest.Version);
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, service.CurrentState);
        Assert.IsNotNull(service.CurrentOfferedManifest);
    }

    [TestMethod]
    public async Task Test18_Http304WithoutCachedSuccessfulResult_ReturnsCheckFailed()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.NotModified)
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("without a cached release manifest") == true);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
    }

    #endregion

    #region Required Tests 19-23: Concurrency, Isolation, Configuration & Default Contract

    [TestMethod]
    public async Task Test19_OverlappingCalls_DoNotPerformCompetingNetworkOperations()
    {
        var handler = new TestHttpMessageHandler
        {
            AsyncHandler = async (_, token) =>
            {
                await Task.Delay(100, token);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
                };
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Launch two checks concurrently
        var task1 = service.CheckForUpdatesAsync();
        var task2 = service.CheckForUpdatesAsync();

        var results = await Task.WhenAll(task1, task2);

        // Exactly one HTTP request was executed across both callers
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, results[0].State);
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, results[1].State);
    }

    [TestMethod]
    public async Task Test20_UpdateFailure_CannotMutateFootballDataFreshnessOrSyncHealth()
    {
        // Architectural proof: verify update failure states remain 100% segregated from football sync
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };

        using var updateService = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Football sync state before update check
        var initialFreshness = DataFreshnessState.Fresh;
        Assert.AreEqual(DataFreshnessState.Fresh, initialFreshness);

        // Execute failing update check
        var updateResult = await updateService.CheckForUpdatesAsync();

        // Update subsystem transitions to CheckFailed
        Assert.AreEqual(UpdateCheckState.CheckFailed, updateResult.State);
        Assert.AreEqual(UpdateCheckState.CheckFailed, updateService.CurrentState);

        // Verify football sync freshness domain is completely decoupled and unmutated
        Assert.AreEqual(DataFreshnessState.Fresh, initialFreshness);
        Assert.AreNotEqual((int)initialFreshness, (int)updateService.CurrentState);
    }

    [TestMethod]
    public void Test21_EnvironmentOverrideResolution()
    {
        const string customUrl = "https://custom-releases.example.com/manifest.json";
        var originalEnv = Environment.GetEnvironmentVariable(UpdateDiscoveryService.FeedUrlEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(UpdateDiscoveryService.FeedUrlEnvironmentVariable, customUrl);
            Assert.AreEqual(customUrl, UpdateDiscoveryService.ResolveFeedUrl());

            using var service = new UpdateDiscoveryService(
                httpClient: new HttpClient(new TestHttpMessageHandler()));
            Assert.AreEqual(customUrl, service.FeedUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateDiscoveryService.FeedUrlEnvironmentVariable, originalEnv);
        }

        // When cleared, reverts to authoritative default
        Assert.AreEqual(UpdateDiscoveryService.DefaultFeedUrl, UpdateDiscoveryService.ResolveFeedUrl());
    }

    [TestMethod]
    public async Task Test22_InvalidProductionFeedUri_FailsSafely()
    {
        var handler = new TestHttpMessageHandler();

        // Non-HTTPS scheme (http://)
        using var serviceHttp = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            feedUrl: "http://insecure.example.com/manifest.json",
            currentVersion: "1.0.0");

        var resultHttp = await serviceHttp.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.CheckFailed, resultHttp.State);
        Assert.IsTrue(resultHttp.Message?.Contains("absolute HTTPS URI") == true);

        // Completely malformed URI
        using var serviceMalformed = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            feedUrl: "not-a-valid-uri",
            currentVersion: "1.0.0");

        var resultMalformed = await serviceMalformed.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.CheckFailed, resultMalformed.State);
        Assert.IsTrue(resultMalformed.Message?.Contains("absolute HTTPS URI") == true);

        // Zero network requests sent for invalid feed URIs
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public void Test23_DefaultFeedUrl_IsAuthoritativeGitHubReleasesUrl()
    {
        string resolved = UpdateDiscoveryService.ResolveFeedUrl();
        Assert.AreEqual(
            "https://github.com/bo3abed505/FootballTray-Releases/releases/latest/download/release-manifest.json",
            resolved);
    }

    #endregion

    #region Additional Tests 24-27: Prerelease Transitions, Strict Validations & Headers

    [TestMethod]
    public async Task Test24_PrereleaseClient_ReceivingStableUpdate_ReturnsUpdateAvailable()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
            }
        };

        // Running 1.0.0-rc.1; target manifest is stable 1.0.0 -> update offered!
        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0-rc.1");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result.State);
        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("1.0.0", result.TargetVersion?.ToString());
    }

    [TestMethod]
    public async Task Test25_InvalidArchitecture_ReturnsCheckFailed()
    {
        string wrongArchJson = SampleManifestJsonV110.Replace("\"architecture\": \"win-x64\"", "\"architecture\": \"linux-x64\"");
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(wrongArchJson, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("Incompatible architecture 'linux-x64'") == true);
    }

    [TestMethod]
    public async Task Test26_InvalidSha256_ReturnsCheckFailed()
    {
        string badHashJson = SampleManifestJsonV110.Replace(
            "\"sha256\": \"87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54\"",
            "\"sha256\": \"short-hash\"");
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(badHashJson, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("sha256") == true);
    }

    [TestMethod]
    public async Task Test27_UserAgentHeader_IncludesApplicationVersion()
    {
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        await service.CheckForUpdatesAsync();

        Assert.IsNotNull(handler.LastRequest);
        Assert.IsTrue(handler.LastRequest.Headers.Contains("User-Agent"));
        Assert.AreEqual("FootballTray/1.0.0", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [TestMethod]
    public async Task Test28_Http304AfterTransientFailure_RestoresCachedUpToDateResult()
    {
        int callCount = 0;
        string? request3IfNoneMatch = null;

        var handler = new TestHttpMessageHandler
        {
            SyncHandler = req =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var res = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
                    };
                    res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-100-A\"");
                    return res;
                }
                if (callCount == 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        ReasonPhrase = "Internal Server Error"
                    };
                }

                request3IfNoneMatch = req.Headers.IfNoneMatch.ToString();
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Request 1: 200 OK -> UpToDate
        var result1 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpToDate, result1.State);
        Assert.AreEqual("\"etag-100-A\"", service.LastKnownETag);
        var timestamp1 = service.LastSuccessfulCheck;

        // Request 2: 500 Internal Server Error -> CheckFailed
        var result2 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.CheckFailed, result2.State);
        Assert.AreEqual(UpdateCheckState.CheckFailed, service.CurrentState);
        Assert.IsNull(service.CurrentOfferedManifest);
        Assert.AreEqual("\"etag-100-A\"", service.LastKnownETag);

        // Request 3: 304 Not Modified -> Restores cached UpToDate!
        var result3 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpToDate, result3.State);
        Assert.IsFalse(result3.IsUpdateAvailable);
        Assert.AreEqual(UpdateCheckState.UpToDate, service.CurrentState);
        Assert.IsNull(service.CurrentOfferedManifest);
        Assert.AreEqual("\"etag-100-A\"", request3IfNoneMatch);
        Assert.IsTrue(service.LastSuccessfulCheck >= timestamp1);
    }

    [TestMethod]
    public async Task Test29_Http304AfterTransientFailure_RestoresCachedUpdateAvailableResult()
    {
        int callCount = 0;
        string? request3IfNoneMatch = null;

        var handler = new TestHttpMessageHandler
        {
            SyncHandler = req =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var res = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
                    };
                    res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-110-B\"");
                    return res;
                }
                if (callCount == 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        ReasonPhrase = "Internal Server Error"
                    };
                }

                request3IfNoneMatch = req.Headers.IfNoneMatch.ToString();
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Request 1: 200 OK -> UpdateAvailable
        var result1 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result1.State);
        Assert.AreEqual("1.1.0", result1.OfferedManifest?.Version);
        Assert.AreEqual("\"etag-110-B\"", service.LastKnownETag);
        Assert.IsNotNull(service.CurrentOfferedManifest);

        // Request 2: 500 Internal Server Error -> CheckFailed
        var result2 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.CheckFailed, result2.State);
        Assert.IsNull(service.CurrentOfferedManifest);
        Assert.AreEqual("\"etag-110-B\"", service.LastKnownETag);

        // Request 3: 304 Not Modified -> Restores cached UpdateAvailable with manifest!
        var result3 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, result3.State);
        Assert.IsTrue(result3.IsUpdateAvailable);
        Assert.IsNotNull(result3.OfferedManifest);
        Assert.AreEqual("1.1.0", result3.OfferedManifest.Version);
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, service.CurrentState);
        Assert.IsNotNull(service.CurrentOfferedManifest);
        Assert.AreEqual("1.1.0", service.CurrentOfferedManifest.Version);
        Assert.AreEqual("\"etag-110-B\"", request3IfNoneMatch);
    }

    [TestMethod]
    public async Task Test30_InitiatorCancellation_DoesNotCancelSharedOperationForJoinedCaller()
    {
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseGate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new TestHttpMessageHandler
        {
            AsyncHandler = async (_, token) =>
            {
                requestStarted.TrySetResult(true);
                return await responseGate.Task.WaitAsync(token);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        using var callerACts = new CancellationTokenSource();
        using var callerBCts = new CancellationTokenSource();

        // Caller A initiates the shared check
        var taskA = service.CheckForUpdatesAsync(callerACts.Token);

        // Wait until handler is actively running the request
        await requestStarted.Task;

        // Caller B joins the ongoing in-flight check
        var taskB = service.CheckForUpdatesAsync(callerBCts.Token);

        // Cancel Caller A's token only
        callerACts.Cancel();

        // Caller A throws OperationCanceledException
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await taskA);

        // Verify request count remains 1 and shared operation is still running
        Assert.AreEqual(1, handler.RequestCount);
        Assert.IsFalse(taskB.IsCompleted);

        // Release the fake response
        responseGate.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleManifestJsonV110, Encoding.UTF8, "application/json")
        });

        // Caller B completes successfully!
        var resultB = await taskB;

        Assert.AreEqual(UpdateCheckState.UpdateAvailable, resultB.State);
        Assert.IsTrue(resultB.IsUpdateAvailable);
        Assert.AreEqual("1.1.0", resultB.OfferedManifest?.Version);
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, service.CurrentState);
    }

    [TestMethod]
    public async Task Test31_InjectedHttpClient_EnforcesOperationScopedServiceTimeout()
    {
        var handler = new TestHttpMessageHandler
        {
            // Never completes unless cancelled by operation token
            AsyncHandler = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var injectedClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        using var service = new UpdateDiscoveryService(
            httpClient: injectedClient,
            currentVersion: "1.0.0",
            timeout: TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.CheckForUpdatesAsync();
        sw.Stop();

        // Verify timeout enforced quickly
        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsTrue(result.Message?.Contains("timed out") == true);
        Assert.IsTrue(sw.ElapsedMilliseconds < 5000, "Timeout should fire in ~100ms, well under 5 seconds.");

        // Injected client was not modified
        Assert.AreEqual(Timeout.InfiniteTimeSpan, injectedClient.Timeout);

        // Disposing the service does not dispose the injected client
        service.Dispose();
        Assert.IsNull(injectedClient.BaseAddress); // client is accessible and undisposed
    }

    [TestMethod]
    public async Task Test32_WeakETag_PreservesWeakValidatorAndSendsExactWeakHeader()
    {
        int callCount = 0;
        string? request2IfNoneMatch = null;

        var handler = new TestHttpMessageHandler
        {
            SyncHandler = req =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var res = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
                    };
                    res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"weak-123\"", isWeak: true);
                    return res;
                }

                request2IfNoneMatch = req.Headers.IfNoneMatch.ToString();
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Request 1: 200 OK with weak ETag W/"weak-123"
        var result1 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpToDate, result1.State);
        Assert.AreEqual("W/\"weak-123\"", service.LastKnownETag);
        Assert.AreEqual("W/\"weak-123\"", result1.ETag);

        // Request 2: Next check sends exact weak header
        var result2 = await service.CheckForUpdatesAsync();
        Assert.AreEqual(UpdateCheckState.UpToDate, result2.State);
        Assert.AreEqual("W/\"weak-123\"", request2IfNoneMatch);
        Assert.AreEqual("W/\"weak-123\"", service.LastKnownETag);
    }

    [TestMethod]
    public async Task Test33_New200ResponseWithoutETag_ClearsStaleETag()
    {
        int callCount = 0;
        string? request3IfNoneMatch = null;
        bool request3HadIfNoneMatch = true;

        var handler = new TestHttpMessageHandler
        {
            SyncHandler = req =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var res = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
                    };
                    res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"A\"");
                    return res;
                }
                if (callCount == 2)
                {
                    // 200 OK without any ETag
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
                    };
                }

                request3HadIfNoneMatch = req.Headers.Contains("If-None-Match");
                request3IfNoneMatch = req.Headers.IfNoneMatch.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleManifestJsonV100, Encoding.UTF8, "application/json")
                };
            }
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        // Request 1: 200 with ETag "A"
        await service.CheckForUpdatesAsync();
        Assert.AreEqual("\"A\"", service.LastKnownETag);

        // Request 2: 200 without ETag -> clears stale ETag
        await service.CheckForUpdatesAsync();
        Assert.IsNull(service.LastKnownETag);

        // Request 3: check -> If-None-Match is NOT sent
        await service.CheckForUpdatesAsync();
        Assert.IsFalse(request3HadIfNoneMatch);
        Assert.IsTrue(string.IsNullOrEmpty(request3IfNoneMatch));
    }

    [TestMethod]
    public async Task Test34_FailureDetail_IsSanitizedAndDoesNotExposeSensitiveMessage()
    {
        const string secretSentinel = "SUPER_SECRET_SENTINEL";
        var handler = new TestHttpMessageHandler
        {
            SyncHandler = _ => throw new HttpRequestException($"Sensitive host details: {secretSentinel}")
        };

        using var service = new UpdateDiscoveryService(
            httpClient: new HttpClient(handler),
            currentVersion: "1.0.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.AreEqual(UpdateCheckState.CheckFailed, result.State);
        Assert.IsFalse(result.Message?.Contains(secretSentinel) == true, "Public result message must not expose sensitive exception detail.");
        Assert.IsFalse(service.LastFailureReason?.Contains(secretSentinel) == true, "LastFailureReason must not expose sensitive exception detail.");
        Assert.AreEqual("Network error during update check.", result.Message);
        Assert.AreEqual("Network error during update check.", service.LastFailureReason);
    }

    #endregion
}
