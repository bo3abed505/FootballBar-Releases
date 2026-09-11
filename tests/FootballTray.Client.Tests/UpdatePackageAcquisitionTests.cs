using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Updates;
using FootballTray.Client.ViewModels;
using FootballTray.Core;
using FootballTray.Core.Dto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
[DoNotParallelize]
public class UpdatePackageAcquisitionTests
{
    private string _tempDir = string.Empty;
    private PreferencesService _prefService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ft_acq_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _prefService = new PreferencesService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    private static (ReleaseManifest Manifest, byte[] Data) CreateTestPackage(
        string version = "1.1.0",
        int sizeBytes = 4096,
        string? downloadUrl = null,
        string? corruptedSha = null)
    {
        byte[] data = new byte[sizeBytes];
        new Random(42).NextBytes(data);
        string sha = corruptedSha ?? ComputeSha256(data);

        var manifest = new ReleaseManifest
        {
            SchemaVersion = "1.0",
            Version = version,
            PublishedAt = DateTimeOffset.UtcNow,
            Architecture = "win-x64",
            DownloadUrl = downloadUrl ?? $"https://example.com/FootballTray-v{version}-win-x64.zip",
            Sha256 = sha,
            SizeBytes = sizeBytes,
            ReleaseNotes = "Test notes"
        };

        return (manifest, data);
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
        public int RequestCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            if (Handler != null)
            {
                return await Handler(request, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    #region Required Tests 1-10: Basic Streaming, Verification & Error Boundaries

    [TestMethod]
    public void Test01_InitialState_IsNotStarted()
    {
        using var service = new UpdatePackageAcquisitionService(
            baseDirectory: _tempDir);

        Assert.AreEqual(UpdateAcquisitionState.NotStarted, service.CurrentState);
        Assert.IsNull(service.CurrentVerifiedPackagePath);
        Assert.IsNull(service.LastFailureReason);
    }

    [TestMethod]
    public async Task Test02_ValidPackage_CorrectSizeAndSha_ReturnsVerified()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        Assert.IsTrue(result.IsVerified);
        Assert.AreEqual("1.1.0", result.Version);
        Assert.IsNotNull(result.LocalPackagePath);
        Assert.IsTrue(File.Exists(result.LocalPackagePath));
        Assert.AreEqual(2048, new FileInfo(result.LocalPackagePath).Length);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Test03_StreamedFileContents_EqualSourcePayload()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 8192);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.IsTrue(result.IsVerified);
        byte[] writtenBytes = await File.ReadAllBytesAsync(result.LocalPackagePath!);
        CollectionAssert.AreEqual(data, writtenBytes);
    }

    [TestMethod]
    public async Task Test04_ShaMismatch_ReturnsFailed_DeletesPartial_NoFinalZip()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048, corruptedSha: new string('0', 64));
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.IsFalse(result.IsVerified);
        Assert.AreEqual("Downloaded update failed integrity verification.", result.Message);

        string expectedDir = Path.Combine(_tempDir, "1.1.0");
        string finalZip = Path.Combine(expectedDir, "FootballTray-v1.1.0-win-x64.zip");
        string partZip = Path.Combine(expectedDir, "FootballTray-v1.1.0-win-x64.zip.part");

