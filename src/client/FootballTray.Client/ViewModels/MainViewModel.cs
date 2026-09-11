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

    public ObservableCollection<MatchItemViewModel> FilteredMatches { get; } = new();
    public ObservableCollection<FollowedTeamItemViewModel> AvailableTeams { get; } = new();
    public ObservableCollection<FollowedCompetitionItemViewModel> AvailableCompetitions { get; } = new();
    public ObservableCollection<NotificationCategoryItemViewModel> NotificationCategories { get; } = new();

    public Diagnostics.IDiagnosticsService DiagnosticsService => _diagnosticsService;
    public IUpdateDiscoveryService? UpdateDiscoveryService => _updateDiscoveryService;
    public bool HasUpdateCapability => _updateDiscoveryService != null;

    public string DiagnosticsVersionText => $"Version {_diagnosticsService.GetCurrentSnapshot().Version} ({_diagnosticsService.GetCurrentSnapshot().Architecture})";
    public string DiagnosticsHealthStatusText => $"{_diagnosticsService.GetCurrentSnapshot().HealthState} ({_diagnosticsService.GetCurrentSnapshot().FreshnessState})";
    public string DiagnosticsLastSyncText => _diagnosticsService.GetCurrentSnapshot().LastSuccessfulServerResponseTime?.ToString("HH:mm:ss 'UTC'") ?? "Never";
    public string DiagnosticsTaskbarText => _diagnosticsService.GetCurrentSnapshot().TaskbarHostStatus;
    public string DiagnosticsReportText => _diagnosticsService.GetSanitizedReportText();

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
        private set => SetProperty(ref _isUpdateAvailable, value);
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
        private set => SetProperty(ref _offeredManifest, value);
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(
        IPreferencesService preferencesService,
        ISyncService? syncService = null,
        Diagnostics.IDiagnosticsService? diagnosticsService = null,
        DispatcherQueue? dispatcherQueue = null,
        IUpdateDiscoveryService? updateDiscoveryService = null,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null)
    {
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _syncService = syncService;
        _diagnosticsService = diagnosticsService ?? new Diagnostics.DiagnosticsService(_syncService, _preferencesService);
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _updateDiscoveryService = updateDiscoveryService;
        _delayFunc = delayFunc ?? Task.Delay;

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
            if (!standardTeams.Any(t => t.Id.Equals(followed.Id, StringComparison.OrdinalIgnoreCase)))
            {
                standardTeams.Add(followed);
            }
        }

        var followedTeamIds = prefs.GetFollowedTeamIdSet();
        foreach (var team in standardTeams)
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
            if (!standardComps.Any(c => c.Id.Equals(followed.Id, StringComparison.OrdinalIgnoreCase)))
            {
                standardComps.Add(followed);
            }
        }

        var followedCompIds = prefs.GetFollowedCompetitionIdSet();
        foreach (var comp in standardComps)
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
            viewModels.Add(MatchItemViewModel.FromDto(match, relevance, isActive));
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

            if (_inFlightCheckTask == null)
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
                LatestVersion = result.TargetVersion?.ToString() ?? result.OfferedManifest?.Version;
                UpdateReleaseNotes = result.OfferedManifest?.ReleaseNotes;
                OfferedManifest = result.OfferedManifest;
                IsUpdateAvailable = true;
                UpdateStatusMessage = isManual
                    ? $"A newer version v{LatestVersion} is available."
                    : $"Update available: FootballTray v{LatestVersion}";
                break;

            case UpdateCheckState.UpToDate:
                IsUpdateAvailable = false;
                OfferedManifest = null;
                if (isManual)
                {
                    UpdateStatusMessage = "FootballTray is up to date.";
                }
                break;

            case UpdateCheckState.CheckFailed:
                IsUpdateAvailable = false;
                OfferedManifest = null;
                if (isManual)
                {
                    UpdateStatusMessage = $"Check failed: {result.Message}";
                }
                break;
        }

        IsCheckingForUpdates = false;
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
