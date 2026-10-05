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
using FootballTray.Client.Services.Updates;

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

    public string? UpdateStatusMessage => _mainViewModel?.UpdateStatusMessage;

    public string? UpdateReleaseNotes => _mainViewModel?.UpdateReleaseNotes;

    public bool CanDownloadUpdate => _mainViewModel?.CanDownloadUpdate ?? false;

    public bool IsAcquiringUpdate => _mainViewModel?.IsAcquiringUpdate ?? false;

    public int DownloadProgressPercent => _mainViewModel?.DownloadProgressPercent ?? 0;

    public string? DownloadStatusText => _mainViewModel?.DownloadStatusText;

    public bool IsUpdateDownloaded => _mainViewModel?.IsUpdateDownloaded ?? false;

    public bool CanInstallUpdate => _mainViewModel?.CanInstallUpdate ?? false;

    public bool IsInstallingUpdate => _mainViewModel?.IsInstallingUpdate ?? false;

    public string? InstallStatusText => _mainViewModel?.InstallStatusText;

    public string? DiagnosticsCopyStatus
    {
        get => _diagnosticsCopyStatus;
        private set => SetProperty(ref _diagnosticsCopyStatus, value);
    }

    public async Task CheckForUpdatesAsync()
    {
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
