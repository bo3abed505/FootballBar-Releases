using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
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
    private readonly AppNotificationRegistrationManager _notificationRegistration = new();

    public static TaskbarHost? CurrentTaskbarHost { get; private set; }
    public static MainViewModel? CurrentMainViewModel { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 1. Initialize Core Client Services first
        _apiClient = new FootballApiClient();
        _cacheService = new LocalCacheService();
        _preferencesService = new PreferencesService();
        var notificationService = new NotificationService();
        _syncService = new SyncService(_apiClient, _cacheService, notificationService, _preferencesService, TimeSpan.FromSeconds(15));

        // 2. Initialize Root MainViewModel
        CurrentMainViewModel = new MainViewModel(_preferencesService, _syncService);

        // 3. Create Main Application Window
        _window = new MainWindow();
        _window.Activate();

        // 4. Register AppNotificationManager
        InitializeAppNotifications();

        _window.Closed += (s, e) =>
        {
            TaskbarLogger.Info("MainWindow closed, cleaning up TaskbarHost, SyncService, MainViewModel, and Notifications...");
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
                TaskbarLogger.Info("TaskbarHost successfully initialized and displayed.");
            }
            else
            {
                TaskbarLogger.Warn("TaskbarHost initialization completed with unattached state. Waiting for recovery...");
            }

            _syncService.TrayUpdated += (sender, updateArgs) =>
            {
                TaskbarLogger.Info($"[SYNC] Ticker update received: '{updateArgs.FormattedText}' (v{updateArgs.Version})");
                _tickerControl?.Update(updateArgs.TrayData, updateArgs.FormattedText);
            };

            // Start initial sync and periodic polling
            await _syncService.StartAsync();
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Unhandled exception during startup sequence.", ex);
        }
    }

    private void InitializeAppNotifications()
    {
        bool registered = _notificationRegistration.Initialize(OnNotificationActivated);
        if (registered)
        {
            TaskbarLogger.Info("AppNotificationManager successfully registered.");
        }
        else
        {
            TaskbarLogger.Warn("AppNotificationManager registration skipped or unsupported in current environment.");
        }
    }

    private void UninitializeAppNotifications()
    {
        _notificationRegistration.Unregister();
        TaskbarLogger.Info("AppNotificationManager unregistered.");
    }

    private void OnNotificationActivated(NotificationActivationResult activation)
    {
        TaskbarLogger.Info($"[NOTIFICATION] Invoked: Action='{activation.Action}', FixtureId='{activation.FixtureId}', EventUid='{activation.EventUid}', IsValid={activation.IsValid}");

        if (_window != null && _window.DispatcherQueue != null)
        {
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                _window.Activate();
            });
        }
    }
}
