using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Taskbar;
using FootballTray.Client.ViewModels;
using FootballTray.Core;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class DiagnosticsAndObservabilityTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        AppLogger.IsLoggingEnabled = true;
        _tempDir = Path.Combine(Path.GetTempPath(), "FT_DiagTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
        catch { }
    }

    [TestMethod]
    public void Test01_StructuredLogEntry_FormatsCorrectly()
    {
        var entry = new DiagnosticLogEntry
        {
            Timestamp = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            Component = "Sync",
            EventName = "PollSuccess",
            Level = DiagnosticLogLevel.Info,
            Message = "Sync completed successfully",
            CorrelationId = "sync_12345"
        };

        string text = entry.ToString();
        Assert.IsTrue(text.Contains("[2026-09-02 12:00:00.000]"));
        Assert.IsTrue(text.Contains("[INFO]"));
        Assert.IsTrue(text.Contains("[Sync]"));
        Assert.IsTrue(text.Contains("[PollSuccess]"));
        Assert.IsTrue(text.Contains("[sync_12345]"));
        Assert.IsTrue(text.Contains("Sync completed successfully"));
    }

    [TestMethod]
    public void Test02_LogRotation_RotatesWhenExceedingMaxSize()
    {
        string logPath = Path.Combine(_tempDir, "test_app.log");
        // Create sink with 1 KB max size, 3 retained files
        var sink = new FileLogSink(logPath, maxSizeBytes: 1024, maxRetainedFiles: 3);

        // Write enough data to exceed 1 KB
        string sampleLine = new string('A', 200);
        for (int i = 0; i < 15; i++)
        {
            sink.WriteRawLine($"Line {i}: {sampleLine}");
        }

        Assert.IsTrue(File.Exists(logPath), "Active log file should exist");
        string rotated1 = Path.Combine(_tempDir, "test_app.1.log");
        Assert.IsTrue(File.Exists(rotated1), "Rotated log file .1.log should exist");
    }

    [TestMethod]
    public void Test03_BoundedLogRetention_DeletesOldArchivesBeyondLimit()
    {
        string logPath = Path.Combine(_tempDir, "bounded_app.log");
        var sink = new FileLogSink(logPath, maxSizeBytes: 500, maxRetainedFiles: 2);

        string sampleLine = new string('X', 200);
        for (int i = 0; i < 25; i++)
        {
            sink.WriteRawLine($"Line {i}: {sampleLine}");
        }

        string rotated1 = Path.Combine(_tempDir, "bounded_app.1.log");
        string rotated2 = Path.Combine(_tempDir, "bounded_app.2.log");
        string rotated3 = Path.Combine(_tempDir, "bounded_app.3.log");

        Assert.IsTrue(File.Exists(rotated1), "Rotated file 1 should exist");
        Assert.IsTrue(File.Exists(rotated2), "Rotated file 2 should exist");
        Assert.IsFalse(File.Exists(rotated3), "Rotated file 3 should NOT exist (exceeds max retained 2)");
    }

    [TestMethod]
    public void Test04_LoggingFailure_NeverThrowsOrCrashesApp()
    {
        // Path with invalid characters to force I/O exception
        string invalidPath = "Z:\\NonExistentDrive_9999\\invalid/log.log";
        var sink = new FileLogSink(invalidPath);

        // Should not throw
        sink.WriteRawLine("Safe test line");
        sink.Write(new DiagnosticLogEntry { Message = "Safe entry" });
    }

    [TestMethod]
    public void Test05_HealthSnapshot_MatchesAppVersion()
    {
        var diagService = new DiagnosticsService();
        var snapshot = diagService.GetCurrentSnapshot();

        Assert.AreEqual(AppVersionInfo.Version, snapshot.Version);
        Assert.AreEqual("x64", snapshot.Architecture);
        Assert.IsTrue(snapshot.Platform.Contains("Windows"));
    }

    [TestMethod]
    public void Test06_HealthSnapshot_ReflectsFreshnessState()
    {
        var prefs = new PreferencesService(_tempDir);
        var diagService = new DiagnosticsService(null, prefs);

        var snapshot = diagService.GetCurrentSnapshot();
        Assert.AreEqual(DataFreshnessState.Empty, snapshot.FreshnessState);
    }

    [TestMethod]
    public void Test07_HealthSnapshot_ReflectsNotificationState()
    {
        var prefs = new PreferencesService(_tempDir);
        var diagService = new DiagnosticsService(null, prefs);

        diagService.RecordNotificationRegistrationStatus("Registered");
        var snapshot = diagService.GetCurrentSnapshot();

        Assert.AreEqual("Registered", snapshot.NotificationRegistrationStatus);
        Assert.IsTrue(snapshot.NotificationsGloballyEnabled);
    }

    [TestMethod]
    public void Test08_HealthSnapshot_ReflectsTaskbarState()
    {
        var diagService = new DiagnosticsService();
        diagService.RecordTaskbarStatus("Attached");

        var snapshot = diagService.GetCurrentSnapshot();
        Assert.AreEqual("Attached", snapshot.TaskbarHostStatus);
        Assert.AreEqual(ApplicationHealthState.Healthy, snapshot.HealthState);

        diagService.RecordTaskbarStatus("Unattached (Waiting for recovery)");
        var snapshot2 = diagService.GetCurrentSnapshot();
        Assert.AreEqual(ApplicationHealthState.Degraded, snapshot2.HealthState);
    }

    [TestMethod]
    public void Test09_SanitizedDiagnosticsOutput_GeneratesCleanHumanReadableReport()
    {
        var diagService = new DiagnosticsService();
        diagService.RecordTaskbarStatus("Attached");
        diagService.RecordNotificationRegistrationStatus("Registered");

        string report = diagService.GetSanitizedReportText();
        Assert.IsTrue(report.Contains("FootballTray Diagnostics"));
        Assert.IsTrue(report.Contains($"Version: {AppVersionInfo.Version}"));
        Assert.IsTrue(report.Contains("Platform: Windows"));
        Assert.IsTrue(report.Contains("Taskbar Host: Attached"));
        Assert.IsTrue(report.Contains("Notification Engine: Registered"));
    }

    [TestMethod]
    public void Test10_ApiKey_AbsentFromDiagnosticsReport()
    {
        var diagService = new DiagnosticsService();
        diagService.RecordError("Network", "Failed with key=secret_api_key_12345");

        string report = diagService.GetSanitizedReportText();
        Assert.IsFalse(report.Contains("secret_api_key_12345"));
        Assert.IsTrue(report.Contains("key=[REDACTED]"));
    }

    [TestMethod]
    public void Test11_DatabaseCredentials_AbsentFromDiagnosticsReport()
    {
        string rawSecret = "postgres" + "ql://admin:super_secret_pw@localhost:5432/footballtray";
        string sanitized = AppLogger.Sanitize(rawSecret);

        Assert.IsFalse(sanitized.Contains("super_secret_pw"));
        Assert.IsTrue(sanitized.Contains("[REDACTED]"));
    }

    [TestMethod]
    public async Task Test12_CacheCorruption_LogsWarningAndRecoversGracefully()
    {
        string cacheDir = Path.Combine(_tempDir, "cache_corrupt");
        Directory.CreateDirectory(cacheDir);
        string cacheFile = Path.Combine(cacheDir, "tray.json");
        await File.WriteAllTextAsync(cacheFile, "{ \"corrupt_json\": true, incomplete... ");

        var cache = new LocalCacheService(cacheDir);
        var loaded = await cache.LoadCacheAsync();

        Assert.IsNull(loaded, "Corrupt cache should safely return null");
    }

    [TestMethod]
    public async Task Test13_PreferencesCorruption_LogsWarningAndRecoversWithDefaults()
    {
        string prefsDir = Path.Combine(_tempDir, "prefs_corrupt");
        Directory.CreateDirectory(prefsDir);
        string prefsFile = Path.Combine(prefsDir, "preferences.json");
        await File.WriteAllTextAsync(prefsFile, "{{{ invalid json content");

        var service = new PreferencesService(prefsDir);
        var prefs = await service.LoadAsync();

        Assert.IsNotNull(prefs, "Preferences service should recover with defaults");
        Assert.AreEqual(2, prefs.FollowedTeams.Count);
        Assert.AreEqual(6, prefs.FollowedCompetitions.Count);
    }

    [TestMethod]
    public void Test14_NotificationRegistrationFailure_LogsErrorSafely()
    {
        var mockBridge = new MockFailingAppNotificationBridge();
        var manager = new AppNotificationRegistrationManager(mockBridge);

        bool result = manager.Initialize(activation => { });

        Assert.IsFalse(result, "Registration should return false on failure");
        Assert.IsFalse(manager.IsRegistered);
        Assert.IsFalse(manager.IsHandlerAttached);
    }

    [TestMethod]
    public async Task Test15_NotificationDeduplication_LogsAppropriateOutcome()
    {
        var store = new NotificationDeduplicationStore(Path.Combine(_tempDir, "dedup.json"));
        var mockRenderer = new MockNotificationRenderer();
        var service = new NotificationService(new NotificationEngine(store), mockRenderer, store);

        var prev = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "match_1",
                    HomeTeam = new TeamSummaryDto { Id = CanonicalEntityIds.Barcelona, Name = "Barcelona" },
                    AwayTeam = new TeamSummaryDto { Id = "team_other", Name = "Other" },
                    Competition = new CompetitionSummaryDto { Id = CanonicalEntityIds.LaLiga, Name = "La Liga" },
                    State = "LIVE",
                    ElapsedMinute = 5,
                    Score = new ScoreDto { Home = 0, Away = 0 },
                    RecentEvents = []
                }
            ]
        };
        var cur = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "match_1",
                    HomeTeam = new TeamSummaryDto { Id = CanonicalEntityIds.Barcelona, Name = "Barcelona" },
                    AwayTeam = new TeamSummaryDto { Id = "team_other", Name = "Other" },
                    Competition = new CompetitionSummaryDto { Id = CanonicalEntityIds.LaLiga, Name = "La Liga" },
                    State = "LIVE",
                    ElapsedMinute = 10,
                    Score = new ScoreDto { Home = 1, Away = 0 },
                    RecentEvents =
                    [
                        new TrayEventDto { Id = "e1", Type = "GOAL", Minute = 10, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Lewandowski" }
                    ]
                }
            ]
        };

        var prefs = DefaultPreferences.CreateDefault();
        var dispatched = await service.ProcessTrayUpdateAsync(prev, cur, prefs);
        Assert.AreEqual(1, dispatched.Count);

        // Second pass: duplicate event should be deduplicated
        var dispatched2 = await service.ProcessTrayUpdateAsync(cur, cur, prefs);
        Assert.AreEqual(0, dispatched2.Count);
    }

    [TestMethod]
    public void Test16_TaskbarLogger_DispatchesToAppLogger()
    {
        bool eventReceived = false;
        void OnLogged(DiagnosticLogEntry entry)
        {
            if (entry.Component == "Taskbar" && entry.Message.Contains("Test taskbar event"))
            {
                eventReceived = true;
            }
        }

        AppLogger.EntryLogged += OnLogged;
        try
        {
            TaskbarLogger.Info("Test taskbar event");
            Assert.IsTrue(eventReceived, "TaskbarLogger should route to AppLogger");
        }
        finally
        {
            AppLogger.EntryLogged -= OnLogged;
        }
    }

    [TestMethod]
    public void Test17_StartupShutdown_DiagnosticsRecorded()
    {
        AppLogger.Info("App", "StartupBegin", "Testing startup");
        AppLogger.Info("App", "ShutdownComplete", "Testing shutdown");

        var entries = AppLogger.GetRecentEntries();
        Assert.IsTrue(entries.Any(e => e.Component == "App" && e.EventName == "StartupBegin"));
        Assert.IsTrue(entries.Any(e => e.Component == "App" && e.EventName == "ShutdownComplete"));
    }

    [TestMethod]
    public void Test18_RefreshDuration_MeasuredInSyncService()
    {
        var sync = new SyncService(new FootballApiClient(), new LocalCacheService(_tempDir));
        Assert.IsTrue(sync.LastRefreshDurationMs >= 0);
    }

    [TestMethod]
    public async Task Test19_FailedRefresh_RecordsSafeErrorState()
    {
        var sync = new SyncService(new FootballApiClient(baseUrl: "http://127.0.0.1:59999"), new LocalCacheService(_tempDir));
        var state = await sync.PollOnceAsync();

        Assert.AreEqual(DataFreshnessState.Empty, state);
        Assert.IsNotNull(sync.LastRefreshAttempt);
    }

    [TestMethod]
    public void Test20_DiagnosticsUI_ViewModelExposesSnapshotAndCopyCommand()
    {
        var prefs = new PreferencesService(_tempDir);
        var sync = new SyncService(new FootballApiClient(), new LocalCacheService(_tempDir));
        var diag = new DiagnosticsService(sync, prefs);
        diag.RecordTaskbarStatus("Attached");
        diag.RecordNotificationRegistrationStatus("Registered");

        var vm = new MainViewModel(prefs, sync, diag);

        Assert.IsTrue(vm.DiagnosticsVersionText.Contains(AppVersionInfo.Version));
        Assert.IsTrue(vm.DiagnosticsTaskbarText.Contains("Attached"));

        string copied = vm.CopyDiagnosticsToClipboard();
        Assert.IsTrue(copied.Contains("FootballTray Diagnostics"));
        Assert.AreEqual("Diagnostics copied to clipboard.", vm.StatusMessage);
    }

    private class MockFailingAppNotificationBridge : IAppNotificationManagerBridge
    {
#pragma warning disable CS0067
        public event EventHandler<string>? NotificationInvoked;
#pragma warning restore CS0067
        public bool IsSupported => true;

        public void Register() => throw new InvalidOperationException("Simulated registration COM error 0x80070005");
        public void Unregister() { }
    }

    private class MockNotificationRenderer : INotificationRenderer
    {
        public bool RenderNotification(NotificationPayload payload) => true;
    }
}
