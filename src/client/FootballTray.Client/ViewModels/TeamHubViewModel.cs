using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.ViewModels;

public sealed class TeamSquadPlayerItemViewModel
{
    private static readonly SolidColorBrush EliteBg = new(Color.FromArgb(255, 14, 165, 233));   // #0EA5E9
    private static readonly SolidColorBrush GoodBg = new(Color.FromArgb(255, 34, 197, 94));     // #22C55E
    private static readonly SolidColorBrush AverageBg = new(Color.FromArgb(255, 245, 158, 11)); // #F59E0B
    private static readonly SolidColorBrush PoorBg = new(Color.FromArgb(255, 239, 68, 68));    // #EF4444
    private static readonly SolidColorBrush NoneBg = new(Color.FromArgb(0, 0, 0, 0));

    public TeamSquadPlayerDto Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string PositionGroup => Model.PositionGroup;
    public int? ShirtNumber => Model.ShirtNumber;
    public string ShirtNumberDisplay => Model.ShirtNumber.HasValue ? $"#{Model.ShirtNumber.Value}" : "-";
    public string CountryCode => Model.CountryCode ?? "";
    public double? Rating => Model.Rating;
    public string RatingDisplay => Model.Rating.HasValue ? Model.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture) : "-";
    public int Goals => Model.Goals ?? 0;
    public int Assists => Model.Assists ?? 0;
    public int YellowCards => Model.YellowCards ?? 0;
    public int RedCards => Model.RedCards ?? 0;

    public bool HasRating => Model.Rating.HasValue && Model.Rating.Value > 0;
    public Visibility RatingVisibility => HasRating ? Visibility.Visible : Visibility.Collapsed;

    public SolidColorBrush RatingBackgroundBrush => Model.Rating switch
    {
        >= 8.0 => EliteBg,
        >= 7.0 => GoodBg,
        >= 6.0 => AverageBg,
        > 0 => PoorBg,
        _ => NoneBg
    };

    public string StatsSummary
    {
        get
        {
            var parts = new List<string>();
            if (Goals > 0) parts.Add($"{Goals}G");
            if (Assists > 0) parts.Add($"{Assists}A");
            if (YellowCards > 0) parts.Add($"{YellowCards}Y");
            if (RedCards > 0) parts.Add($"{RedCards}R");
            return parts.Count > 0 ? string.Join(" • ", parts) : "";
        }
    }

    public Visibility StatsVisibility => !string.IsNullOrEmpty(StatsSummary) ? Visibility.Visible : Visibility.Collapsed;

    public TeamSquadPlayerItemViewModel(TeamSquadPlayerDto model)
    {
        Model = model;
    }
}

public class TeamHubViewModel : INotifyPropertyChanged
{
    private readonly FootballApiClient _apiClient;
    private readonly IPreferencesService? _preferencesService;
    private CancellationTokenSource? _loadingCts;

    private string _teamId = string.Empty;
    private string _teamName = string.Empty;
    private string _shortName = string.Empty;
    private string _teamLogoUrl = string.Empty;
    private string _countryText = string.Empty;
    private string _primaryLeagueId = string.Empty;
    private string _primaryLeagueName = string.Empty;
    private string _primaryLeagueLogoUrl = string.Empty;
    private string _stadiumName = string.Empty;
    private string _coachName = string.Empty;

    private int _subTabIndex = 0;
    private bool _isLoading;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private bool _isFollowed;

    private string _topScorerName = string.Empty;
    private string _topScorerStat = string.Empty;
    private string _topAssisterName = string.Empty;
    private string _topAssisterStat = string.Empty;
    private string _topRatedName = string.Empty;
    private string _topRatedStat = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FormBadgeItemViewModel> RecentFormBadges { get; } = new();
    public ObservableCollection<CompetitionFixtureViewModel> UpcomingFixtures { get; } = new();
    public ObservableCollection<CompetitionFixtureViewModel> RecentResults { get; } = new();
    public ObservableCollection<StandingRowItemViewModel> Standings { get; } = new();
    public ObservableCollection<TeamSquadPlayerItemViewModel> Goalkeepers { get; } = new();
    public ObservableCollection<TeamSquadPlayerItemViewModel> Defenders { get; } = new();
    public ObservableCollection<TeamSquadPlayerItemViewModel> Midfielders { get; } = new();
    public ObservableCollection<TeamSquadPlayerItemViewModel> Forwards { get; } = new();

