using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client;

public sealed partial class MainPage : Page
{
    private MainViewModel? _viewModel;

    public MainViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = value;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                BindViewModel();
            }
        }
    }

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null && App.CurrentMainViewModel != null)
        {
            ViewModel = App.CurrentMainViewModel;
        }

        if (ViewModel != null)
        {
            await ViewModel.InitializeAsync();
            UpdateUiFromViewModel();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void BindViewModel()
    {
        if (_viewModel == null) return;

        MatchItemsList.ItemsSource = _viewModel.FilteredMatches;
        TeamsList.ItemsSource = _viewModel.AvailableTeams;
        CompetitionsList.ItemsSource = _viewModel.AvailableCompetitions;
        NotificationCategoriesList.ItemsSource = _viewModel.NotificationCategories;

        GlobalNotificationsSwitch.IsOn = _viewModel.IsGlobalNotificationsEnabled;
        UpdateUiFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateUiFromViewModel();
        });
    }

    private void UpdateUiFromViewModel()
    {
        if (_viewModel == null) return;

        // Connection status & indicator
        ConnectionStatusText.Text = _viewModel.ConnectionStatusText;
        if (_viewModel.IsOffline)
        {
            ConnectionDot.Fill = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] 
                ?? (Brush)Application.Current.Resources["SystemFillColorAttentionBrush"];
        }
        else
        {
            ConnectionDot.Fill = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }

        // Active Ticker Summary
        ActiveTickerSummaryText.Text = $"📌 {_viewModel.ActiveTickerText}";

        // Global notifications switch
        if (GlobalNotificationsSwitch.IsOn != _viewModel.IsGlobalNotificationsEnabled)
        {
            GlobalNotificationsSwitch.IsOn = _viewModel.IsGlobalNotificationsEnabled;
        }

        // Empty state visibility
        if (_viewModel.IsEmpty)
        {
            EmptyStateBorder.Visibility = Visibility.Visible;
            EmptyStateTitleText.Text = _viewModel.EmptyStateTitle;
            EmptyStateSubtitleText.Text = _viewModel.EmptyStateMessage;
        }
        else
        {
            EmptyStateBorder.Visibility = Visibility.Collapsed;
        }

        // Status / Error InfoBar
        if (_viewModel.HasStatusMessage)
        {
            StatusInfoBar.Message = _viewModel.StatusMessage ?? string.Empty;
            StatusInfoBar.IsOpen = true;
        }
        else
        {
            StatusInfoBar.IsOpen = false;
        }

        // Diagnostics Card
        DiagnosticsVersionText.Text = _viewModel.DiagnosticsVersionText;
        DiagnosticsHealthText.Text = _viewModel.DiagnosticsHealthStatusText;
        DiagnosticsLastSyncText.Text = _viewModel.DiagnosticsLastSyncText;
        DiagnosticsTaskbarText.Text = _viewModel.DiagnosticsTaskbarText;

        // Software Updates Card
        UpdateCurrentVersionText.Text = $"Current: {_viewModel.CurrentVersionText}";
        CheckForUpdatesButton.IsEnabled = _viewModel.CanCheckForUpdates;
        CheckForUpdatesButton.Content = _viewModel.IsCheckingForUpdates ? "Checking..." : "Check for updates";

        if (!string.IsNullOrWhiteSpace(_viewModel.UpdateStatusMessage))
        {
            UpdateStatusText.Text = _viewModel.UpdateStatusMessage;
            UpdateStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatusText.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = string.Empty;
        }

        if (_viewModel.IsUpdateAvailable)
        {
            UpdateAvailablePanel.Visibility = Visibility.Visible;
            UpdateTargetVersionText.Text = $"Version v{_viewModel.LatestVersion}";
            if (!string.IsNullOrWhiteSpace(_viewModel.UpdateReleaseNotes))
            {
                UpdateReleaseNotesHeader.Visibility = Visibility.Visible;
                UpdateReleaseNotesText.Text = _viewModel.UpdateReleaseNotes;
                UpdateReleaseNotesText.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateReleaseNotesHeader.Visibility = Visibility.Collapsed;
                UpdateReleaseNotesText.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            UpdateAvailablePanel.Visibility = Visibility.Collapsed;
        }

        // Update active filter button styling
        UpdateFilterButtons();
    }

    private void UpdateFilterButtons()
    {
        if (_viewModel == null) return;

        var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        var defaultStyle = (Style)Application.Current.Resources["DefaultButtonStyle"];

        FilterAllButton.Style = _viewModel.ActiveFilter == MatchFilterType.All ? accentStyle : defaultStyle;
        FilterFollowingButton.Style = _viewModel.ActiveFilter == MatchFilterType.Following ? accentStyle : defaultStyle;
        FilterLiveButton.Style = _viewModel.ActiveFilter == MatchFilterType.Live ? accentStyle : defaultStyle;
        FilterUpcomingButton.Style = _viewModel.ActiveFilter == MatchFilterType.Upcoming ? accentStyle : defaultStyle;
        FilterFinishedButton.Style = _viewModel.ActiveFilter == MatchFilterType.Finished ? accentStyle : defaultStyle;
    }

    private void OnFilterAllClick(object sender, RoutedEventArgs e) => _viewModel?.SetFilter(MatchFilterType.All);
    private void OnFilterFollowingClick(object sender, RoutedEventArgs e) => _viewModel?.SetFilter(MatchFilterType.Following);
    private void OnFilterLiveClick(object sender, RoutedEventArgs e) => _viewModel?.SetFilter(MatchFilterType.Live);
    private void OnFilterUpcomingClick(object sender, RoutedEventArgs e) => _viewModel?.SetFilter(MatchFilterType.Upcoming);
    private void OnFilterFinishedClick(object sender, RoutedEventArgs e) => _viewModel?.SetFilter(MatchFilterType.Finished);

    private async void OnTeamFollowToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.Tag is string teamId && _viewModel != null)
        {
            await _viewModel.ToggleTeamFollowAsync(teamId);
        }
    }

    private async void OnCompetitionFollowToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.Tag is string compId && _viewModel != null)
        {
            await _viewModel.ToggleCompetitionFollowAsync(compId);
        }
    }

    private async void OnGlobalNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && _viewModel != null && ts.IsOn != _viewModel.IsGlobalNotificationsEnabled)
        {
            await _viewModel.ToggleGlobalNotificationsAsync(ts.IsOn);
        }
    }

    private async void OnNotificationCategoryToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.Tag is NotificationType type && _viewModel != null)
        {
            await _viewModel.ToggleNotificationCategoryAsync(type, ts.IsOn);
        }
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        _viewModel?.CopyDiagnosticsToClipboard();
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.CheckForUpdatesAsync(isManual: true);
        }
    }
}
