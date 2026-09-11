using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core;
using FootballTray.Core.Dto;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Updates;
using FootballTray.Client.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class UpdateIntegrationAndUxTests
{
    private string _tempDir = string.Empty;
    private PreferencesService _prefService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ft_update_test_{Guid.NewGuid():N}");
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

    private static ReleaseManifest CreateValidManifest(string version = "1.1.0", string releaseNotes = "Bug fixes and improvements.")
    {
        return new ReleaseManifest
        {
            SchemaVersion = "1.0",
            Version = version,
            PublishedAt = DateTimeOffset.UtcNow,
            Architecture = "win-x64",
            DownloadUrl = $"https://example.com/FootballTray-v{version}-win-x64.zip",
            Sha256 = new string('a', 64),
            SizeBytes = 10485760,
            ReleaseNotes = releaseNotes
        };
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

        public int CheckCallCount;
        public int DisposeCallCount;
        public TaskCompletionSource<UpdateCheckResult>? InFlightTcs;
        public Func<CancellationToken, Task<UpdateCheckResult>>? CustomHandler;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CheckCallCount);
            if (InFlightTcs != null)
            {
                return InFlightTcs.Task;
            }
            if (CustomHandler != null)
            {
                return CustomHandler(cancellationToken);
            }
            return Task.FromResult(UpdateCheckResult.UpToDate(CurrentVersion, CurrentVersion, "FootballTray is up to date.", null, DateTimeOffset.UtcNow));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCallCount);
        }
    }

    private sealed class FakeSyncService : ISyncService
    {
        public DataFreshnessState FreshnessState { get; set; } = DataFreshnessState.Fresh;
        public TickerConnectionState CurrentState { get; set; } = TickerConnectionState.OnlineFresh;
        public TrayDataDto? CurrentTrayData { get; set; }
        public DateTimeOffset? LastSuccessfulServerResponseTime { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastRefreshAttempt { get; set; } = DateTimeOffset.UtcNow;
        public string? LastKnownETag { get; set; }
        public DateTimeOffset? LastValidatedAt { get; set; } = DateTimeOffset.UtcNow;

        public event EventHandler<TrayUpdateEventArgs>? TrayUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DataFreshnessState> PollOnceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(FreshnessState);
        public void RefreshTickerOnPreferenceChange() { }
        public void Dispose() { }
    }

    [TestMethod]
    public async Task Test01_ManualCheck_InvokesDiscoveryService_AndExposesCheckingState()
    {
        var fake = new FakeUpdateDiscoveryService();
        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        fake.InFlightTcs = tcs;

        using var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        Assert.AreEqual(UpdateCheckState.NotChecked, vm.UpdateCheckState);
        Assert.IsFalse(vm.IsCheckingForUpdates);
        Assert.IsTrue(vm.CanCheckForUpdates);

        var checkTask = vm.CheckForUpdatesAsync(isManual: true);

        Assert.IsTrue(vm.IsCheckingForUpdates);
        Assert.IsFalse(vm.CanCheckForUpdates);
        Assert.AreEqual("Checking for updates...", vm.UpdateStatusMessage);

        tcs.SetResult(UpdateCheckResult.UpToDate(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.0.0"),
            "FootballTray is up to date.",
            etag: null,
            DateTimeOffset.UtcNow));

        var result = await checkTask;

        Assert.IsNotNull(result);
        Assert.AreEqual(1, fake.CheckCallCount);
        Assert.IsFalse(vm.IsCheckingForUpdates);
        Assert.IsTrue(vm.CanCheckForUpdates);
        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
    }

    [TestMethod]
    public async Task Test02_ManualCheck_UpToDate_ProducesUserVisibleConfirmation()
    {
        var fake = new FakeUpdateDiscoveryService();
        fake.CustomHandler = _ => Task.FromResult(UpdateCheckResult.UpToDate(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.0.0"),
            "FootballTray is up to date.",
            etag: "\"etag-123\"",
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        var result = await vm.CheckForUpdatesAsync(isManual: true);

        Assert.IsNotNull(result);
        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
        Assert.IsFalse(vm.IsUpdateAvailable);
        Assert.AreEqual("FootballTray is up to date.", vm.UpdateStatusMessage);
    }

    [TestMethod]
    public async Task Test03_ManualCheck_UpdateAvailable_ExposesTargetVersionAndNotes()
    {
        var manifest = CreateValidManifest("1.2.0", "New UI and faster sync.");
        var fake = new FakeUpdateDiscoveryService();
        fake.CustomHandler = _ => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.2.0"),
            manifest,
            etag: "\"etag-xyz\"",
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        var result = await vm.CheckForUpdatesAsync(isManual: true);

        Assert.IsNotNull(result);
        Assert.AreEqual(UpdateCheckState.UpdateAvailable, vm.UpdateCheckState);
        Assert.IsTrue(vm.IsUpdateAvailable);
        Assert.AreEqual("1.2.0", vm.LatestVersion);
        Assert.AreEqual("New UI and faster sync.", vm.UpdateReleaseNotes);
        Assert.AreSame(manifest, vm.OfferedManifest);
        Assert.IsTrue(vm.UpdateStatusMessage?.Contains("v1.2.0") == true);
    }

    [TestMethod]
    public async Task Test04_ManualCheck_CheckFailed_ExposesUpdateSpecificFailure()
    {
        var fake = new FakeUpdateDiscoveryService();
        fake.CustomHandler = _ => Task.FromResult(UpdateCheckResult.Failed(
            "Remote server returned HTTP 503 Service Unavailable",
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        var result = await vm.CheckForUpdatesAsync(isManual: true);

        Assert.IsNotNull(result);
        Assert.AreEqual(UpdateCheckState.CheckFailed, vm.UpdateCheckState);
        Assert.IsFalse(vm.IsUpdateAvailable);
        Assert.AreEqual("Check failed: Remote server returned HTTP 503 Service Unavailable", vm.UpdateStatusMessage);
    }

    [TestMethod]
    public async Task Test05_UpdateFailure_CannotMutateFootballFreshnessOrConnectionState()
    {
        var fakeSync = new FakeSyncService
        {
            FreshnessState = DataFreshnessState.Fresh,
            CurrentState = TickerConnectionState.OnlineFresh
        };

        var fakeUpdate = new FakeUpdateDiscoveryService();
        fakeUpdate.CustomHandler = _ => Task.FromResult(UpdateCheckResult.Failed(
            "Network unreachable",
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(_prefService, syncService: fakeSync, updateDiscoveryService: fakeUpdate);
        vm.RefreshMatches(new TrayDataDto(), DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        Assert.AreEqual("Online (Live)", vm.ConnectionStatusText);
        Assert.IsFalse(vm.IsOffline);

        await vm.CheckForUpdatesAsync(isManual: true);

        Assert.AreEqual(UpdateCheckState.CheckFailed, vm.UpdateCheckState);
        Assert.AreEqual("Check failed: Network unreachable", vm.UpdateStatusMessage);

        // Football health and connection state MUST remain completely intact
        Assert.AreEqual("Online (Live)", vm.ConnectionStatusText);
        Assert.IsFalse(vm.IsOffline);
        Assert.AreEqual(DataFreshnessState.Fresh, fakeSync.FreshnessState);
        Assert.AreEqual(TickerConnectionState.OnlineFresh, fakeSync.CurrentState);
    }

    [TestMethod]
    public async Task Test06_AutomaticStartupCheck_UpToDate_RemainsSilent()
    {
        var fake = new FakeUpdateDiscoveryService();
        fake.CustomHandler = _ => Task.FromResult(UpdateCheckResult.UpToDate(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.0.0"),
            "FootballTray is up to date.",
            etag: null,
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: (delay, ct) => Task.CompletedTask);

        await vm.RunStartupUpdateCheckAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
        Assert.IsFalse(vm.IsUpdateAvailable);
        // Automatic UpToDate MUST remain quiet (null)
        Assert.IsNull(vm.UpdateStatusMessage);
    }

    [TestMethod]
    public async Task Test07_AutomaticStartupCheck_CheckFailed_RemainsSilent()
    {
        var fake = new FakeUpdateDiscoveryService();
        fake.CustomHandler = _ => Task.FromResult(UpdateCheckResult.Failed(
            "DNS lookup failed",
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: (delay, ct) => Task.CompletedTask);

        await vm.RunStartupUpdateCheckAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(UpdateCheckState.CheckFailed, vm.UpdateCheckState);
        Assert.IsFalse(vm.IsUpdateAvailable);
        // Automatic CheckFailed MUST remain quiet (null)
        Assert.IsNull(vm.UpdateStatusMessage);
    }

    [TestMethod]
    public async Task Test08_AutomaticStartupCheck_UpdateAvailable_SurfacesNotice()
    {
        var manifest = CreateValidManifest("1.1.0", "Performance optimization.");
        var fake = new FakeUpdateDiscoveryService();
        fake.CustomHandler = _ => Task.FromResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifest,
            etag: null,
            DateTimeOffset.UtcNow));

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: (delay, ct) => Task.CompletedTask);

        await vm.RunStartupUpdateCheckAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(UpdateCheckState.UpdateAvailable, vm.UpdateCheckState);
        Assert.IsTrue(vm.IsUpdateAvailable);
        Assert.AreEqual("1.1.0", vm.LatestVersion);
        Assert.AreEqual("Performance optimization.", vm.UpdateReleaseNotes);
        Assert.IsTrue(vm.UpdateStatusMessage?.Contains("v1.1.0") == true);
    }

    [TestMethod]
    public async Task Test09_StartupCheck_WaitsForConfiguredDelay()
    {
        TimeSpan recordedDelay = TimeSpan.Zero;
        var fake = new FakeUpdateDiscoveryService();

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: (delay, ct) =>
            {
                recordedDelay = delay;
                return Task.CompletedTask;
            });

        await vm.RunStartupUpdateCheckAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(TimeSpan.FromSeconds(30), recordedDelay);
        Assert.AreEqual(1, fake.CheckCallCount);
    }

    [TestMethod]
    public async Task Test10_ShutdownCancellationBeforeDelay_DoesNotInvokeDiscovery_AndNoUnobservedException()
    {
        var fake = new FakeUpdateDiscoveryService();
        using var cts = new CancellationTokenSource();

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: async (delay, ct) =>
            {
                await Task.Delay(100, ct);
            });

        cts.Cancel(); // Cancel before delay finishes

        await vm.RunStartupUpdateCheckAsync(TimeSpan.FromSeconds(30), cts.Token);

        Assert.AreEqual(0, fake.CheckCallCount);
        Assert.AreEqual(UpdateCheckState.NotChecked, vm.UpdateCheckState);
    }

    [TestMethod]
    public async Task Test11_SimultaneousManualAndStartupCheck_ExecutesSafely_AndPreservesManualFeedback()
    {
        var fake = new FakeUpdateDiscoveryService();
        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        fake.InFlightTcs = tcs;

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: (d, ct) => Task.CompletedTask);

        // 1. Automatic startup check begins
        var startupTask = vm.RunStartupUpdateCheckAsync(TimeSpan.Zero);

        // 2. User manually requests check while automatic check is in flight
        var manualTask = vm.CheckForUpdatesAsync(isManual: true);

        Assert.IsTrue(vm.IsCheckingForUpdates);
        Assert.AreEqual("Checking for updates...", vm.UpdateStatusMessage);

        // 3. Underlying discovery check finishes UpToDate
        tcs.SetResult(UpdateCheckResult.UpToDate(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.0.0"),
            "FootballTray is up to date.",
            etag: null,
            DateTimeOffset.UtcNow));

        await Task.WhenAll(startupTask, manualTask);

        // 4. Discovery executed exactly once (single-flight)
        Assert.AreEqual(1, fake.CheckCallCount);

        // 5. Final presentation MUST show manual confirmation, NOT overwritten by automatic silent continuation
        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
        Assert.AreEqual("FootballTray is up to date.", vm.UpdateStatusMessage);
    }

    [TestMethod]
    public async Task Test12_SimultaneousManualAndStartupCheck_UpdateAvailable_ExposesFullNotice()
    {
        var manifest = CreateValidManifest("1.3.0", "New match widget.");
        var fake = new FakeUpdateDiscoveryService();
        var tcs = new TaskCompletionSource<UpdateCheckResult>();
        fake.InFlightTcs = tcs;

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: (d, ct) => Task.CompletedTask);

        var startupTask = vm.RunStartupUpdateCheckAsync(TimeSpan.Zero);
        var manualTask = vm.CheckForUpdatesAsync(isManual: true);

        tcs.SetResult(UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.3.0"),
            manifest,
            etag: null,
            DateTimeOffset.UtcNow));

        await Task.WhenAll(startupTask, manualTask);

        Assert.AreEqual(1, fake.CheckCallCount);
        Assert.IsTrue(vm.IsUpdateAvailable);
        Assert.AreEqual("1.3.0", vm.LatestVersion);
        Assert.AreEqual("New match widget.", vm.UpdateReleaseNotes);
        Assert.AreSame(manifest, vm.OfferedManifest);
    }

    [TestMethod]
    public void Test13_ViewModel_DoesNotDisposeInjectedService_AppOwnsLifetime()
    {
        var fake = new FakeUpdateDiscoveryService();
        var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        vm.Dispose();

        // MainViewModel MUST NOT dispose injected IUpdateDiscoveryService (App owns it)
        Assert.AreEqual(0, fake.DisposeCallCount);

        // App disposes it directly
        fake.Dispose();
        Assert.AreEqual(1, fake.DisposeCallCount);
    }

    [TestMethod]
    public async Task Test14_NullDiscoveryService_HandlesGracefullyWithoutThrowing()
    {
        using var vm = new MainViewModel(_prefService, updateDiscoveryService: null);

        Assert.IsFalse(vm.HasUpdateCapability);
        Assert.IsFalse(vm.CanCheckForUpdates);

        var res = await vm.CheckForUpdatesAsync(isManual: true);
        Assert.IsNull(res);
        Assert.AreEqual("Update discovery is not available.", vm.UpdateStatusMessage);

        // Startup check does nothing and does not throw
        await vm.RunStartupUpdateCheckAsync(TimeSpan.Zero);
        Assert.AreEqual(UpdateCheckState.NotChecked, vm.UpdateCheckState);
    }

    [TestMethod]
    public void Test15_CurrentVersionText_DerivedFromAuthoritativeSource()
    {
        var fake = new FakeUpdateDiscoveryService { CurrentVersion = SemanticVersion.Parse("1.0.0") };
        using var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        Assert.AreEqual("v1.0.0", vm.CurrentVersionText);

        using var vmFallback = new MainViewModel(_prefService, updateDiscoveryService: null);
        Assert.AreEqual($"v{AppVersionInfo.Version}", vmFallback.CurrentVersionText);
    }

    [TestMethod]
    public async Task Test16_ManualCheckBeforeStartupDelay_SuppressesRedundantAutomaticCheck()
    {
        var fake = new FakeUpdateDiscoveryService();
        var delayTcs = new TaskCompletionSource();
        var delayStartedTcs = new TaskCompletionSource();

        using var vm = new MainViewModel(
            _prefService,
            updateDiscoveryService: fake,
            delayFunc: async (delay, ct) =>
            {
                delayStartedTcs.SetResult();
                await delayTcs.Task;
            });

        // 1. Start delayed startup check
        var startupTask = vm.RunStartupUpdateCheckAsync(TimeSpan.FromSeconds(30));

        // 2. Wait until delay has started
        await delayStartedTcs.Task;
        Assert.IsFalse(delayTcs.Task.IsCompleted);
        Assert.AreEqual(0, fake.CheckCallCount);

        // 3. User performs manual check before delay finishes
        var manualResult = await vm.CheckForUpdatesAsync(isManual: true);

        Assert.IsNotNull(manualResult);
        Assert.AreEqual(1, fake.CheckCallCount);
        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
        Assert.AreEqual("FootballTray is up to date.", vm.UpdateStatusMessage);

        // 4. Release the startup delay
        delayTcs.SetResult();
        await startupTask;

        // 5. Automatic check was suppressed; invocation count remains 1 and manual message remains
        Assert.AreEqual(1, fake.CheckCallCount);
        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
        Assert.AreEqual("FootballTray is up to date.", vm.UpdateStatusMessage);
    }

    [TestMethod]
    public async Task Test17_CallerCancellation_StopsOnlyThatWaiter()
    {
        var fake = new FakeUpdateDiscoveryService();
        var discoveryTcs = new TaskCompletionSource<UpdateCheckResult>();
        fake.InFlightTcs = discoveryTcs;

        using var vm = new MainViewModel(_prefService, updateDiscoveryService: fake);

        using var callerACts = new CancellationTokenSource();

        // 1. Caller A starts check with cancellable token
        var callerATask = vm.CheckForUpdatesAsync(isManual: true, callerACts.Token);

        // 2. Caller B joins same check with non-cancelled token
        var callerBTask = vm.CheckForUpdatesAsync(isManual: true, CancellationToken.None);

        // 3. Single-flight discovery started
        Assert.AreEqual(1, fake.CheckCallCount);

        // 4. Cancel Caller A token
        callerACts.Cancel();

        // 5. Caller A receives OperationCanceledException
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await callerATask);

        // 6. Underlying discovery is still pending / not canceled
        Assert.IsFalse(discoveryTcs.Task.IsCompleted);

        // 7. Complete discovery
        discoveryTcs.SetResult(UpdateCheckResult.UpToDate(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.0.0"),
            "FootballTray is up to date.",
            etag: null,
            DateTimeOffset.UtcNow));

        // 8. Caller B completes successfully
        var resultB = await callerBTask;
        Assert.IsNotNull(resultB);
        Assert.AreEqual(UpdateCheckState.UpToDate, resultB.State);

        // 9. Discovery was called exactly once
        Assert.AreEqual(1, fake.CheckCallCount);

        // 10. Final ViewModel state is coherent
        Assert.AreEqual(UpdateCheckState.UpToDate, vm.UpdateCheckState);
        Assert.AreEqual("FootballTray is up to date.", vm.UpdateStatusMessage);
        Assert.IsFalse(vm.IsCheckingForUpdates);
        Assert.IsTrue(vm.CanCheckForUpdates);
    }
}
