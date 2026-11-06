using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.ViewModels;
using Windows.ApplicationModel.DataTransfer;

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
            ViewModel.RefreshFollowedLists();
            ViewModel.RefreshCustomQueueMatches();
            ViewModel.RefreshFollowedEntityAlerts();
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
            SelectRotationScope(ViewModel.TickerRotationScope);
            TickerRotationScopeCombo.IsEnabled = ViewModel.EnableTickerCarousel;
            TimeFormat24HourToggle.IsOn = ViewModel.Use24HourFormat;

            // 2. Notifications
            KickoffReminderToggle.IsOn = ViewModel.IsKickoffReminderEnabled;
            KickoffLeadTimePanel.Visibility = ViewModel.IsKickoffReminderEnabled ? Visibility.Visible : Visibility.Collapsed;
            SelectLeadTime(ViewModel.KickoffReminderOffsetMinutes);

            GlobalNotificationsToggle.IsOn = ViewModel.GlobalNotificationsEnabled;
            GoalNotificationsToggle.IsOn = ViewModel.IsGoalsAlertEnabled;
            CardNotificationsToggle.IsOn = ViewModel.IsCardsAlertEnabled;
            MatchStartNotificationsToggle.IsOn = ViewModel.IsMatchStartAlertEnabled;
            HalfTimeNotificationsToggle.IsOn = ViewModel.IsHalfTimeAlertEnabled;
            FullTimeNotificationsToggle.IsOn = ViewModel.IsFullTimeAlertEnabled;
            PostponedNotificationsToggle.IsOn = ViewModel.IsPostponedAlertEnabled;
            CancelledNotificationsToggle.IsOn = ViewModel.IsCancelledAlertEnabled;
            SelectSoundMode(ViewModel.SoundMode);

            // 3. System
            RunAtStartupToggle.IsOn = ViewModel.IsRunAtStartupEnabled;

            // 4. Updates & Diagnostics
            if (FootballTray.Core.AppRuntimeEnvironment.IsPackagedMsix)
            {
                UpdateCurrentVersionText.Text = $"Current: {ViewModel.CurrentVersionText} (Microsoft Store)";
                CheckForUpdatesButton.Visibility = Visibility.Collapsed;
                UpdateAvailablePanel.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = "Updates managed by Microsoft Store";
                UpdateStatusText.Visibility = Visibility.Visible;
            }
            else
            {
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
            FollowedTeamsListView.ItemsSource = null;
            FollowedTeamsListView.ItemsSource = ViewModel.FollowedTeams;
            NoFollowedTeamsText.Visibility = ViewModel.HasFollowedTeams ? Visibility.Collapsed : Visibility.Visible;

            FollowedCompetitionsListView.ItemsSource = null;
            FollowedCompetitionsListView.ItemsSource = ViewModel.FollowedCompetitions;
            NoFollowedCompetitionsText.Visibility = ViewModel.HasFollowedCompetitions ? Visibility.Collapsed : Visibility.Visible;

            // Custom Queue & Entity Alerts
            if (CustomQueueMatchesItemsControl.ItemsSource != ViewModel.CustomQueueMatches)
            {
                CustomQueueMatchesItemsControl.ItemsSource = ViewModel.CustomQueueMatches;
            }
            NoCustomQueueMatchesText.Visibility = ViewModel.CustomQueueMatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            EntityAlertOverridesListView.ItemsSource = null;
            EntityAlertOverridesListView.ItemsSource = ViewModel.FollowedEntityAlerts;
            NoFollowedEntityAlertsText.Visibility = ViewModel.FollowedEntityAlerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void SelectRotationScope(string scope)
    {
        if (TickerRotationScopeCombo == null) return;
        foreach (var item in TickerRotationScopeCombo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, scope, StringComparison.OrdinalIgnoreCase))
            {
                TickerRotationScopeCombo.SelectedItem = cbi;
                return;
            }
        }
        TickerRotationScopeCombo.SelectedIndex = 0;
    }

    private async void OnTickerRotationScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        if (TickerRotationScopeCombo.SelectedItem is ComboBoxItem selected && selected.Tag is string scope)
        {
            await ViewModel.SetTickerRotationScopeAsync(scope);
        }
    }

    private async void OnTickerCarouselToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        TickerRotationScopeCombo.IsEnabled = TickerCarouselToggle.IsOn;
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

    private async void OnGoalNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.Goal, GoalNotificationsToggle.IsOn);
    }

    private async void OnCardNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.RedCard, CardNotificationsToggle.IsOn);
    }

    private async void OnMatchStartNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.MatchStarted, MatchStartNotificationsToggle.IsOn);
    }

    private async void OnHalfTimeNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.HalfTime, HalfTimeNotificationsToggle.IsOn);
    }

    private async void OnFullTimeNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.MatchFinished, FullTimeNotificationsToggle.IsOn);
    }

    private async void OnPostponedNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.MatchPostponed, PostponedNotificationsToggle.IsOn);
    }

    private async void OnCancelledNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ViewModel == null) return;
        await ViewModel.SetNotificationTypeEnabledAsync(FootballTray.Client.Services.Notifications.NotificationType.MatchCancelled, CancelledNotificationsToggle.IsOn);
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
        if (FootballTray.Core.AppRuntimeEnvironment.IsPackagedMsix) return;
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
        if (ViewModel == null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Health: {ViewModel.HealthStatus}");
            sb.AppendLine($"Last Sync: {ViewModel.LastSync}");
            sb.AppendLine($"ETag: {ViewModel.LastEtag}");

            try
            {
                string logPath = AppLogger.FileSink.LogFilePath;
                if (File.Exists(logPath))
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    var lines = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                    var last50 = lines.TakeLast(50);
                    sb.AppendLine();
                    sb.AppendLine("--- Recent Application Logs (Last 50 lines) ---");
                    foreach (var l in last50)
                    {
                        sb.AppendLine(l);
                    }
                }
                else
                {
                    var recent = AppLogger.GetRecentEntries().TakeLast(50);
                    sb.AppendLine();
                    sb.AppendLine("--- Recent Application Logs (In-Memory) ---");
                    foreach (var entry in recent)
                    {
                        sb.AppendLine(entry.ToString());
                    }
                }
            }
            catch (Exception logEx)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Could not read file log: {logEx.Message} ---");
            }

            string text = sb.ToString();

            bool copied = false;
            try
            {
                OleInitialize(IntPtr.Zero);
                var dataPackage = new DataPackage();
                dataPackage.RequestedOperation = DataPackageOperation.Copy;
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);
                copied = true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SettingsPage", "WinRTClipboardError", $"WinRT Clipboard.SetContent failed: [{ex.GetType().Name}] {ex.Message}. Falling back to native Win32 clipboard.", ex: ex);
                copied = SetClipboardTextWin32(text);
                if (!copied)
                {
                    AppLogger.Warn("SettingsPage", "ClipboardError", $"Failed to copy diagnostics via both WinRT and Win32 clipboard: {ex}");
                }
            }

            if (copied && sender is Button btn)
            {
                var originalContent = btn.Content;
                btn.Content = "Copied!";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    btn.Content = originalContent;
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsPage", "ClipboardError", $"Failed to prepare diagnostics: {ex}");
        }
    }

    private static bool SetClipboardTextWin32(string text)
    {
        if (text == null) return false;
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            int bytes = (text.Length + 1) * sizeof(char);
            IntPtr hGlobal = Marshal.AllocHGlobal(bytes);
            if (hGlobal == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, hGlobal, text.Length);
                Marshal.WriteInt16(hGlobal, text.Length * sizeof(char), 0);
                if (SetClipboardData(13 /* CF_UNICODETEXT */, hGlobal) == IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(hGlobal);
                    return false;
                }
                return true;
            }
            catch
            {
                Marshal.FreeHGlobal(hGlobal);
                return false;
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e) => OnCopyDiagnosticsClicked(sender, e);

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

    private static string? GetMatchIdFromSender(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.Tag is string tag && !string.IsNullOrWhiteSpace(tag)) return tag;
            if (fe.DataContext is CustomQueueMatchItemViewModel vm && !string.IsNullOrWhiteSpace(vm.MatchId)) return vm.MatchId;
        }
        return null;
    }

    private async void OnMoveQueueMatchUpClicked(object sender, RoutedEventArgs e)
    {
        string? matchId = GetMatchIdFromSender(sender);
        if (!string.IsNullOrWhiteSpace(matchId) && ViewModel != null)
        {
            await ViewModel.MoveQueueMatchUpAsync(matchId);
            NoCustomQueueMatchesText.Visibility = ViewModel.CustomQueueMatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void OnMoveQueueMatchDownClicked(object sender, RoutedEventArgs e)
    {
        string? matchId = GetMatchIdFromSender(sender);
        if (!string.IsNullOrWhiteSpace(matchId) && ViewModel != null)
        {
            await ViewModel.MoveQueueMatchDownAsync(matchId);
            NoCustomQueueMatchesText.Visibility = ViewModel.CustomQueueMatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void OnRemoveQueueMatchClicked(object sender, RoutedEventArgs e)
    {
        string? matchId = GetMatchIdFromSender(sender);
        if (!string.IsNullOrWhiteSpace(matchId) && ViewModel != null)
        {
            await ViewModel.RemoveQueueMatchAsync(matchId);
            NoCustomQueueMatchesText.Visibility = ViewModel.CustomQueueMatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void OnClearCustomQueueClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            await ViewModel.ClearCustomQueueAsync();
            NoCustomQueueMatchesText.Visibility = ViewModel.CustomQueueMatches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnConfigureEntityAlertsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is FollowedEntityAlertItemViewModel item)
        {
            Controls.EntityAlertFlyoutHelper.ShowEntityAlertFlyout(fe, item.Id, item.Name, item.IsTeam, ViewModel?.PreferencesService, onSaved: () =>
            {
                ViewModel?.RefreshFollowedEntityAlerts();
            });
        }
    }

    private async void OnResetEntityAlertClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is FollowedEntityAlertItemViewModel item && ViewModel != null)
        {
            await ViewModel.ResetEntityAlertProfileAsync(item);
        }
    }
}
