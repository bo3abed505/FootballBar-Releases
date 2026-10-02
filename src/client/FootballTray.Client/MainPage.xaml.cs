using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Audio;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
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
            _viewModel.SearchCatalog.PropertyChanged -= OnSearchCatalogPropertyChanged;
        }
    }

    private void BindViewModel()
    {
        if (_viewModel == null) return;

        FollowedMatchesList.ItemsSource = _viewModel.FollowedMatches;
        OtherMatchesList.ItemsSource = _viewModel.OtherMatches;
        CalendarDaysList.ItemsSource = _viewModel.CalendarDays;
        TeamsList.ItemsSource = _viewModel.SearchCatalog.FollowedTeams;
        CompetitionsList.ItemsSource = _viewModel.SearchCatalog.FollowedCompetitions;
        TeamSearchResultsList.ItemsSource = _viewModel.SearchCatalog.TeamSearchResults;
        CompetitionSearchResultsList.ItemsSource = _viewModel.SearchCatalog.CompetitionSearchResults;
        _viewModel.SearchCatalog.PropertyChanged += OnSearchCatalogPropertyChanged;
        UpdateUiFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateUiFromViewModel();
        });
    }

    private void OnSearchCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateSearchCatalogUi();
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

        // Collapsible Other Matches Expander
        if (OtherMatchesExpander != null)
        {
            OtherMatchesExpander.Header = _viewModel.OtherMatchesHeader;
            OtherMatchesExpander.Visibility = _viewModel.IsOtherMatchesVisible ? Visibility.Visible : Visibility.Collapsed;
            OtherMatchesExpander.IsExpanded = _viewModel.IsOtherMatchesExpanded;
        }

        // 7-Day Calendar Navigation Bar visibility
        if (CalendarScrollViewer != null)
        {
            CalendarScrollViewer.Visibility = _viewModel.IsCalendarVisible ? Visibility.Visible : Visibility.Collapsed;
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

        // Update active filter button styling
        UpdateFilterButtons();
        UpdateSearchCatalogUi();
    }

    private void UpdateSearchCatalogUi()
    {
        if (_viewModel?.SearchCatalog == null) return;

        var catalog = _viewModel.SearchCatalog;

        CatalogSearchProgressBar.Visibility = catalog.IsSearching ? Visibility.Visible : Visibility.Collapsed;

        if (catalog.HasSearchStatusMessage)
        {
            CatalogSearchStatusText.Visibility = Visibility.Visible;
            CatalogSearchStatusText.Text = catalog.SearchStatusMessage ?? string.Empty;
        }
        else
        {
            CatalogSearchStatusText.Visibility = Visibility.Collapsed;
            CatalogSearchStatusText.Text = string.Empty;
        }

        TeamSearchResultsList.Visibility = (catalog.IsTeamsScopeSelected && catalog.HasTeamSearchResults)
            ? Visibility.Visible
            : Visibility.Collapsed;

        CompetitionSearchResultsList.Visibility = (catalog.IsCompetitionsScopeSelected && catalog.HasCompetitionSearchResults)
            ? Visibility.Visible
            : Visibility.Collapsed;

        NoFollowedTeamsText.Visibility = catalog.HasFollowedTeams ? Visibility.Collapsed : Visibility.Visible;
        NoFollowedCompetitionsText.Visibility = catalog.HasFollowedCompetitions ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCatalogSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && _viewModel != null)
        {
            _viewModel.SearchCatalog.SearchQuery = tb.Text;
        }
    }

    private void OnSearchScopeChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SearchCatalog == null) return;

        if (ScopeClubsRadio?.IsChecked == true)
        {
            _viewModel.SearchCatalog.SelectedScope = CatalogSearchScope.Teams;
        }
        else if (ScopeLeaguesRadio?.IsChecked == true)
        {
            _viewModel.SearchCatalog.SelectedScope = CatalogSearchScope.Competitions;
        }
        UpdateSearchCatalogUi();
    }

    private async void OnAddTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FollowedTeamItemViewModel team && _viewModel != null)
        {
            await _viewModel.SearchCatalog.ExecuteToggleFollow(team);
        }
    }

    private async void OnRemoveTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string teamId && _viewModel != null)
        {
            await _viewModel.SearchCatalog.UnfollowTeamAsync(teamId);
        }
    }

    private async void OnAddCompetitionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is FollowedCompetitionItemViewModel comp && _viewModel != null)
        {
            await _viewModel.SearchCatalog.ExecuteToggleFollow(comp);
        }
    }

    private async void OnRemoveCompetitionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string compId && _viewModel != null)
        {
            await _viewModel.SearchCatalog.UnfollowCompetitionAsync(compId);
        }
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

    private void OnCalendarDayClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CalendarDayItemViewModel day && _viewModel != null)
        {
            _viewModel.SelectCalendarDate(day.Date);
        }
    }

    private void OnMatchCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var parent = dep;
            while (parent != null && !ReferenceEquals(parent, sender))
            {
                if (parent is ButtonBase)
                {
                    return;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
        }

        if (sender is FrameworkElement fe && fe.Tag is MatchItemViewModel match)
        {
            OpenMatchCenter(match.Id);
        }
    }

    private void OnTogglePinClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MatchItemViewModel matchItem)
        {
            _viewModel?.TogglePinMatch(matchItem.Id);
        }
    }

    private async void OnToggleFollowHomeTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MatchItemViewModel matchItem && _viewModel != null)
        {
            await _viewModel.ToggleTeamFollowAsync(matchItem.HomeTeamId, matchItem.HomeTeamName, matchItem.HomeTeamLogoUrl);
        }
    }

    private async void OnToggleFollowAwayTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MatchItemViewModel matchItem && _viewModel != null)
        {
            await _viewModel.ToggleTeamFollowAsync(matchItem.AwayTeamId, matchItem.AwayTeamName, matchItem.AwayTeamLogoUrl);
        }
    }

    private async void OnToggleFollowCompetitionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MatchItemViewModel matchItem && _viewModel != null)
        {
            await _viewModel.ToggleCompetitionFollowAsync(matchItem.CompetitionId, matchItem.CompetitionName, matchItem.CompetitionCode);
        }
    }

    private void OpenMatchCenter(string? matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;

        if (App.CurrentTickerCoordinator != null)
        {
            App.CurrentTickerCoordinator.OpenMatchCenter(matchId);
        }
        else
        {
            OpenMatchCenterDirectly(matchId);
        }
    }

    private static async void OpenMatchCenterDirectly(string matchId)
    {
        try
        {
            using var apiClient = new FootballApiClient();
            var response = await apiClient.GetMatchDetailsAsync(matchId);
            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                var window = new Views.MatchCenterWindow();
                window.Populate(response.Envelope.Data);
                window.Activate();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MAIN] Failed to open MatchCenterWindow: {ex.Message}");
        }
    }

    private void OnUnpinMatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is MatchItemViewModel matchItem)
        {
            _viewModel?.UnpinMatch(matchItem.Id);
        }
        else
        {
            _viewModel?.UnpinMatch(null);
        }
    }

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

    private void OnOpenSettingsClicked(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(Views.SettingsPage));
    }
}
