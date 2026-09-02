using System;
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
    private readonly AppNotificationRegistrationManager _notificationRegistration = new();

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

        // 2. Initialize Root MainViewModel
        CurrentMainViewModel = new MainViewModel(_preferencesService, _syncService, _diagnosticsService);

        // 3. Create Main Application Window
        _window = new MainWindow();
        _window.Activate();

        // 4. Register AppNotificationManager
        InitializeAppNotifications();

        _window.Closed += (s, e) =>
        {
            AppLogger.Info("App", "ShutdownBegin", "MainWindow closed. Initiating graceful subsystem shutdown...");
            CurrentMainViewModel?.Dispose();
            CurrentMainViewModel = null;
            _syncService?.Dispose();
            _syncService = null;
            _apiClient?.Dispose();
            _apiClient = null;
            _taskbarHost?.Dispose();
            _taskbarHost = null;
            CurrentTaskbarHost = null;

            UninitializeAppNotifications();
            AppLogger.Info("App", "ShutdownComplete", "FootballTray client shutdown complete.");
        };

        // 5. Initialize Taskbar Host and inject into primary taskbar
        try
        {
            _taskbarHost = new TaskbarHost();
            CurrentTaskbarHost = _taskbarHost;

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
}
