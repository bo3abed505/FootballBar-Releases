using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.UI.Xaml;
using FootballTray.Client.Taskbar;

namespace FootballTray.Client;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private TaskbarHost? _taskbarHost;

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
            TaskbarLogger.Info("MainWindow closed, cleaning up TaskbarHost...");
            _taskbarHost?.Dispose();
            _taskbarHost = null;
            CurrentTaskbarHost = null;
        };

        // Initialize Taskbar Host and inject into primary taskbar
        try
        {
            _taskbarHost = new TaskbarHost();
            CurrentTaskbarHost = _taskbarHost;

            var tickerControl = new TaskbarTicker();
            bool initialized = await _taskbarHost.InitializeAsync(tickerControl);

            if (initialized)
            {
                _taskbarHost.Show();
                TaskbarLogger.Info("TaskbarHost successfully initialized and displayed.");
            }
            else
            {
                TaskbarLogger.Warn("TaskbarHost initialization completed with unattached state. Waiting for recovery...");
            }
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Unhandled exception during TaskbarHost startup.", ex);
        }
    }
}
