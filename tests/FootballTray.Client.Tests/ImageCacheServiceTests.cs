using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class ImageCacheServiceTests
{
    private string _tempCacheDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), $"FootballTray_ImageCacheTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempCacheDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempCacheDir))
        {
            try
            {
                Directory.Delete(_tempCacheDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [TestMethod]
    public async Task GetCachedImagePathAsync_DownloadsImageOnce_AndWritesToDisk()
    {
        int networkRequestCount = 0;
        byte[] expectedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };

        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                Interlocked.Increment(ref networkRequestCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
            }
        };

        var service = new ImageCacheService(new HttpClient(handler), _tempCacheDir);
        const string testUrl = "https://media.api-sports.io/football/teams/529.png";

        // First call: Should perform network request and cache to disk
        string? localPath = await service.GetCachedImagePathAsync(testUrl);

        Assert.IsNotNull(localPath, "Cached local path should not be null.");
        Assert.IsTrue(File.Exists(localPath), "Cached file must exist on disk.");
        Assert.AreEqual(1, networkRequestCount, "Should have issued exactly 1 network request.");

        byte[] savedBytes = await File.ReadAllBytesAsync(localPath);
        CollectionAssert.AreEqual(expectedBytes, savedBytes, "Saved bytes must match downloaded payload.");
    }

    [TestMethod]
    public async Task GetCachedImagePathAsync_SubsequentCall_HitsDiskCacheWithoutNetworkRequest()
    {
        int networkRequestCount = 0;
        byte[] expectedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xAA, 0xBB, 0xCC };

        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                Interlocked.Increment(ref networkRequestCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
            }
        };

        var service = new ImageCacheService(new HttpClient(handler), _tempCacheDir);
        const string testUrl = "https://media.api-sports.io/football/teams/541.png";

        // Call 1
        string? path1 = await service.GetCachedImagePathAsync(testUrl);
        Assert.AreEqual(1, networkRequestCount, "First call should execute network request.");
        Assert.IsNotNull(path1);

        // Call 2: Should hit disk cache immediately
        string? path2 = await service.GetCachedImagePathAsync(testUrl);
        Assert.AreEqual(1, networkRequestCount, "Second call must NOT make a network request.");
        Assert.AreEqual(path1, path2, "Second call should return the exact same cached local path.");
    }

    [TestMethod]
    public async Task GetCachedImagePathAsync_OnHttp404_CachesFailureStateAndAvoidsRetrying()
    {
        int networkRequestCount = 0;

        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                Interlocked.Increment(ref networkRequestCount);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var service = new ImageCacheService(new HttpClient(handler), _tempCacheDir);
        const string testUrl = "https://media.api-sports.io/football/teams/missing_crest.png";

        // Call 1: Receives 404
        string? path1 = await service.GetCachedImagePathAsync(testUrl);
        Assert.IsNull(path1, "Path should be null on 404.");
        Assert.AreEqual(1, networkRequestCount, "Should have attempted 1 network request.");
        Assert.IsTrue(service.IsUrlInFailureState(testUrl), "URL should be marked in failure state.");

        // Call 2: Should return null immediately without triggering a new network request
        string? path2 = await service.GetCachedImagePathAsync(testUrl);
        Assert.IsNull(path2, "Path should remain null on repeated call.");
        Assert.AreEqual(1, networkRequestCount, "Should NOT repeat network request while in failure cooldown.");
    }

    [TestMethod]
    public async Task GetCachedImageUriAsync_ReturnsValidFileUri_WhenCached()
    {
        byte[] expectedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01 };

        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBytes)
            }
        };

        var service = new ImageCacheService(new HttpClient(handler), _tempCacheDir);
        const string testUrl = "https://media.api-sports.io/football/competitions/140.png";

        Uri? fileUri = await service.GetCachedImageUriAsync(testUrl);

        Assert.IsNotNull(fileUri, "Cached URI should not be null.");
        Assert.IsTrue(fileUri.IsAbsoluteUri, "Cached URI must be absolute.");
        Assert.IsTrue(fileUri.IsFile, "Cached URI scheme should be file.");
        Assert.IsTrue(File.Exists(fileUri.LocalPath), "Target file referenced by URI must exist on disk.");
    }

    [TestMethod]
    public async Task GetCachedImageUriAsync_ReturnsFallbackShield_WhenRequestedOnFailure()
    {
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };

        var service = new ImageCacheService(new HttpClient(handler), _tempCacheDir);
        const string testUrl = "https://media.api-sports.io/football/teams/server_error.png";

        // Without fallback: returns null
        Uri? noFallbackUri = await service.GetCachedImageUriAsync(testUrl, returnFallbackOnFailure: false);
        Assert.IsNull(noFallbackUri, "Should return null when returnFallbackOnFailure is false.");

        // With fallback: returns default shield URI
        Uri? fallbackUri = await service.GetCachedImageUriAsync(testUrl, returnFallbackOnFailure: true);
        Assert.IsNotNull(fallbackUri, "Should return fallback shield URI.");
        Assert.AreEqual(ImageCacheService.DefaultFallbackShieldUri, fallbackUri);
    }

    [TestMethod]
    public async Task GetCachedImagePathAsync_CoalescesConcurrentInFlightRequests()
    {
        int networkRequestCount = 0;
        byte[] expectedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x12, 0x34 };

        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                Interlocked.Increment(ref networkRequestCount);
                // Introduce brief latency to guarantee concurrency overlap
                Thread.Sleep(50);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
            }
        };

        var service = new ImageCacheService(new HttpClient(handler), _tempCacheDir);
        const string testUrl = "https://media.api-sports.io/football/teams/concurrent_test.png";

        // Launch 5 concurrent calls for the exact same URL
        var t1 = service.GetCachedImagePathAsync(testUrl);
        var t2 = service.GetCachedImagePathAsync(testUrl);
        var t3 = service.GetCachedImagePathAsync(testUrl);
        var t4 = service.GetCachedImagePathAsync(testUrl);
        var t5 = service.GetCachedImagePathAsync(testUrl);

        string?[] results = await Task.WhenAll(t1, t2, t3, t4, t5);

        Assert.AreEqual(1, networkRequestCount, "In-flight downloads must be coalesced into exactly 1 HTTP request.");
        foreach (var path in results)
        {
            Assert.IsNotNull(path);
            Assert.AreEqual(results[0], path, "All concurrent tasks must resolve to the identical local file path.");
        }
    }

    [TestMethod]
    public async Task GetCachedImagePathAsync_NullOrEmptyUrl_ReturnsNullImmediately()
    {
        var service = new ImageCacheService(new HttpClient(new MockHttpMessageHandler()), _tempCacheDir);

        Assert.IsNull(await service.GetCachedImagePathAsync(null));
        Assert.IsNull(await service.GetCachedImagePathAsync(""));
        Assert.IsNull(await service.GetCachedImagePathAsync("   "));
        Assert.IsNull(await service.GetCachedImageUriAsync(null));
    }
}
