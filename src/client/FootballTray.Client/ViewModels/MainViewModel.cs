using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
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
            if (IsSyncing) return "Syncing active calendar day & followed team fixtures...";
            if (IsOffline) return "Offline";
            if (_latestTrayData?.QuotaExhausted == true) return "API Limit Reached (Resets Midnight)";
            if (_freshnessState == DataFreshnessState.Empty && FilteredMatches.Count == 0) return "No matches available";
            if (FilteredMatches.Count == 0) return "Up to date (No matches)";
            return "Up to date";
        }
    }

    public string SyncIconGlyph => IsSyncing ? "\uE72C" : (_latestTrayData?.QuotaExhausted == true ? "\uE7BA" : "\uE73E");

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
                ApplyCurrentFilter();
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

        SearchCatalog = new SearchCatalogViewModel(
            apiClient ?? new FootballApiClient(),
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
            var existing = AvailableCompetitions.FirstOrDefault(c => c.Id.Equals(comp.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                AvailableCompetitions.Add(FollowedCompetitionItemViewModel.FromDomain(comp, true));
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

    public async Task ToggleCompetitionFollowAsync(string compId, string? compName = null, string? compCode = null)
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
                var domainComp = compVm?.ToDomain() ?? new FollowedCompetition
                {
                    Id = compId,
                    Name = !string.IsNullOrWhiteSpace(compName) ? compName : compId,
                    Code = compCode ?? "COMP"
                };

                await _preferencesService.FollowCompetitionAsync(domainComp);
                if (compVm != null)
                {
                    compVm.IsFollowed = true;
                }
                else
                {
                    AvailableCompetitions.Add(FollowedCompetitionItemViewModel.FromDomain(domainComp, true));
                }
            }

            StatusMessage = null;
            ReapplyRelevanceAndFilters();
            Settings.RefreshFollowedLists();
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
            Diagnostics.AppLogger.Warn("UI", "DispatchEnqueueFailed", $"DispatcherQueue.TryEnqueue returned false for '{actionName}'. UI dispatcher may be shutting down.");
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

        // 1. HTTP 304 Not Modified: Short-circuit collection mutation if already populated (Zero Flicker!)
        if (freshnessState == DataFreshnessState.NotModified && _latestTrayData != null && (FilteredMatches.Count > 0 || _latestTrayData.Matches.Count == 0))
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
            string score = $"{pinnedMatch.Score?.Home ?? 0}–{pinnedMatch.Score?.Away ?? 0}";
            string time = pinnedMatch.State == "HT" ? "HT" : (pinnedMatch.ElapsedMinute > 0 ? $"{pinnedMatch.ElapsedMinute}'" : pinnedMatch.State);
            ActiveTickerText = $"📌 Pinned: {home} {score} {away} ({time})";
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
                string score = $"{pinnedMatch.Score?.Home ?? 0}–{pinnedMatch.Score?.Away ?? 0}";
                string time = pinnedMatch.State == "HT" ? "HT" : (pinnedMatch.ElapsedMinute > 0 ? $"{pinnedMatch.ElapsedMinute}'" : pinnedMatch.State);
                ActiveTickerText = $"📌 Pinned: {home} {score} {away} ({time})";
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

    public void RebuildCalendarDays()
    {
        var today = DateTime.Today;
        var allMatches = _latestTrayData?.Matches ?? (IReadOnlyList<TrayMatchDto>)Array.Empty<TrayMatchDto>();

        var existingMap = CalendarDays.ToDictionary(d => d.Date.Date);

        var targetDates = new List<(DateTime Date, bool IsToday, bool IsSelected, int MatchCount)>();
        for (int offset = -1; offset <= 1; offset++)
        {
            var targetDate = today.AddDays(offset).Date;
            bool isToday = offset == 0;
            bool isSelected = targetDate == _selectedCalendarDate.Date;

            int matchCount = allMatches.Count(m =>
            {
                var localDate = m.KickoffTime.ToLocalTime().Date;
                var utcDate = m.KickoffTime.UtcDateTime.Date;
                var rawDate = m.KickoffTime.Date;
                if (localDate == targetDate || utcDate == targetDate || rawDate == targetDate || m.KickoffTime.DateTime.Date == targetDate) return true;
                if (isToday && (m.KickoffTime == default || m.KickoffTime.Year < 2000 || FixtureRelevanceEngine.IsLive(m))) return true;
                return false;
            });

            if (matchCount == 0 && targetDate != _selectedCalendarDate.Date && existingMap.TryGetValue(targetDate, out var prevDay) && prevDay.MatchCount > 0)
            {
                matchCount = prevDay.MatchCount;
            }

            targetDates.Add((targetDate, isToday, isSelected, matchCount));
        }

        // Fast path: if CalendarDays already matches target 3 days, update in place without .Clear()
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

    private static int GetCompetitionPopularityRank(string? compId, string? compName)
    {
        if (!string.IsNullOrWhiteSpace(compId))
        {
            if (CanonicalEntityIds.TryTranslateLegacyId(compId, out var canon))
            {
                if (canon == CanonicalEntityIds.ChampionsLeague) return 1;
                if (canon == CanonicalEntityIds.WorldCup) return 2;
                if (canon == CanonicalEntityIds.PremierLeague) return 3;
                if (canon == CanonicalEntityIds.LaLiga) return 4;
                if (canon == CanonicalEntityIds.AfricaCupOfNations) return 10;
                if (canon == CanonicalEntityIds.EgyptianPremierLeague) return 12;
            }

            if (compId.Contains("135") || compId.Equals("comp_sa", StringComparison.OrdinalIgnoreCase)) return 5;
            if (compId.Contains("78") || compId.Equals("comp_bl1", StringComparison.OrdinalIgnoreCase)) return 6;
            if (compId.Contains("61") || compId.Equals("comp_fl1", StringComparison.OrdinalIgnoreCase)) return 7;
            if (compId.Contains("3") || compId.Equals("comp_uel", StringComparison.OrdinalIgnoreCase)) return 8;
            if (compId.Contains("848") || compId.Equals("comp_uecl", StringComparison.OrdinalIgnoreCase)) return 9;
            if (compId.Contains("9") || compId.Equals("comp_ca", StringComparison.OrdinalIgnoreCase)) return 11;
            if (compId.Contains("307") || compId.Equals("comp_spl", StringComparison.OrdinalIgnoreCase)) return 13;
        }

        if (!string.IsNullOrWhiteSpace(compName))
        {
            if (compName.Contains("Champions League", StringComparison.OrdinalIgnoreCase) || compName.Contains("UCL", StringComparison.OrdinalIgnoreCase)) return 1;
            if (compName.Contains("World Cup", StringComparison.OrdinalIgnoreCase)) return 2;
            if (compName.Contains("Premier League", StringComparison.OrdinalIgnoreCase)) return 3;
            if (compName.Contains("La Liga", StringComparison.OrdinalIgnoreCase) || compName.Contains("Primera División", StringComparison.OrdinalIgnoreCase)) return 4;
            if (compName.Contains("Serie A", StringComparison.OrdinalIgnoreCase)) return 5;
            if (compName.Contains("Bundesliga", StringComparison.OrdinalIgnoreCase)) return 6;
            if (compName.Contains("Ligue 1", StringComparison.OrdinalIgnoreCase)) return 7;
            if (compName.Contains("Europa League", StringComparison.OrdinalIgnoreCase)) return 8;
            if (compName.Contains("Conference League", StringComparison.OrdinalIgnoreCase)) return 9;
            if (compName.Contains("Africa Cup", StringComparison.OrdinalIgnoreCase) || compName.Contains("AFCON", StringComparison.OrdinalIgnoreCase)) return 10;
            if (compName.Contains("Copa America", StringComparison.OrdinalIgnoreCase)) return 11;
            if (compName.Contains("Egyptian", StringComparison.OrdinalIgnoreCase)) return 12;
            if (compName.Contains("Saudi", StringComparison.OrdinalIgnoreCase)) return 13;
            if (compName.Contains("Cup", StringComparison.OrdinalIgnoreCase) || compName.Contains("Copa", StringComparison.OrdinalIgnoreCase)) return 14;
        }

        return 100;
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
                string score = $"{pinnedMatch.Score?.Home ?? 0}–{pinnedMatch.Score?.Away ?? 0}";
                string time = pinnedMatch.State == "HT" ? "HT" : (pinnedMatch.ElapsedMinute > 0 ? $"{pinnedMatch.ElapsedMinute}'" : pinnedMatch.State);
                ActiveTickerText = $"📌 Pinned: {home} {score} {away} ({time})";
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
            var activeMatch = TrayTickerFormatter.GetActiveMatch(_latestTrayData, prefs);
            ActiveMatchId = activeMatch?.Id;

            if (activeMatch != null)
            {
                string home = activeMatch.HomeTeam?.ShortName ?? activeMatch.HomeTeam?.Name ?? "Home";
                string away = activeMatch.AwayTeam?.ShortName ?? activeMatch.AwayTeam?.Name ?? "Away";
                string score = $"{activeMatch.Score?.Home ?? 0}–{activeMatch.Score?.Away ?? 0}";
                string time = activeMatch.State == "HT" ? "HT" : (activeMatch.ElapsedMinute > 0 ? $"{activeMatch.ElapsedMinute}'" : activeMatch.State);
                ActiveTickerText = $"{home} {score} {away} ({time})";
            }
            else
            {
                ActiveTickerText = prefs?.EnableTickerCarousel == true
                    ? "📌 Auto-Rotate Ticker (Carousel Mode)"
                    : "📌 No pinned matches";
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
            bool isActive = match.Id == ActiveMatchId;
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
                isActive,
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

        // Sort rules: Followed matches at the top (or IsRelevant == true), grouped by CompetitionName, then sorted by KickoffTime ascending.
        var sortedFollowed = filteredByTab
            .Where(m => m.IsRelevant || m.FollowedByTeam || m.FollowedByCompetition)
            .OrderByDescending(m => m.FollowedByTeam)
            .ThenBy(m => m.CompetitionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.KickoffTime)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        var sortedOther = _activeFilter == MatchFilterType.Following
            ? new List<MatchItemViewModel>()
            : filteredByTab
                .Where(m => !m.IsRelevant && !m.FollowedByTeam && !m.FollowedByCompetition)
                .OrderBy(m => GetCompetitionPopularityRank(m.CompetitionId, m.CompetitionName))
                .ThenBy(m => m.CompetitionName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.KickoffTime)
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
                string key = !string.IsNullOrWhiteSpace(compId) ? compId : compName;
                var matchesList = g.ToList();

                return new
                {
                    Key = key,
                    CompId = compId,
                    CompName = compName,
                    Country = country,
                    LogoUrl = logoUrl,
                    HasFollowedTeams = hasFollowedTeams,
                    IsFollowedCompetition = isFollowedCompetition,
                    IsFollowed = hasFollowedTeams || isFollowedCompetition,
                    Matches = matchesList
                };
            })
            .OrderByDescending(g => g.IsFollowed)
            .ThenBy(g => GetCompetitionPopularityRank(g.CompId, g.CompName))
            .ThenBy(g => g.CompName, StringComparer.OrdinalIgnoreCase)
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
            if (dateFiltered.Count == 0)
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
        SearchCatalog.Dispose();
        Settings?.Dispose();
        _preferencesService.PreferencesChanged -= OnPreferencesChanged;
        if (_syncService != null)
        {
            _syncService.TrayUpdated -= OnTrayUpdated;
            _syncService.SyncStateChanged -= OnSyncStateChanged;
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
