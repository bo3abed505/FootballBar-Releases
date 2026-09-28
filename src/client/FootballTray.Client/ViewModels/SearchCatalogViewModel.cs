using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.ViewModels;

public enum CatalogSearchScope
{
    Teams,
    Competitions
}

/// <summary>
/// ViewModel managing catalog search, 300ms debouncing, followed clubs/leagues,
/// image cache preloading, and preferences persistence with thread-safe UI marshaling.
/// </summary>
public sealed class SearchCatalogViewModel : INotifyPropertyChanged, IDisposable
{
    public const int DebounceDelayMs = 300;

    private readonly FootballApiClient _apiClient;
    private readonly IPreferencesService _preferencesService;
    private readonly ImageCacheService _imageCacheService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayFunc;

    private string _searchQuery = string.Empty;
    private CatalogSearchScope _selectedScope = CatalogSearchScope.Teams;
    private bool _isSearching;
    private string? _searchStatusMessage;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public ObservableCollection<FollowedTeamItemViewModel> TeamSearchResults { get; } = new();
    public ObservableCollection<FollowedCompetitionItemViewModel> CompetitionSearchResults { get; } = new();
    public ObservableCollection<FollowedTeamItemViewModel> FollowedTeams { get; } = new();
    public ObservableCollection<FollowedCompetitionItemViewModel> FollowedCompetitions { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchCatalogViewModel(
        FootballApiClient apiClient,
        IPreferencesService preferencesService,
        ImageCacheService? imageCacheService = null,
        DispatcherQueue? dispatcherQueue = null,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _imageCacheService = imageCacheService ?? ImageCacheService.Instance;
        _dispatcherQueue = dispatcherQueue;
        _delayFunc = delayFunc ?? Task.Delay;

        _preferencesService.PreferencesChanged += OnPreferencesChanged;
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnSearchQueryChanged(value);
            }
        }
    }

    public CatalogSearchScope SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (SetProperty(ref _selectedScope, value))
            {
                OnPropertyChanged(nameof(IsTeamsScopeSelected));
                OnPropertyChanged(nameof(IsCompetitionsScopeSelected));

                if (!string.IsNullOrWhiteSpace(_searchQuery))
                {
                    OnSearchQueryChanged(_searchQuery);
                }
            }
        }
    }

    public bool IsTeamsScopeSelected
    {
        get => _selectedScope == CatalogSearchScope.Teams;
        set
        {
            if (value && _selectedScope != CatalogSearchScope.Teams)
            {
                SelectedScope = CatalogSearchScope.Teams;
            }
        }
    }

    public bool IsCompetitionsScopeSelected
    {
        get => _selectedScope == CatalogSearchScope.Competitions;
        set
        {
            if (value && _selectedScope != CatalogSearchScope.Competitions)
            {
                SelectedScope = CatalogSearchScope.Competitions;
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public string? SearchStatusMessage
    {
        get => _searchStatusMessage;
        private set
        {
            if (SetProperty(ref _searchStatusMessage, value))
            {
                OnPropertyChanged(nameof(HasSearchStatusMessage));
            }
        }
    }

    public bool HasSearchStatusMessage => !string.IsNullOrWhiteSpace(_searchStatusMessage);
    public bool HasTeamSearchResults => TeamSearchResults.Count > 0;
    public bool HasCompetitionSearchResults => CompetitionSearchResults.Count > 0;
    public bool HasFollowedTeams => FollowedTeams.Count > 0;
    public bool HasFollowedCompetitions => FollowedCompetitions.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var prefs = await _preferencesService.LoadAsync(cancellationToken);
        UpdateFollowedCollectionsFromPreferences(prefs);
    }

    private void OnSearchQueryChanged(string query)
    {
        try
        {
            _debounceCts?.Cancel();
        }
        catch
        {
        }
        _debounceCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(query))
        {
            RunOnUi(() =>
            {
                TeamSearchResults.Clear();
                CompetitionSearchResults.Clear();
                IsSearching = false;
                SearchStatusMessage = null;
                NotifyCollectionCounts();
            });
            return;
        }

        var token = _debounceCts.Token;
        _ = TriggerDebouncedSearchAsync(query, token);
    }

    private async Task TriggerDebouncedSearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            await _delayFunc(TimeSpan.FromMilliseconds(DebounceDelayMs), cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            await ExecuteSearchAsync(query, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on fast typing
        }
    }

    public async Task ExecuteSearchAsync(string? explicitQuery = null, CancellationToken cancellationToken = default)
    {
        string query = (explicitQuery ?? _searchQuery).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            RunOnUi(() =>
            {
                TeamSearchResults.Clear();
                CompetitionSearchResults.Clear();
                IsSearching = false;
                SearchStatusMessage = null;
                NotifyCollectionCounts();
            });
            return;
        }

        RunOnUi(() =>
        {
            IsSearching = true;
            SearchStatusMessage = null;
        });

        try
        {
            if (_selectedScope == CatalogSearchScope.Teams)
            {
                var response = await _apiClient.SearchTeamsAsync(query, 20, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;

                RunOnUi(() =>
                {
                    TeamSearchResults.Clear();
                    if (response.IsSuccess && response.Envelope?.Data != null)
                    {
                        var followedIds = new HashSet<string>(FollowedTeams.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
                        var uniqueTeams = response.Envelope.Data.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var dto in uniqueTeams)
                        {
                            bool isFollowed = followedIds.Contains(dto.Id) ||
                                              (dto.LegacyId != null && followedIds.Contains(dto.LegacyId));
                            var item = FollowedTeamItemViewModel.FromCatalogDto(dto, isFollowed);
                            _ = item.EnsureLogoCachedAsync(_imageCacheService);
                            TeamSearchResults.Add(item);
                        }

                        if (TeamSearchResults.Count == 0)
                        {
                            SearchStatusMessage = $"No clubs found matching \"{query}\".";
                        }
                    }
                    else
                    {
                        SearchStatusMessage = response.ErrorMessage ?? "Search request failed.";
                    }

                    IsSearching = false;
                    NotifyCollectionCounts();
                });
            }
            else
            {
                var response = await _apiClient.SearchCompetitionsAsync(query, 20, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;

                RunOnUi(() =>
                {
                    CompetitionSearchResults.Clear();
                    if (response.IsSuccess && response.Envelope?.Data != null)
                    {
                        var followedIds = new HashSet<string>(FollowedCompetitions.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
                        var uniqueComps = response.Envelope.Data.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase);
                        foreach (var dto in uniqueComps)
                        {
                            bool isFollowed = followedIds.Contains(dto.Id) ||
                                              (dto.LegacyId != null && followedIds.Contains(dto.LegacyId));
                            var item = FollowedCompetitionItemViewModel.FromCatalogDto(dto, isFollowed);
                            _ = item.EnsureLogoCachedAsync(_imageCacheService);
                            CompetitionSearchResults.Add(item);
                        }

                        if (CompetitionSearchResults.Count == 0)
                        {
                            SearchStatusMessage = $"No competitions found matching \"{query}\".";
                        }
                    }
                    else
                    {
                        SearchStatusMessage = response.ErrorMessage ?? "Search request failed.";
                    }

                    IsSearching = false;
                    NotifyCollectionCounts();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                IsSearching = false;
                SearchStatusMessage = $"Search error: {ex.Message}";
            });
        }
    }

    public async Task FollowTeamAsync(FollowedTeamItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _preferencesService.FollowTeamAsync(item.ToDomain(), cancellationToken);

        RunOnUi(() =>
        {
            item.IsFollowed = true;
            if (!FollowedTeams.Any(t => string.Equals(t.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var followedItem = FollowedTeamItemViewModel.FromDomain(item.ToDomain(), isFollowed: true);
                _ = followedItem.EnsureLogoCachedAsync(_imageCacheService);
                FollowedTeams.Add(followedItem);
            }

            var matchingResult = TeamSearchResults.FirstOrDefault(t => string.Equals(t.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (matchingResult != null)
            {
                matchingResult.IsFollowed = true;
            }

            NotifyCollectionCounts();
        });
    }

    public async Task UnfollowTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;

        await _preferencesService.UnfollowTeamAsync(teamId, cancellationToken);

        RunOnUi(() =>
        {
            var existing = FollowedTeams.FirstOrDefault(t => string.Equals(t.Id, teamId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                FollowedTeams.Remove(existing);
            }

            var matchingResult = TeamSearchResults.FirstOrDefault(t => string.Equals(t.Id, teamId, StringComparison.OrdinalIgnoreCase));
            if (matchingResult != null)
            {
                matchingResult.IsFollowed = false;
            }

            NotifyCollectionCounts();
        });
    }

    public async Task FollowCompetitionAsync(FollowedCompetitionItemViewModel item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _preferencesService.FollowCompetitionAsync(item.ToDomain(), cancellationToken);

        RunOnUi(() =>
        {
            item.IsFollowed = true;
            if (!FollowedCompetitions.Any(c => string.Equals(c.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var followedItem = FollowedCompetitionItemViewModel.FromDomain(item.ToDomain(), isFollowed: true);
                _ = followedItem.EnsureLogoCachedAsync(_imageCacheService);
                FollowedCompetitions.Add(followedItem);
            }

            var matchingResult = CompetitionSearchResults.FirstOrDefault(c => string.Equals(c.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (matchingResult != null)
            {
                matchingResult.IsFollowed = true;
            }

            NotifyCollectionCounts();
        });
    }

    public async Task UnfollowCompetitionAsync(string compId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(compId)) return;

        await _preferencesService.UnfollowCompetitionAsync(compId, cancellationToken);

        RunOnUi(() =>
        {
            var existing = FollowedCompetitions.FirstOrDefault(c => string.Equals(c.Id, compId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                FollowedCompetitions.Remove(existing);
            }

            var matchingResult = CompetitionSearchResults.FirstOrDefault(c => string.Equals(c.Id, compId, StringComparison.OrdinalIgnoreCase));
            if (matchingResult != null)
            {
                matchingResult.IsFollowed = false;
            }

            NotifyCollectionCounts();
        });
    }

    private void OnPreferencesChanged(object? sender, UserPreferences newPrefs)
    {
        UpdateFollowedCollectionsFromPreferences(newPrefs);
    }

    private void UpdateFollowedCollectionsFromPreferences(UserPreferences prefs)
    {
        RunOnUi(() =>
        {
            // Synchronize FollowedTeams
            var followedTeamIds = new HashSet<string>(prefs.FollowedTeams.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);

            // Remove no-longer-followed teams
            for (int i = FollowedTeams.Count - 1; i >= 0; i--)
            {
                if (!followedTeamIds.Contains(FollowedTeams[i].Id))
                {
                    FollowedTeams.RemoveAt(i);
                }
            }

            // Add new followed teams
            foreach (var team in prefs.FollowedTeams)
            {
                var existing = FollowedTeams.FirstOrDefault(t => string.Equals(t.Id, team.Id, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var item = FollowedTeamItemViewModel.FromDomain(team, isFollowed: true);
                    _ = item.EnsureLogoCachedAsync(_imageCacheService);
                    FollowedTeams.Add(item);
                }
            }

            // Update IsFollowed flags in current search results
            foreach (var searchResult in TeamSearchResults)
            {
                searchResult.IsFollowed = followedTeamIds.Contains(searchResult.Id);
            }

            // Synchronize FollowedCompetitions
            var followedCompIds = new HashSet<string>(prefs.FollowedCompetitions.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            for (int i = FollowedCompetitions.Count - 1; i >= 0; i--)
            {
                if (!followedCompIds.Contains(FollowedCompetitions[i].Id))
                {
                    FollowedCompetitions.RemoveAt(i);
                }
            }

            foreach (var comp in prefs.FollowedCompetitions)
            {
                var existing = FollowedCompetitions.FirstOrDefault(c => string.Equals(c.Id, comp.Id, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var item = FollowedCompetitionItemViewModel.FromDomain(comp, isFollowed: true);
                    _ = item.EnsureLogoCachedAsync(_imageCacheService);
                    FollowedCompetitions.Add(item);
                }
            }

            foreach (var searchResult in CompetitionSearchResults)
            {
                searchResult.IsFollowed = followedCompIds.Contains(searchResult.Id);
            }

            NotifyCollectionCounts();
        });
    }

    private void NotifyCollectionCounts()
    {
        OnPropertyChanged(nameof(HasTeamSearchResults));
        OnPropertyChanged(nameof(HasCompetitionSearchResults));
        OnPropertyChanged(nameof(HasFollowedTeams));
        OnPropertyChanged(nameof(HasFollowedCompetitions));
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
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
    }
}
