using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Client.Taskbar;

namespace FootballTray.Client;

public sealed partial class MainPage : Page
{
    private bool _isVisible = true;

    public MainPage()
    {
        InitializeComponent();

        TaskbarLogger.LogMessageReceived += OnLogMessageReceived;
        Unloaded += OnUnloaded;
    }

    private void OnLogMessageReceived(TaskbarLogLevel level, string message, Exception? ex)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {message}";
            if (ex != null) line += $" ({ex.Message})";
            LogTextBox.Text += line + Environment.NewLine;
        });
    }

    private void OnRepositionClick(object sender, RoutedEventArgs e)
    {
        TaskbarLogger.Info("Manual recalculate & reposition requested from UI.");
        App.CurrentTaskbarHost?.Reposition();
    }

    private void OnToggleVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (_isVisible)
        {
            App.CurrentTaskbarHost?.Hide();
            _isVisible = false;
            TaskbarLogger.Info("Taskbar host hidden via UI toggle.");
        }
        else
        {
            App.CurrentTaskbarHost?.Show();
            _isVisible = true;
            TaskbarLogger.Info("Taskbar host shown via UI toggle.");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TaskbarLogger.LogMessageReceived -= OnLogMessageReceived;
    }
}
