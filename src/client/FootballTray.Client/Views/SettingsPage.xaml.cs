using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client.Views;

/// <summary>
/// Dedicated Settings page governing Display, Notifications, Startup, Updates, Diagnostics,
/// and Followed Favorites.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _isInitializing = true;

    public SettingsViewModel? ViewModel { get; private set; }

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel = App.CurrentMainViewModel?.Settings ??
            new SettingsViewModel(App.CurrentMainViewModel, App.CurrentPreferencesService ?? new PreferencesService());

        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SyncUiFromViewModel();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SyncUiFromViewModel();
        });
    }

    private void SyncUiFromViewModel()
    {
        if (ViewModel == null) return;
        _isInitializing = true;

        try
        {
            // 1. Display
            TickerCarouselToggle.IsOn = ViewModel.EnableTickerCarousel;
            TimeFormat24HourToggle.IsOn = ViewModel.Use24HourFormat;

            // 2. Notifications
            KickoffReminderToggle.IsOn = ViewModel.IsKickoffReminderEnabled;
            KickoffLeadTimePanel.Visibility = ViewModel.IsKickoffReminderEnabled ? Visibility.Visible : Visibility.Collapsed;
            SelectLeadTime(ViewModel.KickoffReminderOffsetMinutes);

            GlobalNotificationsToggle.IsOn = ViewModel.GlobalNotificationsEnabled;
            SelectSoundMode(ViewModel.SoundMode);

            // 3. System
            RunAtStartupToggle.IsOn = ViewModel.IsRunAtStartupEnabled;

            // 4. Updates & Diagnostics
            UpdateCurrentVersionText.Text = $"Current: {ViewModel.CurrentVersionText}";
            CheckForUpdatesButton.IsEnabled = ViewModel.CanCheckForUpdates;
            CheckForUpdatesButton.Content = ViewModel.IsCheckingForUpdates ? "Checking..." : "Check for updates";

            if (!string.IsNullOrWhiteSpace(ViewModel.UpdateStatusMessage))
            {
                UpdateStatusText.Text = ViewModel.UpdateStatusMessage;
                UpdateStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = string.Empty;
            }

            if (ViewModel.IsUpdateAvailable)
            {
                UpdateAvailablePanel.Visibility = Visibility.Visible;
                UpdateTargetVersionText.Text = $"Version v{ViewModel.LatestVersion} available";
                UpdateReleaseNotesText.Text = ViewModel.UpdateReleaseNotes ?? string.Empty;
                DownloadUpdateButton.IsEnabled = ViewModel.CanDownloadUpdate;

                if (ViewModel.IsAcquiringUpdate)
                {
                    UpdateDownloadProgressBar.Visibility = Visibility.Visible;
                    UpdateDownloadProgressBar.Value = ViewModel.DownloadProgressPercent;
                    UpdateDownloadStatusText.Visibility = Visibility.Visible;
                    UpdateDownloadStatusText.Text = ViewModel.DownloadStatusText ?? "Downloading update...";
                    DownloadUpdateButton.Visibility = Visibility.Visible;
                    DownloadUpdateButton.Content = "Downloading...";
                    InstallUpdateButton.Visibility = Visibility.Collapsed;
                }
                else if (ViewModel.IsUpdateDownloaded)
                {
                    UpdateDownloadProgressBar.Visibility = Visibility.Collapsed;
                    UpdateDownloadStatusText.Visibility = Visibility.Visible;
                    UpdateDownloadStatusText.Text = "Update downloaded and verified.";
                    DownloadUpdateButton.Visibility = Visibility.Collapsed;

                    InstallUpdateButton.Visibility = Visibility.Visible;
                    InstallUpdateButton.IsEnabled = ViewModel.CanInstallUpdate;
                    InstallUpdateButton.Content = ViewModel.IsInstallingUpdate ? "Installing..." : "Install update";
                }
                else
                {
                    UpdateDownloadProgressBar.Visibility = Visibility.Collapsed;
                    UpdateDownloadStatusText.Visibility = Visibility.Collapsed;
                    DownloadUpdateButton.Visibility = Visibility.Visible;
                    DownloadUpdateButton.Content = "Download update";
                    InstallUpdateButton.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                UpdateAvailablePanel.Visibility = Visibility.Collapsed;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
            }

            // Health Readout
            HealthStatusText.Text = ViewModel.HealthStatus;
            LastSyncText.Text = ViewModel.LastSync;
            LastEtagText.Text = ViewModel.LastEtag;
            TaskbarStatusText.Text = ViewModel.TaskbarStatus;

            // Logging
            DebugLoggingToggle.IsOn = ViewModel.IsDebugLoggingEnabled;

            if (!string.IsNullOrWhiteSpace(ViewModel.DiagnosticsCopyStatus))
            {
                DiagnosticsCopyStatusText.Text = ViewModel.DiagnosticsCopyStatus;
                DiagnosticsCopyStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                DiagnosticsCopyStatusText.Visibility = Visibility.Collapsed;
            }

            // 5. Followed Favorites
            FollowedTeamsListView.ItemsSource = ViewModel.FollowedTeams;
            NoFollowedTeamsText.Visibility = ViewModel.HasFollowedTeams ? Visibility.Collapsed : Visibility.Visible;

            FollowedCompetitionsListView.ItemsSource = ViewModel.FollowedCompetitions;
            NoFollowedCompetitionsText.Visibility = ViewModel.HasFollowedCompetitions ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void SelectLeadTime(int minutes)
    {
        foreach (var item in KickoffLeadTimeCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tagStr && int.TryParse(tagStr, out var tagVal) && tagVal == minutes)
            {
                KickoffLeadTimeCombo.SelectedItem = item;
                return;
            }
        }
        if (KickoffLeadTimeCombo.Items.Count > 2)
        {
            KickoffLeadTimeCombo.SelectedIndex = 2; // 15 min default
        }
    }

    private void SelectSoundMode(NotificationSoundMode mode)
    {
        var modeStr = mode.ToString();
        foreach (var item in SoundModeCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, modeStr, StringComparison.OrdinalIgnoreCase))
            {
                SoundModeCombo.SelectedItem = item;
                return;
            }
        }
        if (SoundModeCombo.Items.Count > 0)
        {
            SoundModeCombo.SelectedIndex = 0;
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true)
        {
            Frame.GoBack();
        }
        else
        {
            Frame?.Navigate(typeof(MainPage));
        }
    }

    private async void OnTickerCarouselToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetEnableTickerCarouselAsync(TickerCarouselToggle.IsOn);
    }

    private async void OnTimeFormat24HourToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetUse24HourFormatAsync(TimeFormat24HourToggle.IsOn);
    }

    private async void OnKickoffReminderToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        var enabled = KickoffReminderToggle.IsOn;
        KickoffLeadTimePanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        await ViewModel.SetKickoffReminderEnabledAsync(enabled);
    }

    private async void OnKickoffLeadTimeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        if (KickoffLeadTimeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            int.TryParse(tagStr, out var minutes))
        {
            await ViewModel.SetKickoffReminderOffsetMinutesAsync(minutes);
        }
    }

    private async void OnGlobalNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetGlobalNotificationsEnabledAsync(GlobalNotificationsToggle.IsOn);
    }

    private async void OnSoundModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        if (SoundModeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<NotificationSoundMode>(tag, true, out var mode))
        {
            await ViewModel.SetSoundModeAsync(mode);
        }
    }

    private void OnTestSoundClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.PlayTestSound();
    }

    private async void OnRunAtStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetRunAtStartupAsync(RunAtStartupToggle.IsOn);
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.CheckForUpdatesAsync();
        }
    }

    private async void OnDownloadUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.DownloadUpdateAsync();
        }
    }

    private async void OnInstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.InstallUpdateAsync();
        }
    }

    private async void OnDebugLoggingToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetDebugLoggingEnabledAsync(DebugLoggingToggle.IsOn);
    }

    private void OnOpenLogsClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.OpenLogsFolder();
    }

    private void OnCopyDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        ViewModel?.CopyDiagnostics();
    }

    private async void OnUnfollowTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string teamId && ViewModel != null)
        {
            await ViewModel.UnfollowTeamAsync(teamId);
        }
    }

    private async void OnUnfollowCompetitionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string compId && ViewModel != null)
        {
            await ViewModel.UnfollowCompetitionAsync(compId);
        }
    }
}
