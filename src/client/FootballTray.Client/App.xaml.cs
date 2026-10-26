using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using Application = Microsoft.UI.Xaml.Application;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Taskbar;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Audio;
using FootballTray.Client.Services.Daemon;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Telemetry;
using FootballTray.Client.Services.Updates;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private static TaskbarHost? _taskbarHost;
    private static TaskbarTicker? _tickerControl;
    private TaskbarTickerCoordinator? _tickerCoordinator;
    private DaemonLifecycleManager? _daemonManager;
    private SyncService? _syncService;
    private FootballApiClient? _apiClient;
    private LocalCacheService? _cacheService;
    private static PreferencesService? _preferencesService;
    private DiagnosticsService? _diagnosticsService;
    private UpdateDiscoveryService? _updateDiscoveryService;
    private UpdatePackageAcquisitionService? _updatePackageAcquisitionService;
    private UpdateInstallationService? _updateInstallationService;
    private readonly CancellationTokenSource _appShutdownCts = new();
    private Task? _startupUpdateCheckTask;
    private readonly AppNotificationRegistrationManager _notificationRegistration = new();
    private volatile bool _updateHandoffExitRequested;
    private static System.Windows.Forms.NotifyIcon? _trayIcon;
    private static System.Windows.Forms.Timer? _trayClickTimer;
    private static bool _isInitialColdStart = true;


    public static TaskbarHost? CurrentTaskbarHost { get; private set; }
    public static TaskbarTickerCoordinator? CurrentTickerCoordinator { get; private set; }
    public static MainViewModel? CurrentMainViewModel { get; private set; }
    public static IDiagnosticsService? CurrentDiagnosticsService { get; private set; }
    public static PreferencesService? CurrentPreferencesService => _preferencesService;
    public static FootballApiClient? CurrentApiClient { get; private set; }
    public DaemonLifecycleManager? CurrentDaemonManager => _daemonManager;

    public App()
    {
        InitializeComponent();

        // Register global crash and unhandled exception hooks
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            string msg = $"Unhandled AppDomain exception: {e.ExceptionObject}";
            AppLogger.Critical("App", "UnhandledAppDomainException", msg);
            AppDiagnosticsLogger.LogError("App", "UnhandledAppDomainException", msg);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            string msg = $"Unobserved task exception: {e.Exception?.Message}";
            AppLogger.Error("App", "UnobservedTaskException", msg, ex: e.Exception);
            AppDiagnosticsLogger.LogError("App", "UnobservedTaskException", msg, ex: e.Exception);
            e.SetObserved();
        };

        UnhandledException += (s, e) =>
        {
            string errorDetails = $"Unhandled XAML exception: {e.Message} (Type: {e.Exception?.GetType().FullName})\n{e.Exception?.StackTrace}";
            AppDiagnosticsLogger.LogError("App", "XamlUnhandledException", errorDetails, ex: e.Exception);
            AppLogger.Critical("App", "XamlUnhandledException", errorDetails, ex: e.Exception);

            // Mark handled for UI layout/binding errors to prevent abrupt process termination
            try
            {
                e.Handled = true;
            }
            catch
            {
                // Cannot mark handled on fatal CLR exceptions
            }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppLogger.Info("App", "StartupBegin", "FootballTray client startup sequence initiated.");

        // 0. Initialize Preferences first so daemon can use it
        _preferencesService = new PreferencesService();

        // 1. Ensure Local Backend Engine is Active
        _daemonManager = new DaemonLifecycleManager();
        AppDomain.CurrentDomain.ProcessExit += (s, e) => _daemonManager?.StopDaemon();
        await _daemonManager.StartDaemonAsync(_preferencesService.Current.ActiveProvider, _appShutdownCts.Token);

        // 2. Initialize Core Client Services
        _apiClient = new FootballApiClient();
        CurrentApiClient = _apiClient;
        _cacheService = new LocalCacheService();
        var audioService = new AudioPlaybackService(_preferencesService);
        AudioPlaybackService.Instance = audioService;
        var notificationService = new NotificationService(audioService: audioService, apiClient: _apiClient);
        var notificationCoordinator = new NotificationCoordinator(_preferencesService);
        _syncService = new SyncService(_apiClient, _cacheService, notificationService, _preferencesService, TimeSpan.FromSeconds(15), notificationCoordinator);
        _diagnosticsService = new DiagnosticsService(_syncService, _preferencesService, _cacheService, _apiClient);
        CurrentDiagnosticsService = _diagnosticsService;
        _updateDiscoveryService = new UpdateDiscoveryService();
        _updatePackageAcquisitionService = new UpdatePackageAcquisitionService();
        _updateInstallationService = new UpdateInstallationService();

        // 3. Start background OTA telemetry manifest updates
        OtaConfigService.Instance.StartBackgroundSync(_appShutdownCts.Token);

        var shutdownHandler = new ActionShutdownHandler(() =>
        {
            // Mark intent before touching the window so the Closed handler sees it even if
            // TryEnqueue races with an in-progress teardown.
            _updateHandoffExitRequested = true;

            var win = _window;
            if (win == null) return false;

            if (win is MainWindow mw)
            {
                mw.IsClosingPermitted = true;
            }

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
            appShutdownHandler: shutdownHandler,
            apiClient: _apiClient);

        // 3. Create Main Application Window
        _window = new MainWindow();
        // Silent Launch: Boot directly to taskbar and tray; do not activate/show MainWindow

        var activationWindow = _window;
        var activationDispatcher = activationWindow?.DispatcherQueue;

        // Register UI activation handler with coordinator now that MainWindow and DispatcherQueue exist
        AppActivationCoordinator.RegisterUiHandler(activatedArgs =>
        {
            if (activationDispatcher == null) return;

            bool enqueued = activationDispatcher.TryEnqueue(() =>
            {
                if (ClassifyActivation(activatedArgs.Kind) == ActivationDispatchAction.ProcessNotification &&
                    activatedArgs.Data is AppNotificationActivatedEventArgs notificationArgs)
                {
                    _isInitialColdStart = false;
                    HandleAppNotificationActivation(notificationArgs, activationWindow);
                }
                else if (_isInitialColdStart)
                {
                    // Silent Startup: Normal cold boot launch keeps MainWindow dormant
                    _isInitialColdStart = false;
                    AppLogger.Info("App", "SilentStartup", "Cold boot launch: keeping MainWindow dormant in system tray.");
                    try
                    {
                        if (activationWindow != null)
                        {
                            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(activationWindow);
                            if (hwnd != IntPtr.Zero)
                            {
                                ShowWindow(hwnd, SW_HIDE);
                            }
                            activationWindow.AppWindow?.Hide();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("App", "SilentStartupHideFailed", $"Failed to ensure window hide on cold boot: {ex.Message}");
                    }
                }
                else
                {
                    BringMainWindowToForeground(activationWindow);
                }
            });

            if (!enqueued)
            {
                AppLogger.Warn(
                    "App",
                    "ActivationDispatchFailed",
                    "Failed to enqueue foreground activation onto MainWindow DispatcherQueue.");
            }
        });

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
            _daemonManager?.StopDaemon();
            _daemonManager?.Dispose();
            _daemonManager = null;
            _updateDiscoveryService?.Dispose();
            _updateDiscoveryService = null;
            _updatePackageAcquisitionService?.Dispose();
            _updatePackageAcquisitionService = null;
            _updateInstallationService?.Dispose();
            _updateInstallationService = null;
            _tickerCoordinator?.Dispose();
            _tickerCoordinator = null;
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
            _trayClickTimer?.Dispose();
            _trayClickTimer = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
        };

        // 5. Initialize Taskbar Host and inject into primary taskbar
        try
        {
            _taskbarHost = new TaskbarHost(null, _preferencesService);
            CurrentTaskbarHost = _taskbarHost;

            _taskbarHost.AttachmentChanged += attached =>
            {
                _diagnosticsService.RecordTaskbarStatus(attached ? "Attached" : "Detached (Recovering)");
            };

            _tickerControl = new TaskbarTicker();
            _tickerControl.PinToggled += (sender, matchId) =>
            {
                _tickerCoordinator?.TogglePin(matchId);
                CurrentMainViewModel?.OnTaskbarPinChanged(_tickerCoordinator?.PinnedMatchId);
            };

            if (activationDispatcher != null)
            {
                _tickerCoordinator = new TaskbarTickerCoordinator(
                    activationDispatcher,
                    (fixtureId, formattedText) => _tickerControl?.UpdateDisplay(fixtureId, formattedText),
                    (fixtureId, formattedText, homeLogo, awayLogo) => _tickerControl?.UpdateDisplay(fixtureId, formattedText, homeLogo, awayLogo),
                    (fixtureId, formattedText, homeLogo, awayLogo, match, isPinned) =>
                        _tickerControl?.UpdateDisplay(fixtureId, formattedText, homeLogo, awayLogo, match, isPinned));
            }
            CurrentTickerCoordinator = _tickerCoordinator;
            if (_tickerControl != null)
            {
                _tickerControl.MatchCenterRequested += (sender, matchId) => _tickerCoordinator?.OpenMatchCenter(matchId);
                _tickerCoordinator?.SubscribeToTicker(_tickerControl);
            }

            bool initialized = _tickerControl != null && await _taskbarHost.InitializeAsync(_tickerControl);

            if (initialized)
            {
                _taskbarHost.ApplyVisibility();
                _diagnosticsService.RecordTaskbarStatus("Attached");
                AppLogger.Info("Taskbar", "Initialized", "TaskbarHost successfully initialized and displayed.");
            }
            else
            {
                _diagnosticsService.RecordTaskbarStatus("Unattached (Waiting for recovery)");
                AppLogger.Warn("Taskbar", "Unattached", "TaskbarHost initialization completed with unattached state. Waiting for recovery...");
            }

            // 6. Initialize native System Tray NotifyIcon
            try
            {
                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "Footballtray",
                    Visible = true
                };
                string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                }
                _trayClickTimer = new System.Windows.Forms.Timer { Interval = 250 };
                _trayClickTimer.Tick += (s, args) =>
                {
                    _trayClickTimer.Stop();
                    var activeId = CurrentMainViewModel?.ActiveMatchId;
                    if (!string.IsNullOrEmpty(activeId))
                    {
                        _tickerCoordinator?.OpenMatchCenter(activeId);
                    }
                    else
                    {
                        OpenMainWindow();
                    }
                };

                _trayIcon.DoubleClick += (s, e) =>
                {
                    _trayClickTimer.Stop();
                    OpenMainWindow();
                };

                _trayIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    {
                        _trayClickTimer.Stop();
                        _trayClickTimer.Start();
                    }
                };

                var trayMenu = new System.Windows.Forms.ContextMenuStrip();
                trayMenu.Items.Add("Open Footballtray", null, (s, e) => OpenMainWindow());
                trayMenu.Items.Add("Settings", null, (s, e) => OpenSettings());

                var showHideTickerItem = new System.Windows.Forms.ToolStripMenuItem("Hide Ticker");
                showHideTickerItem.Click += async (s, e) =>
                {
                    if (_preferencesService == null) return;
                    bool newVisible = !_preferencesService.Current.IsTickerVisible;
                    await _preferencesService.SetTickerVisibleAsync(newVisible);
                    _taskbarHost?.ApplyVisibility();
                };

                var resetPositionItem = new System.Windows.Forms.ToolStripMenuItem("Reset Position");
                resetPositionItem.Click += async (s, e) =>
                {
                    if (_preferencesService != null)
                    {
                        await _preferencesService.SetTickerOffsetXAsync(0);
                    }
                    _taskbarHost?.UpdatePosition();
                };

                var muteSoundsItem = new System.Windows.Forms.ToolStripMenuItem("Mute Sounds");
                muteSoundsItem.Click += async (s, e) =>
                {
                    if (_preferencesService == null) return;
                    var currentMode = _preferencesService.Current.SoundMode;
                    var newMode = (currentMode == NotificationSoundMode.Off)
                        ? NotificationSoundMode.Custom
                        : NotificationSoundMode.Off;
                    await _preferencesService.SetSoundModeAsync(newMode);
                };

                trayMenu.Opening += (s, e) =>
                {
                    var prefs = _preferencesService?.Current;
                    bool isVisible = prefs?.IsTickerVisible ?? true;
                    showHideTickerItem.Text = isVisible ? "Hide Ticker" : "Show Ticker";

                    bool isMuted = prefs?.SoundMode == NotificationSoundMode.Off;
                    muteSoundsItem.Text = isMuted ? "Unmute Sounds" : "Mute Sounds";
                };

                trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                trayMenu.Items.Add(showHideTickerItem);
                trayMenu.Items.Add(resetPositionItem);
                trayMenu.Items.Add(muteSoundsItem);
                trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());
                _trayIcon.ContextMenuStrip = trayMenu;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("App", "TrayIconInitFailed", $"Failed to initialize system tray icon: {ex.Message}");
            }

            _syncService.TrayUpdated += (sender, updateArgs) =>
            {
                AppLogger.Debug("Sync", "TrayUpdated", $"Ticker update: '{updateArgs.FormattedText}' (v{updateArgs.Version})");
                var coordinator = _tickerCoordinator;
                if (activationDispatcher == null || coordinator == null) return;

                void ApplyUpdate()
                {
                    var prefs = _preferencesService?.GetPreferences();
                    coordinator.ApplySnapshot(
                        updateArgs.TrayData,
                        updateArgs.ConnectionState,
                        prefs);
                }

                if (activationDispatcher.HasThreadAccess)
                {
                    ApplyUpdate();
                }
                else
                {
                    bool enqueued = activationDispatcher.TryEnqueue(ApplyUpdate);
                    if (!enqueued)
                    {
                        AppLogger.Warn(
                            "Sync",
                            "TrayUpdateDispatchFailed",
                            "Failed to enqueue ticker update onto MainWindow DispatcherQueue.");
                    }
                }
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
                try { _window.AppWindow.Show(); } catch { }
                BringMainWindowToForeground(_window);
            });
        }
    }

    internal enum ActivationDispatchAction
    {
        BringToForeground,
        ProcessNotification,
    }

    internal static ActivationDispatchAction ClassifyActivation(ExtendedActivationKind kind)
    {
        return kind == ExtendedActivationKind.AppNotification
            ? ActivationDispatchAction.ProcessNotification
            : ActivationDispatchAction.BringToForeground;
    }

    private void HandleAppNotificationActivation(AppNotificationActivatedEventArgs notificationArgs, Window? window)
    {
        NotificationActivationResult result = (notificationArgs.Arguments != null && notificationArgs.Arguments.Count > 0)
            ? NotificationActivationHandler.Parse(notificationArgs.Arguments)
            : NotificationActivationHandler.Parse(notificationArgs.Argument);

        OnNotificationActivated(result);
        BringMainWindowToForeground(window);
    }

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void BringMainWindowToForeground(Window? window)
    {
        if (window == null) return;

        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }
            else
            {
                ShowWindow(hwnd, SW_SHOW);
            }

            window.Activate();
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App", "ForegroundActivationFailed", $"Non-fatal error restoring MainWindow: {ex.Message}");
        }
    }

    public static void OpenMainWindow()
    {
        if (Current is not App app || app._window == null) return;
        var dq = app._window.DispatcherQueue;
        if (dq == null) return;

        void Restore()
        {
            try
            {
                app._window.AppWindow.Show();
            }
            catch { }
            BringMainWindowToForeground(app._window);
        }

        if (dq.HasThreadAccess)
        {
            Restore();
        }
        else
        {
            dq.TryEnqueue(Restore);
        }
    }

    public static void OpenSettings()
    {
        if (Current is not App app || app._window == null) return;
        var dq = app._window.DispatcherQueue;
        if (dq == null) return;

        void RestoreAndNavigate()
        {
            try
            {
                app._window.AppWindow.Show();
            }
            catch { }
            BringMainWindowToForeground(app._window);

            if (app._window is MainWindow mw)
            {
                mw.Navigate(typeof(Views.SettingsPage));
            }
        }

        if (dq.HasThreadAccess)
        {
            RestoreAndNavigate();
        }
        else
        {
            dq.TryEnqueue(RestoreAndNavigate);
        }
    }

    public static async void RestartTaskbarHost()
    {
        _taskbarHost?.Dispose();
        _taskbarHost = new TaskbarHost(null, _preferencesService);
        CurrentTaskbarHost = _taskbarHost;
        _taskbarHost.AttachmentChanged += attached =>
        {
            CurrentDiagnosticsService?.RecordTaskbarStatus(attached ? "Attached" : "Detached (Recovering)");
        };
        if (_tickerControl != null)
        {
            await _taskbarHost.InitializeAsync(_tickerControl);
            _taskbarHost.ApplyVisibility();
        }
    }

    public static void ExitApp()
    {
        try
        {
            _trayClickTimer?.Dispose();
            _trayClickTimer = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
        catch { }

        if (Current is not App app)
        {
            Application.Current?.Exit();
            return;
        }

        app._daemonManager?.StopDaemon();

        var win = app._window;
        if (win is MainWindow mw)
        {
            mw.IsClosingPermitted = true;
        }

        if (win != null && win.DispatcherQueue != null)
        {
            var dq = win.DispatcherQueue;
            if (dq.HasThreadAccess)
            {
                win.Close();
            }
            else
            {
                dq.TryEnqueue(() => win.Close());
            }
        }
        else
        {
            Application.Current?.Exit();
        }
    }

    private sealed class ActionShutdownHandler : IAppShutdownHandler
    {
        private readonly Func<bool> _action;
        public ActionShutdownHandler(Func<bool> action) => _action = action;
        public bool RequestShutdown() => _action();
    }
}
