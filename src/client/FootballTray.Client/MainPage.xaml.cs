using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Client.Taskbar;
using FootballTray.Core.Models;
using FootballTray.Core.Providers.Mock;

namespace FootballTray.Client;

public sealed partial class MainPage : Page
{
    private bool _isVisible = true;
    private readonly MockFootballProvider _mockProvider = new();

    public MainPage()
    {
        InitializeComponent();

        TaskbarLogger.LogMessageReceived += OnLogMessageReceived;
        _mockProvider.FixtureUpdated += OnFixtureUpdated;
        _mockProvider.MatchEventOccurred += OnMatchEventOccurred;

        UpdateUiFromFixture(_mockProvider.CurrentFixture);

        Unloaded += OnUnloaded;
    }

    private void OnFixtureUpdated(Fixture fixture)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateUiFromFixture(fixture);
        });
    }

    private void OnMatchEventOccurred(MatchEvent matchEvent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RecentEventText.Text = $"Latest Event: [{matchEvent.MinuteDisplay}] {matchEvent.Type} - {matchEvent.Team.Name} ({matchEvent.Description})";
        });
    }

    private void UpdateUiFromFixture(Fixture fixture)
    {
        HomeTeamText.Text = fixture.HomeTeam.Name;
        AwayTeamText.Text = fixture.AwayTeam.Name;
        MatchScoreText.Text = fixture.Score.DisplayScore;
        MatchStatusText.Text = $"{fixture.State.ToString().ToUpperInvariant()} ({fixture.MatchTimeDisplay})";

        if (fixture.Events.Count > 0)
        {
            var lastEvent = fixture.Events.Last();
            RecentEventText.Text = $"Latest Event: [{lastEvent.MinuteDisplay}] {lastEvent.Type} - {lastEvent.Team.Name} ({lastEvent.Description})";
        }
        else
        {
            RecentEventText.Text = "Events: None yet";
        }
    }

    private void OnStepNextClick(object sender, RoutedEventArgs e)
    {
        _mockProvider.StepNext();
    }

    private void OnAdvance17Click(object sender, RoutedEventArgs e)
    {
        _mockProvider.AdvanceToMinute(17);
    }

    private void OnAdvanceHtClick(object sender, RoutedEventArgs e)
    {
        _mockProvider.AdvanceToMinute(45);
    }

    private void OnAdvance72Click(object sender, RoutedEventArgs e)
    {
        _mockProvider.AdvanceToMinute(72);
    }

    private void OnAdvanceFtClick(object sender, RoutedEventArgs e)
    {
        _mockProvider.AdvanceToMinute(90, 5);
    }

    private void OnResetSimClick(object sender, RoutedEventArgs e)
    {
        _mockProvider.Reset();
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
        _mockProvider.FixtureUpdated -= OnFixtureUpdated;
        _mockProvider.MatchEventOccurred -= OnMatchEventOccurred;
    }
}
