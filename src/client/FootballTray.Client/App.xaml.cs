using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using FootballTray.Client.Taskbar;
using FootballTray.Client.Services;

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

    public static TaskbarHost? CurrentTaskbarHost { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        _window.Closed += (s, e) =>
        {
            TaskbarLogger.Info("MainWindow closed, cleaning up TaskbarHost and SyncService...");
            _syncService?.Dispose();
            _syncService = null;
            _apiClient?.Dispose();
            _apiClient = null;
            _taskbarHost?.Dispose();
            _taskbarHost = null;
            CurrentTaskbarHost = null;
        };

        // Initialize Taskbar Host and inject into primary taskbar
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

            // Start Football Data Synchronization pipeline
            _apiClient = new FootballApiClient();
            _cacheService = new LocalCacheService();
            _syncService = new SyncService(_apiClient, _cacheService, TimeSpan.FromSeconds(15));

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
}
