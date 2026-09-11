using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Taskbar;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Updates;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private TaskbarHost? _taskbarHost;
    private TaskbarTicker? _tickerControl;
    private SyncService? _syncService;
    private FootballApiClient? _apiClient;
    private LocalCacheService? _cacheService;
    private PreferencesService? _preferencesService;
    private DiagnosticsService? _diagnosticsService;
    private UpdateDiscoveryService? _updateDiscoveryService;
    private UpdatePackageAcquisitionService? _updatePackageAcquisitionService;
    private UpdateInstallationService? _updateInstallationService;
    private readonly CancellationTokenSource _appShutdownCts = new();
    private Task? _startupUpdateCheckTask;
    private readonly AppNotificationRegistrationManager _notificationRegistration = new();
    private volatile bool _updateHandoffExitRequested;


    public static TaskbarHost? CurrentTaskbarHost { get; private set; }
    public static MainViewModel? CurrentMainViewModel { get; private set; }
    public static IDiagnosticsService? CurrentDiagnosticsService { get; private set; }

    public App()
    {
        InitializeComponent();

        // Register global crash and unhandled exception hooks
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            AppLogger.Critical("App", "UnhandledAppDomainException", $"Unhandled AppDomain exception: {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            AppLogger.Error("App", "UnobservedTaskException", $"Unobserved task exception: {e.Exception?.Message}", ex: e.Exception);
            e.SetObserved();
        };

        UnhandledException += (s, e) =>
        {
            AppLogger.Critical("App", "XamlUnhandledException", $"Unhandled XAML exception: {e.Message}", ex: e.Exception);
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppLogger.Info("App", "StartupBegin", "FootballTray client startup sequence initiated.");

        // 1. Initialize Core Client Services first
        _apiClient = new FootballApiClient();
        _cacheService = new LocalCacheService();
        _preferencesService = new PreferencesService();
        var notificationService = new NotificationService();
        _syncService = new SyncService(_apiClient, _cacheService, notificationService, _preferencesService, TimeSpan.FromSeconds(15));
        _diagnosticsService = new DiagnosticsService(_syncService, _preferencesService, _cacheService);
        CurrentDiagnosticsService = _diagnosticsService;
        _updateDiscoveryService = new UpdateDiscoveryService();
        _updatePackageAcquisitionService = new UpdatePackageAcquisitionService();
        _updateInstallationService = new UpdateInstallationService();

        var shutdownHandler = new ActionShutdownHandler(() =>
        {
            // Mark intent before touching the window so the Closed handler sees it even if
            // TryEnqueue races with an in-progress teardown.
            _updateHandoffExitRequested = true;

            var win = _window;
            if (win == null) return false;

            var dq = win.DispatcherQueue;
            if (dq == null) return false;

            if (dq.HasThreadAccess)
            {
                win.Close();
                return true;
            }
            else
            {
                return dq.TryEnqueue(() => win.Close());
            }
        });

        // 2. Initialize Root MainViewModel
        CurrentMainViewModel = new MainViewModel(
            _preferencesService,
            _syncService,
            _diagnosticsService,
            updateDiscoveryService: _updateDiscoveryService,
            updateAcquisitionService: _updatePackageAcquisitionService,
            updateInstallationService: _updateInstallationService,
            appShutdownHandler: shutdownHandler);

        // 3. Create Main Application Window
        _window = new MainWindow();
        _window.Activate();

        // 4. Register AppNotificationManager
        InitializeAppNotifications();

        _window.Closed += (s, e) =>
        {
            // Capture the handoff-exit flag before any state is mutated so
            // Application.Current.Exit() fires at the correct point.
            bool exitAfterCleanup = _updateHandoffExitRequested;

            AppLogger.Info("App", "ShutdownBegin", "MainWindow closed. Initiating graceful subsystem shutdown...");
            _appShutdownCts.Cancel();
            CurrentMainViewModel?.Dispose();
            CurrentMainViewModel = null;
            _syncService?.Dispose();
            _syncService = null;
            _apiClient?.Dispose();
            _apiClient = null;
            _updateDiscoveryService?.Dispose();
            _updateDiscoveryService = null;
            _updatePackageAcquisitionService?.Dispose();
            _updatePackageAcquisitionService = null;
            _updateInstallationService?.Dispose();
            _updateInstallationService = null;
            _taskbarHost?.Dispose();
            _taskbarHost = null;
            CurrentTaskbarHost = null;
            _appShutdownCts.Dispose();

            UninitializeAppNotifications();
            AppLogger.Info("App", "ShutdownComplete", "FootballTray client shutdown complete.");

            // Stage 2: if shutdown was requested for installer handoff, force process exit so
            // the installer's -WaitForProcessId guard is satisfied even if WinUI teardown
            // would otherwise keep a background thread alive.
            if (exitAfterCleanup)
            {
                Application.Current.Exit();
            }
        };

        // 5. Initialize Taskbar Host and inject into primary taskbar
        try
        {
            _taskbarHost = new TaskbarHost();
            CurrentTaskbarHost = _taskbarHost;

            _taskbarHost.AttachmentChanged += attached =>
            {
                _diagnosticsService.RecordTaskbarStatus(attached ? "Attached" : "Detached (Recovering)");
            };

            _tickerControl = new TaskbarTicker();
            bool initialized = await _taskbarHost.InitializeAsync(_tickerControl);

            if (initialized)
            {
                _taskbarHost.Show();
                _diagnosticsService.RecordTaskbarStatus("Attached");
                AppLogger.Info("Taskbar", "Initialized", "TaskbarHost successfully initialized and displayed.");
            }
            else
            {
                _diagnosticsService.RecordTaskbarStatus("Unattached (Waiting for recovery)");
                AppLogger.Warn("Taskbar", "Unattached", "TaskbarHost initialization completed with unattached state. Waiting for recovery...");
            }

            _syncService.TrayUpdated += (sender, updateArgs) =>
            {
                AppLogger.Debug("Sync", "TrayUpdated", $"Ticker update: '{updateArgs.FormattedText}' (v{updateArgs.Version})");
                _tickerControl?.Update(updateArgs.TrayData, updateArgs.FormattedText);
            };

            // Start initial sync and periodic polling
            await _syncService.StartAsync();
            AppLogger.Info("App", "StartupComplete", "FootballTray client startup sequence completed successfully.");

            // 6. Schedule delayed startup update check (isolated from football sync health)
            _startupUpdateCheckTask = CurrentMainViewModel?.RunStartupUpdateCheckAsync(
                TimeSpan.FromSeconds(30),
                _appShutdownCts.Token);
        }
        catch (Exception ex)
        {
            _diagnosticsService.RecordError("Startup", "Unhandled exception during startup sequence", ex);
            AppLogger.Critical("App", "StartupError", "Unhandled exception during startup sequence.", ex: ex);
        }
    }

    private void InitializeAppNotifications()
    {
        bool registered = _notificationRegistration.Initialize(OnNotificationActivated);
        if (registered)
        {
            _diagnosticsService?.RecordNotificationRegistrationStatus("Registered");
            AppLogger.Info("Notifications", "RegistrationSuccess", "AppNotificationManager successfully registered.");
        }
        else
        {
            _diagnosticsService?.RecordNotificationRegistrationStatus("Unregistered / Unsupported");
            AppLogger.Warn("Notifications", "RegistrationSkipped", "AppNotificationManager registration skipped or unsupported in current environment.");
        }
    }

    private void UninitializeAppNotifications()
    {
        _notificationRegistration.Unregister();
        _diagnosticsService?.RecordNotificationRegistrationStatus("Unregistered");
        AppLogger.Info("Notifications", "Unregistered", "AppNotificationManager unregistered.");
    }

    private void OnNotificationActivated(NotificationActivationResult activation)
    {
        AppLogger.Info("Notifications", "Activated", $"Notification invoked: Action='{activation.Action}', FixtureId='{activation.FixtureId}', EventUid='{activation.EventUid}', IsValid={activation.IsValid}");

        if (_window != null && _window.DispatcherQueue != null)
        {
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                _window.Activate();
            });
        }
    }

    private sealed class ActionShutdownHandler : IAppShutdownHandler
    {
        private readonly Func<bool> _action;
        public ActionShutdownHandler(Func<bool> action) => _action = action;
        public bool RequestShutdown() => _action();
    }
}