        Assert.IsFalse(File.Exists(finalZip));
        Assert.IsFalse(File.Exists(partZip));
    }

    [TestMethod]
    public async Task Test05_SizeSmallerThanManifest_ReturnsFailed()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        byte[] smallerData = new byte[2048];
        Array.Copy(data, smallerData, 2048);

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Omitting Content-Length so it streams to EOF
                Content = new StreamContent(new MemoryStream(smallerData))
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Downloaded update size did not match the release manifest.", result.Message);
    }

    [TestMethod]
    public async Task Test06_SizeLargerThanManifest_AbortsWithoutExceedingExpectedBytes()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 1000);
        byte[] oversizedData = new byte[3000];

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(oversizedData))
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Downloaded update size did not match the release manifest.", result.Message);

        string finalZip = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip");
        string partZip = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip.part");
        Assert.IsFalse(File.Exists(finalZip));
        Assert.IsFalse(File.Exists(partZip));
    }

    [TestMethod]
    public async Task Test07_ContentLengthMismatch_FailsSafelyBeforeStreaming()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        var content = new ByteArrayContent(data);
        content.Headers.ContentLength = 2000; // Mismatch with manifest 4096

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Downloaded update size did not match the release manifest.", result.Message);
    }

    [TestMethod]
    public async Task Test08_Http404_ReturnsFailed()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Update package not found on download server (HTTP 404).", result.Message);
    }

    [TestMethod]
    public async Task Test09_Http500_ReturnsFailed()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Update download server error (HTTP 500).", result.Message);
    }

    [TestMethod]
    public async Task Test10_NetworkException_ReturnsFailedWithSanitizedDetail()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => throw new HttpRequestException("Raw connection reset by peer with sensitive IP 192.168.1.50")
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Unable to download update.", result.Message);
        Assert.IsFalse(result.Message?.Contains("192.168.1.50") ?? false);
    }

    #endregion

    #region Required Tests 11-20: Concurrency, Cancellation, Revalidation & Safety

    [TestMethod]
    public async Task Test11_CallerCancellation_CancelsOnlyThatCallerWait()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        var downloadTcs = new TaskCompletionSource<HttpResponseMessage>();

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => downloadTcs.Task
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        using var callerACts = new CancellationTokenSource();
        var taskA = service.AcquirePackageAsync(manifest, cancellationToken: callerACts.Token);
        var taskB = service.AcquirePackageAsync(manifest, cancellationToken: CancellationToken.None);

        Assert.AreEqual(1, handler.RequestCount);

        // Cancel Caller A
        callerACts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await taskA);

        // Complete underlying download for Caller B
        downloadTcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        });

        var resultB = await taskB;
        Assert.AreEqual(UpdateAcquisitionState.Verified, resultB.State);
        Assert.IsTrue(resultB.IsVerified);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Test12_ServiceShutdownCancellation_CleansPartialFile()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var streamTcs = new TaskCompletionSource<int>();

        var mockStream = new BlockedReadStream(streamTcs.Task);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(mockStream)
            })
        };

        var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var task = service.AcquirePackageAsync(manifest);

        // Give the service time to write initial partial header / start streaming
        await Task.Delay(20);

        // Dispose service (triggers application shutdown)
        service.Dispose();

        var result = await task;
        Assert.AreEqual(UpdateAcquisitionState.Cancelled, result.State);

        string partZip = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip.part");
        Assert.IsFalse(File.Exists(partZip));
    }

    [TestMethod]
    public async Task Test13_OverlappingAcquisitions_SamePackage_ExactlyOneNetworkInvocation()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        var downloadTcs = new TaskCompletionSource<HttpResponseMessage>();

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => downloadTcs.Task
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var task1 = service.AcquirePackageAsync(manifest);
        var task2 = service.AcquirePackageAsync(manifest);

        Assert.AreEqual(1, handler.RequestCount);

        downloadTcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        });

        var res1 = await task1;
        var res2 = await task2;

        Assert.AreEqual(UpdateAcquisitionState.Verified, res1.State);
        Assert.AreEqual(UpdateAcquisitionState.Verified, res2.State);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Test14_ExistingValidZip_RevalidatedWithoutNetwork()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        string versionDir = Path.Combine(_tempDir, "1.1.0");
        Directory.CreateDirectory(versionDir);
        string finalPath = Path.Combine(versionDir, "FootballTray-v1.1.0-win-x64.zip");
        await File.WriteAllBytesAsync(finalPath, data);

        var handler = new TestHttpMessageHandler();

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        Assert.AreEqual(finalPath, result.LocalPackagePath);
        Assert.AreEqual(0, handler.RequestCount); // ZERO network requests
    }

    [TestMethod]
    public async Task Test15_ExistingInvalidZip_RejectedAndReplacedOnPromotion()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        string versionDir = Path.Combine(_tempDir, "1.1.0");
        Directory.CreateDirectory(versionDir);
        string finalPath = Path.Combine(versionDir, "FootballTray-v1.1.0-win-x64.zip");
        await File.WriteAllBytesAsync(finalPath, new byte[4096]); // Corrupted bytes (wrong hash)

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        Assert.AreEqual(1, handler.RequestCount); // Fresh download requested
        byte[] replacedBytes = await File.ReadAllBytesAsync(finalPath);
        CollectionAssert.AreEqual(data, replacedBytes);
    }

    [TestMethod]
    public async Task Test16_NonHttpsDownloadUrl_FailsBeforeNetwork()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096, downloadUrl: "http://example.com/unencrypted.zip");
        var handler = new TestHttpMessageHandler();

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual(0, handler.RequestCount);
        StringAssert.Contains(result.Message, "HTTPS");
    }

    [TestMethod]
    public async Task Test17_DeterministicSafeLocalFilenameAndPath()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        string expectedPath = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip");
        Assert.AreEqual(expectedPath, result.LocalPackagePath);
    }

    [TestMethod]
    public async Task Test18_Progress_ClampedAndClimbsTo100()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 4096);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var reportedProgress = new List<UpdateDownloadProgress>();
        var progress = new Progress<UpdateDownloadProgress>(p => reportedProgress.Add(p));

        await service.AcquirePackageAsync(manifest, progress);

        // Give Progress<T> callback time to dispatch
        await Task.Delay(20);

        Assert.IsTrue(reportedProgress.Count >= 2);
        Assert.AreEqual(0, reportedProgress[0].Percent);
        Assert.AreEqual(100, reportedProgress[^1].Percent);
        foreach (var p in reportedProgress)
        {
            Assert.IsTrue(p.Percent >= 0 && p.Percent <= 100);
        }
    }

    [TestMethod]
    public async Task Test19_VerifiedResult_ExposesLocalZipPath()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.IsNotNull(result.LocalPackagePath);
        Assert.IsTrue(Path.IsPathRooted(result.LocalPackagePath));
        Assert.AreEqual(result.LocalPackagePath, service.CurrentVerifiedPackagePath);
    }

    [TestMethod]
    public async Task Test20_AcquisitionFailure_DoesNotMutateFootballHealth()
    {
        var diagService = new DiagnosticsService(syncService: null, preferencesService: _prefService);
        diagService.RecordTaskbarStatus("Attached");
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await acqService.AcquirePackageAsync(manifest);
        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);

        var snapshot = diagService.GetCurrentSnapshot();
        Assert.AreEqual(ApplicationHealthState.Healthy, snapshot.HealthState);
        Assert.IsNull(snapshot.LastErrorSummary);
    }

    #endregion

    #region Required Tests 21-25: ViewModel and UX Integration

    [TestMethod]
    public async Task Test21_DiscoveryUpdateAvailable_RemainsValidIfAcquisitionFails()
    {
        var diagService = new DiagnosticsService(syncService: null, preferencesService: _prefService);
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);

        var fakeDiscovery = new FakeUpdateDiscoveryService
        {
            CurrentVersion = SemanticVersion.Parse("1.0.0"),
            CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
                SemanticVersion.Parse("1.0.0"),
                SemanticVersion.Parse("1.1.0"),
                manifest,
                "\"etag-1\"",
                DateTimeOffset.UtcNow))
        };

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var vm = new MainViewModel(
            _prefService,
            syncService: null,
            diagnosticsService: diagService,
            dispatcherQueue: null,
            updateDiscoveryService: fakeDiscovery,
            delayFunc: (d, t) => Task.CompletedTask,
            updateAcquisitionService: acqService);

        await vm.CheckForUpdatesAsync(isManual: true);
        Assert.IsTrue(vm.IsUpdateAvailable);
        Assert.AreEqual("1.1.0", vm.LatestVersion);
        Assert.IsTrue(vm.CanDownloadUpdate);

        // Attempt download -> fails
        var acqResult = await vm.DownloadUpdateAsync();
        Assert.AreEqual(UpdateAcquisitionState.Failed, acqResult?.State);

        // Discovery UpdateAvailable remains fully intact
        Assert.IsTrue(vm.IsUpdateAvailable);
        Assert.AreEqual("1.1.0", vm.LatestVersion);
        Assert.AreEqual(manifest, vm.OfferedManifest);
        Assert.IsTrue(vm.CanDownloadUpdate); // User can retry download
    }

    [TestMethod]
    public void Test22_ApplicationShutdown_DisposesAcquisitionServiceSafely()
    {
        var service = new UpdatePackageAcquisitionService(baseDirectory: _tempDir);
        service.Dispose();
        // Disposing second time should not throw
        service.Dispose();
    }

    [TestMethod]
    public async Task Test23_UiDownloadButton_InvokesService()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var fakeDiscovery = new FakeUpdateDiscoveryService
        {
            CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
                SemanticVersion.Parse("1.0.0"),
                SemanticVersion.Parse("1.1.0"),
                manifest,
                "\"etag-1\"",
                DateTimeOffset.UtcNow))
        };

        var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fakeDiscovery,
            updateAcquisitionService: acqService);

        await vm.CheckForUpdatesAsync(isManual: true);
        Assert.IsTrue(vm.CanDownloadUpdate);

        var result = await vm.DownloadUpdateAsync();
        Assert.IsNotNull(result);
        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task Test24_UiStateTransitions_DownloadingVerifyingVerifiedFailed()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048);
        var downloadTcs = new TaskCompletionSource<HttpResponseMessage>();

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => downloadTcs.Task
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var fakeDiscovery = new FakeUpdateDiscoveryService
        {
            CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
                SemanticVersion.Parse("1.0.0"),
                SemanticVersion.Parse("1.1.0"),
                manifest,
                "\"etag-1\"",
                DateTimeOffset.UtcNow))
        };

        var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fakeDiscovery,
            updateAcquisitionService: acqService);

        await vm.CheckForUpdatesAsync(isManual: true);

        var downloadTask = vm.DownloadUpdateAsync();
        Assert.IsTrue(vm.IsAcquiringUpdate);
        Assert.AreEqual(UpdateAcquisitionState.Downloading, vm.AcquisitionState);
        Assert.IsFalse(vm.CanDownloadUpdate);

        downloadTcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        });

        await downloadTask;
        Assert.IsFalse(vm.IsAcquiringUpdate);
        Assert.AreEqual(UpdateAcquisitionState.Verified, vm.AcquisitionState);
        Assert.IsTrue(vm.IsUpdateDownloaded);
        Assert.AreEqual("Update downloaded and verified.", vm.DownloadStatusText);
        Assert.IsFalse(vm.CanDownloadUpdate);
    }

    [TestMethod]
    public async Task Test25_VerifiedState_DoesNotExposeInstallOrRestart()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 2048);
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data)
            })
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var fakeDiscovery = new FakeUpdateDiscoveryService
        {
            CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
                SemanticVersion.Parse("1.0.0"),
                SemanticVersion.Parse("1.1.0"),
                manifest,
                "\"etag-1\"",
                DateTimeOffset.UtcNow))
        };

        var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fakeDiscovery,
            updateAcquisitionService: acqService);

        await vm.CheckForUpdatesAsync(isManual: true);
        await vm.DownloadUpdateAsync();

        Assert.IsTrue(vm.IsUpdateDownloaded);
        // Verify installation is disabled when no installation service is provided and no silent restart exists
        Assert.IsFalse(vm.CanInstallUpdate);
        Assert.IsFalse(vm.IsInstallingUpdate);
        var vmType = typeof(MainViewModel);
        Assert.IsNull(vmType.GetMethod("RestartAndInstall"));
    }

    #endregion

    #region Refinement Tests 26-31: Package-Aware Overlap, Offer Replacement, Stalls & Boundaries

    [TestMethod]
    public async Task Test26_DifferentPackageOverlap_DoesNotJoinWrongPackage_ReturnsSafeBusy()
    {
        var (manifestA, _) = CreateTestPackage("1.1.0", 2048);
        var (manifestB, _) = CreateTestPackage("1.2.0", 4096);

        var downloadATcs = new TaskCompletionSource<HttpResponseMessage>();

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => downloadATcs.Task
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var taskA = service.AcquirePackageAsync(manifestA);

        // Caller requesting different package B while package A is in flight
        var taskB = service.AcquirePackageAsync(manifestB);
        var resultB = await taskB;

        Assert.AreEqual(UpdateAcquisitionState.Failed, resultB.State);
        Assert.AreEqual("Another update package acquisition is already in progress.", resultB.Message);
        Assert.AreNotEqual(manifestA.Version, resultB.Version);

        // Complete A cleanly
        var (_, dataA) = CreateTestPackage("1.1.0", 2048);
        downloadATcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(dataA)
        });

        var resultA = await taskA;
        Assert.AreEqual(UpdateAcquisitionState.Verified, resultA.State);
        Assert.AreEqual("1.1.0", resultA.Version);
    }

    [TestMethod]
    public async Task Test27_NewOfferAfterVerifiedPackage_ResetsAcquisitionPresentation()
    {
        var (manifestV1, dataV1) = CreateTestPackage("1.1.0", 2048);
        var (manifestV2, dataV2) = CreateTestPackage("1.2.0", 4096);

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) =>
            {
                if (req.RequestUri!.ToString().Contains("1.1.0"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dataV1) });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dataV2) });
            }
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var fakeDiscovery = new FakeUpdateDiscoveryService();
        var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fakeDiscovery,
            updateAcquisitionService: acqService);

        // 1. Discover v1.1.0
        fakeDiscovery.CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifestV1,
            "\"etag-1\"",
            DateTimeOffset.UtcNow));
        await vm.CheckForUpdatesAsync(isManual: true);

        // 2. Download and verify v1.1.0
        await vm.DownloadUpdateAsync();
        Assert.IsTrue(vm.IsUpdateDownloaded);
        Assert.AreEqual(UpdateAcquisitionState.Verified, vm.AcquisitionState);
        string v1VerifiedPath = vm.VerifiedPackagePath!;
        Assert.IsTrue(v1VerifiedPath.Contains("1.1.0"));

        // 3. New discovery yields v1.2.0
        fakeDiscovery.CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.2.0"),
            manifestV2,
            "\"etag-2\"",
            DateTimeOffset.UtcNow));
        var result2 = await vm.CheckForUpdatesAsync(isManual: true);
        Assert.AreEqual("1.2.0", result2?.OfferedManifest?.Version);
        Assert.AreEqual("1.2.0", vm.OfferedManifest?.Version);

        // 4. Acquisition presentation must reset for newly offered v1.2.0
        Assert.AreEqual(UpdateAcquisitionState.NotStarted, vm.AcquisitionState);
        Assert.IsNull(vm.VerifiedPackagePath);
        Assert.AreEqual(0, vm.DownloadProgressPercent);
        Assert.IsTrue(vm.CanDownloadUpdate);
        Assert.IsFalse(vm.IsUpdateDownloaded);

        // Old verified file still preserved on disk in its version directory
        Assert.IsTrue(File.Exists(v1VerifiedPath));
    }

    [TestMethod]
    public async Task Test28_StreamingStallTimeout_TimesOutAndDeletesPartial()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var stalledStream = new StalledReadStream();

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stalledStream)
            })
        };

        // Short stall timeout of 50ms for deterministic test
        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir,
            headerTimeout: TimeSpan.FromSeconds(5),
            stallTimeout: TimeSpan.FromMilliseconds(50));

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Update download timed out.", result.Message);

        string partZip = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip.part");
        Assert.IsFalse(File.Exists(partZip));
    }

    [TestMethod]
    public async Task Test29_ProgressingSlowStream_SucceedsAcrossMultipleStallIntervals()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 300);
        // 3 chunks of 100 bytes each, delayed by 30ms.
        // Total time ~90ms, which exceeds a 50ms single-interval stall timeout,
        // but each individual chunk arrives well within 50ms.
        var slowStream = new ChunkedDelayedStream(data, chunkSize: 100, delay: TimeSpan.FromMilliseconds(20));

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(slowStream)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir,
            headerTimeout: TimeSpan.FromSeconds(5),
            stallTimeout: TimeSpan.FromMilliseconds(100));

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        Assert.IsTrue(result.IsVerified);
    }

    [TestMethod]
    public async Task Test30_OversizeWriteBoundary_NeverPersistsMoreThanExpectedBytes()
    {
        var (manifest, data) = CreateTestPackage("1.1.0", 100);
        // Server sends 500 bytes
        byte[] oversizedData = new byte[500];

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(oversizedData))
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var result = await service.AcquirePackageAsync(manifest);

        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreEqual("Downloaded update size did not match the release manifest.", result.Message);

        string finalZip = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip");
        string partZip = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip.part");
        Assert.IsFalse(File.Exists(finalZip));
        Assert.IsFalse(File.Exists(partZip));
    }

    [TestMethod]
    public async Task Test31_ExistingFileIdentity_DifferentVersionCannotSatisfy()
    {
        var (manifestV1, dataV1) = CreateTestPackage("1.1.0", 2048);
        var (manifestV2, dataV2) = CreateTestPackage("1.2.0", 2048);

        // Create verified file on disk for v1.1.0
        string v1Dir = Path.Combine(_tempDir, "1.1.0");
        Directory.CreateDirectory(v1Dir);
        await File.WriteAllBytesAsync(Path.Combine(v1Dir, "FootballTray-v1.1.0-win-x64.zip"), dataV1);

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(dataV2)
            })
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        // Acquire v1.2.0
        var result = await service.AcquirePackageAsync(manifestV2);

        Assert.AreEqual(UpdateAcquisitionState.Verified, result.State);
        Assert.AreEqual("1.2.0", result.Version);
        Assert.IsTrue(result.LocalPackagePath!.Contains("1.2.0"));
        Assert.AreEqual(1, handler.RequestCount); // Must download v1.2.0, not reuse v1.1.0
    }

    [TestMethod]
    public async Task Test32_InFlightOldPackage_NewOfferArrives_IgnoresStaleProgressAndCompletion()
    {
        var (manifestV1, dataV1) = CreateTestPackage("1.1.0", 4096);
        var (manifestV2, dataV2) = CreateTestPackage("1.2.0", 8192);

        var chunk1Tcs = new TaskCompletionSource<int>();
        var finishTcs = new TaskCompletionSource<int>();

        var streamV1 = new ControlledChunkStream(dataV1, chunk1Tcs, finishTcs);

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(streamV1)
            })
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var fakeDiscovery = new FakeUpdateDiscoveryService();
        var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fakeDiscovery,
            updateAcquisitionService: acqService);

        // 1. Offer v1.1.0
        fakeDiscovery.CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifestV1,
            "\"etag-1\"",
            DateTimeOffset.UtcNow));
        await vm.CheckForUpdatesAsync(isManual: true);
        Assert.IsTrue(vm.CanDownloadUpdate);

        // 2. Start v1.1.0 acquisition
        var downloadTask = vm.DownloadUpdateAsync();
        Assert.AreEqual(UpdateAcquisitionState.Downloading, vm.AcquisitionState);

        // 3. Emit initial v1.1.0 progress
        chunk1Tcs.SetResult(1024);
        await Task.Yield();

        // 4. Discovery changes offer to v1.2.0 while v1.1.0 is still downloading
        fakeDiscovery.CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.2.0"),
            manifestV2,
            "\"etag-2\"",
            DateTimeOffset.UtcNow));
        await vm.CheckForUpdatesAsync(isManual: true);

        // 5. Presentation reset for v1.2.0
        Assert.AreEqual(UpdateAcquisitionState.NotStarted, vm.AcquisitionState);
        Assert.IsNull(vm.VerifiedPackagePath);
        Assert.AreEqual(0, vm.DownloadProgressPercent);
        // CanDownloadUpdate remains FALSE while incompatible old transfer is running
        Assert.IsFalse(vm.CanDownloadUpdate);

        // 6. Emit late v1.1.0 progress - must NOT overwrite v1.2.0 presentation
        finishTcs.SetResult(3072);

        // 7. Old v1.1.0 transfer finishes Verified
        var oldResult = await downloadTask;
        Assert.IsNotNull(oldResult);
        Assert.AreEqual(UpdateAcquisitionState.Verified, oldResult.State);
        Assert.AreEqual("1.1.0", oldResult.Version);

        // 8. Assert v1.2.0 presentation was NOT overwritten by v1.1.0 completion
        Assert.AreEqual(manifestV2.Version, vm.OfferedManifest?.Version);
        Assert.AreEqual(UpdateAcquisitionState.NotStarted, vm.AcquisitionState);
        Assert.IsNull(vm.VerifiedPackagePath);
        Assert.AreEqual(0, vm.DownloadProgressPercent);

        // 9. Now that old task has fully finished, CanDownloadUpdate becomes true for v1.2.0!
        Assert.IsTrue(vm.CanDownloadUpdate);
    }

    [TestMethod]
    public async Task Test33_DifferentPackage_ViewModelDownloadAsync_ReturnsSafeBusy_DoesNotJoinOldTask()
    {
        var (manifestV1, dataV1) = CreateTestPackage("1.1.0", 2048);
        var (manifestV2, dataV2) = CreateTestPackage("1.2.0", 4096);

        var downloadV1Tcs = new TaskCompletionSource<int>();
        var finishV1Tcs = new TaskCompletionSource<int>();
        var streamV1 = new ControlledChunkStream(dataV1, downloadV1Tcs, finishV1Tcs);

        int networkInvocationCount = 0;
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) =>
            {
                Interlocked.Increment(ref networkInvocationCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(streamV1)
                });
            }
        };

        using var acqService = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        var fakeDiscovery = new FakeUpdateDiscoveryService();
        var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fakeDiscovery,
            updateAcquisitionService: acqService);

        // 1. Offer v1.1.0 and begin downloading
        fakeDiscovery.CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifestV1,
            "\"etag-1\"",
            DateTimeOffset.UtcNow));
        await vm.CheckForUpdatesAsync(isManual: true);

        var taskV1 = vm.DownloadUpdateAsync();
        Assert.AreEqual(UpdateAcquisitionState.Downloading, vm.AcquisitionState);

        // 2. Discover v1.2.0
        fakeDiscovery.CustomHandler = ct => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.2.0"),
            manifestV2,
            "\"etag-2\"",
            DateTimeOffset.UtcNow));
        await vm.CheckForUpdatesAsync(isManual: true);

        // 3. UI button is disabled during the incompatible old transfer
        Assert.IsFalse(vm.CanDownloadUpdate);

        // 4. Programmatic call to DownloadUpdateAsync for v1.2.0 while v1.1.0 is active
        var resultV2 = await vm.DownloadUpdateAsync();
        Assert.IsNotNull(resultV2);
        Assert.AreEqual(UpdateAcquisitionState.Failed, resultV2.State);
        Assert.AreEqual("Another update package acquisition is already in progress.", resultV2.Message);
        Assert.AreEqual("1.2.0", resultV2.Version);

        // Assert no second network transfer occurred
        Assert.AreEqual(1, networkInvocationCount);

        // 5. Complete v1.1.0
        downloadV1Tcs.SetResult(1024);
        finishV1Tcs.SetResult(1024);
        var resultV1 = await taskV1;
        Assert.IsNotNull(resultV1);
        Assert.AreEqual(UpdateAcquisitionState.Verified, resultV1.State);
        Assert.AreEqual("1.1.0", resultV1.Version);

        // 6. v1.2.0 is now available to download
        Assert.IsTrue(vm.CanDownloadUpdate);
    }

    [TestMethod]
    public async Task Test34_ServiceBusy_DifferentPackage_DoesNotMutateActiveTransferState()
    {
        var (manifestA, _) = CreateTestPackage("1.1.0", 2048);
        var (manifestB, _) = CreateTestPackage("1.2.0", 4096);

        var downloadATcs = new TaskCompletionSource<HttpResponseMessage>();

        int networkInvocationCount = 0;
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) =>
            {
                Interlocked.Increment(ref networkInvocationCount);
                return downloadATcs.Task;
            }
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        // 1. Start v1.1.0 acquisition
        var taskA = service.AcquirePackageAsync(manifestA);
        await Task.Yield();

        // Confirm active state is Downloading
        Assert.AreEqual(UpdateAcquisitionState.Downloading, service.CurrentState);
        Assert.IsNull(service.LastFailureReason);

        // 2. Call AcquirePackageAsync for v1.2.0
        var taskB = service.AcquirePackageAsync(manifestB);
        var resultB = await taskB;

        // 3. Busy caller receives safe Failed result
        Assert.AreEqual(UpdateAcquisitionState.Failed, resultB.State);
        Assert.AreEqual("Another update package acquisition is already in progress.", resultB.Message);
        Assert.AreEqual("1.2.0", resultB.Version);

        // 4. Assert service.CurrentState remains Downloading for v1.1.0 and was NOT mutated
        Assert.AreEqual(UpdateAcquisitionState.Downloading, service.CurrentState);
        Assert.IsNull(service.LastFailureReason);
        Assert.IsNull(service.CurrentVerifiedPackagePath);

        // 5. Complete v1.1.0 successfully
        var (_, dataA) = CreateTestPackage("1.1.0", 2048);
        downloadATcs.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(dataA)
        });

        var resultA = await taskA;
        Assert.AreEqual(UpdateAcquisitionState.Verified, resultA.State);
        Assert.AreEqual("1.1.0", resultA.Version);

        // Final service state is Verified for v1.1.0
        Assert.AreEqual(UpdateAcquisitionState.Verified, service.CurrentState);
        Assert.IsNotNull(service.CurrentVerifiedPackagePath);
        Assert.IsTrue(service.CurrentVerifiedPackagePath.Contains("1.1.0"));

        // Network invocation count was exactly 1
        Assert.AreEqual(1, networkInvocationCount);
    }

    [TestMethod]
    public async Task Test35_InvalidExistingFile_FailedRedownload_NeverReturnsInvalidAsVerified()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 2048);
        string v1Dir = Path.Combine(_tempDir, "1.1.0");
        Directory.CreateDirectory(v1Dir);
        string finalPath = Path.Combine(v1Dir, "FootballTray-v1.1.0-win-x64.zip");

        // 1. Write corrupt/incorrect file at deterministic final path
        byte[] corruptData = new byte[2048];
        Array.Fill(corruptData, (byte)0xFF);
        await File.WriteAllBytesAsync(finalPath, corruptData);

        // 2. Network download fails (e.g. HTTP 500)
        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };

        using var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        // 3. Acquire package
        var result = await service.AcquirePackageAsync(manifest);

        // 4. Result must NOT be Verified
        Assert.AreEqual(UpdateAcquisitionState.Failed, result.State);
        Assert.AreNotEqual(UpdateAcquisitionState.Verified, service.CurrentState);
        Assert.IsNull(service.CurrentVerifiedPackagePath);

        // 5. Corrupt file is still on disk, but .part was cleaned
        Assert.IsTrue(File.Exists(finalPath));
        Assert.IsFalse(File.Exists(finalPath + ".part"));

        // 6. Second call to AcquirePackageAsync still rejects the corrupt file
        var retryResult = await service.AcquirePackageAsync(manifest);
        Assert.AreEqual(UpdateAcquisitionState.Failed, retryResult.State);
        Assert.AreNotEqual(UpdateAcquisitionState.Verified, service.CurrentState);
        Assert.IsNull(service.CurrentVerifiedPackagePath);
    }

    [TestMethod]
    public async Task Test36_ActiveDispose_CancelsTransfer_NoObjectDisposedException_CleansPartial()
    {
        var (manifest, _) = CreateTestPackage("1.1.0", 4096);
        var readBlockedTcs = new TaskCompletionSource<int>();
        var readEnteredTcs = new TaskCompletionSource<bool>();

        var blockedStream = new SignalingBlockedReadStream(readEnteredTcs, readBlockedTcs.Task);

        var handler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(blockedStream)
            })
        };

        var service = new UpdatePackageAcquisitionService(
            httpClient: new HttpClient(handler),
            baseDirectory: _tempDir);

        // 1. Start acquisition
        var acquireTask = service.AcquirePackageAsync(manifest);

        // 2. Wait until streaming body read has actually been entered
        await readEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 3. Confirm .part exists
        string partPath = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip.part");
        Assert.IsTrue(File.Exists(partPath));

        // 4. Dispose service during active transfer
        service.Dispose();

        // 5. Calling Dispose a second time must be idempotent
        service.Dispose();

        // 6. Await acquisition task - must complete cleanly without ObjectDisposedException
        var result = await acquireTask;
        Assert.AreEqual(UpdateAcquisitionState.Cancelled, result.State);

        // 7. Confirm .part was cleaned up and no final file was created
        Assert.IsFalse(File.Exists(partPath));
        string finalPath = Path.Combine(_tempDir, "1.1.0", "FootballTray-v1.1.0-win-x64.zip");
        Assert.IsFalse(File.Exists(finalPath));

        // 8. New acquisition after Dispose must throw ObjectDisposedException
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await service.AcquirePackageAsync(manifest));
    }

    #endregion

    #region Helper Stream Classes

    private sealed class BlockedReadStream : Stream
    {
        private readonly Task<int> _blockedTask;
        public BlockedReadStream(Task<int> blockedTask) => _blockedTask = blockedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var reg = cancellationToken.Register(() => { });
            return await _blockedTask.WaitAsync(cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return await _blockedTask.WaitAsync(cancellationToken);
        }
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Hang until cancelled by caller stall token
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class ChunkedDelayedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private readonly TimeSpan _delay;
        private int _position;

        public ChunkedDelayedStream(byte[] data, int chunkSize, TimeSpan delay)
        {
            _data = data;
            _chunkSize = chunkSize;
            _delay = delay;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length) return 0;

            await Task.Delay(_delay, cancellationToken);

            int toRead = Math.Min(_chunkSize, Math.Min(buffer.Length, _data.Length - _position));
            _data.AsMemory(_position, toRead).CopyTo(buffer);
            _position += toRead;
            return toRead;
        }
    }

    private sealed class FakeUpdateDiscoveryService : IUpdateDiscoveryService
    {
        public UpdateCheckState CurrentState { get; set; } = UpdateCheckState.NotChecked;
        public ReleaseManifest? CurrentOfferedManifest { get; set; }
        public DateTimeOffset? LastCheckAttempt { get; set; }
        public DateTimeOffset? LastSuccessfulCheck { get; set; }
        public string? LastKnownETag { get; set; }
        public string? LastFailureReason { get; set; }
        public string FeedUrl { get; set; } = "https://example.com/feed.json";
        public SemanticVersion CurrentVersion { get; set; } = SemanticVersion.Parse("1.0.0");

        public Func<CancellationToken, Task<UpdateCheckResult>>? CustomHandler;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            if (CustomHandler != null)
            {
                return CustomHandler(cancellationToken);
            }
            return Task.FromResult(UpdateCheckResult.UpToDate(CurrentVersion, null, "Up to date", null, DateTimeOffset.UtcNow));
        }

        public void Dispose() { }
    }

    private sealed class ControlledChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly TaskCompletionSource<int> _firstChunkTcs;
        private readonly TaskCompletionSource<int> _secondChunkTcs;
        private int _position;
        private bool _firstChunkRead;

        public ControlledChunkStream(byte[] data, TaskCompletionSource<int> firstChunkTcs, TaskCompletionSource<int> secondChunkTcs)
        {
            _data = data;
            _firstChunkTcs = firstChunkTcs;
            _secondChunkTcs = secondChunkTcs;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= _data.Length) return 0;

            if (!_firstChunkRead)
            {
                int chunkSize = await _firstChunkTcs.Task.WaitAsync(cancellationToken);
                int bytesToCopy = Math.Min(chunkSize, Math.Min(buffer.Length, _data.Length - _position));
                _data.AsSpan(_position, bytesToCopy).CopyTo(buffer.Span);
                _position += bytesToCopy;
                _firstChunkRead = true;
                return bytesToCopy;
            }

            await _secondChunkTcs.Task.WaitAsync(cancellationToken);
            int remaining = _data.Length - _position;
            int count = Math.Min(remaining, buffer.Length);
            _data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }
    }

    private sealed class SignalingBlockedReadStream : Stream
    {
        private readonly TaskCompletionSource<bool> _signalEntered;
        private readonly Task<int> _blockedTask;

        public SignalingBlockedReadStream(TaskCompletionSource<bool> signalEntered, Task<int> blockedTask)
        {
            _signalEntered = signalEntered;
            _blockedTask = blockedTask;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _signalEntered.TrySetResult(true);
            return await _blockedTask.WaitAsync(cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _signalEntered.TrySetResult(true);
            return await _blockedTask.WaitAsync(cancellationToken);
        }
    }

    #endregion
}
