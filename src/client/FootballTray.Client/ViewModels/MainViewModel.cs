using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using FootballTray.Core;
using FootballTray.Core.Dto;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Updates;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Root view model coordinating Match Discovery, Followed Preferences, Notification Controls, and Taskbar status.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISyncService? _syncService;
    private readonly IPreferencesService _preferencesService;
    private readonly Diagnostics.IDiagnosticsService _diagnosticsService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly IUpdateDiscoveryService? _updateDiscoveryService;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayFunc;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _checkLock = new();
    private readonly FootballApiClient _apiClient;
    private readonly bool _disposeApiClient;

    private bool _isMatchesMode = true;
    private int _standingsSubTabIndex;
    private HubCompetitionItemViewModel? _selectedHubCompetition;
    private string? _selectedHubCompetitionId;
    private bool _isTransientJumpActive;
    private readonly List<CompetitionSummaryDto> _followedTeamCompetitions = new();
    private bool _isLoadingHub;
    private string? _hubErrorMessage;
    private string? _currentCompetitionName;
    private string? _currentCompetitionLogoUrl;
    private string? _currentSeasonDisplay;

    private MatchFilterType _activeFilter = MatchFilterType.All;
    private string _connectionStatusText = "Connecting...";
    private bool _isOffline;
    private string _activeTickerText = "No active match";
    private string? _activeMatchId;
    private bool _isGlobalNotificationsEnabled = true;
    private string? _statusMessage;
    private bool _hasStatusMessage;
    private bool _hasMatches;
    private bool _isEmpty;
    private string _emptyStateTitle = "No Matches";
    private string _emptyStateMessage = "Waiting for synchronized football data...";
    private bool _isInitialized;
    private TrayDataDto? _latestTrayData;
    private DateTime _selectedCalendarDate = DateTime.Today;
    private MatchFilterType? _lastFilterApplied;
    private DateTime? _lastDateApplied;
    private bool _hasFollowedMatches;
    private bool _hasOtherMatches;
    private bool _isOtherMatchesVisible;
    private bool _isOtherMatchesExpanded = true;
    private string _otherMatchesHeader = "Other Matches";
    private readonly Dictionary<string, int> _followedCountsByDate = new(StringComparer.OrdinalIgnoreCase);
    private bool _isDateSynchronizing;
    public bool IsSynchronizingSchedule => _isDateSynchronizing;

    private UpdateCheckState _updateCheckState = UpdateCheckState.NotChecked;
    private bool _isCheckingForUpdates;
    private bool _isUpdateAvailable;
    private string? _latestVersion;
    private string? _updateStatusMessage;
    private string? _updateReleaseNotes;
    private ReleaseManifest? _offeredManifest;
    private Task<UpdateCheckResult>? _inFlightCheckTask;
    private bool _currentCheckIsManual;
    private int _startupCheckScheduled;
    private int _manualUpdateCheckRequested;

    private readonly IUpdatePackageAcquisitionService? _updateAcquisitionService;
    private UpdateAcquisitionState _acquisitionState = UpdateAcquisitionState.NotStarted;
    private int _downloadProgressPercent;
    private string? _downloadStatusText;
    private string? _verifiedPackagePath;
    private Task<UpdateAcquisitionResult?>? _inFlightDownloadTask;
    private PackageIdentity? _inFlightDownloadIdentity;
    private readonly object _downloadLock = new();

    private readonly IUpdateInstallationService? _updateInstallationService;
    private readonly IAppShutdownHandler? _appShutdownHandler;
    private UpdateInstallationState _installationState = UpdateInstallationState.NotReady;
    private string? _installStatusText;
    private Task<UpdateInstallationResult?>? _inFlightInstallTask;
    private PackageIdentity? _inFlightInstallIdentity;
    private readonly object _installLock = new();

    private bool IsCurrentOffer(PackageIdentity targetIdentity)
    {
        if (_offeredManifest == null) return false;
        return PackageIdentity.FromManifest(_offeredManifest) == targetIdentity;
    }

    public ObservableCollection<MatchItemViewModel> FilteredMatches { get; } = new();
    public ObservableCollection<MatchItemViewModel> FollowedMatches { get; } = new();
    public ObservableCollection<MatchItemViewModel> OtherMatches { get; } = new();
    public ObservableCollection<CalendarDayItemViewModel> CalendarDays { get; } = new();
    public ObservableCollection<CompetitionGroupViewModel> CompetitionGroups { get; } = new();
    public ObservableCollection<FollowedTeamItemViewModel> AvailableTeams { get; } = new();
    public ObservableCollection<FollowedCompetitionItemViewModel> AvailableCompetitions { get; } = new();
    public ObservableCollection<NotificationCategoryItemViewModel> NotificationCategories { get; } = new();
    public ObservableCollection<HubCompetitionItemViewModel> HubCompetitions { get; } = new();
    public ObservableCollection<StandingRowItemViewModel> CurrentStandings { get; } = new();
    public ObservableCollection<PlayerStatLeaderItemViewModel> CurrentTopScorers { get; } = new();
    public ObservableCollection<PlayerStatLeaderItemViewModel> CurrentTopAssists { get; } = new();

    public bool IsMatchesMode
    {
        get => _isMatchesMode;
        set
        {
            if (SetProperty(ref _isMatchesMode, value))
            {
                OnPropertyChanged(nameof(IsStandingsMode));
                OnPropertyChanged(nameof(MatchesTabBackgroundHex));
                OnPropertyChanged(nameof(MatchesTabForegroundHex));
                OnPropertyChanged(nameof(StandingsTabBackgroundHex));
                OnPropertyChanged(nameof(StandingsTabForegroundHex));
                if (!_isMatchesMode && !_isTransientJumpActive && _selectedHubCompetition == null && HubCompetitions.Count > 0)
                {
                    var match = !string.IsNullOrEmpty(_selectedHubCompetitionId)
                        ? HubCompetitions.FirstOrDefault(c =>
                            c.Id.Equals(_selectedHubCompetitionId, StringComparison.OrdinalIgnoreCase) ||
                            (int.TryParse(ExtractNumericDigits(_selectedHubCompetitionId), out int sNum) && int.TryParse(ExtractNumericDigits(c.Id), out int cNum) && sNum == cNum))
                        : null;
                    SelectedHubCompetition = match ?? HubCompetitions[0];
                }
            }
        }
    }

    public bool IsStandingsMode => !_isMatchesMode;

    public string MatchesTabBackgroundHex => _isMatchesMode ? "#38BDF8" : "Transparent";
    public string MatchesTabForegroundHex => _isMatchesMode ? "#0F172A" : "#94A3B8";
    public string StandingsTabBackgroundHex => !_isMatchesMode ? "#38BDF8" : "Transparent";
    public string StandingsTabForegroundHex => !_isMatchesMode ? "#0F172A" : "#94A3B8";

    public int StandingsSubTabIndex
    {
        get => _standingsSubTabIndex;
        set
        {
            if (SetProperty(ref _standingsSubTabIndex, value))
            {
                OnPropertyChanged(nameof(IsLeagueTableSelected));
                OnPropertyChanged(nameof(IsTopScorersSelected));
                OnPropertyChanged(nameof(IsTopAssistsSelected));
                OnPropertyChanged(nameof(HubTableSubTabVisibility));
                OnPropertyChanged(nameof(HubTopScorersSubTabVisibility));
                OnPropertyChanged(nameof(HubTopAssistsSubTabVisibility));
                OnPropertyChanged(nameof(LeagueTableTabBackgroundHex));
                OnPropertyChanged(nameof(LeagueTableTabForegroundHex));
                OnPropertyChanged(nameof(TopScorersTabBackgroundHex));
                OnPropertyChanged(nameof(TopScorersTabForegroundHex));
                OnPropertyChanged(nameof(TopAssistsTabBackgroundHex));
                OnPropertyChanged(nameof(TopAssistsTabForegroundHex));
            }
        }
    }

    public bool IsLeagueTableSelected => _standingsSubTabIndex == 0;
    public bool IsTopScorersSelected => _standingsSubTabIndex == 1;
    public bool IsTopAssistsSelected => _standingsSubTabIndex == 2;

    public string LeagueTableTabBackgroundHex => _standingsSubTabIndex == 0 ? "#2538BDF8" : "Transparent";
    public string LeagueTableTabForegroundHex => _standingsSubTabIndex == 0 ? "#38BDF8" : "#94A3B8";
    public string TopScorersTabBackgroundHex => _standingsSubTabIndex == 1 ? "#2538BDF8" : "Transparent";
    public string TopScorersTabForegroundHex => _standingsSubTabIndex == 1 ? "#38BDF8" : "#94A3B8";
    public string TopAssistsTabBackgroundHex => _standingsSubTabIndex == 2 ? "#2538BDF8" : "Transparent";
    public string TopAssistsTabForegroundHex => _standingsSubTabIndex == 2 ? "#38BDF8" : "#94A3B8";

    public HubCompetitionItemViewModel? SelectedHubCompetition
    {
        get => _selectedHubCompetition;
        set
        {
            if (SetProperty(ref _selectedHubCompetition, value))
            {
                if (value != null)
                {
                    _isTransientJumpActive = false;
                    _selectedHubCompetitionId = value.Id;
                }
                foreach (var comp in HubCompetitions)
                {
                    comp.IsSelected = comp == value;
                }
                if (value != null)
                {
                    _ = LoadCompetitionHubAsync(value.Id, value.Name, value.LogoUrl);
                }
            }
        }
    }

    public void SelectHubCompetition(HubCompetitionItemViewModel? comp)
    {
        _isTransientJumpActive = false;
        SelectedHubCompetition = comp;
    }

    public bool IsLoadingHub
    {
        get => _isLoadingHub;
        set
        {
            if (_isLoadingHub != value)
            {
                _isLoadingHub = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsHubLoading));
                OnPropertyChanged(nameof(HubLoadingVisibility));
                OnPropertyChanged(nameof(HubContentVisibility));
                OnPropertyChanged(nameof(HubErrorVisibility));
                OnPropertyChanged(nameof(HubTableSubTabVisibility));
                OnPropertyChanged(nameof(HubTopScorersSubTabVisibility));
                OnPropertyChanged(nameof(HubTopAssistsSubTabVisibility));
            }
        }
    }

    public bool IsHubLoading
    {
        get => _isLoadingHub;
        set => IsLoadingHub = value;
    }

    public Visibility HubLoadingVisibility => IsLoadingHub ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HubErrorVisibility => (HasHubError && !IsLoadingHub) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HubContentVisibility => (!IsLoadingHub && !HasHubError) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HubTableSubTabVisibility => (!IsLoadingHub && !HasHubError && IsLeagueTableSelected) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HubTopScorersSubTabVisibility => (!IsLoadingHub && !HasHubError && IsTopScorersSelected) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HubTopAssistsSubTabVisibility => (!IsLoadingHub && !HasHubError && IsTopAssistsSelected) ? Visibility.Visible : Visibility.Collapsed;

    public string? HubErrorMessage
    {
        get => _hubErrorMessage;
        set
        {
            if (SetProperty(ref _hubErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasHubError));
                OnPropertyChanged(nameof(HubErrorVisibility));
                OnPropertyChanged(nameof(HubContentVisibility));
                OnPropertyChanged(nameof(HubTableSubTabVisibility));
                OnPropertyChanged(nameof(HubTopScorersSubTabVisibility));
                OnPropertyChanged(nameof(HubTopAssistsSubTabVisibility));
            }
        }
    }

    public bool HasHubError => !string.IsNullOrEmpty(_hubErrorMessage);

    public string? CurrentCompetitionName
    {
        get => _currentCompetitionName;
        private set => SetProperty(ref _currentCompetitionName, value);
    }

    public string? CurrentCompetitionLogoUrl
    {
        get => _currentCompetitionLogoUrl;
        private set
        {
            if (SetProperty(ref _currentCompetitionLogoUrl, value))
            {
                OnPropertyChanged(nameof(CurrentCompetitionLogoSource));
                OnPropertyChanged(nameof(CurrentCompetitionHasLogo));
                OnPropertyChanged(nameof(CurrentCompetitionLogoVisibility));
                OnPropertyChanged(nameof(CurrentCompetitionFallbackVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Media.ImageSource? CurrentCompetitionLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_currentCompetitionLogoUrl)) return null;
            if (Uri.TryCreate(_currentCompetitionLogoUrl, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public bool CurrentCompetitionHasLogo => !string.IsNullOrWhiteSpace(_currentCompetitionLogoUrl);
    public Visibility CurrentCompetitionLogoVisibility => CurrentCompetitionHasLogo ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurrentCompetitionFallbackVisibility => CurrentCompetitionHasLogo ? Visibility.Collapsed : Visibility.Visible;

    public string? CurrentSeasonDisplay
    {
        get => _currentSeasonDisplay;
        private set => SetProperty(ref _currentSeasonDisplay, value);
    }

    public bool HasStandings => CurrentStandings.Count > 0;
    public bool HasTopScorers => CurrentTopScorers.Count > 0;
    public bool HasTopAssists => CurrentTopAssists.Count > 0;

    public void SwitchToMatchesMode() => IsMatchesMode = true;
    public void SwitchToStandingsMode()
    {
        _isTransientJumpActive = false;
        if (_isMatchesMode)
        {
            IsMatchesMode = false;
            if (SelectedHubCompetition == null)
            {
                if (HubCompetitions.Count > 0)
                {
                    SelectedHubCompetition = HubCompetitions[0];
                }
            }
            else
            {
                if (CurrentStandings.Count == 0 && !IsLoadingHub)
                {
                    _ = LoadCompetitionHubAsync(SelectedHubCompetition.Id, SelectedHubCompetition.Name, SelectedHubCompetition.LogoUrl);
                }
            }
        }
        else
        {
            if (SelectedHubCompetition == null && HubCompetitions.Count > 0)
            {
                SelectedHubCompetition = HubCompetitions[0];
            }
            else if (SelectedHubCompetition != null)
            {
                _ = LoadCompetitionHubAsync(SelectedHubCompetition.Id, SelectedHubCompetition.Name, SelectedHubCompetition.LogoUrl);
            }
        }
    }

    public Diagnostics.IDiagnosticsService DiagnosticsService => _diagnosticsService;
    public IPreferencesService PreferencesService => _preferencesService;
    public IUpdateDiscoveryService? UpdateDiscoveryService => _updateDiscoveryService;
    public IUpdatePackageAcquisitionService? UpdateAcquisitionService => _updateAcquisitionService;
    public IUpdateInstallationService? UpdateInstallationService => _updateInstallationService;
    public IAppShutdownHandler? AppShutdownHandler => _appShutdownHandler;
    public bool HasUpdateCapability => _updateDiscoveryService != null;
    public SettingsViewModel Settings { get; }

    public string DiagnosticsVersionText => $"Version {_diagnosticsService.GetCurrentSnapshot().Version} ({_diagnosticsService.GetCurrentSnapshot().Architecture})";
    public string DiagnosticsHealthStatusText => $"{_diagnosticsService.GetCurrentSnapshot().HealthState} ({_diagnosticsService.GetCurrentSnapshot().FreshnessState})";
    public string DiagnosticsLastSyncText => _diagnosticsService.GetCurrentSnapshot().LastSuccessfulServerResponseTime?.ToString("HH:mm:ss 'UTC'") ?? "Never";
    public string DiagnosticsTaskbarText => _diagnosticsService.GetCurrentSnapshot().TaskbarHostStatus;
    public string DiagnosticsReportText => _diagnosticsService.GetSanitizedReportText();

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Use24HourFormat)));
        ApplyCurrentFilter();
    }

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

    public async Task SetEnableTickerCarouselAsync(bool enableCarousel)
    {
        await _preferencesService.SetEnableTickerCarouselAsync(enableCarousel);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnableTickerCarousel)));
    }

    public UpdateCheckState UpdateCheckState
    {
        get => _updateCheckState;
        private set => SetProperty(ref _updateCheckState, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCheckForUpdates)));
            }
        }
    }

    public bool CanCheckForUpdates => !_isCheckingForUpdates && _updateDiscoveryService != null;

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set
        {
            if (SetProperty(ref _isUpdateAvailable, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
            }
        }
    }

    public string? LatestVersion
    {
        get => _latestVersion;
        private set => SetProperty(ref _latestVersion, value);
    }

    public string? UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetProperty(ref _updateStatusMessage, value);
    }

    public string? UpdateReleaseNotes
    {
        get => _updateReleaseNotes;
        private set => SetProperty(ref _updateReleaseNotes, value);
    }

    public ReleaseManifest? OfferedManifest
    {
        get => _offeredManifest;
        private set
        {
            if (SetProperty(ref _offeredManifest, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
            }
        }
    }

    public UpdateAcquisitionState AcquisitionState
    {
        get => _acquisitionState;
        private set
        {
            if (SetProperty(ref _acquisitionState, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAcquiringUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdateDownloaded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
            }
        }
    }

    public bool IsAcquiringUpdate => _acquisitionState is UpdateAcquisitionState.Downloading or UpdateAcquisitionState.Verifying;

    public bool CanDownloadUpdate
    {
        get
        {
            if (IsAcquiringUpdate || !_isUpdateAvailable || _offeredManifest == null || _updateAcquisitionService == null)
            {
                return false;
            }

            if (_acquisitionState == UpdateAcquisitionState.Verified)
            {
                return false;
            }

            lock (_downloadLock)
            {
                if (_inFlightDownloadTask != null && !_inFlightDownloadTask.IsCompleted)
                {
                    // If an active acquisition task is downloading a DIFFERENT package than current offer,
                    // keep download disabled until that old task finishes.
                    if (_inFlightDownloadIdentity != null && !IsCurrentOffer(_inFlightDownloadIdentity.Value))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    public bool IsUpdateDownloaded =>
        _acquisitionState == UpdateAcquisitionState.Verified &&
        !string.IsNullOrEmpty(_verifiedPackagePath);

    public int DownloadProgressPercent
    {
        get => _downloadProgressPercent;
        private set => SetProperty(ref _downloadProgressPercent, value);
    }

    public string? DownloadStatusText
    {
        get => _downloadStatusText;
        private set => SetProperty(ref _downloadStatusText, value);
    }

    public string? VerifiedPackagePath
    {
        get => _verifiedPackagePath;
        private set
        {
            if (SetProperty(ref _verifiedPackagePath, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUpdateDownloaded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
            }
        }
    }

    public UpdateInstallationState InstallationState
    {
        get => _installationState;
        private set
        {
            if (SetProperty(ref _installationState, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstallingUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
            }
        }
    }

    public bool IsInstallingUpdate =>
        _installationState is UpdateInstallationState.Preparing
            or UpdateInstallationState.StartingInstaller
            or UpdateInstallationState.HandoffStarted;

    public bool CanInstallUpdate
    {
        get
        {
            if (IsInstallingUpdate || !_isUpdateAvailable || _offeredManifest == null || _updateInstallationService == null)
            {
                return false;
            }

            if (!IsUpdateDownloaded || string.IsNullOrEmpty(_verifiedPackagePath))
            {
                return false;
            }

            lock (_installLock)
            {
                if (_inFlightInstallTask != null && !_inFlightInstallTask.IsCompleted)
                {
                    if (_inFlightInstallIdentity != null && !IsCurrentOffer(_inFlightInstallIdentity.Value))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    public string? InstallStatusText
    {
        get => _installStatusText;
        private set => SetProperty(ref _installStatusText, value);
    }

    public string CurrentVersionText => $"v{_updateDiscoveryService?.CurrentVersion?.ToString() ?? AppVersionInfo.Version}";

    public MatchFilterType ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetProperty(ref _activeFilter, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCalendarVisible)));
                if (value == MatchFilterType.Upcoming)
                {
                    _syncService?.TriggerImmediateSync();
                }
                ApplyCurrentFilter();
            }
        }
    }

    public bool IsCalendarVisible => _activeFilter is MatchFilterType.All or MatchFilterType.Following;

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetProperty(ref _connectionStatusText, value);
    }

    public bool IsOffline
    {
        get => _isOffline;
        private set => SetProperty(ref _isOffline, value);
    }

    public string ActiveTickerText
    {
        get => _activeTickerText;
        private set
        {
            if (SetProperty(ref _activeTickerText, value))
            {
                OnPropertyChanged(nameof(TickerMatchDisplay));
                OnPropertyChanged(nameof(HasActiveTicker));
            }
        }
    }

    public string TickerMatchDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_activeTickerText) ||
                _activeTickerText.Equals("No active match", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return _activeTickerText;
        }
    }

    public bool HasActiveTicker => !string.IsNullOrWhiteSpace(TickerMatchDisplay);

    public string? ActiveMatchId
    {
        get => _activeMatchId;
        private set => SetProperty(ref _activeMatchId, value);
    }

    public string? ManualPinnedMatchId { get; private set; }
    public bool IsExplicitlyUnpinned { get; private set; }
    public string? GoalJumpMatchId { get; private set; }
    private readonly Dictionary<string, (int Home, int Away)> _previousScores = new(StringComparer.OrdinalIgnoreCase);

    public bool IsGlobalNotificationsEnabled
    {
        get => _isGlobalNotificationsEnabled;
        set => SetProperty(ref _isGlobalNotificationsEnabled, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                HasStatusMessage = !string.IsNullOrWhiteSpace(value);
            }
        }
    }

    public bool HasStatusMessage
    {
        get => _hasStatusMessage;
        private set => SetProperty(ref _hasStatusMessage, value);
    }

    public bool HasMatches
    {
        get => _hasMatches;
        private set => SetProperty(ref _hasMatches, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    public string EmptyStateTitle
    {
        get => _emptyStateTitle;
        private set => SetProperty(ref _emptyStateTitle, value);
    }

    public string EmptyStateMessage
    {
        get => _emptyStateMessage;
        private set => SetProperty(ref _emptyStateMessage, value);
    }

    private DataFreshnessState _freshnessState;
    private TickerConnectionState _connectionState;

    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncStatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncIconGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSyncFooterVisible)));
            }
        }
    }

    public string SyncStatusText
    {
        get
        {
            if (_isDateSynchronizing) return "Synchronizing schedule...";
            if (IsSyncing) return "Syncing active calendar day & followed team fixtures...";
            if (IsOffline) return "Offline";
            if (_latestTrayData?.QuotaExhausted == true) return "API Limit Reached (Resets Midnight)";
            if (_freshnessState == DataFreshnessState.Empty && FilteredMatches.Count == 0) return "No matches available";
            if (FilteredMatches.Count == 0) return "Up to date (No matches)";
            return "Up to date";
        }
    }

    public string SyncIconGlyph => (_isDateSynchronizing || IsSyncing) ? "\uE72C" : (_latestTrayData?.QuotaExhausted == true ? "\uE7BA" : "\uE73E");

    public bool IsSyncFooterVisible => true;

    public DateTime SelectedCalendarDate
    {
        get => _selectedCalendarDate;
        set
        {
            var target = value.Date;
            if (_selectedCalendarDate.Date != target)
            {
                _selectedCalendarDate = target;
                bool hasCached = _syncService?.HasCachedDataForDate(target) ?? false;

                if (!hasCached)
                {
                    // Immediate loading state:
                    // When no cached payload for that date is immediately available,
                    // immediately transition into synchronizing state
                    _isDateSynchronizing = true;
                    _isEmpty = true;
                    _hasMatches = false;
                    _emptyStateTitle = "Synchronizing Schedule...";
                    _emptyStateMessage = $"Loading fixtures for {_selectedCalendarDate:dddd, MMM d}...";
                    FilteredMatches.Clear();
                    FollowedMatches.Clear();
                    OtherMatches.Clear();
                    CompetitionGroups.Clear();

                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMatches)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyStateTitle)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyStateMessage)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSynchronizingSchedule)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncStatusText)));
                }

                if (_syncService != null)
                {
                    _syncService.TargetDate = target;
                    _syncService.TriggerImmediateSync();
                }
                if (_latestTrayData == null && _syncService?.CurrentTrayData != null)
                {
                    _latestTrayData = _syncService.CurrentTrayData;
                }
                UpdateCalendarSelection();
                if (hasCached)
                {
                    _isDateSynchronizing = false;
                    ApplyCurrentFilter();
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCalendarDate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCalendarDateDisplay)));
            }
        }
    }

    public string SelectedCalendarDateDisplay => _selectedCalendarDate.Date == DateTime.Today
        ? "Today"
        : _selectedCalendarDate.Date == DateTime.Today.AddDays(-1)
            ? "Yesterday"
            : _selectedCalendarDate.Date == DateTime.Today.AddDays(1)
                ? "Tomorrow"
                : _selectedCalendarDate.ToString("dddd, MMM d");

    public bool HasFollowedMatches
    {
        get => _hasFollowedMatches;
        private set => SetProperty(ref _hasFollowedMatches, value);
    }

    public bool HasOtherMatches
    {
        get => _hasOtherMatches;
        private set => SetProperty(ref _hasOtherMatches, value);
    }

    public bool IsOtherMatchesVisible
    {
        get => _isOtherMatchesVisible;
        private set => SetProperty(ref _isOtherMatchesVisible, value);
    }

    public bool IsOtherMatchesExpanded
    {
        get => _isOtherMatchesExpanded;
        set => SetProperty(ref _isOtherMatchesExpanded, value);
    }

    public string OtherMatchesHeader
    {
        get => _otherMatchesHeader;
        private set => SetProperty(ref _otherMatchesHeader, value);
    }

    public void SelectCalendarDate(DateTime date)
    {
        SelectedCalendarDate = date.Date;
    }

    public SearchCatalogViewModel SearchCatalog { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(
        IPreferencesService preferencesService,
        ISyncService? syncService = null,
        Diagnostics.IDiagnosticsService? diagnosticsService = null,
        DispatcherQueue? dispatcherQueue = null,
        IUpdateDiscoveryService? updateDiscoveryService = null,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null,
        IUpdatePackageAcquisitionService? updateAcquisitionService = null,
        IUpdateInstallationService? updateInstallationService = null,
        IAppShutdownHandler? appShutdownHandler = null,
        FootballApiClient? apiClient = null)
    {
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _syncService = syncService;
        _diagnosticsService = diagnosticsService ?? new Diagnostics.DiagnosticsService(_syncService, _preferencesService);
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _updateDiscoveryService = updateDiscoveryService;
        _delayFunc = delayFunc ?? Task.Delay;
        _updateAcquisitionService = updateAcquisitionService;
        _updateInstallationService = updateInstallationService;
        _appShutdownHandler = appShutdownHandler;
        _apiClient = apiClient ?? new FootballApiClient();
        _disposeApiClient = apiClient == null;

        SearchCatalog = new SearchCatalogViewModel(
            _apiClient,
            _preferencesService,
            ImageCacheService.Instance,
            _dispatcherQueue,
            _delayFunc);

        Settings = new SettingsViewModel(
            this,
            _preferencesService,
            _diagnosticsService,
            _updateDiscoveryService,
            _dispatcherQueue);

        _preferencesService.PreferencesChanged += OnPreferencesChanged;

        if (_syncService != null)
        {
            _syncService.TargetDate = _selectedCalendarDate.Date;
            _syncService.TrayUpdated += OnTrayUpdated;
            _syncService.SyncStateChanged += OnSyncStateChanged;
        }

        RebuildCalendarDays();
    }

    public string CopyDiagnosticsToClipboard()
    {
        string report = _diagnosticsService.GetSanitizedReportText();
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            dataPackage.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
        catch
        {
            // Defensive clipboard fallback for headless/test environments
        }

        StatusMessage = "Diagnostics copied to clipboard.";
        return report;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        try
        {
            var prefs = await _preferencesService.LoadAsync();
            PopulatePreferenceCollections(prefs);
            Settings.RefreshFollowedLists();
            await SearchCatalog.InitializeAsync();
            RebuildHubCompetitions(prefs);

            if (_syncService != null)
            {
                _latestTrayData = _syncService.CurrentTrayData;
                RefreshMatches(_syncService.CurrentTrayData, _syncService.FreshnessState, _syncService.CurrentState);
            }
            else
            {
                RefreshMatches(null, DataFreshnessState.Empty, TickerConnectionState.OfflineCached);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load preferences: {ex.Message}";
        }
    }

    private void PopulatePreferenceCollections(UserPreferences prefs)
    {
        IsGlobalNotificationsEnabled = prefs.Notifications.GlobalEnabled;

        // 1. Teams
        var teamsToDisplay = prefs.FollowedTeams ?? new List<FollowedTeam>();
        var followedTeamIds = prefs.GetFollowedTeamIdSet();
        var dedupedTeams = teamsToDisplay.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var team in dedupedTeams)
        {
            var existing = AvailableTeams.FirstOrDefault(t => t.Id.Equals(team.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                AvailableTeams.Add(FollowedTeamItemViewModel.FromDomain(team, true));
            }
        }

        foreach (var available in AvailableTeams)
        {
            available.IsFollowed = followedTeamIds.Contains(available.Id);
        }

        // 2. Competitions
        var compsToDisplay = prefs.FollowedCompetitions ?? new List<FollowedCompetition>();
        var followedCompIds = prefs.GetFollowedCompetitionIdSet();
        var dedupedComps = compsToDisplay.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var comp in dedupedComps)
        {
            if (string.IsNullOrWhiteSpace(comp.LogoUrl))
            {
                comp.LogoUrl = ResolveCompetitionLogoUrl(comp.Id, comp.Name, null);
            }

            var existing = AvailableCompetitions.FirstOrDefault(c => c.Id.Equals(comp.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                AvailableCompetitions.Add(FollowedCompetitionItemViewModel.FromDomain(comp, true));
            }
            else if (string.IsNullOrWhiteSpace(existing.LogoUrl) || existing.LogoUrl != comp.LogoUrl)
            {
                existing.LogoUrl = comp.LogoUrl;
            }
        }

        foreach (var available in AvailableCompetitions)
        {
            available.IsFollowed = followedCompIds.Contains(available.Id);
        }

        // 3. Notification Categories
        var allTypes = new[]
        {
            NotificationType.MatchStarted,
            NotificationType.Goal,
            NotificationType.HalfTime,
            NotificationType.SecondHalf,
            NotificationType.MatchFinished,
            NotificationType.RedCard,
            NotificationType.PenaltyAwarded,
            NotificationType.Kickoff,
            NotificationType.MatchPostponed,
            NotificationType.MatchCancelled,
            NotificationType.MatchResumed
        };

        foreach (var type in allTypes)
        {
            bool isEnabled = prefs.Notifications.IsTypeEnabled(type);
            var existing = NotificationCategories.FirstOrDefault(c => c.Type == type);
            if (existing != null)
            {
                existing.IsEnabled = isEnabled;
            }
            else
            {
                NotificationCategories.Add(NotificationCategoryItemViewModel.Create(type, isEnabled));
            }
        }

        RebuildHubCompetitions(prefs);
    }

    public void RebuildHubCompetitions(UserPreferences? prefs = null)
    {
        RunOnUiSafe(() =>
        {
            prefs ??= _preferencesService.GetPreferences();
            var list = new List<HubCompetitionItemViewModel>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Followed Competitions
            if (prefs?.FollowedCompetitions != null)
            {
                foreach (var fc in prefs.FollowedCompetitions)
                {
                    if (!string.IsNullOrWhiteSpace(fc.Id))
                    {
                        var resolvedLogo = ResolveCompetitionLogoUrl(fc.Id, fc.Name, fc.LogoUrl);
                        var resolvedName = ResolveCompetitionFriendlyName(fc.Id, fc.Name);

                        if (seenIds.Add(fc.Id) && seenNames.Add(resolvedName))
                        {
                            list.Add(new HubCompetitionItemViewModel
                            {
                                Id = fc.Id,
                                Name = resolvedName,
                                Country = fc.Country,
                                LogoUrl = resolvedLogo,
                                IsFollowed = true,
                            });
                        }
                    }
                }
            }

            // 2. Followed-Team Competitions (from backend Tray payload across all dates)
            foreach (var ftc in _followedTeamCompetitions)
            {
                if (!string.IsNullOrWhiteSpace(ftc.Id))
                {
                    var resolvedLogo = ResolveCompetitionLogoUrl(ftc.Id, ftc.Name, ftc.LogoUrl);
                    var resolvedName = ResolveCompetitionFriendlyName(ftc.Id, ftc.Name);

                    if (seenIds.Add(ftc.Id) && seenNames.Add(resolvedName))
                    {
                        list.Add(new HubCompetitionItemViewModel
                        {
                            Id = ftc.Id,
                            Name = resolvedName,
                            Country = ftc.Country,
                            LogoUrl = resolvedLogo,
                            IsFollowed = true,
                        });
                    }
                }
            }

            // 3. The 6 Default Leagues (always ensure top leagues are available with crisp FotMob logos)
            var defaultTopLeagues = new[]
            {
                new { Id = "87", Name = "LaLiga", Country = "Spain", LogoUrl = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/87.png" },
                new { Id = "47", Name = "Premier League", Country = "England", LogoUrl = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/47.png" },
                new { Id = "42", Name = "UEFA Champions League", Country = "Europe", LogoUrl = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/42.png" },
                new { Id = "55", Name = "Serie A", Country = "Italy", LogoUrl = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/55.png" },
                new { Id = "54", Name = "Bundesliga", Country = "Germany", LogoUrl = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/54.png" },
                new { Id = "53", Name = "Ligue 1", Country = "France", LogoUrl = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/53.png" },
            };

            foreach (var dl in defaultTopLeagues)
            {
                if (seenIds.Add(dl.Id) && seenNames.Add(dl.Name))
                {
                    list.Add(new HubCompetitionItemViewModel
                    {
                        Id = dl.Id,
                        Name = dl.Name,
                        Country = dl.Country,
                        LogoUrl = dl.LogoUrl,
                        IsFollowed = false,
                    });
                }
            }

            var currentSelectedId = _selectedHubCompetitionId ?? _selectedHubCompetition?.Id;
            HubCompetitions.Clear();
            foreach (var item in list)
            {
                HubCompetitions.Add(item);
            }

            if (_isTransientJumpActive)
            {
                var match = !string.IsNullOrEmpty(_selectedHubCompetitionId)
                    ? FindMatchingHubCompetition(_selectedHubCompetitionId, _currentCompetitionName, _currentCompetitionLogoUrl)
                    : null;
                if (match != null)
                {
                    _isTransientJumpActive = false;
                    _selectedHubCompetitionId = match.Id;
                    SelectedHubCompetition = match;
                }
                else
                {
                    _selectedHubCompetition = null;
                    foreach (var c in HubCompetitions)
                    {
                        c.IsSelected = false;
                    }
                }
            }
            else if (_selectedHubCompetition != null && !string.IsNullOrEmpty(currentSelectedId))
            {
                var match = FindMatchingHubCompetition(currentSelectedId, _selectedHubCompetition.Name, _selectedHubCompetition.LogoUrl);
                if (match != null)
                {
                    _selectedHubCompetitionId = match.Id;
                    SelectedHubCompetition = match;
                }
                else
                {
                    SelectedHubCompetition = null;
                    _selectedHubCompetitionId = null;
                    foreach (var c in HubCompetitions)
                    {
                        c.IsSelected = false;
                    }
                }
            }
            else
            {
                foreach (var c in HubCompetitions)
                {
                    c.IsSelected = false;
                }
            }
        }, nameof(RebuildHubCompetitions));
    }

    public static string? ExtractFotmobLeagueIdFromLogoUrl(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(logoUrl, @"/leaguelogo/(?:dark|light)/(\d+)\.png", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        var genericMatch = System.Text.RegularExpressions.Regex.Match(logoUrl, @"/(\d+)\.png", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (genericMatch.Success) return genericMatch.Groups[1].Value;
        return null;
    }

    private HubCompetitionItemViewModel? FindMatchingHubCompetition(string? id, string? name, string? logoUrl)
    {
        if (HubCompetitions.Count == 0) return null;

        var numericFromLogo = ExtractFotmobLeagueIdFromLogoUrl(logoUrl);
        var numericFromId = ExtractNumericDigits(id);

        return HubCompetitions.FirstOrDefault(c =>
        {
            if (!string.IsNullOrWhiteSpace(id) && c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(name))
            {
                var normTarget = NormalizeCompetitionSearchKey(name);
                var normChip = NormalizeCompetitionSearchKey(c.Name);
                if (normTarget.Equals(normChip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var chipNumericLogo = ExtractFotmobLeagueIdFromLogoUrl(c.LogoUrl);
            var chipNumericId = ExtractNumericDigits(c.Id);

            if (!string.IsNullOrEmpty(numericFromLogo))
            {
                if (numericFromLogo == chipNumericId || numericFromLogo == chipNumericLogo)
                    return true;
            }
            if (!string.IsNullOrEmpty(numericFromId) && int.TryParse(numericFromId, out int targetNum))
            {
                if ((int.TryParse(chipNumericId, out int cNum) && targetNum == cNum) ||
                    (int.TryParse(chipNumericLogo, out int cLogoNum) && targetNum == cLogoNum))
                    return true;
            }

            return false;
        });
    }

    public static string NormalizeCompetitionSearchKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    public static string ExtractNumericDigits(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(id, @"\d+");
        return match.Success ? match.Value : string.Empty;
    }

    public static string? ResolveCompetitionLogoUrl(string id, string? name, string? existingLogoUrl)
    {
        if (!string.IsNullOrWhiteSpace(existingLogoUrl) &&
            !existingLogoUrl.Contains("placeholder", StringComparison.OrdinalIgnoreCase) &&
            !existingLogoUrl.EndsWith("/comp.png", StringComparison.OrdinalIgnoreCase))
        {
            return existingLogoUrl;
        }

        var digits = ExtractNumericDigits(id);
        if (!string.IsNullOrEmpty(digits))
        {
            return $"https://images.fotmob.com/image_resources/logo/leaguelogo/dark/{digits}.png";
        }

        int? fotmobId = ResolveFotmobLeagueId(id, name);
        if (fotmobId.HasValue && fotmobId.Value > 0)
        {
            return $"https://images.fotmob.com/image_resources/logo/leaguelogo/dark/{fotmobId.Value}.png";
        }

        return existingLogoUrl;
    }

    public string ResolveCompetitionFriendlyName(string id, string? name)
    {
        var scheduleGroup = CompetitionGroups.FirstOrDefault(g =>
            string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g.CompetitionId, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g.CompetitionKey, id, StringComparison.OrdinalIgnoreCase) ||
            (int.TryParse(ExtractNumericDigits(id), out int idNum) && int.TryParse(ExtractNumericDigits(g.CompetitionId), out int gNum) && idNum == gNum));

        if (scheduleGroup != null && !string.IsNullOrWhiteSpace(scheduleGroup.Name))
        {
            return scheduleGroup.Name;
        }

        return ResolveCompetitionName(id, name);
    }

    public static string ResolveCompetitionName(string id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) &&
            !name.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("fotmob_", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("comp_", StringComparison.OrdinalIgnoreCase) &&
            !int.TryParse(name, out _))
        {
            return name;
        }

        var lower = (id ?? string.Empty).ToLowerInvariant();
        var digits = ExtractNumericDigits(id);

        if (digits == "10216" || lower.Contains("conference") || lower.Contains("uecl")) return "UEFA Conference League";
        if (digits == "73" || lower.Contains("europa") || lower.Contains("uel")) return "UEFA Europa League";
        if (digits == "47" || lower.Contains("premier_league") || lower.Contains("pl") || lower == CanonicalEntityIds.PremierLeague.ToLowerInvariant()) return "Premier League";
        if (digits == "87" || lower.Contains("laliga") || lower.Contains("la_liga") || lower == CanonicalEntityIds.LaLiga.ToLowerInvariant()) return "LaLiga";
        if (digits == "42" || lower.Contains("champions_league") || lower.Contains("ucl") || lower == CanonicalEntityIds.ChampionsLeague.ToLowerInvariant()) return "UEFA Champions League";
        if (digits == "55" || lower.Contains("serie_a") || lower.Contains("serie a")) return "Serie A";
        if (digits == "54" || lower.Contains("bundesliga")) return "Bundesliga";
        if (digits == "53" || lower.Contains("ligue_1") || lower.Contains("ligue 1")) return "Ligue 1";
        if (digits == "525" || lower.Contains("egypt") || lower == CanonicalEntityIds.EgyptianPremierLeague.ToLowerInvariant()) return "Egyptian Premier League";
        if (digits == "536" || lower.Contains("saudi") || lower.Contains("spl")) return "Saudi Pro League";
        if (digits == "130" || lower.Contains("mls")) return "Major League Soccer";
        if (digits == "57" || lower.Contains("eredivisie")) return "Eredivisie";
        if (digits == "61" || lower.Contains("primeira") || lower.Contains("portugal")) return "Liga Portugal";
        if (digits == "48" || lower.Contains("championship")) return "Championship";
        if (digits == "77" || lower.Contains("world_cup") || lower.Contains("world cup")) return "World Cup";
        if (digits == "132" || lower.Contains("afcon") || lower.Contains("africa_cup")) return "Africa Cup of Nations";

        return !string.IsNullOrWhiteSpace(name) ? name : "Competition";
    }

    public static int? ResolveFotmobLeagueId(string? id, string? name, string? logoUrl = null)
    {
        var logoDigits = ExtractFotmobLeagueIdFromLogoUrl(logoUrl);
        if (int.TryParse(logoDigits, out int logoNum) && logoNum > 0)
        {
            return logoNum;
        }

        var digits = ExtractNumericDigits(id);
        if (int.TryParse(digits, out int numeric) && numeric > 0)
        {
            return numeric;
        }

        var combined = $"{id} {name}".ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(id))
        {
            var idLower = id.ToLowerInvariant();
            if (idLower == CanonicalEntityIds.PremierLeague.ToLowerInvariant()) return 47;
            if (idLower == CanonicalEntityIds.LaLiga.ToLowerInvariant()) return 87;
            if (idLower == CanonicalEntityIds.EgyptianPremierLeague.ToLowerInvariant()) return 525;
            if (idLower == CanonicalEntityIds.ChampionsLeague.ToLowerInvariant()) return 42;
            if (idLower == CanonicalEntityIds.AfricaCupOfNations.ToLowerInvariant()) return 132;
            if (idLower == CanonicalEntityIds.WorldCup.ToLowerInvariant()) return 77;
        }

        if (combined.Contains("conference") || combined.Contains("uecl")) return 10216;
        if (combined.Contains("europa") || combined.Contains("uel")) return 73;
        if (combined.Contains("premier") && combined.Contains("league") && !combined.Contains("egy")) return 47;
        if (combined.Contains("laliga") || combined.Contains("la liga") || combined.Contains("la_liga")) return 87;
        if (combined.Contains("champions") || combined.Contains("ucl")) return 42;
        if (combined.Contains("serie a") || combined.Contains("serie_a")) return 55;
        if (combined.Contains("bundesliga")) return 54;
        if (combined.Contains("ligue 1") || combined.Contains("ligue_1")) return 53;
        if (combined.Contains("egypt")) return 525;
        if (combined.Contains("saudi") || combined.Contains("spl")) return 536;
        if (combined.Contains("mls") || combined.Contains("major league")) return 130;
        if (combined.Contains("eredivisie")) return 57;
        if (combined.Contains("primeira") || combined.Contains("portugal")) return 61;
        if (combined.Contains("championship")) return 48;
        if (combined.Contains("world cup")) return 77;
        if (combined.Contains("afcon") || combined.Contains("africa cup")) return 132;

        return null;
    }

    private CancellationTokenSource? _hubLoadingCts;

    public async Task LoadCompetitionHubAsync(string competitionId, string? targetName = null, string? targetLogoUrl = null)
    {
        if (string.IsNullOrWhiteSpace(competitionId)) return;

        _hubLoadingCts?.Cancel();
        _hubLoadingCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _hubLoadingCts = cts;

        RunOnUiSafe(() =>
        {
            IsLoadingHub = true;
            HubErrorMessage = null;
        }, nameof(LoadCompetitionHubAsync));

        try
        {
            // Extract numeric ID from logoUrl if available (e.g. /leaguelogo/dark/73.png -> 73) or resolve from name/id
            string effectiveId = competitionId;
            var logoDigits = ExtractFotmobLeagueIdFromLogoUrl(targetLogoUrl);
            if (!string.IsNullOrEmpty(logoDigits))
            {
                effectiveId = logoDigits;
            }
            else
            {
                var resolvedId = ResolveFotmobLeagueId(competitionId, targetName, targetLogoUrl);
                if (resolvedId.HasValue && resolvedId.Value > 0)
                {
                    effectiveId = resolvedId.Value.ToString();
                }
                else
                {
                    var idDigits = ExtractNumericDigits(competitionId);
                    if (!string.IsNullOrEmpty(idDigits))
                    {
                        effectiveId = idDigits;
                    }
                }
            }

            var res = await _apiClient.GetCompetitionHubAsync(effectiveId, targetName, targetLogoUrl, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (res.IsSuccess && res.Envelope?.Data != null)
            {
                var hub = res.Envelope.Data;
                RunOnUiSafe(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    var resolvedName = !string.IsNullOrWhiteSpace(targetName) && !targetName.StartsWith("fotmob_", StringComparison.OrdinalIgnoreCase) && !targetName.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase) && !targetName.StartsWith("comp_", StringComparison.OrdinalIgnoreCase)
                        ? targetName
                        : (!string.IsNullOrWhiteSpace(hub.CompetitionName) && hub.CompetitionName != "Competition" ? hub.CompetitionName : ResolveCompetitionFriendlyName(hub.CompetitionId, hub.CompetitionName));

                    var resolvedLogo = ResolveCompetitionLogoUrl(hub.CompetitionId, resolvedName, !string.IsNullOrWhiteSpace(targetLogoUrl) ? targetLogoUrl : hub.CompetitionLogoUrl);

                    CurrentCompetitionName = resolvedName;
                    CurrentCompetitionLogoUrl = resolvedLogo;
                    OnPropertyChanged(nameof(CurrentCompetitionName));
                    OnPropertyChanged(nameof(CurrentCompetitionLogoUrl));
                    CurrentSeasonDisplay = !string.IsNullOrEmpty(hub.Season) ? $"Season {hub.Season}" : "";

                    CurrentStandings.Clear();
                    var prefs = _preferencesService.GetPreferences();
                    var followedTeamIds = prefs?.GetFollowedTeamIdSet() ?? new HashSet<string>();
                    var followedNames = prefs?.FollowedTeams?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var row in hub.Standings)
                    {
                        bool isFollowed = followedTeamIds.Contains(row.TeamId) || followedNames.Contains(row.TeamName) || (!string.IsNullOrEmpty(row.TeamShortName) && followedNames.Contains(row.TeamShortName));
                        CurrentStandings.Add(new StandingRowItemViewModel(row, isFollowed));
                    }

                    CurrentTopScorers.Clear();
                    foreach (var s in hub.TopScorers)
                    {
                        CurrentTopScorers.Add(new PlayerStatLeaderItemViewModel(s, isAssistLeader: false));
                    }

                    CurrentTopAssists.Clear();
                    foreach (var a in hub.TopAssists)
                    {
                        CurrentTopAssists.Add(new PlayerStatLeaderItemViewModel(a, isAssistLeader: true));
                    }

                    OnPropertyChanged(nameof(HasStandings));
                    OnPropertyChanged(nameof(HasTopScorers));
                    OnPropertyChanged(nameof(HasTopAssists));
                }, nameof(LoadCompetitionHubAsync));
            }
            else
            {
                if (cts.IsCancellationRequested) return;
                var errMsg = res.ErrorMessage ?? "Failed to load competition data.";
                Diagnostics.AppDiagnosticsLogger.LogError("MainViewModel", "LoadCompetitionHubFailed", $"Failed to load competition '{competitionId}': {errMsg}");
                RunOnUiSafe(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        HubErrorMessage = errMsg;
                    }
                }, nameof(LoadCompetitionHubAsync));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation or disposal
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                Diagnostics.AppDiagnosticsLogger.LogError("MainViewModel", "LoadCompetitionHubException", $"Exception loading competition '{competitionId}': [{ex.GetType().Name}] {ex.Message}", ex);
                RunOnUiSafe(() =>
                {
                    HubErrorMessage = $"Error loading competition data: {ex.Message}";
                }, nameof(LoadCompetitionHubAsync));
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                RunOnUiSafe(() =>
                {
                    IsLoadingHub = false;
                }, nameof(LoadCompetitionHubAsync));
            }
        }
    }

    public void NavigateToCompetitionStandings(CompetitionGroupViewModel group)
    {
        if (group == null) return;

        if (HubCompetitions.Count == 0)
        {
            RebuildHubCompetitions();
        }

        var match = FindMatchingHubCompetition(group.CompetitionId, group.CompetitionName, group.LogoUrl);
        var resolvedLogo = ResolveCompetitionLogoUrl(group.CompetitionId, group.CompetitionName, group.LogoUrl);
        var resolvedName = !string.IsNullOrWhiteSpace(group.CompetitionName) && !group.CompetitionName.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase) && !group.CompetitionName.StartsWith("fotmob_", StringComparison.OrdinalIgnoreCase)
            ? group.CompetitionName
            : ResolveCompetitionFriendlyName(group.CompetitionId, group.CompetitionName);

        if (match != null)
        {
            _isTransientJumpActive = false;
            SelectedHubCompetition = match;
            CurrentCompetitionName = match.Name;
            CurrentCompetitionLogoUrl = match.LogoUrl;
            _isMatchesMode = false;
            OnPropertyChanged(nameof(IsMatchesMode));
            OnPropertyChanged(nameof(IsStandingsMode));
            OnPropertyChanged(nameof(MatchesTabBackgroundHex));
            OnPropertyChanged(nameof(MatchesTabForegroundHex));
            OnPropertyChanged(nameof(StandingsTabBackgroundHex));
            OnPropertyChanged(nameof(StandingsTabForegroundHex));
            _ = LoadCompetitionHubAsync(match.Id, match.Name, match.LogoUrl);
        }
        else
        {
            // Case B: No match in HubCompetitions — Transient Click-to-Jump League
            _isTransientJumpActive = true;
            _selectedHubCompetitionId = group.CompetitionId;
            _selectedHubCompetition = null;
            OnPropertyChanged(nameof(SelectedHubCompetition));

            foreach (var c in HubCompetitions)
            {
                c.IsSelected = false;
            }

            CurrentCompetitionName = resolvedName;
            CurrentCompetitionLogoUrl = resolvedLogo;

            _isMatchesMode = false;
            OnPropertyChanged(nameof(IsMatchesMode));
            OnPropertyChanged(nameof(IsStandingsMode));
            OnPropertyChanged(nameof(MatchesTabBackgroundHex));
            OnPropertyChanged(nameof(MatchesTabForegroundHex));
            OnPropertyChanged(nameof(StandingsTabBackgroundHex));
            OnPropertyChanged(nameof(StandingsTabForegroundHex));

            _ = LoadCompetitionHubAsync(group.CompetitionId, resolvedName, resolvedLogo);
        }
    }

    public void NavigateToCompetitionStandings(string competitionId, string? competitionName = null, string? logoUrl = null, string? country = null)
    {
        if (string.IsNullOrWhiteSpace(competitionId)) return;

        if (HubCompetitions.Count == 0)
        {
            RebuildHubCompetitions();
        }

        var match = FindMatchingHubCompetition(competitionId, competitionName, logoUrl);
        var resolvedLogo = ResolveCompetitionLogoUrl(competitionId, competitionName, logoUrl);
        var resolvedName = !string.IsNullOrWhiteSpace(competitionName) && !competitionName.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase) && !competitionName.StartsWith("fotmob_", StringComparison.OrdinalIgnoreCase)
            ? competitionName
            : ResolveCompetitionFriendlyName(competitionId, competitionName);

        if (match != null)
        {
            _isTransientJumpActive = false;
            SelectedHubCompetition = match;
            CurrentCompetitionName = match.Name;
            CurrentCompetitionLogoUrl = match.LogoUrl;
            _isMatchesMode = false;
            OnPropertyChanged(nameof(IsMatchesMode));
            OnPropertyChanged(nameof(IsStandingsMode));
            OnPropertyChanged(nameof(MatchesTabBackgroundHex));
            OnPropertyChanged(nameof(MatchesTabForegroundHex));
            OnPropertyChanged(nameof(StandingsTabBackgroundHex));
            OnPropertyChanged(nameof(StandingsTabForegroundHex));
            _ = LoadCompetitionHubAsync(match.Id, match.Name, match.LogoUrl);
        }
        else
        {
            // Case B: No match in HubCompetitions — Transient Click-to-Jump League
            _isTransientJumpActive = true;
            _selectedHubCompetitionId = competitionId;
            _selectedHubCompetition = null;
            OnPropertyChanged(nameof(SelectedHubCompetition));

            foreach (var c in HubCompetitions)
            {
                c.IsSelected = false;
            }

            CurrentCompetitionName = resolvedName;
            CurrentCompetitionLogoUrl = resolvedLogo;

            _isMatchesMode = false;
            OnPropertyChanged(nameof(IsMatchesMode));
            OnPropertyChanged(nameof(IsStandingsMode));
            OnPropertyChanged(nameof(MatchesTabBackgroundHex));
            OnPropertyChanged(nameof(MatchesTabForegroundHex));
            OnPropertyChanged(nameof(StandingsTabBackgroundHex));
            OnPropertyChanged(nameof(StandingsTabForegroundHex));

            _ = LoadCompetitionHubAsync(competitionId, resolvedName, resolvedLogo);
        }
    }

    public async Task ToggleTeamFollowAsync(string teamId, string? teamName = null, string? logoUrl = null)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;

        var prefs = _preferencesService.GetPreferences();
        bool isCurrentlyFollowed = prefs.GetFollowedTeamIdSet().Contains(teamId);

        try
        {
            if (isCurrentlyFollowed)
            {
                await _preferencesService.UnfollowTeamAsync(teamId);
                var existing = AvailableTeams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.IsFollowed = false;
            }
            else
            {
                var teamVm = AvailableTeams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
                var domainTeam = teamVm?.ToDomain() ?? new FollowedTeam
                {
                    Id = teamId,
                    Name = !string.IsNullOrWhiteSpace(teamName) ? teamName : teamId,
                    LogoUrl = logoUrl
                };

                await _preferencesService.FollowTeamAsync(domainTeam);
                if (teamVm != null)
                {
                    teamVm.IsFollowed = true;
                }
                else
                {
                    AvailableTeams.Add(FollowedTeamItemViewModel.FromDomain(domainTeam, true));
                }
            }

            StatusMessage = null;
            ReapplyRelevanceAndFilters();
            Settings.RefreshFollowedLists();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to update followed team: {ex.Message}";
        }
    }

    public async Task ToggleCompetitionFollowAsync(string compId, string? compName = null, string? compCode = null, string? logoUrl = null, string? country = null)
    {
        if (string.IsNullOrWhiteSpace(compId)) return;

        var prefs = _preferencesService.GetPreferences();
        bool isCurrentlyFollowed = prefs.GetFollowedCompetitionIdSet().Contains(compId);

        try
        {
            if (isCurrentlyFollowed)
            {
                await _preferencesService.UnfollowCompetitionAsync(compId);
                var existing = AvailableCompetitions.FirstOrDefault(c => c.Id.Equals(compId, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.IsFollowed = false;
            }
            else
            {
                var compVm = AvailableCompetitions.FirstOrDefault(c => c.Id.Equals(compId, StringComparison.OrdinalIgnoreCase));
                var friendlyName = !string.IsNullOrWhiteSpace(compName) && !compName.StartsWith("fotmob_", StringComparison.OrdinalIgnoreCase) && !compName.StartsWith("ft_comp_", StringComparison.OrdinalIgnoreCase) && !int.TryParse(compName, out _)
                    ? compName
                    : ResolveCompetitionFriendlyName(compId, compName);

                var resolvedLogo = ResolveCompetitionLogoUrl(compId, friendlyName, logoUrl ?? compVm?.LogoUrl);

                var domainComp = compVm?.ToDomain() ?? new FollowedCompetition
                {
                    Id = compId,
                    Name = friendlyName,
                    Code = compCode ?? "COMP",
                    LogoUrl = resolvedLogo,
                    Country = country ?? compVm?.Country
                };

                if (string.IsNullOrWhiteSpace(domainComp.LogoUrl) && !string.IsNullOrWhiteSpace(resolvedLogo))
                {
                    domainComp.LogoUrl = resolvedLogo;
                }

                await _preferencesService.FollowCompetitionAsync(domainComp);
                if (compVm != null)
                {
                    compVm.IsFollowed = true;
                    if (string.IsNullOrWhiteSpace(compVm.LogoUrl) && !string.IsNullOrWhiteSpace(resolvedLogo))
                    {
                        compVm.LogoUrl = resolvedLogo;
                    }
                }
                else
                {
                    AvailableCompetitions.Add(FollowedCompetitionItemViewModel.FromDomain(domainComp, true));
                }
            }

            StatusMessage = null;
            ReapplyRelevanceAndFilters();
            Settings.RefreshFollowedLists();

            // Issue 3: If unfollowed competition was the currently selected hub competition,
            // ensure selection resets cleanly to the first available competition and immediately loads it.
            if (isCurrentlyFollowed && !string.IsNullOrEmpty(_selectedHubCompetitionId))
            {
                bool stillPresent = HubCompetitions.Any(c =>
                    c.Id.Equals(_selectedHubCompetitionId, StringComparison.OrdinalIgnoreCase) ||
                    (int.TryParse(ExtractNumericDigits(_selectedHubCompetitionId), out int sNum) && int.TryParse(ExtractNumericDigits(c.Id), out int cNum) && sNum == cNum));

                if (!stillPresent)
                {
                    _selectedHubCompetition = null;
                    _selectedHubCompetitionId = HubCompetitions.FirstOrDefault()?.Id;
                    SelectedHubCompetition = HubCompetitions.FirstOrDefault();
                    if (_selectedHubCompetitionId != null)
                    {
                        _ = LoadCompetitionHubAsync(_selectedHubCompetitionId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to update followed competition: {ex.Message}";
        }
    }

    public async Task ToggleGlobalNotificationsAsync(bool enabled)
    {
        try
        {
            await _preferencesService.SetNotificationsGloballyEnabledAsync(enabled);
            IsGlobalNotificationsEnabled = enabled;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            IsGlobalNotificationsEnabled = !enabled; // Revert UI
            StatusMessage = $"Unable to update notification settings: {ex.Message}";
        }
    }

    public async Task ToggleNotificationCategoryAsync(NotificationType type, bool enabled)
    {
        var catVm = NotificationCategories.FirstOrDefault(c => c.Type == type);
        try
        {
            await _preferencesService.SetNotificationTypeEnabledAsync(type, enabled);
            if (catVm != null) catVm.IsEnabled = enabled;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            if (catVm != null) catVm.IsEnabled = !enabled; // Revert UI
            StatusMessage = $"Unable to update {type} alert: {ex.Message}";
        }
    }

    public NotificationSoundMode SoundMode => _preferencesService.Current.SoundMode;

    public async Task SetSoundModeAsync(NotificationSoundMode mode)
    {
        try
        {
            await _preferencesService.SetSoundModeAsync(mode);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SoundMode)));
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to update notification sound mode: {ex.Message}";
        }
    }

    public void SetFilter(MatchFilterType filter)
    {
        ActiveFilter = filter;
        if (filter == MatchFilterType.Upcoming)
        {
            _syncService?.TriggerImmediateSync();
        }
    }

    private void RunOnUiSafe(Action action, string actionName)
    {
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Diagnostics.AppLogger.Error("UI", "DispatchExecutionError", $"Failed executing UI action '{actionName}': [{ex.GetType().Name}] {ex.Message}", ex: ex);
            }
        });

        if (!enqueued)
        {
            Diagnostics.AppLogger.Warn("UI", "DispatchEnqueueFailed", $"DispatcherQueue.TryEnqueue returned false for '{actionName}'. Fallback to synchronous execution.");
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Diagnostics.AppLogger.Error("UI", "FallbackExecutionError", $"Fallback execution failed for '{actionName}': [{ex.GetType().Name}] {ex.Message}", ex: ex);
            }
        }
    }

    private DateTime _lastEvaluatedToday = DateTime.Today;

    public void CheckAndExecuteSync()
    {
        var currentToday = DateTime.Today;
        if (currentToday != _lastEvaluatedToday)
        {
            bool wasViewingToday = _selectedCalendarDate.Date == _lastEvaluatedToday.Date;
            _lastEvaluatedToday = currentToday;
            RebuildCalendarDays();

            if (wasViewingToday)
            {
                SelectedCalendarDate = currentToday;
            }
            else
            {
                ApplyCurrentFilter();
            }
        }

        if (_latestTrayData == null && _syncService?.CurrentTrayData != null)
        {
            _latestTrayData = _syncService.CurrentTrayData;
        }
    }

    private void OnTrayUpdated(object? sender, TrayUpdateEventArgs e)
    {
        RunOnUiSafe(() =>
        {
            CheckAndExecuteSync();
            IsSyncing = false;
            if (e.TrayData != null)
            {
                _latestTrayData = e.TrayData;
            }
            else if (_latestTrayData == null && _syncService?.CurrentTrayData != null)
            {
                _latestTrayData = _syncService.CurrentTrayData;
            }

            if (e.QuotaExhausted && _latestTrayData != null)
            {
                _latestTrayData.QuotaExhausted = true;
            }

            RefreshMatches(_latestTrayData, e.FreshnessState, e.ConnectionState);
        }, nameof(OnTrayUpdated));
    }

    private void OnSyncStateChanged(object? sender, bool isSyncing)
    {
        RunOnUiSafe(() =>
        {
            IsSyncing = isSyncing;
        }, nameof(OnSyncStateChanged));
    }

    private void OnPreferencesChanged(object? sender, UserPreferences prefs)
    {
        RunOnUiSafe(() =>
        {
            PopulatePreferenceCollections(prefs);
            ReapplyRelevanceAndFilters();
        }, nameof(OnPreferencesChanged));
    }

    public void RefreshMatches(TrayDataDto? trayData, DataFreshnessState freshnessState, TickerConnectionState connectionState)
    {
        CheckAndExecuteSync();

        // 1. HTTP 304 Not Modified: Short-circuit collection mutation if already populated for the SAME date (Zero Flicker!)
        if (freshnessState == DataFreshnessState.NotModified && _latestTrayData != null && _lastDateApplied == _selectedCalendarDate.Date && (FilteredMatches.Count > 0 || _latestTrayData.Matches.Count == 0))
        {
            UpdateConnectionUi(connectionState, freshnessState);
            return;
        }

        if (trayData != null)
        {
            _latestTrayData = trayData;
        }
        else if (_latestTrayData == null && _syncService?.CurrentTrayData != null)
        {
            _latestTrayData = _syncService.CurrentTrayData;
        }

        if (_latestTrayData?.FollowedCountsByDate != null)
        {
            foreach (var kvp in _latestTrayData.FollowedCountsByDate)
            {
                _followedCountsByDate[kvp.Key] = kvp.Value;
            }
        }

        if (_latestTrayData?.FollowedTeamCompetitions != null)
        {
            bool changed = _followedTeamCompetitions.Count != _latestTrayData.FollowedTeamCompetitions.Count ||
                           !_followedTeamCompetitions.Select(c => c.Id).SequenceEqual(_latestTrayData.FollowedTeamCompetitions.Select(c => c.Id));
            if (changed)
            {
                _followedTeamCompetitions.Clear();
                _followedTeamCompetitions.AddRange(_latestTrayData.FollowedTeamCompetitions);
                RebuildHubCompetitions();
            }
        }

        // Handle still-syncing responses from backend:
        // If backend response returns 0 matches for selected date while syncState == "SYNCING",
        // maintain synchronizing state and trigger a follow-up poll 2 seconds later.
        bool isSyncingResponse = string.Equals(_latestTrayData?.SyncState, "SYNCING", StringComparison.OrdinalIgnoreCase);
        int matchesOnSelectedDate = _latestTrayData?.Matches?.Count(m =>
        {
            var localDate = m.KickoffTime.ToLocalTime().Date;
            var utcDate = m.KickoffTime.UtcDateTime.Date;
            var rawDate = m.KickoffTime.Date;
            return localDate == _selectedCalendarDate.Date || utcDate == _selectedCalendarDate.Date || rawDate == _selectedCalendarDate.Date || m.KickoffTime.DateTime.Date == _selectedCalendarDate.Date;
        }) ?? 0;

        if (matchesOnSelectedDate == 0 && (isSyncingResponse || _isDateSynchronizing))
        {
            _isDateSynchronizing = true;
            IsEmpty = true;
            HasMatches = false;
            EmptyStateTitle = "Synchronizing Schedule...";
            EmptyStateMessage = $"Loading fixtures for {_selectedCalendarDate:dddd, MMM d}...";
            FilteredMatches.Clear();
            FollowedMatches.Clear();
            OtherMatches.Clear();
            CompetitionGroups.Clear();
            UpdateConnectionUi(connectionState, freshnessState);
            RebuildCalendarDays();

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSynchronizingSchedule)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncStatusText)));

            if (isSyncingResponse)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _delayFunc(TimeSpan.FromSeconds(2), _disposeCts.Token);
                        _syncService?.TriggerImmediateSync();
                    }
                    catch (OperationCanceledException) { }
                });
            }
            return;
        }

        _isDateSynchronizing = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSynchronizingSchedule)));

        // 2. Offline Resilience: Retain existing matches in memory if backend is unreachable or returning cached data
        UpdateConnectionUi(connectionState, freshnessState);

        ApplyCurrentFilter();
    }

    private void UpdateConnectionUi(TickerConnectionState connectionState, DataFreshnessState freshnessState)
    {
        _freshnessState = freshnessState;
        _connectionState = connectionState;

        switch (connectionState)
        {
            case TickerConnectionState.OnlineFresh:
                ConnectionStatusText = "Online (Live)";
                IsOffline = false;
                break;
            case TickerConnectionState.OnlineStale:
                ConnectionStatusText = "Online (Cached)";
                IsOffline = false;
                break;
            case TickerConnectionState.OfflineCached:
                ConnectionStatusText = "Offline (Cached Data)";
                IsOffline = true;
                break;
            default:
                ConnectionStatusText = freshnessState == DataFreshnessState.Fresh ? "Online" : "Offline (Cached Data)";
                IsOffline = freshnessState != DataFreshnessState.Fresh;
                break;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOffline)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncIconGlyph)));
    }

    public void TogglePinMatch(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;

        if (string.Equals(ManualPinnedMatchId, matchId, StringComparison.Ordinal))
        {
            UnpinMatch(matchId);
        }
        else
        {
            PinMatch(matchId);
        }
    }

    public void PinMatch(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;

        ManualPinnedMatchId = matchId;
        IsExplicitlyUnpinned = false;
        ActiveMatchId = matchId;

        App.CurrentTickerCoordinator?.PinMatch(matchId);

        foreach (var match in FilteredMatches)
        {
            match.IsActiveTaskbarMatch = string.Equals(match.Id, matchId, StringComparison.Ordinal);
        }

        var allMatches = _latestTrayData?.Matches;
        var pinnedMatch = allMatches?.FirstOrDefault(m => string.Equals(m.Id, matchId, StringComparison.Ordinal));
        if (pinnedMatch != null)
        {
            if (_syncService is SyncService sync) sync.NotificationService?.Engine?.IngestMatchBaseline(pinnedMatch);
            string home = pinnedMatch.HomeTeam?.ShortName ?? pinnedMatch.HomeTeam?.Name ?? "Home";
            string away = pinnedMatch.AwayTeam?.ShortName ?? pinnedMatch.AwayTeam?.Name ?? "Away";
            string stateNorm = (pinnedMatch.State ?? string.Empty).Trim().ToUpperInvariant();
            bool isPreMatch = stateNorm is "SCHEDULED" or "TIMED" or "NOT_STARTED" or "NS" or "TBD" or "PREMATCH";
            var prefs = _preferencesService?.GetPreferences();

            if (isPreMatch)
            {
                var timeFormat = (prefs?.Use24HourFormat ?? true) ? "HH:mm" : "h:mm tt";
                string time = pinnedMatch.KickoffTime != default ? pinnedMatch.KickoffTime.ToLocalTime().ToString(timeFormat) : "Upcoming";
                ActiveTickerText = $"📌 Pinned: {home} vs {away} ({time})";
            }
            else
            {
                string score = $"{pinnedMatch.Score?.Home ?? 0}–{pinnedMatch.Score?.Away ?? 0}";
                string time = pinnedMatch.State == "HT" ? "HT" : (pinnedMatch.ElapsedMinute > 0 ? $"{pinnedMatch.ElapsedMinute}'" : (pinnedMatch.State ?? "LIVE"));
                ActiveTickerText = $"📌 Pinned: {home} {score} {away} ({time})";
            }
        }
        else
        {
            ActiveTickerText = "📌 Match pinned to taskbar";
        }
    }

    public void UnpinMatch(string? matchId = null)
    {
        App.CurrentTickerCoordinator?.UnpinMatch();
        ManualPinnedMatchId = null;
        IsExplicitlyUnpinned = true;
        ActiveMatchId = null;
        var prefs = _preferencesService?.GetPreferences();
        ActiveTickerText = prefs?.EnableTickerCarousel == true
            ? "📌 Auto-Rotate Ticker (Carousel Mode)"
            : "📌 No pinned matches";

        foreach (var match in FilteredMatches)
        {
            if (string.IsNullOrEmpty(matchId) || match.Id == matchId)
            {
                match.IsActiveTaskbarMatch = false;
            }
        }
    }

    public void OnTaskbarPinChanged(string? pinnedMatchId)
    {
        ManualPinnedMatchId = pinnedMatchId;
        IsExplicitlyUnpinned = string.IsNullOrEmpty(pinnedMatchId);
        ActiveMatchId = pinnedMatchId;

        foreach (var match in FilteredMatches)
        {
            match.IsActiveTaskbarMatch = !string.IsNullOrEmpty(pinnedMatchId) && string.Equals(match.Id, pinnedMatchId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(pinnedMatchId))
        {
            var pinnedMatch = _latestTrayData?.Matches?.FirstOrDefault(m => string.Equals(m.Id, pinnedMatchId, StringComparison.Ordinal));
            if (pinnedMatch != null)
            {
                if (_syncService is SyncService sync) sync.NotificationService?.Engine?.IngestMatchBaseline(pinnedMatch);
                string home = pinnedMatch.HomeTeam?.ShortName ?? pinnedMatch.HomeTeam?.Name ?? "Home";
                string away = pinnedMatch.AwayTeam?.ShortName ?? pinnedMatch.AwayTeam?.Name ?? "Away";
                string stateNorm = (pinnedMatch.State ?? string.Empty).Trim().ToUpperInvariant();
                bool isPreMatch = stateNorm is "SCHEDULED" or "TIMED" or "NOT_STARTED" or "NS" or "TBD" or "PREMATCH";
                var prefs = _preferencesService?.GetPreferences();

                if (isPreMatch)
                {
                    var timeFormat = (prefs?.Use24HourFormat ?? true) ? "HH:mm" : "h:mm tt";
                    string time = pinnedMatch.KickoffTime != default ? pinnedMatch.KickoffTime.ToLocalTime().ToString(timeFormat) : "Upcoming";
                    ActiveTickerText = $"📌 Pinned: {home} vs {away} ({time})";
                }
                else
                {
                    string score = $"{pinnedMatch.Score?.Home ?? 0}–{pinnedMatch.Score?.Away ?? 0}";
                    string time = pinnedMatch.State == "HT" ? "HT" : (pinnedMatch.ElapsedMinute > 0 ? $"{pinnedMatch.ElapsedMinute}'" : (pinnedMatch.State ?? "LIVE"));
                    ActiveTickerText = $"📌 Pinned: {home} {score} {away} ({time})";
                }
            }
        }
        else
        {
            var prefs = _preferencesService.GetPreferences();
            ActiveTickerText = prefs?.EnableTickerCarousel == true ? "📌 Auto-Rotate Ticker (Carousel Mode)" : "📌 No pinned matches";
        }
    }

    private void ReapplyRelevanceAndFilters()
    {
        ApplyCurrentFilter();
    }

    private static bool IsMatchFollowed(TrayMatchDto match, UserPreferences? prefs)
    {
        if (match == null || prefs == null) return false;

        var followedTeamIds = prefs.GetFollowedTeamIdSet();
        var followedCompIds = prefs.GetFollowedCompetitionIdSet();

        // 1. Team follow check
        if (!string.IsNullOrEmpty(match.HomeTeam?.Id) && followedTeamIds.Contains(match.HomeTeam.Id)) return true;
        if (!string.IsNullOrEmpty(match.HomeTeam?.LegacyId) && followedTeamIds.Contains(match.HomeTeam.LegacyId)) return true;
        if (!string.IsNullOrEmpty(match.HomeTeam?.Name) && followedTeamIds.Contains(match.HomeTeam.Name)) return true;
        if (CanonicalEntityIds.TryTranslateLegacyId(match.HomeTeam?.Id, out var hCanon) && followedTeamIds.Contains(hCanon)) return true;
        if (CanonicalEntityIds.TryTranslateLegacyId(match.HomeTeam?.LegacyId, out var hLegCanon) && followedTeamIds.Contains(hLegCanon)) return true;
        if (prefs.FollowedTeams != null && prefs.FollowedTeams.Any(t => string.Equals(t.Name?.Trim(), match.HomeTeam?.Name?.Trim(), StringComparison.OrdinalIgnoreCase))) return true;

        if (!string.IsNullOrEmpty(match.AwayTeam?.Id) && followedTeamIds.Contains(match.AwayTeam.Id)) return true;
        if (!string.IsNullOrEmpty(match.AwayTeam?.LegacyId) && followedTeamIds.Contains(match.AwayTeam.LegacyId)) return true;
        if (!string.IsNullOrEmpty(match.AwayTeam?.Name) && followedTeamIds.Contains(match.AwayTeam.Name)) return true;
        if (CanonicalEntityIds.TryTranslateLegacyId(match.AwayTeam?.Id, out var aCanon) && followedTeamIds.Contains(aCanon)) return true;
        if (CanonicalEntityIds.TryTranslateLegacyId(match.AwayTeam?.LegacyId, out var aLegCanon) && followedTeamIds.Contains(aLegCanon)) return true;
        if (prefs.FollowedTeams != null && prefs.FollowedTeams.Any(t => string.Equals(t.Name?.Trim(), match.AwayTeam?.Name?.Trim(), StringComparison.OrdinalIgnoreCase))) return true;

        // 2. Competition follow check
        if (!string.IsNullOrEmpty(match.Competition?.Id) && followedCompIds.Contains(match.Competition.Id)) return true;
        if (!string.IsNullOrEmpty(match.Competition?.LegacyId) && followedCompIds.Contains(match.Competition.LegacyId)) return true;
        if (!string.IsNullOrWhiteSpace(match.Competition?.Name) && !string.IsNullOrWhiteSpace(match.Competition?.Country) && followedCompIds.Contains($"name_country:{match.Competition.Name.Trim()}|{match.Competition.Country.Trim()}")) return true;
        if (CanonicalEntityIds.TryTranslateLegacyId(match.Competition?.Id, out var cCanon) && followedCompIds.Contains(cCanon)) return true;
        if (CanonicalEntityIds.TryTranslateLegacyId(match.Competition?.LegacyId, out var cLegCanon) && followedCompIds.Contains(cLegCanon)) return true;
        if (prefs.FollowedCompetitions != null && prefs.FollowedCompetitions.Any(c =>
            string.Equals(c.Name?.Trim(), match.Competition?.Name?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(c.Country) || string.IsNullOrEmpty(match.Competition?.Country) || string.Equals(c.Country?.Trim(), match.Competition?.Country?.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(match.Competition?.Id) || string.Equals(c.Id, match.Competition.Id, StringComparison.OrdinalIgnoreCase)))) return true;

        var relevance = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);
        return relevance.FollowedByTeam || relevance.FollowedByCompetition;
    }

    public void RebuildCalendarDays()
    {
        var today = DateTime.Today;
        var allMatches = _latestTrayData?.Matches ?? (IReadOnlyList<TrayMatchDto>)Array.Empty<TrayMatchDto>();
        var prefs = _preferencesService.GetPreferences();

        var existingMap = CalendarDays.ToDictionary(d => d.Date.Date);

        var targetDates = new List<(DateTime Date, bool IsToday, bool IsSelected, int MatchCount)>();
        for (int offset = -1; offset <= 5; offset++)
        {
            var targetDate = today.AddDays(offset).Date;
            bool isToday = offset == 0;
            bool isSelected = targetDate == _selectedCalendarDate.Date;
            string dateKey = targetDate.ToString("yyyy-MM-dd");

            int matchCount = 0;

            // Followed matches in current in-memory payload matching target date
            int liveFollowedInPayload = allMatches.Count(m =>
            {
                var localDate = m.KickoffTime.ToLocalTime().Date;
                var utcDate = m.KickoffTime.UtcDateTime.Date;
                var rawDate = m.KickoffTime.Date;
                bool dateMatches = localDate == targetDate || utcDate == targetDate || rawDate == targetDate || m.KickoffTime.DateTime.Date == targetDate;
                if (!dateMatches && !(isToday && (m.KickoffTime == default || m.KickoffTime.Year < 2000 || FixtureRelevanceEngine.IsLive(m))))
                {
                    return false;
                }
                return IsMatchFollowed(m, prefs);
            });

            if (targetDate == _selectedCalendarDate.Date)
            {
                // Authoritative live calculation for currently selected calendar date
                matchCount = liveFollowedInPayload;
                _followedCountsByDate[dateKey] = liveFollowedInPayload;
            }
            else
            {
                if (liveFollowedInPayload > 0)
                {
                    matchCount = liveFollowedInPayload;
                    _followedCountsByDate[dateKey] = liveFollowedInPayload;
                }
                else if (_followedCountsByDate.TryGetValue(dateKey, out var cachedFollowedCount))
                {
                    matchCount = cachedFollowedCount;
                }
                else if (existingMap.TryGetValue(targetDate, out var prevDay) && prevDay.MatchCount > 0)
                {
                    matchCount = prevDay.MatchCount;
                }
            }

            targetDates.Add((targetDate, isToday, isSelected, matchCount));
        }

        // Fast path: if CalendarDays already matches target 7 days, update in place without .Clear()
        if (CalendarDays.Count == targetDates.Count && CalendarDays.Select(d => d.Date.Date).SequenceEqual(targetDates.Select(t => t.Date)))
        {
            for (int i = 0; i < CalendarDays.Count; i++)
            {
                var dayVm = CalendarDays[i];
                var target = targetDates[i];
                dayVm.IsSelected = target.IsSelected;
                dayVm.MatchCount = target.MatchCount;
            }
            return;
        }

        // Slow path: cold boot or date boundary shifted past midnight
        if (CalendarDays.Count == 0)
        {
            foreach (var t in targetDates)
            {
                CalendarDays.Add(new CalendarDayItemViewModel(t.Date, t.IsToday, t.IsSelected, t.MatchCount));
            }
        }
        else
        {
            for (int i = 0; i < targetDates.Count; i++)
            {
                var t = targetDates[i];
                if (i < CalendarDays.Count)
                {
                    if (CalendarDays[i].Date.Date == t.Date)
                    {
                        CalendarDays[i].IsSelected = t.IsSelected;
                        CalendarDays[i].MatchCount = t.MatchCount;
                    }
                    else
                    {
                        CalendarDays[i] = new CalendarDayItemViewModel(t.Date, t.IsToday, t.IsSelected, t.MatchCount);
                    }
                }
                else
                {
                    CalendarDays.Add(new CalendarDayItemViewModel(t.Date, t.IsToday, t.IsSelected, t.MatchCount));
                }
            }

            while (CalendarDays.Count > targetDates.Count)
            {
                CalendarDays.RemoveAt(CalendarDays.Count - 1);
            }
        }
    }

    private void UpdateCalendarSelection()
    {
        foreach (var day in CalendarDays)
        {
            day.IsSelected = day.Date.Date == _selectedCalendarDate.Date;
        }
    }

    public readonly record struct CompetitionSortScore(
        int Tier,
        int SubRank,
        string CountrySort,
        string NameSort
    );

    private static (string Iso2, string Iso3, string EnglishName) GetUserRegion()
    {
        try
        {
            var reg = RegionInfo.CurrentRegion;
            return (
                reg.TwoLetterISORegionName?.ToUpperInvariant() ?? "US",
                reg.ThreeLetterISORegionName?.ToUpperInvariant() ?? "USA",
                reg.EnglishName ?? "United States"
            );
        }
        catch
        {
            return ("US", "USA", "United States");
        }
    }

    private static CompetitionSortScore GetCompetitionSortScore(
        bool isFollowed,
        string? compId,
        string? compName,
        string? country,
        string? compCode)
    {
        string cName = compName?.Trim() ?? string.Empty;
        string cCountry = country?.Trim() ?? string.Empty;
        string cCode = compCode?.Trim().ToUpperInvariant() ?? string.Empty;
        string cId = compId?.Trim() ?? string.Empty;

        var userRegion = GetUserRegion();

        // Tier 0 — User Followed
        if (isFollowed)
        {
            int subRank = GetPopularitySubRank(cId, cName, cCountry, cCode, userRegion);
            return new CompetitionSortScore(0, subRank, cCountry, cName);
        }

        // Tier 1 — User's Local Region & Continental Competitions
        if (TryGetTier1Rank(cId, cName, cCountry, cCode, userRegion, out int tier1SubRank))
        {
            return new CompetitionSortScore(1, tier1SubRank, cCountry, cName);
        }

        // Tier 2 — Global Elite Popularity Ranking
        if (TryGetTier2Rank(cId, cName, cCountry, cCode, out int tier2SubRank))
        {
            return new CompetitionSortScore(2, tier2SubRank, cCountry, cName);
        }

        // Tier 3 — Remaining Competitions (sorted alphabetically by Country Code/Name and Competition Name)
        string countrySortKey = !string.IsNullOrWhiteSpace(cCountry) ? cCountry : (!string.IsNullOrWhiteSpace(cCode) ? cCode : "ZZ");
        return new CompetitionSortScore(3, 0, countrySortKey, cName);
    }

    private static bool TryGetTier1Rank(
        string compId,
        string compName,
        string country,
        string compCode,
        (string Iso2, string Iso3, string EnglishName) userRegion,
        out int subRank)
    {
        subRank = 100;
        string u2 = userRegion.Iso2;
        string u3 = userRegion.Iso3;
        string uName = userRegion.EnglishName;

        // 1. Domestic League & Cup Match
        if (IsUserDomestic(compId, compName, country, compCode, u2, u3, uName))
        {
            if (IsCup(compName, compCode))
            {
                subRank = 2;
            }
            else if (IsSecondDivision(compName, compCode))
            {
                subRank = 3;
            }
            else
            {
                subRank = 1;
            }
            return true;
        }

        // 2. Continental Competitions for User's Continent/Confederation
        if (TryGetContinentalRankForRegion(compId, compName, country, compCode, u2, out subRank))
        {
            return true;
        }

        return false;
    }

    private static bool IsUserDomestic(
        string compId,
        string compName,
        string country,
        string compCode,
        string u2,
        string u3,
        string uName)
    {
        // Egypt / EGY
        if (u2 == "EG" || u3 == "EGY")
        {
            if (country.Contains("Egypt", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Egypt", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Egyptian", StringComparison.OrdinalIgnoreCase) ||
                compCode == "EG" ||
                compId is "233" or "234" or "546" or "comp_233" ||
                (CanonicalEntityIds.TryTranslateLegacyId(compId, out var canon) && canon == CanonicalEntityIds.EgyptianPremierLeague))
            {
                return true;
            }
        }
        // Great Britain / England / Scotland
        else if (u2 == "GB" || u3 == "GBR" || u2 == "UK")
        {
            if (country.Contains("England", StringComparison.OrdinalIgnoreCase) ||
                country.Contains("Scotland", StringComparison.OrdinalIgnoreCase) ||
                country.Contains("Wales", StringComparison.OrdinalIgnoreCase) ||
                country.Contains("Northern Ireland", StringComparison.OrdinalIgnoreCase) ||
                country.Contains("United Kingdom", StringComparison.OrdinalIgnoreCase) ||
                compCode is "ENG" or "SCO" or "WAL" or "NIR" or "PL" or "CHA" or "FAC" or "EFL" ||
                (CanonicalEntityIds.TryTranslateLegacyId(compId, out var canon) && canon == CanonicalEntityIds.PremierLeague) ||
                compId is "39" or "40" or "45" or "48" or "528" or "179" or "181")
            {
                return true;
            }
            if ((compName.Contains("Premier League", StringComparison.OrdinalIgnoreCase) ||
                 compName.Contains("FA Cup", StringComparison.OrdinalIgnoreCase) ||
                 compName.Contains("EFL Cup", StringComparison.OrdinalIgnoreCase) ||
                 compName.Contains("Championship", StringComparison.OrdinalIgnoreCase)) &&
                !compName.Contains("Egyptian", StringComparison.OrdinalIgnoreCase) &&
                !compName.Contains("Russian", StringComparison.OrdinalIgnoreCase) &&
                !compName.Contains("Ukrainian", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // Spain / ESP
        else if (u2 == "ES" || u3 == "ESP")
        {
            if (country.Contains("Spain", StringComparison.OrdinalIgnoreCase) ||
                compCode is "ESP" or "ES" or "PD" or "SD" or "CDR" ||
                compName.Contains("La Liga", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("LaLiga", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Copa del Rey", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Supercopa de España", StringComparison.OrdinalIgnoreCase) ||
                compId is "140" or "141" or "143" or "556" ||
                (CanonicalEntityIds.TryTranslateLegacyId(compId, out var canon) && canon == CanonicalEntityIds.LaLiga))
            {
                return true;
            }
        }
        // Germany / DEU / GER
        else if (u2 == "DE" || u3 == "DEU" || u3 == "GER")
        {
            if (country.Contains("Germany", StringComparison.OrdinalIgnoreCase) ||
                compCode is "GER" or "DEU" or "DE" or "BL1" or "BL2" or "DFB" ||
                compName.Contains("Bundesliga", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("DFB-Pokal", StringComparison.OrdinalIgnoreCase) ||
                compId is "78" or "79" or "81" or "529")
            {
                return true;
            }
        }
        // Italy / ITA
        else if (u2 == "IT" || u3 == "ITA")
        {
            if (country.Contains("Italy", StringComparison.OrdinalIgnoreCase) ||
                compCode is "ITA" or "IT" or "SA" or "SB" or "CIT" ||
                compName.Contains("Serie A", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Serie B", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Coppa Italia", StringComparison.OrdinalIgnoreCase) ||
                compId is "135" or "136" or "137" or "547")
            {
                return true;
            }
        }
        // France / FRA
        else if (u2 == "FR" || u3 == "FRA")
        {
            if (country.Contains("France", StringComparison.OrdinalIgnoreCase) ||
                compCode is "FRA" or "FR" or "FL1" or "FL2" or "CDF" ||
                compName.Contains("Ligue 1", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Ligue 2", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("Coupe de France", StringComparison.OrdinalIgnoreCase) ||
                compId is "61" or "62" or "66" or "526")
            {
                return true;
            }
        }
        // Saudi Arabia / SAU / KSA
        else if (u2 == "SA" || u3 == "SAU" || u3 == "KSA")
        {
            if (country.Contains("Saudi", StringComparison.OrdinalIgnoreCase) ||
                compCode is "KSA" or "SAU" or "SA" or "SPL" ||
                compName.Contains("Saudi", StringComparison.OrdinalIgnoreCase) ||
                compId is "307" or "308" or "541")
            {
                return true;
            }
        }
        // United States / USA
        else if (u2 == "US" || u3 == "USA")
        {
            if (country.Contains("USA", StringComparison.OrdinalIgnoreCase) ||
                country.Contains("United States", StringComparison.OrdinalIgnoreCase) ||
                compCode is "USA" or "US" or "MLS" ||
                compName.Contains("Major League Soccer", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("MLS", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("US Open Cup", StringComparison.OrdinalIgnoreCase) ||
                compId is "253" or "257")
            {
                return true;
            }
        }
        else
        {
            // Generic domestic match for any country
            if (!string.IsNullOrWhiteSpace(country) &&
                (country.Equals(uName, StringComparison.OrdinalIgnoreCase) ||
                 country.Equals(u2, StringComparison.OrdinalIgnoreCase) ||
                 country.Equals(u3, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(compCode) &&
                (compCode.Equals(u2, StringComparison.OrdinalIgnoreCase) ||
                 compCode.Equals(u3, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetContinentalRankForRegion(
        string compId,
        string compName,
        string country,
        string compCode,
        string u2,
        out int subRank)
    {
        subRank = 100;

        // Africa / MENA
        if (IsAfricaOrMena(u2))
        {
            if (compName.Contains("CAF Champions League", StringComparison.OrdinalIgnoreCase) || compCode == "CAFCL" || compId == "12")
            {
                subRank = 10; return true;
            }
            if (compName.Contains("CAF Confederation", StringComparison.OrdinalIgnoreCase) || compCode == "CAFCC" || compId == "20" ||
                compName.Contains("CAF Super Cup", StringComparison.OrdinalIgnoreCase) || compId == "412")
            {
                subRank = 11; return true;
            }
            if (compName.Contains("Africa Cup of Nations", StringComparison.OrdinalIgnoreCase) || compName.Contains("AFCON", StringComparison.OrdinalIgnoreCase) ||
                compCode == "AFCON" || compId == "6" || (CanonicalEntityIds.TryTranslateLegacyId(compId, out var c6) && c6 == CanonicalEntityIds.AfricaCupOfNations))
            {
                subRank = 12; return true;
            }
            if (compName.Contains("Arab Cup", StringComparison.OrdinalIgnoreCase) || compName.Contains("World Cup", StringComparison.OrdinalIgnoreCase) ||
                compId == "1" || (CanonicalEntityIds.TryTranslateLegacyId(compId, out var c1) && c1 == CanonicalEntityIds.WorldCup))
            {
                subRank = 13; return true;
            }
            if (u2 is "SA" or "AE" or "QA" or "KW" or "OM" or "BH" or "IQ" or "JO" or "LB")
            {
                if (compName.Contains("AFC Champions League", StringComparison.OrdinalIgnoreCase) || compCode == "AFCCL" || compId is "17" or "849")
                {
                    subRank = 10; return true;
                }
                if (compName.Contains("Asian Cup", StringComparison.OrdinalIgnoreCase) || compCode == "AC" || compId == "7")
                {
                    subRank = 12; return true;
                }
            }
        }
        // Europe (UEFA)
        else if (IsEurope(u2))
        {
            if (compName.Contains("Champions League", StringComparison.OrdinalIgnoreCase) || compCode == "UCL" || compId == "2" ||
                (CanonicalEntityIds.TryTranslateLegacyId(compId, out var c2) && c2 == CanonicalEntityIds.ChampionsLeague))
            {
                subRank = 10; return true;
            }
            if (compName.Contains("Europa League", StringComparison.OrdinalIgnoreCase) || compCode == "UEL" || compId == "3")
            {
                subRank = 11; return true;
            }
            if (compName.Contains("Conference League", StringComparison.OrdinalIgnoreCase) || compCode == "UECL" || compId == "848")
            {
                subRank = 12; return true;
            }
            if (compName.Contains("UEFA Super Cup", StringComparison.OrdinalIgnoreCase) || compCode == "USC" || compId == "531")
            {
                subRank = 13; return true;
            }
            if (compName.Contains("European Championship", StringComparison.OrdinalIgnoreCase) || compName.Contains("Euro", StringComparison.OrdinalIgnoreCase) ||
                compCode == "EURO" || compId == "4" || (CanonicalEntityIds.TryTranslateLegacyId(compId, out var c4) && c4 == CanonicalEntityIds.EuroChampionship))
            {
                subRank = 14; return true;
            }
            if (compName.Contains("Nations League", StringComparison.OrdinalIgnoreCase) || compCode == "UNL" || compId == "5" ||
                (CanonicalEntityIds.TryTranslateLegacyId(compId, out var c5) && c5 == CanonicalEntityIds.NationsLeague))
            {
                subRank = 15; return true;
            }
        }
        // South America (CONMEBOL)
        else if (IsSouthAmerica(u2))
        {
            if (compName.Contains("Libertadores", StringComparison.OrdinalIgnoreCase) || compCode == "LIB" || compId == "13")
            {
                subRank = 10; return true;
            }
            if (compName.Contains("Sudamericana", StringComparison.OrdinalIgnoreCase) || compCode == "SUD" || compId == "11")
            {
                subRank = 11; return true;
            }
            if (compName.Contains("Copa America", StringComparison.OrdinalIgnoreCase) || compName.Contains("Copa América", StringComparison.OrdinalIgnoreCase) ||
                compCode == "CA" || compId == "9" || (CanonicalEntityIds.TryTranslateLegacyId(compId, out var c9) && c9 == CanonicalEntityIds.CopaAmerica))
            {
                subRank = 12; return true;
            }
        }
        // North / Central America (CONCACAF)
        else if (IsNorthAmerica(u2))
        {
            if (compName.Contains("CONCACAF Champions", StringComparison.OrdinalIgnoreCase) || compCode == "CCC" || compId == "16")
            {
                subRank = 10; return true;
            }
            if (compName.Contains("Leagues Cup", StringComparison.OrdinalIgnoreCase) || compCode == "LC" || compId == "846")
            {
                subRank = 11; return true;
            }
            if (compName.Contains("Gold Cup", StringComparison.OrdinalIgnoreCase) || compCode == "CGC" || compId == "22")
            {
                subRank = 12; return true;
            }
        }
        // Asia (AFC)
        else if (IsAsia(u2))
        {
            if (compName.Contains("AFC Champions League", StringComparison.OrdinalIgnoreCase) || compCode == "AFCCL" || compId is "17" or "849")
            {
                subRank = 10; return true;
            }
            if (compName.Contains("Asian Cup", StringComparison.OrdinalIgnoreCase) || compCode == "AC" || compId == "7")
            {
                subRank = 12; return true;
            }
        }

        return false;
    }

    private static bool TryGetTier2Rank(
        string compId,
        string compName,
        string country,
        string compCode,
        out int subRank)
    {
        subRank = 100;

        if (CanonicalEntityIds.TryTranslateLegacyId(compId, out var canon))
        {
            if (canon == CanonicalEntityIds.WorldCup) { subRank = 1; return true; }
            if (canon == CanonicalEntityIds.EuroChampionship) { subRank = 2; return true; }
            if (canon == CanonicalEntityIds.CopaAmerica) { subRank = 3; return true; }
            if (canon == CanonicalEntityIds.AfricaCupOfNations) { subRank = 4; return true; }
            if (canon == CanonicalEntityIds.ChampionsLeague) { subRank = 10; return true; }
            if (canon == CanonicalEntityIds.PremierLeague) { subRank = 20; return true; }
            if (canon == CanonicalEntityIds.LaLiga) { subRank = 30; return true; }
        }

        // 1. FIFA World Cup / UEFA Euro / Copa América / AFCON / FIFA Club World Cup
        if ((compName.Contains("World Cup", StringComparison.OrdinalIgnoreCase) && !compName.Contains("Club", StringComparison.OrdinalIgnoreCase)) || compId == "1" || compCode == "WC")
        {
            subRank = 1; return true;
        }
        if (compName.Contains("European Championship", StringComparison.OrdinalIgnoreCase) || compName.Contains("UEFA Euro", StringComparison.OrdinalIgnoreCase) || compId == "4" || compCode == "EURO")
        {
            subRank = 2; return true;
        }
        if (compName.Contains("Copa America", StringComparison.OrdinalIgnoreCase) || compName.Contains("Copa América", StringComparison.OrdinalIgnoreCase) || compId == "9" || compCode == "CA")
        {
            subRank = 3; return true;
        }
        if (compName.Contains("Africa Cup of Nations", StringComparison.OrdinalIgnoreCase) || compName.Contains("AFCON", StringComparison.OrdinalIgnoreCase) || compId == "6" || compCode == "AFCON")
        {
            subRank = 4; return true;
        }
        if (compName.Contains("Club World Cup", StringComparison.OrdinalIgnoreCase) || compId == "15" || compCode == "CWC")
        {
            subRank = 5; return true;
        }

        // 2. UEFA Champions League (INT / EUR)
        if (compName.Contains("Champions League", StringComparison.OrdinalIgnoreCase) || compId == "2" || compId.Equals("comp_ucl", StringComparison.OrdinalIgnoreCase) || compCode == "UCL")
        {
            subRank = 10; return true;
        }

        // 3. English Premier League (ENG)
        if ((compName.Contains("Premier League", StringComparison.OrdinalIgnoreCase) && (country.Contains("England", StringComparison.OrdinalIgnoreCase) || compCode == "ENG" || compCode == "PL" || string.IsNullOrWhiteSpace(country))) ||
            compId == "39" || compId.Equals("comp_pl", StringComparison.OrdinalIgnoreCase))
        {
            subRank = 20; return true;
        }

        // 4. Spanish LaLiga (ESP)
        if (compName.Contains("La Liga", StringComparison.OrdinalIgnoreCase) || compName.Contains("LaLiga", StringComparison.OrdinalIgnoreCase) || compName.Contains("Primera División", StringComparison.OrdinalIgnoreCase) ||
            compId == "140" || compId.Equals("comp_laliga", StringComparison.OrdinalIgnoreCase) || compCode == "PD")
        {
            subRank = 30; return true;
        }

        // 5. Italian Serie A (ITA)
        if ((compName.Contains("Serie A", StringComparison.OrdinalIgnoreCase) && (country.Contains("Italy", StringComparison.OrdinalIgnoreCase) || compCode == "ITA" || compCode == "SA" || string.IsNullOrWhiteSpace(country))) ||
            compId == "135" || compId.Equals("comp_sa", StringComparison.OrdinalIgnoreCase))
        {
            subRank = 40; return true;
        }

        // 6. German Bundesliga (GER)
        if ((compName.Contains("Bundesliga", StringComparison.OrdinalIgnoreCase) && !compName.Contains("2. Bundesliga", StringComparison.OrdinalIgnoreCase) && !compName.Contains("Austrian", StringComparison.OrdinalIgnoreCase)) ||
            compId == "78" || compId.Equals("comp_bl1", StringComparison.OrdinalIgnoreCase) || compCode == "BL1")
        {
            subRank = 50; return true;
        }

        // 7. French Ligue 1 (FRA)
        if ((compName.Contains("Ligue 1", StringComparison.OrdinalIgnoreCase) && (country.Contains("France", StringComparison.OrdinalIgnoreCase) || compCode == "FRA" || compCode == "FL1" || string.IsNullOrWhiteSpace(country))) ||
            compId == "61" || compId.Equals("comp_fl1", StringComparison.OrdinalIgnoreCase))
        {
            subRank = 60; return true;
        }

        // 8. UEFA Europa League & Conference League (INT)
        if (compName.Contains("Europa League", StringComparison.OrdinalIgnoreCase) || compId == "3" || compId.Equals("comp_uel", StringComparison.OrdinalIgnoreCase) || compCode == "UEL")
        {
            subRank = 70; return true;
        }
        if (compName.Contains("Conference League", StringComparison.OrdinalIgnoreCase) || compId == "848" || compId.Equals("comp_uecl", StringComparison.OrdinalIgnoreCase) || compCode == "UECL")
        {
            subRank = 75; return true;
        }
        if (compName.Contains("UEFA Super Cup", StringComparison.OrdinalIgnoreCase) || compId == "531" || compCode == "USC")
        {
            subRank = 78; return true;
        }

        // 9. Major domestic cups & top regional leagues
        if (compName.Contains("FA Cup", StringComparison.OrdinalIgnoreCase) || compId == "45") { subRank = 80; return true; }
        if (compName.Contains("EFL Cup", StringComparison.OrdinalIgnoreCase) || compId == "48") { subRank = 81; return true; }
        if (compName.Contains("Copa del Rey", StringComparison.OrdinalIgnoreCase) || compId == "143") { subRank = 82; return true; }
        if (compName.Contains("DFB-Pokal", StringComparison.OrdinalIgnoreCase) || compId == "81") { subRank = 83; return true; }
        if (compName.Contains("Coppa Italia", StringComparison.OrdinalIgnoreCase) || compId == "137") { subRank = 84; return true; }
        if (compName.Contains("Coupe de France", StringComparison.OrdinalIgnoreCase) || compId == "66") { subRank = 85; return true; }

        if (compName.Contains("Saudi Pro League", StringComparison.OrdinalIgnoreCase) || compId == "307" || compCode == "SPL") { subRank = 90; return true; }
        if (compName.Contains("Major League Soccer", StringComparison.OrdinalIgnoreCase) || compName.Contains("MLS", StringComparison.OrdinalIgnoreCase) || compId == "253") { subRank = 91; return true; }
        if (compName.Contains("Brasileir", StringComparison.OrdinalIgnoreCase) || (compName.Contains("Serie A", StringComparison.OrdinalIgnoreCase) && country.Contains("Brazil", StringComparison.OrdinalIgnoreCase)) || compId == "71") { subRank = 92; return true; }
        if (compName.Contains("Liga Profesional", StringComparison.OrdinalIgnoreCase) || (compName.Contains("Primera", StringComparison.OrdinalIgnoreCase) && country.Contains("Argentina", StringComparison.OrdinalIgnoreCase)) || compId == "128") { subRank = 93; return true; }
        if (compName.Contains("Eredivisie", StringComparison.OrdinalIgnoreCase) || compId == "88") { subRank = 94; return true; }
        if (compName.Contains("Primeira Liga", StringComparison.OrdinalIgnoreCase) || compName.Contains("Liga Portugal", StringComparison.OrdinalIgnoreCase) || compId == "94") { subRank = 95; return true; }
        if (compName.Contains("Süper Lig", StringComparison.OrdinalIgnoreCase) || compId == "203") { subRank = 96; return true; }
        if (compName.Contains("Qualification", StringComparison.OrdinalIgnoreCase) || compName.Contains("Play-offs", StringComparison.OrdinalIgnoreCase)) { subRank = 97; return true; }
        if (compName.Contains("Championship", StringComparison.OrdinalIgnoreCase) || compId == "40") { subRank = 98; return true; }

        return false;
    }

    private static bool IsCup(string name, string code) =>
        name.Contains("Cup", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Copa", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Pokal", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Coppa", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Coupe", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Beker", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Taça", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Super Cup", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Supercup", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Shield", StringComparison.OrdinalIgnoreCase) ||
        code is "FAC" or "EFL" or "CDR" or "DFB" or "CIT" or "CDF" or "KNVB" or "TDP" or "CS";

    private static bool IsSecondDivision(string name, string code) =>
        name.Contains("Championship", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Segunda", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Serie B", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("2. Bundesliga", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Ligue 2", StringComparison.OrdinalIgnoreCase) ||
        code is "CHA" or "SD" or "SB" or "BL2" or "FL2";

    private static bool IsAfricaOrMena(string iso2) =>
        iso2 is "EG" or "MA" or "DZ" or "TN" or "ZA" or "LY" or "SD" or "NG" or "GH" or "SN" or "CM" or "CI" or "KE" or "UG" or "TZ"
             or "SA" or "AE" or "QA" or "KW" or "OM" or "BH" or "IQ" or "JO" or "LB" or "YE" or "SY" or "PS";

    private static bool IsEurope(string iso2) =>
        iso2 is "GB" or "ES" or "DE" or "IT" or "FR" or "NL" or "PT" or "BE" or "TR" or "AT" or "CH" or "SE" or "NO" or "DK" or "PL"
             or "GR" or "HR" or "RS" or "CZ" or "UA" or "RO" or "IE" or "HU" or "FI" or "SK" or "BG" or "IL" or "CY" or "IS" or "AL"
             or "BA" or "SI" or "LT" or "LV" or "EE" or "GE" or "AM" or "AZ" or "KZ" or "MD" or "LU" or "MT" or "ME" or "MK";

    private static bool IsSouthAmerica(string iso2) =>
        iso2 is "BR" or "AR" or "CO" or "CL" or "UY" or "PE" or "EC" or "PY" or "BO" or "VE";

    private static bool IsNorthAmerica(string iso2) =>
        iso2 is "US" or "MX" or "CA" or "CR" or "PA" or "JM" or "HN" or "SV" or "GT" or "TT";

    private static bool IsAsia(string iso2) =>
        iso2 is "JP" or "KR" or "AU" or "CN" or "IN" or "ID" or "TH" or "VN" or "MY" or "SG" or "PH" or "UZ" or "IR";

    private static int GetPopularitySubRank(
        string compId,
        string compName,
        string country,
        string compCode,
        (string Iso2, string Iso3, string EnglishName) userRegion)
    {
        if (TryGetTier1Rank(compId, compName, country, compCode, userRegion, out int t1)) return t1;
        if (TryGetTier2Rank(compId, compName, country, compCode, out int t2)) return t2;
        return 100;
    }

    private static int GetCompetitionPopularityRank(string? compId, string? compName)
    {
        var score = GetCompetitionSortScore(false, compId, compName, null, null);
        return score.Tier * 100 + score.SubRank;
    }

    private void ApplyCurrentFilter()
    {
        if (_latestTrayData == null && _syncService?.CurrentTrayData != null)
        {
            _latestTrayData = _syncService.CurrentTrayData;
        }
        var prefs = _preferencesService.GetPreferences();
        var allMatches = _latestTrayData?.Matches ?? new List<TrayMatchDto>();

        // Rebuild/update calendar days match counts
        RebuildCalendarDays();

        // Check for score change across followed live matches
        string? goalJumpId = null;
        if (allMatches.Count > 0 && prefs != null)
        {
            var teamIds = prefs.GetFollowedTeamIdSet();
            var compIds = prefs.GetFollowedCompetitionIdSet();
            foreach (var m in allMatches)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Id)) continue;
                int h = m.Score?.Home ?? 0;
                int a = m.Score?.Away ?? 0;
                if (_previousScores.TryGetValue(m.Id, out var prevScore))
                {
                    bool isLive = FixtureRelevanceEngine.IsLive(m);
                    bool isFollowed = FixtureRelevanceEngine.EvaluateRelevance(m, teamIds, compIds).IsRelevant;
                    if (isLive && isFollowed && (h != prevScore.Home || a != prevScore.Away))
                    {
                        goalJumpId ??= m.Id;
                    }
                }
                _previousScores[m.Id] = (h, a);
            }
        }
        if (!string.IsNullOrEmpty(goalJumpId))
        {
            GoalJumpMatchId = goalJumpId;
        }

        // Prioritize and determine active taskbar match: respect explicit user pin or explicit unpin
        if (!string.IsNullOrEmpty(ManualPinnedMatchId))
        {
            ActiveMatchId = ManualPinnedMatchId;
            var pinnedMatch = allMatches.FirstOrDefault(m => string.Equals(m.Id, ManualPinnedMatchId, StringComparison.Ordinal));
            if (pinnedMatch != null)
            {
                string home = pinnedMatch.HomeTeam?.ShortName ?? pinnedMatch.HomeTeam?.Name ?? "Home";
                string away = pinnedMatch.AwayTeam?.ShortName ?? pinnedMatch.AwayTeam?.Name ?? "Away";
                string stateNorm = (pinnedMatch.State ?? string.Empty).Trim().ToUpperInvariant();
                bool isPreMatch = stateNorm is "SCHEDULED" or "TIMED" or "NOT_STARTED" or "NS" or "TBD" or "PREMATCH";

                if (isPreMatch)
                {
                    var timeFormat = (prefs?.Use24HourFormat ?? true) ? "HH:mm" : "h:mm tt";
                    string time = pinnedMatch.KickoffTime != default ? pinnedMatch.KickoffTime.ToLocalTime().ToString(timeFormat) : "Upcoming";
                    ActiveTickerText = $"📌 Pinned: {home} vs {away} ({time})";
                }
                else
                {
                    string score = $"{pinnedMatch.Score?.Home ?? 0}–{pinnedMatch.Score?.Away ?? 0}";
                    string time = pinnedMatch.State == "HT" ? "HT" : (pinnedMatch.ElapsedMinute > 0 ? $"{pinnedMatch.ElapsedMinute}'" : (pinnedMatch.State ?? "LIVE"));
                    ActiveTickerText = $"📌 Pinned: {home} {score} {away} ({time})";
                }
            }
            else
            {
                ActiveTickerText = "📌 Pinned match";
            }
        }
        else if (!string.IsNullOrEmpty(GoalJumpMatchId) && allMatches.Any(m => string.Equals(m.Id, GoalJumpMatchId, StringComparison.Ordinal)))
        {
            ActiveMatchId = GoalJumpMatchId;
            var goalMatch = allMatches.First(m => string.Equals(m.Id, GoalJumpMatchId, StringComparison.Ordinal));
            string home = goalMatch.HomeTeam?.ShortName ?? goalMatch.HomeTeam?.Name ?? "Home";
            string away = goalMatch.AwayTeam?.ShortName ?? goalMatch.AwayTeam?.Name ?? "Away";
            string score = $"{goalMatch.Score?.Home ?? 0}–{goalMatch.Score?.Away ?? 0}";
            string time = goalMatch.State == "HT" ? "HT" : (goalMatch.ElapsedMinute > 0 ? $"{goalMatch.ElapsedMinute}'" : goalMatch.State);
            ActiveTickerText = $"⚽ GOAL: {home} {score} {away} ({time})";
        }
        else if (IsExplicitlyUnpinned)
        {
            ActiveMatchId = null;
            ActiveTickerText = prefs?.EnableTickerCarousel == true
                ? "📌 Auto-Rotate Ticker (Carousel Mode)"
                : "📌 No pinned matches";
        }
        else
        {
            // No manual pin active: do NOT set ActiveMatchId so unpinned matches are never falsely marked pinned
            ActiveMatchId = null;

            if (prefs?.EnableTickerCarousel == true)
            {
                var activeMatch = TrayTickerFormatter.GetActiveMatch(_latestTrayData, prefs);
                if (activeMatch != null)
                {
                    string home = activeMatch.HomeTeam?.ShortName ?? activeMatch.HomeTeam?.Name ?? "Home";
                    string away = activeMatch.AwayTeam?.ShortName ?? activeMatch.AwayTeam?.Name ?? "Away";
                    string stateNorm = (activeMatch.State ?? string.Empty).Trim().ToUpperInvariant();
                    bool isPreMatch = stateNorm is "SCHEDULED" or "TIMED" or "NOT_STARTED" or "NS" or "TBD" or "PREMATCH";

                    if (isPreMatch)
                    {
                        var timeFormat = (prefs?.Use24HourFormat ?? true) ? "HH:mm" : "h:mm tt";
                        string time = activeMatch.KickoffTime != default ? activeMatch.KickoffTime.ToLocalTime().ToString(timeFormat) : "Upcoming";
                        ActiveTickerText = $"{home} vs {away} ({time})";
                    }
                    else
                    {
                        string score = $"{activeMatch.Score?.Home ?? 0}–{activeMatch.Score?.Away ?? 0}";
                        string time = activeMatch.State == "HT" ? "HT" : (activeMatch.ElapsedMinute > 0 ? $"{activeMatch.ElapsedMinute}'" : (activeMatch.State ?? "LIVE"));
                        ActiveTickerText = $"{home} {score} {away} ({time})";
                    }
                }
                else
                {
                    ActiveTickerText = "📌 Auto-Rotate Ticker (Carousel Mode)";
                }
            }
            else
            {
                ActiveTickerText = "📌 No pinned matches";
            }
        }

        // 1. Filter by Selected Calendar Date
        var targetDate = _selectedCalendarDate.Date;
        var dateFiltered = allMatches.Where(m =>
        {
            if (!IsCalendarVisible)
            {
                // On Live, Upcoming, and Finished tabs, calendar date filtering does not constrain the query
                return true;
            }

            var localDate = m.KickoffTime.ToLocalTime().Date;
            var utcDate = m.KickoffTime.UtcDateTime.Date;
            var rawDate = m.KickoffTime.Date;
            if (localDate == targetDate || utcDate == targetDate || rawDate == targetDate || m.KickoffTime.DateTime.Date == targetDate) return true;
            if (targetDate == DateTime.Today && (m.KickoffTime == default || m.KickoffTime.Year < 2000 || FixtureRelevanceEngine.IsLive(m))) return true;
            return false;
        }).ToList();

        var followedTeamIds = prefs?.GetFollowedTeamIdSet() ?? new HashSet<string>();
        var followedCompIds = prefs?.GetFollowedCompetitionIdSet() ?? new HashSet<string>();

        // 2. Evaluate relevance and build view models
        var viewModels = new List<MatchItemViewModel>();
        foreach (var match in dateFiltered)
        {
            var relevance = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);
            bool isExplicitlyPinned = !string.IsNullOrEmpty(ManualPinnedMatchId) &&
                string.Equals(match.Id, ManualPinnedMatchId, StringComparison.Ordinal);
            bool homeFollowed = (!string.IsNullOrEmpty(match.HomeTeam?.Id) && followedTeamIds.Contains(match.HomeTeam.Id))
                || (!string.IsNullOrEmpty(match.HomeTeam?.LegacyId) && followedTeamIds.Contains(match.HomeTeam.LegacyId))
                || (!string.IsNullOrEmpty(match.HomeTeam?.Name) && followedTeamIds.Contains(match.HomeTeam.Name))
                || (CanonicalEntityIds.TryTranslateLegacyId(match.HomeTeam?.Id, out var hCanon) && followedTeamIds.Contains(hCanon))
                || (CanonicalEntityIds.TryTranslateLegacyId(match.HomeTeam?.LegacyId, out var hLegCanon) && followedTeamIds.Contains(hLegCanon))
                || (prefs?.FollowedTeams.Any(t => string.Equals(t.Name?.Trim(), match.HomeTeam?.Name?.Trim(), StringComparison.OrdinalIgnoreCase)) == true);

            bool awayFollowed = (!string.IsNullOrEmpty(match.AwayTeam?.Id) && followedTeamIds.Contains(match.AwayTeam.Id))
                || (!string.IsNullOrEmpty(match.AwayTeam?.LegacyId) && followedTeamIds.Contains(match.AwayTeam.LegacyId))
                || (!string.IsNullOrEmpty(match.AwayTeam?.Name) && followedTeamIds.Contains(match.AwayTeam.Name))
                || (CanonicalEntityIds.TryTranslateLegacyId(match.AwayTeam?.Id, out var aCanon) && followedTeamIds.Contains(aCanon))
                || (CanonicalEntityIds.TryTranslateLegacyId(match.AwayTeam?.LegacyId, out var aLegCanon) && followedTeamIds.Contains(aLegCanon))
                || (prefs?.FollowedTeams.Any(t => string.Equals(t.Name?.Trim(), match.AwayTeam?.Name?.Trim(), StringComparison.OrdinalIgnoreCase)) == true);

            bool compFollowed = relevance.FollowedByCompetition
                || (!string.IsNullOrEmpty(match.Competition?.Id) && followedCompIds.Contains(match.Competition.Id))
                || (!string.IsNullOrEmpty(match.Competition?.LegacyId) && followedCompIds.Contains(match.Competition.LegacyId))
                || (!string.IsNullOrWhiteSpace(match.Competition?.Name) && !string.IsNullOrWhiteSpace(match.Competition?.Country) && followedCompIds.Contains($"name_country:{match.Competition.Name.Trim()}|{match.Competition.Country.Trim()}"))
                || (CanonicalEntityIds.TryTranslateLegacyId(match.Competition?.Id, out var cCanon) && followedCompIds.Contains(cCanon))
                || (CanonicalEntityIds.TryTranslateLegacyId(match.Competition?.LegacyId, out var cLegCanon) && followedCompIds.Contains(cLegCanon))
                || (prefs?.FollowedCompetitions.Any(c => 
                    string.Equals(c.Name?.Trim(), match.Competition?.Name?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(c.Country) || string.IsNullOrEmpty(match.Competition?.Country) || string.Equals(c.Country?.Trim(), match.Competition?.Country?.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(match.Competition?.Id) || string.Equals(c.Id, match.Competition.Id, StringComparison.OrdinalIgnoreCase))) == true);

            viewModels.Add(MatchItemViewModel.FromDto(
                match,
                relevance,
                isExplicitlyPinned,
                prefs?.Use24HourFormat ?? true,
                homeFollowed,
                awayFollowed,
                compFollowed));
        }

        // 3. Partition into the 3 strict buckets:
        //    Bucket 0: Matches from Followed Teams
        //    Bucket 1: Matches from Followed Competitions (and not followed teams)
        //    Bucket 2: Other matches
        var bucket0 = viewModels.Where(m => m.FollowedByTeam).ToList();
        var bucket1 = viewModels.Where(m => m.FollowedByCompetition && !m.FollowedByTeam).ToList();
        var bucket2 = viewModels.Where(m => !m.FollowedByTeam && !m.FollowedByCompetition).ToList();

        IEnumerable<MatchItemViewModel> FilterByTab(IEnumerable<MatchItemViewModel> bucket)
        {
            return _activeFilter switch
            {
                MatchFilterType.Live => bucket.Where(m => m.IsLive),
                MatchFilterType.Upcoming => bucket.Where(m => m.IsPrematch),
                MatchFilterType.Finished => bucket.Where(m => m.IsFinished),
                _ => bucket
            };
        }

        var filteredByTab = FilterByTab(viewModels).ToList();

        // Sort rules: Followed matches at the top (or IsRelevant == true), grouped by CompetitionName, then sorted by KickoffTime (StartTimeUtc) ascending with HomeTeamName tie-breaker.
        var sortedFollowed = filteredByTab
            .Where(m => m.IsRelevant || m.FollowedByTeam || m.FollowedByCompetition)
            .OrderByDescending(m => m.FollowedByTeam)
            .ThenBy(m => GetCompetitionSortScore(true, m.CompetitionId, m.CompetitionName, m.CompetitionCountry, m.CompetitionCode).SubRank)
            .ThenBy(m => m.CompetitionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.StartTimeUtc)
            .ThenBy(m => m.HomeTeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var sortedOther = _activeFilter == MatchFilterType.Following
            ? new List<MatchItemViewModel>()
            : filteredByTab
                .Where(m => !m.IsRelevant && !m.FollowedByTeam && !m.FollowedByCompetition)
                .OrderBy(m => GetCompetitionSortScore(false, m.CompetitionId, m.CompetitionName, m.CompetitionCountry, m.CompetitionCode).Tier)
                .ThenBy(m => GetCompetitionSortScore(false, m.CompetitionId, m.CompetitionName, m.CompetitionCountry, m.CompetitionCode).SubRank)
                .ThenBy(m => m.CompetitionCountry ?? m.CompetitionCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.CompetitionName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.StartTimeUtc)
                .ThenBy(m => m.HomeTeamName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();

        ReconcileMatchCollection(FollowedMatches, sortedFollowed);
        ReconcileMatchCollection(OtherMatches, sortedOther);

        var completeFilteredList = sortedFollowed.Concat(sortedOther).ToList();
        ReconcileMatchCollection(FilteredMatches, completeFilteredList);

        // Smart Merge expandable competition accordion groups (Anti-Blink UX)
        var groupedData = completeFilteredList
            .GroupBy(m => !string.IsNullOrWhiteSpace(m.CompetitionId) ? m.CompetitionId : m.CompetitionName)
            .Select(g =>
            {
                var first = g.First();
                string compId = first.CompetitionId;
                string compName = first.CompetitionName;
                string compCode = first.CompetitionCode;
                string? country = !string.IsNullOrWhiteSpace(first.CompetitionCountry)
                    ? first.CompetitionCountry
                    : first.CompetitionCode;
                string? logoUrl = first.CompetitionLogoUrl;
                if (string.IsNullOrWhiteSpace(logoUrl) && !string.IsNullOrWhiteSpace(first.CompetitionCode))
                {
                    logoUrl = $"https://media.api-sports.io/flags/{first.CompetitionCode.ToLowerInvariant()}.svg";
                }
                bool hasFollowedTeams = g.Any(m => m.FollowedByTeam);
                bool isFollowedCompetition = g.Any(m => m.FollowedByCompetition);
                bool isFollowed = hasFollowedTeams || isFollowedCompetition;
                string key = !string.IsNullOrWhiteSpace(compId) ? compId : compName;

                // Deterministically sort matches by kickoff time (StartTimeUtc ascending), with tie-breakers on HomeTeamName
                var matchesList = g
                    .OrderBy(m => m.StartTimeUtc)
                    .ThenBy(m => m.HomeTeamName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(m => m.Id, StringComparer.Ordinal)
                    .ToList();

                var sortScore = GetCompetitionSortScore(isFollowed, compId, compName, country, compCode);

                return new
                {
                    Key = key,
                    CompId = compId,
                    CompName = compName,
                    Country = country,
                    CompCode = compCode,
                    LogoUrl = logoUrl,
                    HasFollowedTeams = hasFollowedTeams,
                    IsFollowedCompetition = isFollowedCompetition,
                    IsFollowed = isFollowed,
                    Matches = matchesList,
                    SortScore = sortScore
                };
            })
            .OrderBy(g => g.SortScore.Tier)
            .ThenBy(g => g.SortScore.SubRank)
            .ThenBy(g => g.SortScore.CountrySort, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.SortScore.NameSort, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var incomingKeys = new HashSet<string>(groupedData.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

        // 1. Remove groups that are no longer present in incoming data
        for (int i = CompetitionGroups.Count - 1; i >= 0; i--)
        {
            if (!incomingKeys.Contains(CompetitionGroups[i].CompetitionKey))
            {
                CompetitionGroups.RemoveAt(i);
            }
        }

        // 2. Insert new groups or update existing groups in-place using Smart Merge
        for (int i = 0; i < groupedData.Count; i++)
        {
            var data = groupedData[i];
            int existingIndex = -1;
            for (int j = 0; j < CompetitionGroups.Count; j++)
            {
                if (string.Equals(CompetitionGroups[j].CompetitionKey, data.Key, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                // New group: construct and set initial expansion
                var newGroup = new CompetitionGroupViewModel(
                    data.CompId,
                    data.CompName,
                    data.Country,
                    data.LogoUrl,
                    data.HasFollowedTeams,
                    data.IsFollowedCompetition);

                foreach (var match in data.Matches)
                {
                    newGroup.AddMatch(match);
                }

                if (i <= CompetitionGroups.Count)
                {
                    CompetitionGroups.Insert(i, newGroup);
                }
                else
                {
                    CompetitionGroups.Add(newGroup);
                }
            }
            else
            {
                // Existing group: update metadata and sync matches in-place
                var existingGroup = CompetitionGroups[existingIndex];
                existingGroup.UpdateMetadata(data.Country, data.LogoUrl, data.HasFollowedTeams, data.IsFollowedCompetition);
                existingGroup.SyncMatches(data.Matches);

                // Auto-expand if the competition contains followed teams or is a followed competition
                if (data.HasFollowedTeams || data.IsFollowedCompetition)
                {
                    existingGroup.IsExpanded = true;
                }

                if (existingIndex != i && i < CompetitionGroups.Count)
                {
                    CompetitionGroups.Move(existingIndex, i);
                }
            }
        }

        HasFollowedMatches = FollowedMatches.Count > 0;
        HasOtherMatches = OtherMatches.Count > 0;
        IsOtherMatchesVisible = HasOtherMatches && _activeFilter != MatchFilterType.Following;
        OtherMatchesHeader = $"Other Matches ({OtherMatches.Count})";
        RebuildHubCompetitions();

        bool filterChanged = _lastFilterApplied != _activeFilter;
        bool dateChanged = _lastDateApplied != _selectedCalendarDate.Date;
        _lastFilterApplied = _activeFilter;
        _lastDateApplied = _selectedCalendarDate.Date;

        if (_activeFilter == MatchFilterType.Following)
        {
            IsOtherMatchesExpanded = false;
        }
        else if (_activeFilter == MatchFilterType.All)
        {
            IsOtherMatchesExpanded = true;
        }
        else if (filterChanged || dateChanged)
        {
            IsOtherMatchesExpanded = !HasFollowedMatches;
        }

        HasMatches = FilteredMatches.Count > 0;
        IsEmpty = !HasMatches;

        if (IsEmpty)
        {
            if (_isDateSynchronizing)
            {
                EmptyStateTitle = "Synchronizing Schedule...";
                EmptyStateMessage = $"Loading fixtures for {_selectedCalendarDate:dddd, MMM d}...";
            }
            else if (dateFiltered.Count == 0)
            {
                string dateLabel = _selectedCalendarDate.Date == DateTime.Today
                    ? "today"
                    : _selectedCalendarDate.Date == DateTime.Today.AddDays(-1)
                        ? "yesterday"
                        : _selectedCalendarDate.Date == DateTime.Today.AddDays(1)
                            ? "tomorrow"
                            : $"on {_selectedCalendarDate:dddd, MMM d}";

                if (allMatches.Count == 0)
                {
                    if (IsOffline)
                    {
                        EmptyStateTitle = "Offline — No Cached Matches";
                        EmptyStateMessage = "FootballTray is offline and no cached fixture data is stored locally. Connect to backend to sync matches.";
                    }
                    else
                    {
                        EmptyStateTitle = "No Matches Synchronized";
                        EmptyStateMessage = "There are no matches currently scheduled or live across target competitions.";
                    }
                }
                else
                {
                    EmptyStateTitle = "No Matches Scheduled";
                    EmptyStateMessage = $"There are no fixtures synchronized {dateLabel}. Select another date from the calendar or refresh.";
                }
            }
            else if (_activeFilter == MatchFilterType.Following)
            {
                EmptyStateTitle = "No Followed Matches";
                EmptyStateMessage = "None of the currently synchronized matches involve your followed teams or competitions. Follow teams or competitions from the preferences panel to see them here.";
            }
            else if (_activeFilter == MatchFilterType.Live)
            {
                EmptyStateTitle = "No Live Matches";
                EmptyStateMessage = "There are no fixtures currently in progress.";
            }
            else if (_activeFilter == MatchFilterType.Upcoming)
            {
                EmptyStateTitle = "No Upcoming Matches";
                EmptyStateMessage = "There are no upcoming prematch fixtures scheduled for your followed teams or competitions.";
            }
            else if (_activeFilter == MatchFilterType.Finished)
            {
                EmptyStateTitle = "No Finished Matches";
                EmptyStateMessage = "There are no completed fixtures in today's match window.";
            }
            else
            {
                EmptyStateTitle = "No Matches";
                EmptyStateMessage = "No matches match the selected criteria.";
            }
        }
        else
        {
            EmptyStateTitle = string.Empty;
            EmptyStateMessage = string.Empty;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncStatusText)));
    }

    /// <summary>
    /// Reconciles ObservableCollection items in-place instead of calling Clear() + Add().
    /// Preserves ListView item containers, scroll position, and eliminates visual flickering.
    /// </summary>
    private static void ReconcileMatchCollection(
        ObservableCollection<MatchItemViewModel> targetCollection,
        IReadOnlyList<MatchItemViewModel> newItems)
    {
        var newIdSet = new HashSet<string>(newItems.Select(m => m.Id), StringComparer.Ordinal);

        // 1. Remove deleted/absent items from the end backwards
        for (int i = targetCollection.Count - 1; i >= 0; i--)
        {
            if (!newIdSet.Contains(targetCollection[i].Id))
            {
                targetCollection.RemoveAt(i);
            }
        }

        // 2. Update existing items in-place or insert newly appeared items
        for (int targetIndex = 0; targetIndex < newItems.Count; targetIndex++)
        {
            var sourceItem = newItems[targetIndex];
            int existingIndex = -1;

            for (int j = targetIndex; j < targetCollection.Count; j++)
            {
                if (string.Equals(targetCollection[j].Id, sourceItem.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex == targetIndex)
            {
                // Already at correct index: update in-place (fires PropertyChanged ONLY on changed fields)
                targetCollection[targetIndex].UpdateFromSource(sourceItem);
            }
            else if (existingIndex > targetIndex)
            {
                // Item order changed: move into position and update in-place
                var item = targetCollection[existingIndex];
                targetCollection.Move(existingIndex, targetIndex);
                item.UpdateFromSource(sourceItem);
            }
            else
            {
                // Brand new item: insert at targetIndex
                targetCollection.Insert(targetIndex, sourceItem);
            }
        }
    }

    /// <summary>
    /// Checks for remote application updates.
    /// When <paramref name="isManual"/> is true, surfaces user feedback on in-progress, up-to-date, and failure states.
    /// When <paramref name="isManual"/> is false, runs silently unless an update is available.
    /// Manual requests take presentation precedence over concurrent/in-flight automatic startup checks.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckForUpdatesAsync(
        bool isManual = true,
        CancellationToken cancellationToken = default)
    {
        if (isManual)
        {
            Interlocked.Exchange(ref _manualUpdateCheckRequested, 1);
        }

        if (_updateDiscoveryService == null)
        {
            if (isManual)
            {
                RunOnUiSafe(() =>
                {
                    UpdateStatusMessage = "Update discovery is not available.";
                }, nameof(CheckForUpdatesAsync));
            }
            return null;
        }

        Task<UpdateCheckResult> checkTask;
        lock (_checkLock)
        {
            if (isManual)
            {
                _currentCheckIsManual = true;
            }

            if (_inFlightCheckTask == null || _inFlightCheckTask.IsCompleted)
            {
                _inFlightCheckTask = RunCheckCoreAsync();
            }
            else if (isManual)
            {
                // Immediate feedback for manual user joining an in-flight check
                RunOnUiSafe(() =>
                {
                    IsCheckingForUpdates = true;
                    UpdateStatusMessage = "Checking for updates...";
                }, nameof(CheckForUpdatesAsync));
            }

            checkTask = _inFlightCheckTask;
        }

        return await checkTask.WaitAsync(cancellationToken);
    }

    private async Task<UpdateCheckResult> RunCheckCoreAsync()
    {
        RunOnUiSafe(() =>
        {
            IsCheckingForUpdates = true;
            if (_currentCheckIsManual)
            {
                UpdateStatusMessage = "Checking for updates...";
            }
        }, nameof(RunCheckCoreAsync));

        UpdateCheckResult result;
        try
        {
            result = await _updateDiscoveryService!.CheckForUpdatesAsync(_disposeCts.Token);
        }
        finally
        {
            lock (_checkLock)
            {
                _inFlightCheckTask = null;
            }
        }

        bool isManualPresentation;
        lock (_checkLock)
        {
            isManualPresentation = _currentCheckIsManual;
            _currentCheckIsManual = false;
        }

        RunOnUiSafe(() =>
        {
            ApplyUpdateResult(result, isManualPresentation);
        }, nameof(RunCheckCoreAsync));

        return result;
    }

    private void ApplyUpdateResult(UpdateCheckResult result, bool isManual)
    {
        UpdateCheckState = result.State;

        switch (result.State)
        {
            case UpdateCheckState.UpdateAvailable:
                bool isNewOffer = OfferedManifest == null ||
                    !string.Equals(OfferedManifest.Version, result.OfferedManifest?.Version, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(OfferedManifest.Sha256, result.OfferedManifest?.Sha256, StringComparison.OrdinalIgnoreCase);

                LatestVersion = result.TargetVersion?.ToString() ?? result.OfferedManifest?.Version;
                UpdateReleaseNotes = result.OfferedManifest?.ReleaseNotes;
                OfferedManifest = result.OfferedManifest;
                IsUpdateAvailable = true;
                UpdateStatusMessage = isManual
                    ? $"A newer version v{LatestVersion} is available."
                    : $"Update available: FootballTray v{LatestVersion}";

                if (isNewOffer)
                {
                    ResetAcquisitionPresentation();
                }
                break;

            case UpdateCheckState.UpToDate:
                IsUpdateAvailable = false;
                OfferedManifest = null;
                ResetAcquisitionPresentation();
                if (isManual)
                {
                    UpdateStatusMessage = "FootballTray is up to date.";
                }
                break;

            case UpdateCheckState.CheckFailed:
                IsUpdateAvailable = false;
                OfferedManifest = null;
                ResetAcquisitionPresentation();
                if (isManual)
                {
                    UpdateStatusMessage = $"Check failed: {result.Message}";
                }
                break;
        }

        IsCheckingForUpdates = false;
    }

    private void ResetAcquisitionPresentation()
    {
        AcquisitionState = UpdateAcquisitionState.NotStarted;
        VerifiedPackagePath = null;
        DownloadProgressPercent = 0;
        DownloadStatusText = null;
        InstallationState = UpdateInstallationState.NotReady;
        InstallStatusText = null;
    }

    /// <summary>
    /// Downloads and cryptographically verifies the currently offered update package.
    /// User-initiated action. Coalesces concurrent callers for the same package.
    /// Caller cancellation unblocks only that caller's wait.
    /// </summary>
    public async Task<UpdateAcquisitionResult?> DownloadUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_updateAcquisitionService == null || _offeredManifest == null)
        {
            return null;
        }

        var targetManifest = _offeredManifest;
        var targetIdentity = PackageIdentity.FromManifest(targetManifest);
        Task<UpdateAcquisitionResult?> downloadTask;

        lock (_downloadLock)
        {
            if (_inFlightDownloadTask != null && !_inFlightDownloadTask.IsCompleted)
            {
                if (_inFlightDownloadIdentity == targetIdentity)
                {
                    downloadTask = _inFlightDownloadTask;
                }
                else
                {
                    // Programmatic request for a different package while an acquisition is active:
                    // do NOT join the wrong task; return safe busy failure.
                    return UpdateAcquisitionResult.Failed(
                        "Another update package acquisition is already in progress.",
                        DateTimeOffset.UtcNow,
                        targetManifest.Version);
                }
            }
            else
            {
                if (!CanDownloadUpdate)
                {
                    return null;
                }

                _inFlightDownloadIdentity = targetIdentity;
                _inFlightDownloadTask = RunDownloadCoreAsync(targetManifest, targetIdentity);
                downloadTask = _inFlightDownloadTask;
            }
        }

        return await downloadTask.WaitAsync(cancellationToken);
    }

    private async Task<UpdateAcquisitionResult?> RunDownloadCoreAsync(ReleaseManifest targetManifest, PackageIdentity targetIdentity)
    {
        RunOnUiSafe(() =>
        {
            if (IsCurrentOffer(targetIdentity))
            {
                AcquisitionState = UpdateAcquisitionState.Downloading;
                DownloadProgressPercent = 0;
                DownloadStatusText = "Downloading update...";
            }
        }, nameof(DownloadUpdateAsync));

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            RunOnUiSafe(() =>
            {
                // Guard: ignore stale progress reports if the offered package changed
                if (!IsCurrentOffer(targetIdentity))
                {
                    return;
                }

                DownloadProgressPercent = p.Percent;
                if (AcquisitionState == UpdateAcquisitionState.Downloading)
                {
                    double mbReceived = p.BytesReceived / 1048576.0;
                    double mbTotal = p.TotalExpectedBytes / 1048576.0;
                    DownloadStatusText = $"Downloading... {mbReceived:F1} MB / {mbTotal:F1} MB ({p.Percent}%)";
                }
                else if (AcquisitionState == UpdateAcquisitionState.Verifying)
                {
                    DownloadStatusText = "Verifying update...";
                }
            }, nameof(DownloadUpdateAsync));
        });

        UpdateAcquisitionResult result;
        try
        {
            result = await _updateAcquisitionService!.AcquirePackageAsync(
                targetManifest,
                progress,
                _disposeCts.Token);
        }
        catch (OperationCanceledException)
        {
            result = UpdateAcquisitionResult.Cancelled(DateTimeOffset.UtcNow, targetManifest.Version);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLogger.Error("Updates", "DownloadExecutionError", $"Error during package download: {ex.Message}", ex: ex);
            result = UpdateAcquisitionResult.Failed("Unable to download update.", DateTimeOffset.UtcNow, targetManifest.Version);
        }
        finally
        {
            lock (_downloadLock)
            {
                _inFlightDownloadTask = null;
                _inFlightDownloadIdentity = null;
            }

            RunOnUiSafe(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDownloadUpdate)));
            }, nameof(DownloadUpdateAsync));
        }

        RunOnUiSafe(() =>
        {
            // Only apply state if the offered manifest still matches the downloaded package identity
            if (IsCurrentOffer(targetIdentity))
            {
                AcquisitionState = result.State;
                if (result.State == UpdateAcquisitionState.Verified)
                {
                    VerifiedPackagePath = result.LocalPackagePath;
                    DownloadProgressPercent = 100;
                    DownloadStatusText = "Update downloaded and verified.";
                }
                else if (result.State == UpdateAcquisitionState.Failed)
                {
                    VerifiedPackagePath = null;
                    DownloadStatusText = $"Download failed: {result.Message}";
                }
                else if (result.State == UpdateAcquisitionState.Cancelled)
                {
                    VerifiedPackagePath = null;
                    DownloadStatusText = "Download was cancelled.";
                }
            }
        }, nameof(DownloadUpdateAsync));

        return result;
    }

    /// <summary>
    /// Prepares and initiates installer handoff for the verified update package.
    /// User-initiated action. Coalesces concurrent callers for the same package.
    /// Caller cancellation unblocks only that caller's wait.
    /// Executes the final offer check and StartPreparedHandoff synchronously with ZERO async gap.
    /// </summary>
    public async Task<UpdateInstallationResult?> InstallUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_updateInstallationService == null || _offeredManifest == null || string.IsNullOrEmpty(_verifiedPackagePath))
        {
            return null;
        }

        var targetManifest = _offeredManifest;
        var targetIdentity = PackageIdentity.FromManifest(targetManifest);
        var verifiedPath = _verifiedPackagePath;
        Task<UpdateInstallationResult?> installTask;

        lock (_installLock)
        {
            if (_inFlightInstallTask != null && !_inFlightInstallTask.IsCompleted)
            {
                if (_inFlightInstallIdentity == targetIdentity)
                {
                    installTask = _inFlightInstallTask;
                }
                else
                {
                    return UpdateInstallationResult.Failed(
                        targetManifest.Version,
                        "Another update installation is already in progress.");
                }
            }
            else
            {
                if (!CanInstallUpdate)
                {
                    return null;
                }

                _inFlightInstallIdentity = targetIdentity;
                _inFlightInstallTask = RunInstallCoreAsync(targetManifest, verifiedPath, targetIdentity);
                installTask = _inFlightInstallTask;
            }
        }

        return await installTask.WaitAsync(cancellationToken);
    }

    private async Task<UpdateInstallationResult?> RunInstallCoreAsync(
        ReleaseManifest targetManifest,
        string verifiedPath,
        PackageIdentity targetIdentity)
    {
        RunOnUiSafe(() =>
        {
            if (IsCurrentOffer(targetIdentity))
            {
                InstallationState = UpdateInstallationState.Preparing;
                InstallStatusText = "Preparing update installer...";
            }
        }, nameof(InstallUpdateAsync));

        PreparedUpdateInstall prepared;
        try
        {
            prepared = await _updateInstallationService!.PrepareInstallAsync(
                targetManifest,
                verifiedPath,
                _disposeCts.Token);
        }
        catch (OperationCanceledException)
        {
            RunOnUiSafe(() =>
            {
                if (IsCurrentOffer(targetIdentity))
                {
                    InstallationState = UpdateInstallationState.NotReady;
                    InstallStatusText = "Installation preparation was cancelled.";
                }
            }, nameof(InstallUpdateAsync));
            return UpdateInstallationResult.Cancelled(targetManifest.Version);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLogger.Error("Updates", "InstallPreparationError", $"Error preparing update installation: {ex.Message}", ex: ex);
            RunOnUiSafe(() =>
            {
                if (IsCurrentOffer(targetIdentity))
                {
                    InstallationState = UpdateInstallationState.Failed;
                    InstallStatusText = $"Installation failed: {ex.Message}";
                }
            }, nameof(InstallUpdateAsync));
            return UpdateInstallationResult.Failed(targetManifest.Version, ex.Message);
        }
        finally
        {
            lock (_installLock)
            {
                _inFlightInstallTask = null;
                _inFlightInstallIdentity = null;
            }

            RunOnUiSafe(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanInstallUpdate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInstallingUpdate)));
            }, nameof(InstallUpdateAsync));
        }

        UpdateInstallationResult? result = null;
        bool initiateShutdown = false;

        void ExecuteLaunchOnUi()
        {
            // Critical: Final offer check and StartPreparedHandoff executed synchronously with ZERO async gap.
            if (!IsCurrentOffer(prepared.TargetIdentity))
            {
                _updateInstallationService.DiscardPreparedInstall(prepared);
                InstallationState = UpdateInstallationState.NotReady;
                InstallStatusText = null;
                result = UpdateInstallationResult.Failed(
                    prepared.TargetVersion,
                    "The update offer changed while preparation was in progress.");
                return;
            }

            result = _updateInstallationService.StartPreparedHandoff(prepared);
            if (result.Success)
            {
                InstallationState = UpdateInstallationState.HandoffStarted;
                InstallStatusText = "Update installer launched. Closing application...";
                initiateShutdown = true;
            }
            else
            {
                InstallationState = UpdateInstallationState.Failed;
                InstallStatusText = $"Failed to start installer: {result.Message}";
            }
        }

        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
        {
            ExecuteLaunchOnUi();
        }
        else
        {
            var tcs = new TaskCompletionSource();
            bool enqueued = _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ExecuteLaunchOnUi();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (!enqueued)
            {
                ExecuteLaunchOnUi();
            }
            else
            {
                await tcs.Task;
            }
        }

        if (initiateShutdown)
        {
            bool shutdownAccepted = _appShutdownHandler?.RequestShutdown() ?? false;
            if (!shutdownAccepted && result != null)
            {
                // Dispatcher queue was unavailable — window close could not be enqueued.
                // Surface a factual message so the user knows to close manually.
                RunOnUiSafe(() =>
                {
                    if (IsCurrentOffer(prepared.TargetIdentity))
                    {
                        InstallStatusText = "Update installer started. Close FootballTray to continue.";
                    }
                }, nameof(InstallUpdateAsync));
            }
        }

        return result;
    }

    /// <summary>
    /// Executes a delayed startup update check.
    /// Runs at most once per application lifetime and guarantees clean shutdown cancellation without unobserved exceptions.
    /// </summary>
    public async Task RunStartupUpdateCheckAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (_updateDiscoveryService == null) return;

        if (Interlocked.Exchange(ref _startupCheckScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCts.Token);

            var token = linkedCts.Token;

            if (delay > TimeSpan.Zero)
            {
                await _delayFunc(delay, token);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (Volatile.Read(ref _manualUpdateCheckRequested) != 0)
            {
                return;
            }

            await CheckForUpdatesAsync(isManual: false, token);
        }
        catch (OperationCanceledException)
        {
            // Expected on application shutdown or disposal: silent, no error surfaced
        }
        catch (Exception ex)
        {
            Diagnostics.AppLogger.Warn("Updates", "StartupCheckOrchestrationError", $"Startup update check orchestration failed: [{ex.GetType().Name}] {ex.Message}", ex: ex);
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _hubLoadingCts?.Cancel();
        _hubLoadingCts?.Dispose();
        SearchCatalog.Dispose();
        Settings?.Dispose();
        _preferencesService.PreferencesChanged -= OnPreferencesChanged;
        if (_syncService != null)
        {
            _syncService.TrayUpdated -= OnTrayUpdated;
            _syncService.SyncStateChanged -= OnSyncStateChanged;
        }
        if (_disposeApiClient)
        {
            _apiClient.Dispose();
        }
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
