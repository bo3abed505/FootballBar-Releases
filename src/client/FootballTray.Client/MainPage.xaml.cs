using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        NotificationCategoriesList.ItemsSource = _viewModel.NotificationCategories;

        GlobalNotificationsSwitch.IsOn = _viewModel.IsGlobalNotificationsEnabled;
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

        // Global notifications switch
        if (GlobalNotificationsSwitch.IsOn != _viewModel.IsGlobalNotificationsEnabled)
        {
            GlobalNotificationsSwitch.IsOn = _viewModel.IsGlobalNotificationsEnabled;
        }

        // Ticker Carousel switch
        if (TickerCarouselSwitch != null && TickerCarouselSwitch.IsOn != _viewModel.EnableTickerCarousel)
        {
            TickerCarouselSwitch.IsOn = _viewModel.EnableTickerCarousel;
        }

        // 24-Hour Time Format switch
        if (TimeFormat24HourSwitch != null && TimeFormat24HourSwitch.IsOn != _viewModel.Use24HourFormat)
        {
            TimeFormat24HourSwitch.IsOn = _viewModel.Use24HourFormat;
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

            // Update package download and verification UI
            DownloadUpdateButton.IsEnabled = _viewModel.CanDownloadUpdate;

            if (_viewModel.IsAcquiringUpdate)
            {
                UpdateDownloadProgressBar.Visibility = Visibility.Visible;
                UpdateDownloadProgressBar.Value = _viewModel.DownloadProgressPercent;
                UpdateDownloadProgressBar.IsIndeterminate = _viewModel.AcquisitionState == Services.Updates.UpdateAcquisitionState.Verifying;
                UpdateDownloadStatusText.Visibility = Visibility.Visible;
                UpdateDownloadStatusText.Text = _viewModel.DownloadStatusText ?? "Downloading update...";
                DownloadUpdateButton.Visibility = Visibility.Visible;
                DownloadUpdateButton.Content = _viewModel.AcquisitionState == Services.Updates.UpdateAcquisitionState.Verifying
                    ? "Verifying..."
                    : "Downloading...";
            }
            else if (_viewModel.IsUpdateDownloaded)
            {
                UpdateDownloadProgressBar.Visibility = Visibility.Collapsed;
                UpdateDownloadStatusText.Visibility = Visibility.Visible;
                UpdateDownloadStatusText.Text = "Update downloaded and verified.";
                DownloadUpdateButton.Visibility = Visibility.Collapsed;

                InstallUpdateButton.Visibility = Visibility.Visible;
                InstallUpdateButton.IsEnabled = _viewModel.CanInstallUpdate;

                if (_viewModel.IsInstallingUpdate)
                {
                    InstallUpdateButton.Content = _viewModel.InstallationState == Services.Updates.UpdateInstallationState.Preparing
                        ? "Preparing..."
                        : "Installing...";
                    UpdateInstallStatusText.Visibility = Visibility.Visible;
                    UpdateInstallStatusText.Text = _viewModel.InstallStatusText ?? "Preparing update installation...";
                }
                else
                {
                    InstallUpdateButton.Content = "Install update";
                    if (!string.IsNullOrWhiteSpace(_viewModel.InstallStatusText))
                    {
                        UpdateInstallStatusText.Visibility = Visibility.Visible;
                        UpdateInstallStatusText.Text = _viewModel.InstallStatusText;
                    }
                    else
                    {
                        UpdateInstallStatusText.Visibility = Visibility.Collapsed;
                        UpdateInstallStatusText.Text = string.Empty;
                    }
                }
            }
            else
            {
                UpdateDownloadProgressBar.Visibility = Visibility.Collapsed;
                DownloadUpdateButton.Visibility = Visibility.Visible;
                DownloadUpdateButton.Content = "Download update";
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                UpdateInstallStatusText.Visibility = Visibility.Collapsed;

                if (!string.IsNullOrWhiteSpace(_viewModel.DownloadStatusText))
                {
                    UpdateDownloadStatusText.Visibility = Visibility.Visible;
                    UpdateDownloadStatusText.Text = _viewModel.DownloadStatusText;
                }
                else
                {
                    UpdateDownloadStatusText.Visibility = Visibility.Collapsed;
                    UpdateDownloadStatusText.Text = string.Empty;
                }
            }
        }
        else
        {
            UpdateAvailablePanel.Visibility = Visibility.Collapsed;
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            UpdateInstallStatusText.Visibility = Visibility.Collapsed;
        }

        // Update active filter button styling
        UpdateFilterButtons();
        UpdateSearchCatalogUi();
        UpdateSoundModeUi();
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
            await _viewModel.SearchCatalog.FollowTeamAsync(team);
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
            await _viewModel.SearchCatalog.FollowCompetitionAsync(comp);
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
        try
        {
            if (_viewModel != null)
            {
                var report = _viewModel.DiagnosticsService.GenerateReport();
                ClipboardHelper.SetText(report);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DIAGNOSTICS] Copy to clipboard failed: {ex.Message}");
        }
    }

    private static class ClipboardHelper
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public static bool SetText(string text)
        {
            if (text == null) return false;

            bool opened = false;
            for (int i = 0; i < 10; i++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    opened = true;
                    break;
                }
                Thread.Sleep(20);
            }

            if (!opened) return false;

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                char[] chars = text.ToCharArray();
                int byteCount = (chars.Length + 1) * sizeof(char);
                IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
                if (hGlobal == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr target = GlobalLock(hGlobal);
                if (target == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }

                try
                {
                    Marshal.Copy(chars, 0, target, chars.Length);
                    Marshal.WriteInt16(target + (chars.Length * sizeof(char)), 0);
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                    return false;
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.CheckForUpdatesAsync(isManual: true);
        }
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.DownloadUpdateAsync();
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            await _viewModel.InstallUpdateAsync();
        }
    }

    private bool _isUpdatingSoundModeUi;

    private void UpdateSoundModeUi()
    {
        if (_viewModel == null || SoundModeComboBox == null) return;

        _isUpdatingSoundModeUi = true;
        try
        {
            SoundModeComboBox.SelectedIndex = _viewModel.SoundMode switch
            {
                NotificationSoundMode.Custom => 0,
                NotificationSoundMode.SystemDefault => 1,
                NotificationSoundMode.Off => 2,
                _ => 0
            };
        }
        finally
        {
            _isUpdatingSoundModeUi = false;
        }
    }

    private async void OnSoundModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSoundModeUi || _viewModel == null || SoundModeComboBox == null) return;

        var targetMode = SoundModeComboBox.SelectedIndex switch
        {
            0 => NotificationSoundMode.Custom,
            1 => NotificationSoundMode.SystemDefault,
            2 => NotificationSoundMode.Off,
            _ => NotificationSoundMode.Custom
        };

        if (_viewModel.SoundMode != targetMode)
        {
            await _viewModel.SetSoundModeAsync(targetMode);
        }
    }

    private void OnTestSoundClicked(object sender, RoutedEventArgs e)
    {
        AudioPlaybackService.Instance.PlayTestSound();
    }

    private async void OnTickerCarouselToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn != _viewModel.EnableTickerCarousel)
        {
            await _viewModel.SetEnableTickerCarouselAsync(toggle.IsOn);
        }
    }

    private async void OnTimeFormat24HourToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not ToggleSwitch toggle) return;
        if (toggle.IsOn != _viewModel.Use24HourFormat)
        {
            await _viewModel.SetUse24HourFormatAsync(toggle.IsOn);
        }
    }
}
