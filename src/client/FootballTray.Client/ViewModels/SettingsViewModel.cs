using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Audio;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Startup;
using FootballTray.Client.Services.Telemetry;
using FootballTray.Client.Services.Updates;
using FootballTray.Core.Dto;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Dedicated ViewModel governing Display, Notifications, System Startup, Updates, Diagnostics,
/// and Followed Favorites configuration.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainViewModel? _mainViewModel;
    private readonly IPreferencesService _preferencesService;
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly IUpdateDiscoveryService? _updateDiscoveryService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private bool _disposed;
    private string? _diagnosticsCopyStatus;

    public ObservableCollection<FollowedTeam> FollowedTeams { get; } = new();
    public ObservableCollection<FollowedCompetition> FollowedCompetitions { get; } = new();
    public ObservableCollection<CustomQueueMatchItemViewModel> CustomQueueMatches { get; } = new();
    public bool HasCustomQueueMatches => CustomQueueMatches.Count > 0;
    public ObservableCollection<FollowedEntityAlertItemViewModel> FollowedEntityAlerts { get; } = new();
    public bool HasFollowedEntityAlerts => FollowedEntityAlerts.Count > 0;
    public IPreferencesService PreferencesService => _preferencesService;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsViewModel(
        MainViewModel? mainViewModel,
        IPreferencesService preferencesService,
        IDiagnosticsService? diagnosticsService = null,
        IUpdateDiscoveryService? updateDiscoveryService = null,
        DispatcherQueue? dispatcherQueue = null)
    {
        _mainViewModel = mainViewModel;
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _diagnosticsService = diagnosticsService ?? _mainViewModel?.DiagnosticsService ?? new DiagnosticsService(null, _preferencesService);
        _updateDiscoveryService = updateDiscoveryService ?? _mainViewModel?.UpdateDiscoveryService;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

        _preferencesService.PreferencesChanged += OnPreferencesChanged;

        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        }

        RefreshFollowedLists();
    }

    #region Display Settings

    public bool EnableTickerCarousel
    {
        get => _preferencesService.Current.EnableTickerCarousel;
        set
        {
            if (_preferencesService.Current.EnableTickerCarousel != value)
            {
                _ = SetEnableTickerCarouselAsync(value);
            }
        }
    }

    public async Task SetEnableTickerCarouselAsync(bool enable)
    {
        await _preferencesService.SetEnableTickerCarouselAsync(enable);
        OnPropertyChanged(nameof(EnableTickerCarousel));
        if (_mainViewModel != null && _mainViewModel.EnableTickerCarousel != enable)
        {
            await _mainViewModel.SetEnableTickerCarouselAsync(enable);
        }
    }

    public string TickerRotationScope
    {
        get => _preferencesService.Current.TickerRotationScope;
        set
        {
            if (!string.Equals(_preferencesService.Current.TickerRotationScope, value, StringComparison.OrdinalIgnoreCase))
            {
                _ = SetTickerRotationScopeAsync(value);
            }
        }
    }

    public async Task SetTickerRotationScopeAsync(string scope)
    {
        await _preferencesService.SetTickerRotationScopeAsync(scope);
        OnPropertyChanged(nameof(TickerRotationScope));
    }

    public bool EnableTickerEventPreemption
    {
        get => _preferencesService.Current.EnableTickerEventPreemption;
        set
        {
            if (_preferencesService.Current.EnableTickerEventPreemption != value)
            {
                _ = SetEnableTickerEventPreemptionAsync(value);
            }
        }
    }

    public async Task SetEnableTickerEventPreemptionAsync(bool enable)
    {
        await _preferencesService.SetEnableTickerEventPreemptionAsync(enable);
        OnPropertyChanged(nameof(EnableTickerEventPreemption));
    }

    public bool Use24HourFormat
    {
        get => _preferencesService.Current.Use24HourFormat;
        set
        {
            if (_preferencesService.Current.Use24HourFormat != value)
            {
                _ = SetUse24HourFormatAsync(value);
            }
        }
    }

    public async Task SetUse24HourFormatAsync(bool use24Hour)
    {
        await _preferencesService.SetUse24HourFormatAsync(use24Hour);
        OnPropertyChanged(nameof(Use24HourFormat));
        if (_mainViewModel != null && _mainViewModel.Use24HourFormat != use24Hour)
        {
            await _mainViewModel.SetUse24HourFormatAsync(use24Hour);
        }
    }

    #endregion

    #region Notification Settings

    public bool IsKickoffReminderEnabled
    {
        get => _preferencesService.Current.IsKickoffReminderEnabled;
        set
        {
            if (_preferencesService.Current.IsKickoffReminderEnabled != value)
            {
                _ = SetKickoffReminderEnabledAsync(value);
            }
        }
    }

    public async Task SetKickoffReminderEnabledAsync(bool enabled)
    {
        await _preferencesService.SetKickoffReminderEnabledAsync(enabled);
        OnPropertyChanged(nameof(IsKickoffReminderEnabled));
    }

    public int KickoffReminderOffsetMinutes
    {
        get => _preferencesService.Current.KickoffReminderOffsetMinutes;
        set
        {
            if (_preferencesService.Current.KickoffReminderOffsetMinutes != value)
            {
                _ = SetKickoffReminderOffsetMinutesAsync(value);
            }
        }
    }

    public async Task SetKickoffReminderOffsetMinutesAsync(int minutes)
    {
        await _preferencesService.SetKickoffReminderOffsetMinutesAsync(minutes);
        OnPropertyChanged(nameof(KickoffReminderOffsetMinutes));
    }

    public bool GlobalNotificationsEnabled
    {
        get => _preferencesService.Current.Notifications.GlobalEnabled;
        set
        {
            if (_preferencesService.Current.Notifications.GlobalEnabled != value)
            {
                _ = SetGlobalNotificationsEnabledAsync(value);
            }
        }
    }

    public async Task SetGlobalNotificationsEnabledAsync(bool enabled)
    {
        await _preferencesService.SetNotificationsGloballyEnabledAsync(enabled);
        OnPropertyChanged(nameof(GlobalNotificationsEnabled));
    }

    public bool IsGoalsAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.Goal);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.Goal) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.Goal, value);
            }
        }
    }

    public bool IsCardsAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.RedCard);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.RedCard) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.RedCard, value);
            }
        }
    }

    public bool IsMatchStartAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchStarted);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchStarted) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.MatchStarted, value);
            }
        }
    }

    public bool IsHalfTimeAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.HalfTime);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.HalfTime) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.HalfTime, value);
            }
        }
    }

    public bool IsFullTimeAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchFinished);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchFinished) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.MatchFinished, value);
            }
        }
    }

    public bool IsPostponedAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchPostponed);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchPostponed) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.MatchPostponed, value);
            }
        }
    }

    public bool IsCancelledAlertEnabled
    {
        get => _preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchCancelled);
        set
        {
            if (_preferencesService.Current.Notifications.IsTypeEnabled(NotificationType.MatchCancelled) != value)
            {
                _ = SetNotificationTypeEnabledAsync(NotificationType.MatchCancelled, value);
            }
        }
    }

    public async Task SetNotificationTypeEnabledAsync(NotificationType type, bool enabled)
    {
        await _preferencesService.SetNotificationTypeEnabledAsync(type, enabled);
        OnPropertyChanged(nameof(IsGoalsAlertEnabled));
        OnPropertyChanged(nameof(IsCardsAlertEnabled));
        OnPropertyChanged(nameof(IsMatchStartAlertEnabled));
        OnPropertyChanged(nameof(IsHalfTimeAlertEnabled));
        OnPropertyChanged(nameof(IsFullTimeAlertEnabled));
        OnPropertyChanged(nameof(IsPostponedAlertEnabled));
        OnPropertyChanged(nameof(IsCancelledAlertEnabled));
    }

    public NotificationSoundMode SoundMode
    {
        get => _preferencesService.Current.SoundMode;
        set
        {
            if (_preferencesService.Current.SoundMode != value)
            {
                _ = SetSoundModeAsync(value);
            }
        }
    }

    public async Task SetSoundModeAsync(NotificationSoundMode mode)
    {
        await _preferencesService.SetSoundModeAsync(mode);
        OnPropertyChanged(nameof(SoundMode));
    }

    public void PlayTestSound()
    {
        try
        {
            AudioPlaybackService.Instance.PlayTestSound();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsViewModel", "PlayTestSoundFailed", $"Failed to play test sound: {ex.Message}");
        }
    }

    #endregion

    #region System Settings

    public bool IsRunAtStartupEnabled
    {
        get => StartupHelper.IsRunAtStartupEnabled() || _preferencesService.Current.IsRunAtStartupEnabled;
        set
        {
            _ = SetRunAtStartupAsync(value);
        }
    }

    public async Task SetRunAtStartupAsync(bool enabled)
    {
        StartupHelper.SetRunAtStartup(enabled);
        await _preferencesService.SetRunAtStartupAsync(enabled);
        OnPropertyChanged(nameof(IsRunAtStartupEnabled));
    }

    #endregion

    #region Updates & Diagnostics

    public string CurrentVersionText => _mainViewModel?.DiagnosticsVersionText
        ?? $"Version {_diagnosticsService.GetCurrentSnapshot().Version} ({_diagnosticsService.GetCurrentSnapshot().Architecture})";

    public string HealthStatus => _diagnosticsService.GetCurrentSnapshot().HealthState.ToString();

    public string LastSync => _diagnosticsService.GetCurrentSnapshot().LastSuccessfulServerResponseTime?.ToString("HH:mm:ss 'UTC'") ?? "Never";

    public string LastEtag => !string.IsNullOrWhiteSpace(_diagnosticsService.GetCurrentSnapshot().LastKnownETag)
        ? _diagnosticsService.GetCurrentSnapshot().LastKnownETag!
        : "None";

    public string TaskbarStatus => _diagnosticsService.GetCurrentSnapshot().TaskbarHostStatus;

    public bool IsCheckingForUpdates => _mainViewModel?.IsCheckingForUpdates ?? false;

    public bool CanCheckForUpdates => _mainViewModel?.CanCheckForUpdates ?? (_updateDiscoveryService != null);

    public bool IsUpdateAvailable => _mainViewModel?.IsUpdateAvailable ?? false;

    public string? LatestVersion => _mainViewModel?.LatestVersion;

    private string? _customUpdateStatusMessage;
    public string? UpdateStatusMessage
    {
        get => _customUpdateStatusMessage ?? _mainViewModel?.UpdateStatusMessage;
        private set
        {
            if (_customUpdateStatusMessage != value)
            {
                _customUpdateStatusMessage = value;
                OnPropertyChanged(nameof(UpdateStatusMessage));
            }
        }
    }

    public string? UpdateReleaseNotes => _mainViewModel?.UpdateReleaseNotes;

    public bool CanDownloadUpdate => !AppDistributionHelper.IsPackaged && (_mainViewModel?.CanDownloadUpdate ?? false);

    public bool IsAcquiringUpdate => _mainViewModel?.IsAcquiringUpdate ?? false;

    public int DownloadProgressPercent => _mainViewModel?.DownloadProgressPercent ?? 0;

    public string? DownloadStatusText => _mainViewModel?.DownloadStatusText;

    public bool IsUpdateDownloaded => _mainViewModel?.IsUpdateDownloaded ?? false;

    public bool CanInstallUpdate => !AppDistributionHelper.IsPackaged && (_mainViewModel?.CanInstallUpdate ?? false);

    public bool IsInstallingUpdate => _mainViewModel?.IsInstallingUpdate ?? false;

    public string? InstallStatusText => _mainViewModel?.InstallStatusText;

    public string? DiagnosticsCopyStatus
    {
        get => _diagnosticsCopyStatus;
        private set => SetProperty(ref _diagnosticsCopyStatus, value);
    }

    public async Task CheckForUpdatesAsync()
    {
        // On manual update check, always trigger on-demand OTA live telemetry refresh
        _ = Task.Run(() => OtaConfigService.Instance.RefreshAsync());

        if (AppDistributionHelper.IsPackaged)
        {
            UpdateStatusMessage = "App updates are managed automatically by the Microsoft Store. Live telemetry is up to date.";
            return;
        }

        if (_mainViewModel != null)
        {
            await _mainViewModel.CheckForUpdatesAsync();
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_mainViewModel != null)
        {
            await _mainViewModel.DownloadUpdateAsync();
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (_mainViewModel != null)
        {
            await _mainViewModel.InstallUpdateAsync();
        }
    }

    public bool IsDebugLoggingEnabled
    {
        get => _preferencesService.Current.IsDebugLoggingEnabled;
        set
        {
            if (_preferencesService.Current.IsDebugLoggingEnabled != value)
            {
                _ = SetDebugLoggingEnabledAsync(value);
            }
        }
    }

    public async Task SetDebugLoggingEnabledAsync(bool enabled)
    {
        await _preferencesService.SetDebugLoggingEnabledAsync(enabled);
        OnPropertyChanged(nameof(IsDebugLoggingEnabled));
    }

    public void OpenLogsFolder()
    {
        try
        {
            var dir = AppLogger.LogDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsViewModel", "OpenLogsFailed", $"Failed to open logs folder: {ex.Message}");
        }
    }

    public string CopyDiagnostics()
    {
        string report = _diagnosticsService.GetSanitizedReportText();
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            dataPackage.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            DiagnosticsCopyStatus = "Diagnostics report copied to clipboard!";
        }
        catch (Exception ex)
        {
            DiagnosticsCopyStatus = $"Copy failed: {ex.Message}";
        }
        return report;
    }

    public void RefreshDiagnosticsReadout()
    {
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(HealthStatus));
        OnPropertyChanged(nameof(LastSync));
        OnPropertyChanged(nameof(LastEtag));
        OnPropertyChanged(nameof(TaskbarStatus));
    }

    #endregion

    #region Favorites Management

    public void RefreshFollowedLists()
    {
        RunOnUi(() =>
        {
            var prefs = _preferencesService.Current;
            FollowedTeams.Clear();
            if (prefs.FollowedTeams != null)
            {
                foreach (var t in prefs.FollowedTeams)
                {
                    FollowedTeams.Add(t);
                }
            }

            FollowedCompetitions.Clear();
            if (prefs.FollowedCompetitions != null)
            {
                foreach (var c in prefs.FollowedCompetitions)
                {
                    FollowedCompetitions.Add(c);
                }
            }

            OnPropertyChanged(nameof(HasFollowedTeams));
            OnPropertyChanged(nameof(HasFollowedCompetitions));
            RefreshFollowedEntityAlerts();
            RefreshCustomQueueMatches();
        });
    }

    public bool HasFollowedTeams => FollowedTeams.Count > 0;
    public bool HasFollowedCompetitions => FollowedCompetitions.Count > 0;

    public async Task UnfollowTeamAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;
        if (_mainViewModel != null)
        {
            await _mainViewModel.ToggleTeamFollowAsync(teamId, "", null);
        }
        else
        {
            await _preferencesService.UnfollowTeamAsync(teamId);
        }
        RefreshFollowedLists();
    }

    public async Task UnfollowCompetitionAsync(string competitionId)
    {
        if (string.IsNullOrWhiteSpace(competitionId)) return;
        if (_mainViewModel != null)
        {
            await _mainViewModel.ToggleCompetitionFollowAsync(competitionId, "", null);
        }
        else
        {
            await _preferencesService.UnfollowCompetitionAsync(competitionId);
        }
        RefreshFollowedLists();
    }

    #endregion

    private void OnPreferencesChanged(object? sender, UserPreferences newPrefs)
    {
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(EnableTickerCarousel));
            OnPropertyChanged(nameof(Use24HourFormat));
            OnPropertyChanged(nameof(IsKickoffReminderEnabled));
            OnPropertyChanged(nameof(KickoffReminderOffsetMinutes));
            OnPropertyChanged(nameof(GlobalNotificationsEnabled));
            OnPropertyChanged(nameof(SoundMode));
            OnPropertyChanged(nameof(IsRunAtStartupEnabled));
            OnPropertyChanged(nameof(IsDebugLoggingEnabled));
            RefreshFollowedLists();
            RefreshCustomQueueMatches();
            RefreshDiagnosticsReadout();
        });
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RunOnUi(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsCheckingForUpdates):
                case nameof(MainViewModel.CanCheckForUpdates):
                case nameof(MainViewModel.IsUpdateAvailable):
                case nameof(MainViewModel.LatestVersion):
                case nameof(MainViewModel.UpdateStatusMessage):
                case nameof(MainViewModel.UpdateReleaseNotes):
                case nameof(MainViewModel.CanDownloadUpdate):
                case nameof(MainViewModel.IsAcquiringUpdate):
                case nameof(MainViewModel.DownloadProgressPercent):
                case nameof(MainViewModel.DownloadStatusText):
                case nameof(MainViewModel.IsUpdateDownloaded):
                case nameof(MainViewModel.CanInstallUpdate):
                case nameof(MainViewModel.IsInstallingUpdate):
                case nameof(MainViewModel.InstallStatusText):
                    OnPropertyChanged(e.PropertyName);
                    break;
                case nameof(MainViewModel.DiagnosticsVersionText):
                case nameof(MainViewModel.DiagnosticsHealthStatusText):
                case nameof(MainViewModel.DiagnosticsLastSyncText):
                case nameof(MainViewModel.DiagnosticsTaskbarText):
                    RefreshDiagnosticsReadout();
                    break;
                case nameof(MainViewModel.Use24HourFormat):
                    OnPropertyChanged(nameof(Use24HourFormat));
                    break;
                case nameof(MainViewModel.EnableTickerCarousel):
                    OnPropertyChanged(nameof(EnableTickerCarousel));
                    break;
            }
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
        else
        {
            action();
        }
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #region Custom Queue Management

    public void RefreshCustomQueueMatches()
    {
        RunOnUi(() =>
        {
            var prefs = _preferencesService.GetPreferences();
            var queueIds = prefs.CustomTickerQueueMatchIds ?? new List<string>();
            CustomQueueMatches.Clear();

            int idx = 1;
            foreach (var qId in queueIds)
            {
                var match = _mainViewModel?.GetMatchDtoById(qId)
                    ?? App.CurrentMainViewModel?.GetMatchDtoById(qId)
                    ?? App.CurrentTickerCoordinator?.GetQueuedMatchSnapshot(qId)
                    ?? App.CurrentTickerCoordinator?.State?.Candidates?.FirstOrDefault(m => string.Equals(m.Id, qId, StringComparison.OrdinalIgnoreCase))
                    ?? _mainViewModel?.LatestTrayData?.Matches?.FirstOrDefault(m => 
                        string.Equals(m.Id, qId, StringComparison.OrdinalIgnoreCase) ||
                        m.Id.EndsWith($"_{qId}", StringComparison.OrdinalIgnoreCase) ||
                        qId.EndsWith($"_{m.Id}", StringComparison.OrdinalIgnoreCase))
                    ?? App.CurrentMainViewModel?.LatestTrayData?.Matches?.FirstOrDefault(m => 
                        string.Equals(m.Id, qId, StringComparison.OrdinalIgnoreCase) ||
                        m.Id.EndsWith($"_{qId}", StringComparison.OrdinalIgnoreCase) ||
                        qId.EndsWith($"_{m.Id}", StringComparison.OrdinalIgnoreCase));

                string title = match != null 
                    ? $"{match.HomeTeam?.ShortName ?? match.HomeTeam?.Name ?? "Home"} vs {match.AwayTeam?.ShortName ?? match.AwayTeam?.Name ?? "Away"}"
                    : $"Match {qId}";

                string comp = match?.Competition?.Name ?? "Custom Queue";
                string status = match != null
                    ? (match.Score != null && (match.Score.Home > 0 || match.Score.Away > 0 || match.State == "LIVE" || match.State == "FT" || match.State == "HT")
                        ? $"{match.Score.Home}–{match.Score.Away} ({match.State})"
                        : (match.KickoffTime != default ? match.KickoffTime.ToLocalTime().ToString(prefs.Use24HourFormat ? "HH:mm" : "h:mm tt") : match.State ?? ""))
                    : "Queued";

                CustomQueueMatches.Add(new CustomQueueMatchItemViewModel
                {
                    MatchId = qId,
                    DisplayIndex = idx++,
                    MatchTitle = title,
                    CompetitionName = comp,
                    StatusText = status
                });
            }

            OnPropertyChanged(nameof(HasCustomQueueMatches));
        });
    }

    public async Task MoveQueueMatchUpAsync(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;
        string norm = Services.Preferences.CanonicalEntityIds.StripLegacyProviderPrefix(matchId);
        var prefs = _preferencesService.GetPreferences();
        var list = (prefs.CustomTickerQueueMatchIds ?? new List<string>()).ToList();
        int idx = list.FindIndex(id => string.Equals(id, matchId, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(Services.Preferences.CanonicalEntityIds.StripLegacyProviderPrefix(id), norm, StringComparison.OrdinalIgnoreCase));
        if (idx > 0)
        {
            (list[idx - 1], list[idx]) = (list[idx], list[idx - 1]);
            await _preferencesService.SetCustomTickerQueueMatchIdsAsync(list);
            var updatedPrefs = _preferencesService.GetPreferences();
            App.CurrentTickerCoordinator?.ApplyPreferences(updatedPrefs);
            RunOnUi(() => RefreshCustomQueueMatches());
        }
    }

    public async Task MoveQueueMatchDownAsync(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;
        string norm = Services.Preferences.CanonicalEntityIds.StripLegacyProviderPrefix(matchId);
        var prefs = _preferencesService.GetPreferences();
        var list = (prefs.CustomTickerQueueMatchIds ?? new List<string>()).ToList();
        int idx = list.FindIndex(id => string.Equals(id, matchId, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(Services.Preferences.CanonicalEntityIds.StripLegacyProviderPrefix(id), norm, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx < list.Count - 1)
        {
            (list[idx + 1], list[idx]) = (list[idx], list[idx + 1]);
            await _preferencesService.SetCustomTickerQueueMatchIdsAsync(list);
            var updatedPrefs = _preferencesService.GetPreferences();
            App.CurrentTickerCoordinator?.ApplyPreferences(updatedPrefs);
            RunOnUi(() => RefreshCustomQueueMatches());
        }
    }

    public async Task RemoveQueueMatchAsync(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;
        string norm = Services.Preferences.CanonicalEntityIds.StripLegacyProviderPrefix(matchId);
        var prefs = _preferencesService.GetPreferences();
        var list = (prefs.CustomTickerQueueMatchIds ?? new List<string>())
            .Where(id => !string.Equals(id, matchId, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(Services.Preferences.CanonicalEntityIds.StripLegacyProviderPrefix(id), norm, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await _preferencesService.SetCustomTickerQueueMatchIdsAsync(list);
        var updatedPrefs = _preferencesService.GetPreferences();
        App.CurrentTickerCoordinator?.ApplyPreferences(updatedPrefs);
        _mainViewModel?.SyncPinnedStateAcrossItems(null);
        App.CurrentMainViewModel?.SyncPinnedStateAcrossItems(null);
        RunOnUi(() => RefreshCustomQueueMatches());
    }

    public async Task ClearCustomQueueAsync()
    {
        await _preferencesService.SetCustomTickerQueueMatchIdsAsync(new List<string>());
        var updatedPrefs = _preferencesService.GetPreferences();
        App.CurrentTickerCoordinator?.ApplyPreferences(updatedPrefs);
        _mainViewModel?.SyncPinnedStateAcrossItems(null);
        App.CurrentMainViewModel?.SyncPinnedStateAcrossItems(null);
        RunOnUi(() => RefreshCustomQueueMatches());
    }

    public async Task ReorderCustomQueueAsync(IEnumerable<string> orderedMatchIds)
    {
        if (orderedMatchIds == null) return;
        var list = orderedMatchIds.ToList();
        await _preferencesService.SetCustomTickerQueueMatchIdsAsync(list);
        var updatedPrefs = _preferencesService.GetPreferences();
        App.CurrentTickerCoordinator?.ApplyPreferences(updatedPrefs);
        RunOnUi(() => RefreshCustomQueueMatches());
    }

    #endregion

    #region Entity Alert Overrides Management

    public void RefreshFollowedEntityAlerts()
    {
        RunOnUi(() =>
        {
            var prefs = _preferencesService.Current;
            FollowedEntityAlerts.Clear();

            if (prefs.FollowedTeams != null)
            {
                foreach (var t in prefs.FollowedTeams)
                {
                    EntityAlertProfileDto? profile = null;
                    string canon = CanonicalEntityIds.TryTranslateLegacyId(t.Id, out var cId) ? cId : t.Id;
                    if (prefs.TeamAlertProfiles != null)
                    {
                        if (!prefs.TeamAlertProfiles.TryGetValue(t.Id, out profile))
                        {
                            prefs.TeamAlertProfiles.TryGetValue(canon, out profile);
                        }
                    }

                    var (badge, canReset) = ComputeAlertStatusBadge(profile);
                    FollowedEntityAlerts.Add(new FollowedEntityAlertItemViewModel
                    {
                        Id = t.Id,
                        Name = t.Name,
                        IsTeam = true,
                        StatusBadge = badge,
                        CanReset = canReset
                    });
                }
            }

            if (prefs.FollowedCompetitions != null)
            {
                foreach (var c in prefs.FollowedCompetitions)
                {
                    EntityAlertProfileDto? profile = null;
                    string canon = CanonicalEntityIds.TryTranslateLegacyId(c.Id, out var cId) ? cId : c.Id;
                    if (prefs.CompetitionAlertProfiles != null)
                    {
                        if (!prefs.CompetitionAlertProfiles.TryGetValue(c.Id, out profile))
                        {
                            prefs.CompetitionAlertProfiles.TryGetValue(canon, out profile);
                        }
                    }

                    var (badge, canReset) = ComputeAlertStatusBadge(profile);
                    FollowedEntityAlerts.Add(new FollowedEntityAlertItemViewModel
                    {
                        Id = c.Id,
                        Name = c.DisplayName,
                        IsTeam = false,
                        StatusBadge = badge,
                        CanReset = canReset
                    });
                }
            }

            OnPropertyChanged(nameof(HasFollowedEntityAlerts));
        });
    }

    public static (string badge, bool canReset) ComputeAlertStatusBadge(EntityAlertProfileDto? profile)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.Mode) || string.Equals(profile.Mode, "InheritGlobal", StringComparison.OrdinalIgnoreCase))
        {
            return ("🌐 Global Default", false);
        }

        if (string.Equals(profile.Mode, "Muted", StringComparison.OrdinalIgnoreCase))
        {
            return ("🔕 Muted", true);
        }

        var parts = new List<string>();
        if (profile.EnableGoals) parts.Add("Goals");
        if (profile.EnableRedCards) parts.Add("Cards");
        if (profile.EnableKickoff) parts.Add("Kickoff");
        if (profile.EnableHalfTime) parts.Add("HT");
        if (profile.EnableFullTime) parts.Add("FT");
        if (profile.EnableKickoffReminder) parts.Add("Remind");
        if (profile.EnablePostponed) parts.Add("Postponed");
        if (profile.EnableCancelled) parts.Add("Cancelled");

        string summary = parts.Count > 0 ? string.Join(", ", parts) : "None";
        if (!string.IsNullOrWhiteSpace(profile.SoundOverride) && !string.Equals(profile.SoundOverride, "InheritGlobal", StringComparison.OrdinalIgnoreCase))
        {
            summary += $" • 🔊 {profile.SoundOverride}";
        }

        return ($"⚙️ Custom ({summary})", true);
    }

    public async Task ResetEntityAlertProfileAsync(FollowedEntityAlertItemViewModel item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Id)) return;
        var defaultProfile = new EntityAlertProfileDto { Mode = "InheritGlobal" };
        if (item.IsTeam)
        {
            await _preferencesService.SetTeamAlertProfileAsync(item.Id, defaultProfile);
        }
        else
        {
            await _preferencesService.SetCompetitionAlertProfileAsync(item.Id, defaultProfile);
        }
        RefreshFollowedEntityAlerts();
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _preferencesService.PreferencesChanged -= OnPreferencesChanged;
            if (_mainViewModel != null)
            {
                _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            }
        }
    }
}

public class CustomQueueMatchItemViewModel : INotifyPropertyChanged
{
    public string MatchId { get; init; } = string.Empty;
    
    private int _displayIndex;
    public int DisplayIndex
    {
        get => _displayIndex;
        set
        {
            if (_displayIndex != value)
            {
                _displayIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIndex)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexBadge)));
            }
        }
    }
    
    public string IndexBadge => $"#{DisplayIndex}";
    public string MatchTitle { get; init; } = string.Empty;
    public string CompetitionName { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class FollowedEntityAlertItemViewModel : INotifyPropertyChanged
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsTeam { get; init; }
    public string TypeBadge => IsTeam ? "🛡️ Club" : "🏆 Competition";
    
    private string _statusBadge = "🌐 Global Default";
    public string StatusBadge
    {
        get => _statusBadge;
        set
        {
            if (_statusBadge != value)
            {
                _statusBadge = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBadge)));
            }
        }
    }

    private bool _canReset;
    public bool CanReset
    {
        get => _canReset;
        set
        {
            if (_canReset != value)
            {
                _canReset = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReset)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}