    public TeamHubViewModel(FootballApiClient? apiClient = null, IPreferencesService? preferencesService = null)
    {
        _apiClient = apiClient ?? App.CurrentApiClient ?? new FootballApiClient();
        _preferencesService = preferencesService ?? App.CurrentPreferencesService;
        if (_preferencesService != null)
        {
            _preferencesService.PreferencesChanged += OnPreferencesChanged;
        }
    }

    private void OnPreferencesChanged(object? sender, UserPreferences prefs)
    {
        bool use24 = prefs.Use24HourFormat;
        foreach (var fix in UpcomingFixtures)
        {
            fix.Use24HourTime = use24;
        }
        foreach (var fix in RecentResults)
        {
            fix.Use24HourTime = use24;
        }
    }

    #region Properties

    public string TeamId
    {
        get => _teamId;
        set => SetProperty(ref _teamId, value);
    }

    public string TeamName
    {
        get => _teamName;
        set => SetProperty(ref _teamName, value);
    }

    public string ShortName
    {
        get => _shortName;
        set => SetProperty(ref _shortName, value);
    }

    public string TeamLogoUrl
    {
        get => _teamLogoUrl;
        set
        {
            if (SetProperty(ref _teamLogoUrl, value))
            {
                OnPropertyChanged(nameof(TeamLogoSource));
                OnPropertyChanged(nameof(HasTeamLogo));
            }
        }
    }

