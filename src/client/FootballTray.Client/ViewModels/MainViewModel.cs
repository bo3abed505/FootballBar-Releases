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
                ApplyCurrentFilter();
            }
        }
    }

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
        private set => SetProperty(ref _activeTickerText, value);
    }

    public string? ActiveMatchId
    {
        get => _activeMatchId;
        private set => SetProperty(ref _activeMatchId, value);
    }

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

        _preferencesService.PreferencesChanged += OnPreferencesChanged;

        if (_syncService != null)
        {
            _syncService.TrayUpdated += OnTrayUpdated;
        }
    }

    public string CopyDiagnosticsToClipboard()
    {
        string report = _diagnosticsService.GetSanitizedReportText();
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
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
        var standardTeams = new List<FollowedTeam>
        {
            DefaultPreferences.Barcelona,
            DefaultPreferences.AlAhly
        };

        // Also add any extra teams in saved preferences
        foreach (var followed in prefs.FollowedTeams)
        {
            if (!standardTeams.Any(t => t.Id.Equals(followed.Id, StringComparison.OrdinalIgnoreCase) ||
                                        (!string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(followed.Name) && string.Equals(t.Name.Trim(), followed.Name.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                standardTeams.Add(followed);
            }
        }

        var followedTeamIds = prefs.GetFollowedTeamIdSet();
        var dedupedTeams = standardTeams.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var team in dedupedTeams)
        {
            bool isFollowed = followedTeamIds.Contains(team.Id);
            var existing = AvailableTeams.FirstOrDefault(t => t.Id.Equals(team.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsFollowed = isFollowed;
            }
            else
            {
                AvailableTeams.Add(FollowedTeamItemViewModel.FromDomain(team, isFollowed));
            }
        }

        // 2. Competitions
        var standardComps = new List<FollowedCompetition>
        {
            DefaultPreferences.PremierLeague,
            DefaultPreferences.LaLiga,
            DefaultPreferences.EgyptianPremierLeague,
            DefaultPreferences.ChampionsLeague,
            DefaultPreferences.AfricaCupOfNations,
            DefaultPreferences.WorldCup
        };

        // Also add any extra competitions in saved preferences
        foreach (var followed in prefs.FollowedCompetitions)
        {
            if (!standardComps.Any(c => c.Id.Equals(followed.Id, StringComparison.OrdinalIgnoreCase) ||
                                        (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(followed.Name) && string.Equals(c.Name.Trim(), followed.Name.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                standardComps.Add(followed);
            }
        }

        var followedCompIds = prefs.GetFollowedCompetitionIdSet();
        var dedupedComps = standardComps.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var comp in dedupedComps)
        {
            bool isFollowed = followedCompIds.Contains(comp.Id);
            var existing = AvailableCompetitions.FirstOrDefault(c => c.Id.Equals(comp.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsFollowed = isFollowed;
            }
            else
            {
                AvailableCompetitions.Add(FollowedCompetitionItemViewModel.FromDomain(comp, isFollowed));
            }
        }

        // 3. Notification Categories (all 7 canonical NotificationType members)
        var allTypes = new[]
        {
            NotificationType.MatchStarted,
            NotificationType.Goal,
            NotificationType.HalfTime,
            NotificationType.MatchFinished,
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

    public async Task ToggleTeamFollowAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;

        var teamVm = AvailableTeams.FirstOrDefault(t => t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
        if (teamVm == null) return;

        bool targetFollow = !teamVm.IsFollowed;
        try
        {
            if (targetFollow)
            {
                await _preferencesService.FollowTeamAsync(teamVm.ToDomain());
            }
            else
            {
                await _preferencesService.UnfollowTeamAsync(teamId);
            }

            teamVm.IsFollowed = targetFollow;
            StatusMessage = null;
            ReapplyRelevanceAndFilters();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to update followed team: {ex.Message}";
        }
    }

    public async Task ToggleCompetitionFollowAsync(string compId)
    {
        if (string.IsNullOrWhiteSpace(compId)) return;

        var compVm = AvailableCompetitions.FirstOrDefault(c => c.Id.Equals(compId, StringComparison.OrdinalIgnoreCase));
        if (compVm == null) return;

        bool targetFollow = !compVm.IsFollowed;
        try
        {
            if (targetFollow)
            {
                await _preferencesService.FollowCompetitionAsync(compVm.ToDomain());
            }
            else
            {
                await _preferencesService.UnfollowCompetitionAsync(compId);
            }

            compVm.IsFollowed = targetFollow;
            StatusMessage = null;
            ReapplyRelevanceAndFilters();
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

    private void OnTrayUpdated(object? sender, TrayUpdateEventArgs e)
    {
        RunOnUiSafe(() =>
        {
            _latestTrayData = e.TrayData;
            RefreshMatches(e.TrayData, e.FreshnessState, e.ConnectionState);
        }, nameof(OnTrayUpdated));
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
        _latestTrayData = trayData;

        // Update connection status
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
                ConnectionStatusText = freshnessState == DataFreshnessState.Fresh ? "Online" : "Offline";
                IsOffline = freshnessState != DataFreshnessState.Fresh;
                break;
        }

        ApplyCurrentFilter();
    }

    public void UnpinMatch(string? matchId = null)
    {
        App.CurrentTickerCoordinator?.UnpinMatch();
        ActiveMatchId = null;
        ActiveTickerText = "Taskbar match unpinned";

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
        foreach (var match in FilteredMatches)
        {
            match.IsActiveTaskbarMatch = !string.IsNullOrEmpty(pinnedMatchId)
                ? string.Equals(match.Id, pinnedMatchId, StringComparison.Ordinal)
                : string.Equals(match.Id, ActiveMatchId, StringComparison.Ordinal);
        }
    }

    private void ReapplyRelevanceAndFilters()
    {
        ApplyCurrentFilter();
    }

    private void ApplyCurrentFilter()
    {
        var prefs = _preferencesService.GetPreferences();
        var allMatches = _latestTrayData?.Matches ?? new List<TrayMatchDto>();

        // Prioritize and determine active taskbar match
        var activeMatch = TrayTickerFormatter.GetActiveMatch(_latestTrayData, prefs);
        ActiveMatchId = activeMatch?.Id;

        if (activeMatch != null)
        {
            string home = activeMatch.HomeTeam?.ShortName ?? activeMatch.HomeTeam?.Name ?? "Home";
            string away = activeMatch.AwayTeam?.ShortName ?? activeMatch.AwayTeam?.Name ?? "Away";
            string score = $"{activeMatch.Score?.Home ?? 0}–{activeMatch.Score?.Away ?? 0}";
            string time = activeMatch.State == "HT" ? "HT" : (activeMatch.ElapsedMinute > 0 ? $"{activeMatch.ElapsedMinute}'" : activeMatch.State);
            ActiveTickerText = $"Taskbar Active: {home} {score} {away} ({time})";
        }
        else
        {
            ActiveTickerText = allMatches.Count > 0 ? "No prioritized match for taskbar" : "No matches synchronized";
        }

        // Evaluate relevance for all matches
        var prioritizedList = FixtureRelevanceEngine.PrioritizeMatches(allMatches, prefs);
        var viewModels = new List<MatchItemViewModel>();

        foreach (var match in prioritizedList)
        {
            var relevance = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);
            bool isActive = match.Id == ActiveMatchId;
            viewModels.Add(MatchItemViewModel.FromDto(match, relevance, isActive, prefs?.Use24HourFormat ?? true));
        }

        // Apply selected filter
        IEnumerable<MatchItemViewModel> filtered = _activeFilter switch
        {
            MatchFilterType.Following => viewModels.Where(m => m.IsRelevant),
            MatchFilterType.Live => viewModels.Where(m => m.IsLive),
            MatchFilterType.Upcoming => viewModels.Where(m => m.IsPrematch),
            MatchFilterType.Finished => viewModels.Where(m => m.IsFinished),
            _ => viewModels
        };

        var filteredList = filtered.ToList();

        // Update ObservableCollection in-place or replacement to minimize UI thrashing
        FilteredMatches.Clear();
        foreach (var item in filteredList)
        {
            FilteredMatches.Add(item);
        }

        HasMatches = FilteredMatches.Count > 0;
        IsEmpty = !HasMatches;

        // Update empty state messages
        if (IsEmpty)
        {
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
                EmptyStateMessage = "There are no upcoming prematch fixtures scheduled for today.";
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
        _preferencesService.PreferencesChanged -= OnPreferencesChanged;
        if (_syncService != null)
        {
            _syncService.TrayUpdated -= OnTrayUpdated;
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
}
