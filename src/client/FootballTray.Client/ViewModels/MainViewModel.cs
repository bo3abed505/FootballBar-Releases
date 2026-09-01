using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FootballTray.Core.Dto;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Root view model coordinating Match Discovery, Followed Preferences, Notification Controls, and Taskbar status.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISyncService? _syncService;
    private readonly IPreferencesService _preferencesService;

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

    public ObservableCollection<MatchItemViewModel> FilteredMatches { get; } = new();
    public ObservableCollection<FollowedTeamItemViewModel> AvailableTeams { get; } = new();
    public ObservableCollection<FollowedCompetitionItemViewModel> AvailableCompetitions { get; } = new();
    public ObservableCollection<NotificationCategoryItemViewModel> NotificationCategories { get; } = new();

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

    public MainViewModel(IPreferencesService preferencesService, ISyncService? syncService = null)
    {
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _syncService = syncService;

        _preferencesService.PreferencesChanged += OnPreferencesChanged;

        if (_syncService != null)
        {
            _syncService.TrayUpdated += OnTrayUpdated;
        }
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

    private void OnTrayUpdated(object? sender, TrayUpdateEventArgs e)
    {
        _latestTrayData = e.TrayData;
        RefreshMatches(e.TrayData, e.FreshnessState, e.ConnectionState);
    }

    private void OnPreferencesChanged(object? sender, UserPreferences prefs)
    {
        PopulatePreferenceCollections(prefs);
        ReapplyRelevanceAndFilters();
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

    public void Dispose()
    {
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