    public ImageSource? TeamLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_teamLogoUrl)) return null;
            try
            {
                if (Uri.TryCreate(_teamLogoUrl, UriKind.Absolute, out var uri))
                {
                    return new BitmapImage(uri);
                }
            }
            catch
            {
                // Headless/test fallback
            }
            return null;
        }
    }

    public bool HasTeamLogo => !string.IsNullOrWhiteSpace(_teamLogoUrl);

    public string CountryText
    {
        get => _countryText;
        set => SetProperty(ref _countryText, value);
    }

    public string PrimaryLeagueId
    {
        get => _primaryLeagueId;
        set => SetProperty(ref _primaryLeagueId, value);
    }

    public string PrimaryLeagueName
    {
        get => _primaryLeagueName;
        set
        {
            if (SetProperty(ref _primaryLeagueName, value))
            {
                OnPropertyChanged(nameof(HasPrimaryLeague));
            }
        }
    }

    public bool HasPrimaryLeague => !string.IsNullOrWhiteSpace(_primaryLeagueName);
    public Visibility HasPrimaryLeagueVisibility => HasPrimaryLeague ? Visibility.Visible : Visibility.Collapsed;

    public string PrimaryLeagueLogoUrl
    {
        get => _primaryLeagueLogoUrl;
        set
        {
            if (SetProperty(ref _primaryLeagueLogoUrl, value))
            {
                OnPropertyChanged(nameof(PrimaryLeagueLogoSource));
                OnPropertyChanged(nameof(HasPrimaryLeagueLogo));
                OnPropertyChanged(nameof(HasPrimaryLeagueLogoVisibility));
            }
        }
    }

    public ImageSource? PrimaryLeagueLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_primaryLeagueLogoUrl)) return null;
            try
            {
                if (Uri.TryCreate(_primaryLeagueLogoUrl, UriKind.Absolute, out var uri))
                {
                    return new BitmapImage(uri);
                }
            }
            catch
            {
                // Headless/test fallback
            }
            return null;
        }
    }

    public bool HasPrimaryLeagueLogo => !string.IsNullOrWhiteSpace(_primaryLeagueLogoUrl);
    public Visibility HasPrimaryLeagueLogoVisibility => HasPrimaryLeagueLogo ? Visibility.Visible : Visibility.Collapsed;

    public string StadiumName
    {
        get => _stadiumName;
        set
        {
            if (SetProperty(ref _stadiumName, value))
            {
                OnPropertyChanged(nameof(SubtitleText));
                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(HasSubtitleVisibility));
            }
        }
    }

    public string CoachName
    {
        get => _coachName;
        set
        {
            if (SetProperty(ref _coachName, value))
            {
                OnPropertyChanged(nameof(SubtitleText));
                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(HasSubtitleVisibility));
            }
        }
    }

    public string SubtitleText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_stadiumName)) parts.Add($"🏟️ {_stadiumName}");
            if (!string.IsNullOrWhiteSpace(_coachName)) parts.Add($"👔 {_coachName}");
            return string.Join("   •   ", parts);
        }
    }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(SubtitleText);
    public Visibility HasSubtitleVisibility => HasSubtitle ? Visibility.Visible : Visibility.Collapsed;

    public bool IsFollowed
    {
        get => _isFollowed;
        set
        {
            if (SetProperty(ref _isFollowed, value))
            {
                OnPropertyChanged(nameof(FollowButtonText));
                OnPropertyChanged(nameof(FollowIconGlyph));
                OnPropertyChanged(nameof(FollowButtonBackground));
                OnPropertyChanged(nameof(FollowButtonForeground));
            }
        }
    }

    public string FollowButtonText => IsFollowed ? "Following" : "+ Follow";
    public string FollowIconGlyph => IsFollowed ? "\uE73E" : "\uE710";
    public string FollowButtonBackground => IsFollowed ? "#2D38BDF8" : "#1E293B";
    public string FollowButtonForeground => IsFollowed ? "#38BDF8" : "#E2E8F0";

    public int SubTabIndex
    {
        get => _subTabIndex;
        set
        {
            if (SetProperty(ref _subTabIndex, value))
            {
                OnPropertyChanged(nameof(IsFixturesTabSelected));
                OnPropertyChanged(nameof(IsStandingsTabSelected));
                OnPropertyChanged(nameof(IsSquadTabSelected));
                OnPropertyChanged(nameof(FixturesSubTabVisibility));
                OnPropertyChanged(nameof(StandingsSubTabVisibility));
                OnPropertyChanged(nameof(SquadSubTabVisibility));
                OnPropertyChanged(nameof(FixturesTabBackgroundHex));
                OnPropertyChanged(nameof(FixturesTabForegroundHex));
                OnPropertyChanged(nameof(StandingsTabBackgroundHex));
                OnPropertyChanged(nameof(StandingsTabForegroundHex));
                OnPropertyChanged(nameof(SquadTabBackgroundHex));
                OnPropertyChanged(nameof(SquadTabForegroundHex));
            }
        }
    }

    public bool IsFixturesTabSelected => _subTabIndex == 0;
    public bool IsStandingsTabSelected => _subTabIndex == 1;
    public bool IsSquadTabSelected => _subTabIndex == 2;

    public Visibility FixturesSubTabVisibility => _subTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StandingsSubTabVisibility => _subTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SquadSubTabVisibility => _subTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

    public string FixturesTabBackgroundHex => _subTabIndex == 0 ? "#2538BDF8" : "Transparent";
    public string FixturesTabForegroundHex => _subTabIndex == 0 ? "#38BDF8" : "#94A3B8";
    public string StandingsTabBackgroundHex => _subTabIndex == 1 ? "#2538BDF8" : "Transparent";
    public string StandingsTabForegroundHex => _subTabIndex == 1 ? "#38BDF8" : "#94A3B8";
    public string SquadTabBackgroundHex => _subTabIndex == 2 ? "#2538BDF8" : "Transparent";
    public string SquadTabForegroundHex => _subTabIndex == 2 ? "#38BDF8" : "#94A3B8";

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(LoadingVisibility));
                OnPropertyChanged(nameof(ContentVisibility));
            }
        }
    }

    public Visibility LoadingVisibility => _isLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => !_isLoading && !_hasError ? Visibility.Visible : Visibility.Collapsed;

    public bool HasError
    {
        get => _hasError;
        set
        {
            if (SetProperty(ref _hasError, value))
            {
                OnPropertyChanged(nameof(ErrorVisibility));
                OnPropertyChanged(nameof(ContentVisibility));
            }
        }
    }

    public Visibility ErrorVisibility => _hasError && !_isLoading ? Visibility.Visible : Visibility.Collapsed;

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool HasRecentForm => RecentFormBadges.Count > 0;
    public bool HasUpcomingFixtures => UpcomingFixtures.Count > 0;
    public bool HasRecentResults => RecentResults.Count > 0;
    public bool HasStandings => Standings.Count > 0;
    public bool HasSquad => Goalkeepers.Count > 0 || Defenders.Count > 0 || Midfielders.Count > 0 || Forwards.Count > 0;

    public string TopScorerName
    {
        get => _topScorerName;
        private set
        {
            if (SetProperty(ref _topScorerName, value))
                OnPropertyChanged(nameof(HasTopScorer));
        }
    }

    public string TopScorerStat
    {
        get => _topScorerStat;
        private set => SetProperty(ref _topScorerStat, value);
    }

    public bool HasTopScorer => !string.IsNullOrEmpty(_topScorerName);

    public string TopAssisterName
    {
        get => _topAssisterName;
        private set
        {
            if (SetProperty(ref _topAssisterName, value))
                OnPropertyChanged(nameof(HasTopAssister));
        }
    }

    public string TopAssisterStat
    {
        get => _topAssisterStat;
        private set => SetProperty(ref _topAssisterStat, value);
    }

    public bool HasTopAssister => !string.IsNullOrEmpty(_topAssisterName);

    public string TopRatedName
    {
        get => _topRatedName;
        private set
        {
            if (SetProperty(ref _topRatedName, value))
                OnPropertyChanged(nameof(HasTopRated));
        }
    }

    public string TopRatedStat
    {
        get => _topRatedStat;
        private set => SetProperty(ref _topRatedStat, value);
    }

    public bool HasTopRated => !string.IsNullOrEmpty(_topRatedName);

    #endregion

    #region Methods & Commands

    public void SelectFixturesTab() => SubTabIndex = 0;
    public void SelectStandingsTab() => SubTabIndex = 1;
    public void SelectSquadTab() => SubTabIndex = 2;

    public async Task ToggleFollowAsync()
    {
        if (_preferencesService == null || string.IsNullOrWhiteSpace(TeamId)) return;

        try
        {
            var prefs = _preferencesService.GetPreferences();
            var followedSet = prefs?.GetFollowedTeamIdSet() ?? new HashSet<string>();

            if (followedSet.Contains(TeamId))
            {
                await _preferencesService.UnfollowTeamAsync(TeamId);
                IsFollowed = false;
            }
            else
            {
                var entry = new FollowedTeam
                {
                    Id = TeamId,
                    Name = !string.IsNullOrWhiteSpace(TeamName) ? TeamName : "Team",
                    ShortName = !string.IsNullOrWhiteSpace(ShortName) ? ShortName : TeamName,
                    LogoUrl = TeamLogoUrl,
                    Country = CountryText
                };
                await _preferencesService.FollowTeamAsync(entry);
                IsFollowed = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("TeamHubViewModel", "ToggleFollowFailed", $"Failed to toggle follow for {TeamId}: {ex.Message}", ex: ex);
        }
    }

    public void CheckFollowState()
    {
        if (_preferencesService == null || string.IsNullOrWhiteSpace(TeamId)) return;
        var prefs = _preferencesService.GetPreferences();
        var set = prefs?.GetFollowedTeamIdSet() ?? new HashSet<string>();
        IsFollowed = set.Contains(TeamId);
    }

    public async Task LoadAsync(string teamId, string? initialName = null, string? initialLogo = null)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;

        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var ct = _loadingCts.Token;

        TeamId = teamId;
        if (!string.IsNullOrWhiteSpace(initialName)) TeamName = initialName;
        if (!string.IsNullOrWhiteSpace(initialLogo)) TeamLogoUrl = initialLogo;

        CheckFollowState();

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            var res = await _apiClient.GetTeamHubAsync(teamId, initialName, initialLogo, ct);
            if (ct.IsCancellationRequested) return;

            if (res.IsSuccess && res.Envelope?.Data != null)
            {
                Populate(res.Envelope.Data);
            }
            else
            {
                HasError = true;
                ErrorMessage = res.ErrorMessage ?? $"Failed to load data for team {teamId}.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"Error loading team hub: {ex.Message}";
            AppLogger.Error("TeamHubViewModel", "LoadException", ErrorMessage, ex: ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Populate(TeamHubDto dto)
    {
        TeamId = dto.TeamId;
        TeamName = !string.IsNullOrWhiteSpace(dto.TeamName) ? dto.TeamName : "Team";
        ShortName = !string.IsNullOrWhiteSpace(dto.ShortName) ? dto.ShortName : TeamName;
        TeamLogoUrl = dto.TeamLogo;
        CountryText = dto.CountryCode;
        PrimaryLeagueId = dto.PrimaryLeagueId;
        PrimaryLeagueName = dto.PrimaryLeagueName;
        PrimaryLeagueLogoUrl = dto.PrimaryLeagueLogo;
        StadiumName = dto.StadiumName;
        CoachName = dto.CoachName;

        CheckFollowState();

        // Recent Form Badges
        RecentFormBadges.Clear();
        foreach (var f in dto.RecentForm)
        {
            if (!string.IsNullOrWhiteSpace(f))
            {
                RecentFormBadges.Add(new FormBadgeItemViewModel { Result = f[0] });
            }
        }
        OnPropertyChanged(nameof(HasRecentForm));

        var prefs = _preferencesService?.GetPreferences();
        var followedTeamIds = prefs?.GetFollowedTeamIdSet() ?? new HashSet<string>();
        var followedNames = prefs?.FollowedTeams?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Upcoming Fixtures
        UpcomingFixtures.Clear();
        foreach (var fix in dto.UpcomingFixtures)
        {
            bool isHome = string.Equals(fix.HomeTeamId, _teamId, StringComparison.OrdinalIgnoreCase) ||
                          (!string.IsNullOrWhiteSpace(_teamName) && string.Equals(fix.HomeTeamName, _teamName, StringComparison.OrdinalIgnoreCase)) ||
                          followedTeamIds.Contains(fix.HomeTeamId) ||
                          followedNames.Contains(fix.HomeTeamName);

            bool isAway = string.Equals(fix.AwayTeamId, _teamId, StringComparison.OrdinalIgnoreCase) ||
                          (!string.IsNullOrWhiteSpace(_teamName) && string.Equals(fix.AwayTeamName, _teamName, StringComparison.OrdinalIgnoreCase)) ||
                          followedTeamIds.Contains(fix.AwayTeamId) ||
                          followedNames.Contains(fix.AwayTeamName);

            UpcomingFixtures.Add(new CompetitionFixtureViewModel(fix, id => App.OpenMatchCenter(id), isHome, isAway)
            {
                Use24HourTime = prefs?.Use24HourFormat ?? true
            });
        }
        OnPropertyChanged(nameof(HasUpcomingFixtures));

        // Recent Results
        RecentResults.Clear();
        foreach (var fix in dto.RecentResults)
        {
            bool isHome = string.Equals(fix.HomeTeamId, _teamId, StringComparison.OrdinalIgnoreCase) ||
                          (!string.IsNullOrWhiteSpace(_teamName) && string.Equals(fix.HomeTeamName, _teamName, StringComparison.OrdinalIgnoreCase)) ||
                          followedTeamIds.Contains(fix.HomeTeamId) ||
                          followedNames.Contains(fix.HomeTeamName);

            bool isAway = string.Equals(fix.AwayTeamId, _teamId, StringComparison.OrdinalIgnoreCase) ||
                          (!string.IsNullOrWhiteSpace(_teamName) && string.Equals(fix.AwayTeamName, _teamName, StringComparison.OrdinalIgnoreCase)) ||
                          followedTeamIds.Contains(fix.AwayTeamId) ||
                          followedNames.Contains(fix.AwayTeamName);

            RecentResults.Add(new CompetitionFixtureViewModel(fix, id => App.OpenMatchCenter(id), isHome, isAway)
            {
                Use24HourTime = prefs?.Use24HourFormat ?? true
            });
        }
        OnPropertyChanged(nameof(HasRecentResults));

        // Standings
        Standings.Clear();
        foreach (var row in dto.Standings)
        {
            bool isCurrentTeam = string.Equals(row.TeamId, _teamId, StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrWhiteSpace(_teamName) && string.Equals(row.TeamName, _teamName, StringComparison.OrdinalIgnoreCase));
            bool isRowFollowed = followedTeamIds.Contains(row.TeamId) || followedNames.Contains(row.TeamName) || (!string.IsNullOrEmpty(row.TeamShortName) && followedNames.Contains(row.TeamShortName));

            Standings.Add(new StandingRowItemViewModel(row, isRowFollowed, isHomeTeam: isCurrentTeam));
        }
        OnPropertyChanged(nameof(HasStandings));

        // Squad Players
        Goalkeepers.Clear();
        Defenders.Clear();
        Midfielders.Clear();
        Forwards.Clear();

        foreach (var player in dto.SquadPlayers)
        {
            var item = new TeamSquadPlayerItemViewModel(player);
            switch (player.PositionGroup)
            {
                case "Goalkeepers":
                    Goalkeepers.Add(item);
                    break;
                case "Defenders":
                    Defenders.Add(item);
                    break;
                case "Midfielders":
                    Midfielders.Add(item);
                    break;
                case "Forwards":
                    Forwards.Add(item);
                    break;
                default:
                    Midfielders.Add(item);
                    break;
            }
        }
        OnPropertyChanged(nameof(HasSquad));

        // Compute Leaders
        var players = dto.SquadPlayers;

        var topScorer = players.Where(p => p.Goals.HasValue && p.Goals.Value > 0).OrderByDescending(p => p.Goals ?? 0).FirstOrDefault();
        if (topScorer != null && topScorer.Goals.HasValue)
        {
            TopScorerName = topScorer.Name;
            TopScorerStat = $"{topScorer.Goals.Value} goals";
        }
        else
        {
            TopScorerName = string.Empty;
            TopScorerStat = string.Empty;
        }

        var topAssister = players.Where(p => p.Assists.HasValue && p.Assists.Value > 0).OrderByDescending(p => p.Assists ?? 0).FirstOrDefault();
        if (topAssister != null && topAssister.Assists.HasValue)
        {
            TopAssisterName = topAssister.Name;
            TopAssisterStat = $"{topAssister.Assists.Value} assists";
        }
        else
        {
            TopAssisterName = string.Empty;
            TopAssisterStat = string.Empty;
        }

        var topRated = players.Where(p => p.Rating.HasValue && p.Rating.Value > 0).OrderByDescending(p => p.Rating ?? 0.0).FirstOrDefault();
        if (topRated != null && topRated.Rating.HasValue)
        {
            TopRatedName = topRated.Name;
            TopRatedStat = $"{topRated.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)} rating";
        }
        else
        {
            TopRatedName = string.Empty;
            TopRatedStat = string.Empty;
        }
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    #endregion
}
