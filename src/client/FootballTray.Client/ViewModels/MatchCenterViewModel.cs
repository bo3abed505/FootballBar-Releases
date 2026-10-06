using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using FootballTray.Core.Dto;

namespace FootballTray.Client.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class MatchHeaderIncidentViewModel
{
    public string Icon { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class MatchTimelineItem
{
    public string MinuteDisplay { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PlayerInfo { get; set; }
    public bool HasPlayerInfo => !string.IsNullOrWhiteSpace(PlayerInfo);

    public bool IsHomeTeam { get; set; }
    public bool IsAwayTeam { get; set; }
    public bool IsNeutral => !IsHomeTeam && !IsAwayTeam;

    public string EventTitle { get; set; } = string.Empty;
    public string EventSubtitle { get; set; } = string.Empty;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(EventSubtitle);

    // Direct Visibility properties for clean WinUI 3 XAML bindings without converters
    public Visibility HomeVisibility => IsHomeTeam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayVisibility => IsAwayTeam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NonNeutralVisibility => !IsNeutral ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralVisibility => IsNeutral ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubtitleVisibility => HasSubtitle ? Visibility.Visible : Visibility.Collapsed;
}

public class LineupPlayerItemViewModel
{
    public string Number { get; set; } = "-";
    public string Name { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string PositionBadge { get; set; } = string.Empty;
}

public class LineupPresentationViewModel : INotifyPropertyChanged
{
    private string _teamName = string.Empty;
    private string _formation = string.Empty;
    private string _coachName = string.Empty;
    private bool _hasLineup;

    public string TeamName
    {
        get => _teamName;
        set => SetProperty(ref _teamName, value);
    }

    public string Formation
    {
        get => _formation;
        set => SetProperty(ref _formation, value);
    }

    public string CoachName
    {
        get => _coachName;
        set => SetProperty(ref _coachName, value);
    }

    public bool HasLineup
    {
        get => _hasLineup;
        set => SetProperty(ref _hasLineup, value);
    }

    public bool HasFormation => !string.IsNullOrWhiteSpace(Formation);
    public bool HasCoach => !string.IsNullOrWhiteSpace(CoachName);

    public ObservableCollection<LineupPlayerItemViewModel> StartingXI { get; } = new();
    public ObservableCollection<LineupPlayerItemViewModel> Substitutes { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

public class PitchPlayerNodeViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _shortDisplayName = string.Empty;
    private string _number = "-";
    private string _position = string.Empty;
    private string _positionBadge = string.Empty;
    private bool _isGoalkeeper;
    private bool _isHomeTeam;
    private int _row = 1;
    private int _col = 1;
    private double _xPercent = 0.5;
    private double _yPercent = 0.5;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string ShortDisplayName
    {
        get => _shortDisplayName;
        set => SetProperty(ref _shortDisplayName, value);
    }

    public string Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    public string Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    public string PositionBadge
    {
        get => _positionBadge;
        set => SetProperty(ref _positionBadge, value);
    }

    public bool IsGoalkeeper
    {
        get => _isGoalkeeper;
        set
        {
            if (SetProperty(ref _isGoalkeeper, value))
            {
                OnPropertyChanged(nameof(GoalkeeperVisibility));
                OnPropertyChanged(nameof(HomeOutfieldVisibility));
                OnPropertyChanged(nameof(AwayOutfieldVisibility));
                OnPropertyChanged(nameof(IsHomeOutfield));
                OnPropertyChanged(nameof(IsAwayOutfield));
            }
        }
    }

    public bool IsHomeTeam
    {
        get => _isHomeTeam;
        set
        {
            if (SetProperty(ref _isHomeTeam, value))
            {
                OnPropertyChanged(nameof(HomeOutfieldVisibility));
                OnPropertyChanged(nameof(AwayOutfieldVisibility));
                OnPropertyChanged(nameof(IsHomeOutfield));
                OnPropertyChanged(nameof(IsAwayOutfield));
            }
        }
    }

    public int Row
    {
        get => _row;
        set => SetProperty(ref _row, value);
    }

    public int Col
    {
        get => _col;
        set => SetProperty(ref _col, value);
    }

    public double XPercent
    {
        get => _xPercent;
        set
        {
            if (SetProperty(ref _xPercent, value))
            {
                OnPropertyChanged(nameof(CanvasLeft));
            }
        }
    }

    public double YPercent
    {
        get => _yPercent;
        set
        {
            if (SetProperty(ref _yPercent, value))
            {
                OnPropertyChanged(nameof(CanvasTop));
            }
        }
    }

    public double CanvasLeft => Math.Round((XPercent * 340.0) - 35.0, 1);
    public double CanvasTop => Math.Round((YPercent * 500.0) - 20.0, 1);

    public bool IsHomeOutfield => !IsGoalkeeper && IsHomeTeam;
    public bool IsAwayOutfield => !IsGoalkeeper && !IsHomeTeam;

    public Visibility GoalkeeperVisibility =>
        IsGoalkeeper ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HomeOutfieldVisibility =>
        (!IsGoalkeeper && IsHomeTeam) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AwayOutfieldVisibility =>
        (!IsGoalkeeper && !IsHomeTeam) ? Visibility.Visible : Visibility.Collapsed;

    public static string ExtractShortDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0];
        if (parts.Length >= 2 && (parts[^1].Equals("Jr", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^1].Equals("Jr.", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^1].Equals("III", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^1].Equals("II", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{parts[^2]} {parts[^1]}";
        }
        return parts[^1];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        return false;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class StatComparisonViewModel
{
    public string Name { get; set; } = string.Empty;
    public string HomeValue { get; set; } = "0";
    public string AwayValue { get; set; } = "0";
    public double HomeRatio { get; set; } = 50.0;
    public double AwayRatio { get; set; } = 50.0;
}

public class MatchCenterViewModel : INotifyPropertyChanged
{
    private string _matchId = string.Empty;
    private string _title = "Match Center";
    private string _competitionName = "Football";
    private string _statusText = "PREMATCH";
    private string _homeTeamName = "Home Team";
    private string _awayTeamName = "Away Team";
    private string? _homeTeamLogoUrl;
    private string? _awayTeamLogoUrl;
    private string _scoreText = "0 - 0";
    private string _halfTimeText = string.Empty;
    private bool _hasHalfTime;
    private string? _venue;
    private string? _referee;
    private bool _hasStatistics;
    private bool _hasLineups;
    private bool _isStatisticsViewSelected = true;
    private bool _isLineupsViewSelected;
    private string _activeLineupTeam = "Home";
    private string? _aggregateScore;
    private string? _historicalLeg;
    private string _aggregateText = string.Empty;
    private bool _hasAggregateScore;
    private bool _isHomeTeamFollowed;
    private bool _isAwayTeamFollowed;
    private bool _isCompetitionFollowed;
    private bool _isPinned;

    public string HomeTeamId { get; private set; } = string.Empty;
    public string HomeTeamLegacyId { get; private set; } = string.Empty;
    public string AwayTeamId { get; private set; } = string.Empty;
    public string AwayTeamLegacyId { get; private set; } = string.Empty;
    public string CompetitionId { get; private set; } = string.Empty;
    public string CompetitionLegacyId { get; private set; } = string.Empty;
    public string? CompetitionCode { get; private set; }

    public bool IsHomeTeamFollowed
    {
        get => _isHomeTeamFollowed;
        set
        {
            if (SetProperty(ref _isHomeTeamFollowed, value))
            {
                OnPropertyChanged(nameof(HomeStarGlyph));
                OnPropertyChanged(nameof(HomeStarOpacity));
            }
        }
    }

    public bool IsAwayTeamFollowed
    {
        get => _isAwayTeamFollowed;
        set
        {
            if (SetProperty(ref _isAwayTeamFollowed, value))
            {
                OnPropertyChanged(nameof(AwayStarGlyph));
                OnPropertyChanged(nameof(AwayStarOpacity));
            }
        }
    }

    public bool IsCompetitionFollowed
    {
        get => _isCompetitionFollowed;
        set
        {
            if (SetProperty(ref _isCompetitionFollowed, value))
            {
                OnPropertyChanged(nameof(CompetitionStarGlyph));
                OnPropertyChanged(nameof(CompetitionStarOpacity));
            }
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinGlyph));
                OnPropertyChanged(nameof(PinGlyphOpacity));
            }
        }
    }

    public string HomeStarGlyph => _isHomeTeamFollowed ? "\uE735" : "\uE734";
    public string AwayStarGlyph => _isAwayTeamFollowed ? "\uE735" : "\uE734";
    public string CompetitionStarGlyph => _isCompetitionFollowed ? "\uE735" : "\uE734";
    public string PinGlyph => _isPinned ? "\uE840" : "\uE718";

    public double HomeStarOpacity => _isHomeTeamFollowed ? 1.0 : 0.45;
    public double AwayStarOpacity => _isAwayTeamFollowed ? 1.0 : 0.45;
    public double CompetitionStarOpacity => _isCompetitionFollowed ? 1.0 : 0.45;
    public double PinGlyphOpacity => _isPinned ? 1.0 : 0.6;

    public ICommand ToggleHomeStarCommand { get; }
    public ICommand ToggleAwayStarCommand { get; }
    public ICommand ToggleCompetitionStarCommand { get; }
    public ICommand TogglePinCommand { get; }

    public ICommand ToggleFollowHomeTeamCommand => ToggleHomeStarCommand;
    public ICommand ToggleFollowAwayTeamCommand => ToggleAwayStarCommand;
    public ICommand ToggleFollowCompetitionCommand => ToggleCompetitionStarCommand;

    public MatchCenterViewModel()
    {
        ToggleHomeStarCommand = new RelayCommand(async () => await ToggleFollowHomeTeamAsync());
        ToggleAwayStarCommand = new RelayCommand(async () => await ToggleFollowAwayTeamAsync());
        ToggleCompetitionStarCommand = new RelayCommand(async () => await ToggleFollowCompetitionAsync());
        TogglePinCommand = new RelayCommand(() => TogglePin());
    }

    public async Task ToggleFollowHomeTeamAsync()
    {
        if (string.IsNullOrWhiteSpace(HomeTeamId)) return;
        var mainVm = App.CurrentMainViewModel;
        if (mainVm != null)
        {
            await mainVm.ToggleTeamFollowAsync(HomeTeamId, HomeTeamName, HomeTeamLogoUrl);
            RefreshFollowAndPinStates();
        }
    }

    public async Task ToggleFollowAwayTeamAsync()
    {
        if (string.IsNullOrWhiteSpace(AwayTeamId)) return;
        var mainVm = App.CurrentMainViewModel;
        if (mainVm != null)
        {
            await mainVm.ToggleTeamFollowAsync(AwayTeamId, AwayTeamName, AwayTeamLogoUrl);
            RefreshFollowAndPinStates();
        }
    }

    public async Task ToggleFollowCompetitionAsync()
    {
        if (string.IsNullOrWhiteSpace(CompetitionId)) return;
        var mainVm = App.CurrentMainViewModel;
        if (mainVm != null)
        {
            await mainVm.ToggleCompetitionFollowAsync(CompetitionId, CompetitionName, CompetitionCode);
            RefreshFollowAndPinStates();
        }
    }

    public void TogglePin()
    {
        if (string.IsNullOrWhiteSpace(MatchId)) return;
        var mainVm = App.CurrentMainViewModel;
        if (mainVm != null)
        {
            mainVm.TogglePinMatch(MatchId);
            RefreshFollowAndPinStates();
        }
    }

    public void RefreshFollowAndPinStates()
    {
        var mainVm = App.CurrentMainViewModel;
        if (mainVm != null)
        {
            var prefs = mainVm.PreferencesService?.GetPreferences();
            if (prefs != null)
            {
                var followedTeams = prefs.GetFollowedTeamIdSet();
                var followedComps = prefs.GetFollowedCompetitionIdSet();

                IsHomeTeamFollowed = (!string.IsNullOrEmpty(HomeTeamId) && followedTeams.Contains(HomeTeamId)) ||
                                     (!string.IsNullOrEmpty(HomeTeamLegacyId) && followedTeams.Contains(HomeTeamLegacyId));

                IsAwayTeamFollowed = (!string.IsNullOrEmpty(AwayTeamId) && followedTeams.Contains(AwayTeamId)) ||
                                     (!string.IsNullOrEmpty(AwayTeamLegacyId) && followedTeams.Contains(AwayTeamLegacyId));

                IsCompetitionFollowed = (!string.IsNullOrEmpty(CompetitionId) && followedComps.Contains(CompetitionId)) ||
                                        (!string.IsNullOrEmpty(CompetitionLegacyId) && followedComps.Contains(CompetitionLegacyId));
            }

            string? pinnedId = App.CurrentTickerCoordinator?.PinnedMatchId ?? mainVm.ManualPinnedMatchId;
            IsPinned = !string.IsNullOrEmpty(MatchId) && string.Equals(MatchId, pinnedId, StringComparison.Ordinal);
        }
    }

    public string MatchId
    {
        get => _matchId;
        set => SetProperty(ref _matchId, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string CompetitionName
    {
        get => _competitionName;
        set => SetProperty(ref _competitionName, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string HomeTeamName
    {
        get => _homeTeamName;
        set => SetProperty(ref _homeTeamName, value);
    }

    public string AwayTeamName
    {
        get => _awayTeamName;
        set => SetProperty(ref _awayTeamName, value);
    }

    public string? HomeTeamLogoUrl
    {
        get => _homeTeamLogoUrl;
        set => SetProperty(ref _homeTeamLogoUrl, value);
    }

    public string? AwayTeamLogoUrl
    {
        get => _awayTeamLogoUrl;
        set => SetProperty(ref _awayTeamLogoUrl, value);
    }

    public string ScoreText
    {
        get => _scoreText;
        set => SetProperty(ref _scoreText, value);
    }

    public string HalfTimeText
    {
        get => _halfTimeText;
        set => SetProperty(ref _halfTimeText, value);
    }

    public bool HasHalfTime
    {
        get => _hasHalfTime;
        set => SetProperty(ref _hasHalfTime, value);
    }

    public string? Venue
    {
        get => _venue;
        set => SetProperty(ref _venue, value);
    }

    public string? Referee
    {
        get => _referee;
        set => SetProperty(ref _referee, value);
    }

    public bool HasStatistics
    {
        get => _hasStatistics;
        set => SetProperty(ref _hasStatistics, value);
    }

    public bool HasLineups
    {
        get => _hasLineups;
        set => SetProperty(ref _hasLineups, value);
    }

    public bool IsStatisticsViewSelected
    {
        get => _isStatisticsViewSelected;
        set
        {
            if (SetProperty(ref _isStatisticsViewSelected, value) && value)
            {
                IsLineupsViewSelected = false;
                OnPropertyChanged(nameof(ShowStatistics));
                OnPropertyChanged(nameof(ShowLineups));
            }
        }
    }

    public bool IsLineupsViewSelected
    {
        get => _isLineupsViewSelected;
        set
        {
            if (SetProperty(ref _isLineupsViewSelected, value) && value)
            {
                IsStatisticsViewSelected = false;
                OnPropertyChanged(nameof(ShowStatistics));
                OnPropertyChanged(nameof(ShowLineups));
            }
        }
    }

    public bool ShowStatistics => _isStatisticsViewSelected;
    public bool ShowLineups => _isLineupsViewSelected;

    public string ActiveLineupTeam
    {
        get => _activeLineupTeam;
        set
        {
            if (SetProperty(ref _activeLineupTeam, value))
            {
                OnPropertyChanged(nameof(CurrentLineup));
            }
        }
    }

    public LineupPresentationViewModel CurrentLineup =>
        _activeLineupTeam == "Away" ? AwayLineup : HomeLineup;

    public ObservableCollection<MatchTimelineItem> TimelineItems { get; } = new();
    public ObservableCollection<MatchHeaderIncidentViewModel> HomeIncidents { get; } = new();
    public ObservableCollection<MatchHeaderIncidentViewModel> AwayIncidents { get; } = new();
    public ObservableCollection<StatComparisonViewModel> MatchStats { get; } = new();

    public LineupPresentationViewModel HomeLineup { get; } = new();
    public LineupPresentationViewModel AwayLineup { get; } = new();
    public ObservableCollection<PitchPlayerNodeViewModel> PitchPlayers { get; } = new();

    public string? AggregateScore
    {
        get => _aggregateScore;
        set => SetProperty(ref _aggregateScore, value);
    }

    public string? HistoricalLeg
    {
        get => _historicalLeg;
        set => SetProperty(ref _historicalLeg, value);
    }

    public string AggregateText
    {
        get => _aggregateText;
        set => SetProperty(ref _aggregateText, value);
    }

    public bool HasAggregateScore
    {
        get => _hasAggregateScore;
        set => SetProperty(ref _hasAggregateScore, value);
    }

    public void Load(MatchDetailDto match)
    {
        if (match == null) return;

        MatchId = match.Id;
        HomeTeamName = !string.IsNullOrWhiteSpace(match.HomeTeam?.Name) ? match.HomeTeam.Name : "Home Team";
        AwayTeamName = !string.IsNullOrWhiteSpace(match.AwayTeam?.Name) ? match.AwayTeam.Name : "Away Team";
        HomeTeamLogoUrl = match.HomeTeam?.LogoUrl;
        AwayTeamLogoUrl = match.AwayTeam?.LogoUrl;
        HomeTeamId = match.HomeTeam?.Id ?? match.HomeTeamId ?? string.Empty;
        HomeTeamLegacyId = match.HomeTeam?.LegacyId ?? string.Empty;
        AwayTeamId = match.AwayTeam?.Id ?? match.AwayTeamId ?? string.Empty;
        AwayTeamLegacyId = match.AwayTeam?.LegacyId ?? string.Empty;
        CompetitionId = match.Competition?.Id ?? string.Empty;
        CompetitionLegacyId = match.Competition?.LegacyId ?? string.Empty;
        CompetitionCode = match.Competition?.Code;
        Title = $"{HomeTeamName} vs {AwayTeamName} - Match Center";
        CompetitionName = match.Competition?.Name ?? "Football";

        RefreshFollowAndPinStates();

        // Status badge
        string state = (match.State ?? "PREMATCH").ToUpperInvariant();
        StatusText = state switch
        {
            "LIVE" or "IN_PLAY" or "1H" or "2H" => $"LIVE {match.ElapsedMinute}'",
            "HALFTIME" or "HT" => "HALF TIME",
            "FINISHED" or "FT" or "AET" or "PEN" => "FINAL",
            _ => state
        };

        ScoreText = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";

        if (match.Score?.HalfTime?.Home != null && match.Score?.HalfTime?.Away != null)
        {
            HalfTimeText = $"(HT {match.Score.HalfTime.Home}-{match.Score.HalfTime.Away})";
            HasHalfTime = true;
        }
        else
        {
            HalfTimeText = string.Empty;
            HasHalfTime = false;
        }

        Venue = match.Venue;
        Referee = match.Referee;

        // Aggregate Score
        AggregateScore = match.AggregateScore;
        HistoricalLeg = match.HistoricalLeg;

        string? agg = null;
        if (!string.IsNullOrWhiteSpace(match.AggregateScore))
        {
            agg = match.AggregateScore;
        }
        else if (!string.IsNullOrWhiteSpace(match.Score?.AggregateDisplay))
        {
            agg = match.Score.AggregateDisplay;
        }
        else if (match.Score?.Aggregate?.Home != null && match.Score?.Aggregate?.Away != null)
        {
            agg = $"{match.Score.Aggregate.Home} - {match.Score.Aggregate.Away}";
        }

        if (!string.IsNullOrWhiteSpace(agg))
        {
            string aggFormatted = agg.Trim();
            if (!aggFormatted.StartsWith("Agg", StringComparison.OrdinalIgnoreCase))
            {
                aggFormatted = $"Agg {aggFormatted}";
            }

            if (!string.IsNullOrWhiteSpace(match.HistoricalLeg))
            {
                aggFormatted = $"{aggFormatted} ({match.HistoricalLeg.Trim()})";
            }

            AggregateText = aggFormatted;
            HasAggregateScore = true;
        }
        else
        {
            AggregateText = string.Empty;
            HasAggregateScore = false;
        }

        // 1. Populate Timeline and Header Incidents (Goal Scorers & Red Cards)
        TimelineItems.Clear();
        HomeIncidents.Clear();
        AwayIncidents.Clear();

        if (match.Events != null && match.Events.Count > 0)
        {
            // Helper to determine if an event belongs to a team, supporting canonical IDs, legacy IDs, prefix stripping, and integer comparison
            bool IsTeamMatch(string? eventTeamId, TeamSummaryDto? team, string? fallbackTeamId = null)
            {
                if (string.IsNullOrWhiteSpace(eventTeamId)) return false;

                string evId = eventTeamId.Trim();
                var candidates = new List<string>();

                if (team != null)
                {
                    if (!string.IsNullOrWhiteSpace(team.Id)) candidates.Add(team.Id.Trim());
                    if (!string.IsNullOrWhiteSpace(team.LegacyId)) candidates.Add(team.LegacyId.Trim());
                }
                if (!string.IsNullOrWhiteSpace(fallbackTeamId))
                {
                    candidates.Add(fallbackTeamId.Trim());
                }

                if (candidates.Count == 0) return false;

                foreach (var c in candidates)
                {
                    if (string.Equals(evId, c, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                static string StripPrefix(string val)
                {
                    if (val.StartsWith("ft_team_", StringComparison.OrdinalIgnoreCase))
                        return val.Substring(8);
                    if (val.StartsWith("team_", StringComparison.OrdinalIgnoreCase))
                        return val.Substring(5);
                    return val;
                }

                string evStripped = StripPrefix(evId);
                foreach (var c in candidates)
                {
                    string cStripped = StripPrefix(c);
                    if (string.Equals(evStripped, cStripped, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (long.TryParse(evStripped, out long evNum) && long.TryParse(cStripped, out long cNum))
                    {
                        if (evNum == cNum)
                            return true;
                    }
                }

                return false;
            }

            bool IsEventForTeam(TrayEventDto e, TeamSummaryDto? team, string? fallbackTeamId = null)
            {
                if (team == null) return false;
                if (IsTeamMatch(e.TeamId, team, fallbackTeamId)) return true;

                if (!string.IsNullOrWhiteSpace(e.Description))
                {
                    if (!string.IsNullOrWhiteSpace(team.Name) && e.Description.Contains(team.Name, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!string.IsNullOrWhiteSpace(team.ShortName) && team.ShortName.Length >= 2 && e.Description.Contains(team.ShortName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }

            static string CleanDescription(string? rawDesc, string? type)
            {
                if (string.IsNullOrWhiteSpace(rawDesc)) return string.Empty;
                string desc = rawDesc.Trim();
                if (!string.IsNullOrEmpty(type) && desc.StartsWith($"[{type}]", StringComparison.OrdinalIgnoreCase))
                {
                    desc = desc.Substring(type.Length + 2).Trim();
                }
                else if (desc.StartsWith("[") && desc.Contains("]"))
                {
                    int closeIdx = desc.IndexOf(']');
                    if (closeIdx > 0 && closeIdx < desc.Length - 1)
                    {
                        desc = desc.Substring(closeIdx + 1).Trim();
                    }
                }
                return desc;
            }

            bool IsMissedPenalty(TrayEventDto e)
            {
                string t = (e.Type ?? string.Empty).ToLowerInvariant();
                string d = (e.Detail ?? string.Empty).ToLowerInvariant();
                string desc = (e.Description ?? string.Empty).ToLowerInvariant();

                if (t.Contains("missed") && t.Contains("pen")) return true;
                if (t is "missed_penalty" or "missedpenalty" or "penalty_missed") return true;
                if ((t is "penalty" || t.Contains("pen")) && (d.Contains("missed") || desc.Contains("missed"))) return true;
                if (d.Contains("missed penalty") || d.Contains("penalty missed") || desc.Contains("missed penalty") || desc.Contains("penalty missed")) return true;

                return false;
            }

            string FormatPlayerWithLineup(string? playerName, List<LineupDto>? lineups, string? playerId = null)
            {
                if (string.IsNullOrWhiteSpace(playerName))
                    return string.Empty;

                string trimmedName = playerName.Trim();
                if (lineups == null || lineups.Count == 0)
                    return trimmedName;

                LineupPlayerDto? matchedPlayer = null;

                foreach (var lineup in lineups)
                {
                    if (lineup == null) continue;

                    var starters = lineup.GetStartingXI();
                    var subs = lineup.GetSubstitutes();
                    if (starters != null)
                    {
                        matchedPlayer = starters.FirstOrDefault(p =>
                            p != null && (
                                (!string.IsNullOrEmpty(playerId) && (string.Equals(p.PlayerId, playerId, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, playerId, StringComparison.OrdinalIgnoreCase))) ||
                                string.Equals(p.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                            ));
                    }

                    if (matchedPlayer == null && subs != null)
                    {
                        matchedPlayer = subs.FirstOrDefault(p =>
                            p != null && (
                                (!string.IsNullOrEmpty(playerId) && (string.Equals(p.PlayerId, playerId, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, playerId, StringComparison.OrdinalIgnoreCase))) ||
                                string.Equals(p.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                            ));
                    }

                    // Fallback to substring matching if name is long enough and not yet matched
                    if (matchedPlayer == null && trimmedName.Length >= 4)
                    {
                        if (starters != null)
                        {
                            matchedPlayer = starters.FirstOrDefault(p =>
                                p != null && !string.IsNullOrWhiteSpace(p.Name) &&
                                (p.Name.Contains(trimmedName, StringComparison.OrdinalIgnoreCase) || trimmedName.Contains(p.Name, StringComparison.OrdinalIgnoreCase)));
                        }
                        if (matchedPlayer == null && subs != null)
                        {
                            matchedPlayer = subs.FirstOrDefault(p =>
                                p != null && !string.IsNullOrWhiteSpace(p.Name) &&
                                (p.Name.Contains(trimmedName, StringComparison.OrdinalIgnoreCase) || trimmedName.Contains(p.Name, StringComparison.OrdinalIgnoreCase)));
                        }
                    }

                    if (matchedPlayer != null)
                        break;
                }

                if (matchedPlayer == null)
                    return trimmedName;

                string? numPart = matchedPlayer.Number.HasValue ? $"#{matchedPlayer.Number.Value}" : null;
                string? posPart = null;
                if (!string.IsNullOrWhiteSpace(matchedPlayer.Position))
                {
                    string rawPos = matchedPlayer.Position.Trim().ToUpperInvariant();
                    string badge = rawPos switch
                    {
                        "G" or "GK" => "GK",
                        "D" or "DEF" or "DF" => "DEF",
                        "M" or "MID" or "MF" => "MID",
                        "F" or "FWD" or "FW" or "ATT" => "ATT",
                        _ => rawPos
                    };
                    posPart = $"({badge})";
                }

                var parts = new List<string>();
                if (numPart != null) parts.Add(numPart);
                if (posPart != null) parts.Add(posPart);

                if (parts.Count > 0)
                {
                    return $"{string.Join(" ", parts)} {trimmedName}";
                }

                return trimmedName;
            }

            // Home Goals
            var homeGoals = match.Events
                .Where(e => (e.Type?.ToLowerInvariant().Contains("goal") == true || e.Type?.ToLowerInvariant() == "penalty") &&
                            e.Type?.ToLowerInvariant().Contains("missed") != true &&
                            !IsMissedPenalty(e) &&
                            IsEventForTeam(e, match.HomeTeam, match.HomeTeamId))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Goal"));

            foreach (var g in homeGoals)
            {
                var minutes = string.Join(", ", g.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                HomeIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "⚽",
                    Text = $"{g.Key} {minutes}"
                });
            }

            // Home Missed Penalties
            var homeMissedPens = match.Events
                .Where(e => IsMissedPenalty(e) &&
                            IsEventForTeam(e, match.HomeTeam, match.HomeTeamId))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Missed Penalty"));

            foreach (var mp in homeMissedPens)
            {
                var minutes = string.Join(", ", mp.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                HomeIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "❌",
                    Text = $"{mp.Key} {minutes}"
                });
            }

            // Home Red Cards
            var homeReds = match.Events
                .Where(e => (e.Type?.ToLowerInvariant().Contains("red") == true) &&
                            IsEventForTeam(e, match.HomeTeam, match.HomeTeamId))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName) ? e.PlayerName.Trim() : "Red Card");

            foreach (var r in homeReds)
            {
                var minutes = string.Join(", ", r.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                HomeIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "🟥",
                    Text = $"{r.Key} {minutes}"
                });
            }

            // Away Goals
            var awayGoals = match.Events
                .Where(e => (e.Type?.ToLowerInvariant().Contains("goal") == true || e.Type?.ToLowerInvariant() == "penalty") &&
                            e.Type?.ToLowerInvariant().Contains("missed") != true &&
                            !IsMissedPenalty(e) &&
                            IsEventForTeam(e, match.AwayTeam, match.AwayTeamId))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Goal"));

            foreach (var g in awayGoals)
            {
                var minutes = string.Join(", ", g.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                AwayIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "⚽",
                    Text = $"{g.Key} {minutes}"
                });
            }

            // Away Missed Penalties
            var awayMissedPens = match.Events
                .Where(e => IsMissedPenalty(e) &&
                            IsEventForTeam(e, match.AwayTeam, match.AwayTeamId))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Missed Penalty"));

            foreach (var mp in awayMissedPens)
            {
                var minutes = string.Join(", ", mp.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                AwayIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "❌",
                    Text = $"{mp.Key} {minutes}"
                });
            }

            // Away Red Cards
            var awayReds = match.Events
                .Where(e => (e.Type?.ToLowerInvariant().Contains("red") == true) &&
                            IsEventForTeam(e, match.AwayTeam, match.AwayTeamId))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName) ? e.PlayerName.Trim() : "Red Card");

            foreach (var r in awayReds)
            {
                var minutes = string.Join(", ", r.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                AwayIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "🟥",
                    Text = $"{r.Key} {minutes}"
                });
            }

            // Reverse chronological Timeline items (descending: 90+2' down to 1')
            foreach (var ev in match.Events.OrderByDescending(e => e.Minute).ThenByDescending(e => e.ExtraMinute))
            {
                string typeLower = (ev.Type ?? string.Empty).ToLowerInvariant();
                bool isMissedPen = IsMissedPenalty(ev);
                string icon = isMissedPen ? "❌" : typeLower switch
                {
                    "goal" => "⚽",
                    "penalty" => "⚽",
                    "owngoal" => "⚽",
                    "yellowcard" or "yellow_card" => "🟨",
                    "redcard" or "red_card" => "🟥",
                    "secondyellow" or "yellowred" => "🟨🟥",
                    "substitution" or "sub" => "🔄",
                    "var" => "🖥️",
                    "periodstart" or "kickoff" => "⏱️",
                    "periodend" or "halftime" or "fulltime" => "🏁",
                    "missed_penalty" or "missedpenalty" => "❌",
                    _ => "📌"
                };

                bool isNeutralType = typeLower is "periodstart" or "periodend" or "halftime" or "fulltime" or "kickoff";
                bool isHome = false;
                bool isAway = false;

                if (!isNeutralType)
                {
                    isHome = IsEventForTeam(ev, match.HomeTeam, match.HomeTeamId);
                    isAway = !isHome && IsEventForTeam(ev, match.AwayTeam, match.AwayTeamId);

                    // If neither matched by TeamId or description, try matching via lineups
                    if (!isHome && !isAway && !string.IsNullOrWhiteSpace(ev.PlayerName) && match.Lineups != null)
                    {
                        foreach (var lineup in match.Lineups)
                        {
                            var startingXi = lineup.GetStartingXI();
                            bool playerInLineup = (startingXi != null && startingXi.Any(p => string.Equals(p.Name, ev.PlayerName, StringComparison.OrdinalIgnoreCase))) ||
                                                  (lineup.Substitutes != null && lineup.Substitutes.Any(p => string.Equals(p.Name, ev.PlayerName, StringComparison.OrdinalIgnoreCase)));
                            if (playerInLineup)
                            {
                                if (IsTeamMatch(lineup.TeamId, match.HomeTeam, match.HomeTeamId))
                                {
                                    isHome = true;
                                    break;
                                }
                                else if (IsTeamMatch(lineup.TeamId, match.AwayTeam, match.AwayTeamId))
                                {
                                    isAway = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                string minuteDisplay = ev.ExtraMinute > 0 ? $"{ev.Minute}+{ev.ExtraMinute}'" : $"{ev.Minute}'";
                string cleanedDesc = CleanDescription(ev.Description, ev.Type);
                string eventTitle;
                string eventSubtitle;

                bool isSub = typeLower is "substitution" or "sub";

                if (isSub)
                {
                    string outPlayer = FormatPlayerWithLineup(ev.PlayerName, match.Lineups, ev.PlayerId);
                    string inPlayer = FormatPlayerWithLineup(ev.AssistPlayerName, match.Lineups);

                    eventTitle = !string.IsNullOrWhiteSpace(outPlayer)
                        ? $"Out: {outPlayer}"
                        : (!string.IsNullOrWhiteSpace(cleanedDesc) ? cleanedDesc : "Substitution");

                    eventSubtitle = !string.IsNullOrWhiteSpace(inPlayer)
                        ? $"In: {inPlayer}"
                        : (!string.Equals(cleanedDesc, eventTitle, StringComparison.OrdinalIgnoreCase) ? cleanedDesc : string.Empty);
                }
                else if (isMissedPen)
                {
                    string player = FormatPlayerWithLineup(ev.PlayerName, match.Lineups, ev.PlayerId);
                    eventTitle = !string.IsNullOrWhiteSpace(player)
                        ? player
                        : (!string.IsNullOrWhiteSpace(cleanedDesc) ? cleanedDesc : "Missed Penalty");
                    eventSubtitle = "Missed Penalty";
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(ev.PlayerName))
                    {
                        eventTitle = FormatPlayerWithLineup(ev.PlayerName, match.Lineups, ev.PlayerId);
                        eventSubtitle = !string.IsNullOrWhiteSpace(ev.AssistPlayerName)
                            ? $"Assist: {FormatPlayerWithLineup(ev.AssistPlayerName, match.Lineups)}"
                            : cleanedDesc;
                    }
                    else
                    {
                        eventTitle = !string.IsNullOrWhiteSpace(cleanedDesc) ? cleanedDesc : (ev.Type ?? "Event");
                        eventSubtitle = !string.IsNullOrWhiteSpace(ev.AssistPlayerName)
                            ? $"Assist: {FormatPlayerWithLineup(ev.AssistPlayerName, match.Lineups)}"
                            : string.Empty;
                    }
                }

                if (string.Equals(eventTitle, eventSubtitle, StringComparison.OrdinalIgnoreCase))
                {
                    eventSubtitle = string.Empty;
                }

                TimelineItems.Add(new MatchTimelineItem
                {
                    MinuteDisplay = minuteDisplay,
                    IconGlyph = icon,
                    Description = !string.IsNullOrWhiteSpace(cleanedDesc) ? cleanedDesc : (ev.Description ?? ev.Type ?? "Event"),
                    PlayerInfo = !string.IsNullOrWhiteSpace(ev.PlayerName) ? ev.PlayerName.Trim() : null,
                    IsHomeTeam = isHome,
                    IsAwayTeam = isAway,
                    EventTitle = eventTitle,
                    EventSubtitle = eventSubtitle
                });
            }
        }

        // 2. Populate Lineups
        PopulateLineups(match);

        // 3. Populate Statistics
        PopulateStatistics(match);
    }

    private void PopulateLineups(MatchDetailDto match)
    {
        HomeLineup.StartingXI.Clear();
        HomeLineup.Substitutes.Clear();
        AwayLineup.StartingXI.Clear();
        AwayLineup.Substitutes.Clear();
        PitchPlayers.Clear();

        HomeLineup.TeamName = HomeTeamName;
        AwayLineup.TeamName = AwayTeamName;

        if (match.Lineups == null || match.Lineups.Count == 0)
        {
            HomeLineup.HasLineup = false;
            AwayLineup.HasLineup = false;
            HasLineups = false;
            return;
        }

        string homeId = match.HomeTeam?.Id ?? string.Empty;
        string homeLegacyId = match.HomeTeam?.LegacyId ?? string.Empty;
        string awayId = match.AwayTeam?.Id ?? string.Empty;
        string awayLegacyId = match.AwayTeam?.LegacyId ?? string.Empty;

        LineupDto? homeDto = match.Lineups.FirstOrDefault(l =>
            (!string.IsNullOrEmpty(homeId) && l.TeamId == homeId) ||
            (!string.IsNullOrEmpty(homeLegacyId) && l.TeamId == homeLegacyId));

        LineupDto? awayDto = match.Lineups.FirstOrDefault(l =>
            (!string.IsNullOrEmpty(awayId) && l.TeamId == awayId) ||
            (!string.IsNullOrEmpty(awayLegacyId) && l.TeamId == awayLegacyId));

        // Fallback by order if TeamId matching is inconclusive
        if (homeDto == null && match.Lineups.Count > 0)
        {
            homeDto = match.Lineups[0];
        }
        if (awayDto == null && match.Lineups.Count > 1)
        {
            awayDto = match.Lineups[1];
        }

        if (homeDto != null)
        {
            HomeLineup.Formation = homeDto.Formation ?? string.Empty;
            HomeLineup.CoachName = homeDto.EffectiveCoachName;
            var starters = homeDto.GetStartingXI();
            var subs = homeDto.GetSubstitutes();

            foreach (var p in starters)
            {
                HomeLineup.StartingXI.Add(CreatePlayerItem(p));
            }
            foreach (var p in subs)
            {
                HomeLineup.Substitutes.Add(CreatePlayerItem(p));
            }
            HomeLineup.HasLineup = starters.Count > 0 || subs.Count > 0;
        }
        else
        {
            HomeLineup.HasLineup = false;
        }

        if (awayDto != null)
        {
            AwayLineup.Formation = awayDto.Formation ?? string.Empty;
            AwayLineup.CoachName = awayDto.EffectiveCoachName;
            var starters = awayDto.GetStartingXI();
            var subs = awayDto.GetSubstitutes();

            foreach (var p in starters)
            {
                AwayLineup.StartingXI.Add(CreatePlayerItem(p));
            }
            foreach (var p in subs)
            {
                AwayLineup.Substitutes.Add(CreatePlayerItem(p));
            }
            AwayLineup.HasLineup = starters.Count > 0 || subs.Count > 0;
        }
        else
        {
            AwayLineup.HasLineup = false;
        }

        BuildPitchPlayers(homeDto, awayDto);

        HasLineups = PitchPlayers.Count > 0 || HomeLineup.HasLineup || AwayLineup.HasLineup;
        OnPropertyChanged(nameof(CurrentLineup));
    }

    private void BuildPitchPlayers(LineupDto? homeDto, LineupDto? awayDto)
    {
        PitchPlayers.Clear();

        var awayStarters = awayDto?.GetStartingXI() ?? (IReadOnlyList<LineupPlayerDto>)Array.Empty<LineupPlayerDto>();
        var homeStarters = homeDto?.GetStartingXI() ?? (IReadOnlyList<LineupPlayerDto>)Array.Empty<LineupPlayerDto>();

        if (awayStarters.Count == 0 && homeStarters.Count == 0)
        {
            return;
        }

        // Build Away Players (Top Half, Y ranges 0.06 -> 0.44)
        if (awayStarters.Count > 0)
        {
            var awayNodes = CreateTeamPitchNodes(awayStarters, isHomeTeam: false);
            foreach (var node in awayNodes)
            {
                PitchPlayers.Add(node);
            }
        }

        // Build Home Players (Bottom Half, Y ranges 0.94 -> 0.56)
        if (homeStarters.Count > 0)
        {
            var homeNodes = CreateTeamPitchNodes(homeStarters, isHomeTeam: true);
            foreach (var node in homeNodes)
            {
                PitchPlayers.Add(node);
            }
        }
    }

    public static (int row, int col) ParseGridCoordinates(string? grid, string? position, int defaultCol)
    {
        if (!string.IsNullOrWhiteSpace(grid))
        {
            var parts = grid.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int r) && int.TryParse(parts[1], out int c) && r > 0 && c > 0)
            {
                return (r, c);
            }
        }

        string pos = position?.Trim().ToUpperInvariant() ?? string.Empty;
        int fallbackRow = pos switch
        {
            "G" or "GK" => 1,
            "D" or "DEF" or "DF" => 2,
            "M" or "MID" or "MF" => 3,
            "F" or "FWD" or "FW" or "ATT" => 4,
            _ => 2
        };

        return (fallbackRow, defaultCol);
    }

    public static List<PitchPlayerNodeViewModel> CreateTeamPitchNodes(IReadOnlyList<LineupPlayerDto> starters, bool isHomeTeam)
    {
        var result = new List<PitchPlayerNodeViewModel>();
        if (starters == null || starters.Count == 0) return result;

        var parsed = new List<(LineupPlayerDto Player, int Row, int Col, int Index)>();
        for (int i = 0; i < starters.Count; i++)
        {
            var p = starters[i];
            var (row, col) = ParseGridCoordinates(p.Grid, p.Position, i + 1);
            parsed.Add((p, row, col, i));
        }

        var rowGroups = parsed.GroupBy(p => p.Row).OrderBy(g => g.Key).ToList();
        int minRow = rowGroups.Min(g => g.Key);
        int maxRow = rowGroups.Max(g => g.Key);

        foreach (var g in rowGroups)
        {
            int rowKey = g.Key;
            double t = (maxRow > minRow) ? (double)(rowKey - minRow) / (maxRow - minRow) : 0.0;

            // Away (Top half): GK at 0.06, Attackers at 0.44
            // Home (Bottom half): GK at 0.94, Attackers at 0.56
            double y = isHomeTeam
                ? Math.Round(0.94 - (t * 0.38), 3)
                : Math.Round(0.06 + (t * 0.38), 3);

            var playersInRow = g.OrderBy(p => p.Col).ThenBy(p => p.Index).ToList();
            int countInRow = playersInRow.Count;

            for (int i = 0; i < countInRow; i++)
            {
                var item = playersInRow[i];
                double x = Math.Round((i + 1.0) / (countInRow + 1.0), 3);

                string pos = item.Player.Position?.Trim().ToUpperInvariant() ?? string.Empty;
                string badge = pos switch
                {
                    "G" or "GK" => "GK",
                    "D" or "DEF" or "DF" => "DEF",
                    "M" or "MID" or "MF" => "MID",
                    "F" or "FWD" or "FW" or "ATT" => "ATT",
                    _ => string.IsNullOrEmpty(pos) ? "-" : pos
                };

                bool isGk = (rowKey == minRow) || (badge == "GK");

                var node = new PitchPlayerNodeViewModel
                {
                    Name = item.Player.Name,
                    ShortDisplayName = PitchPlayerNodeViewModel.ExtractShortDisplayName(item.Player.Name),
                    Number = item.Player.Number.HasValue ? item.Player.Number.Value.ToString() : "-",
                    Position = pos,
                    PositionBadge = badge,
                    IsGoalkeeper = isGk,
                    IsHomeTeam = isHomeTeam,
                    Row = rowKey,
                    Col = item.Col,
                    XPercent = x,
                    YPercent = y
                };

                result.Add(node);
            }
        }

        return result;
    }

    private static LineupPlayerItemViewModel CreatePlayerItem(LineupPlayerDto p)
    {
        string pos = p.Position?.Trim().ToUpperInvariant() ?? string.Empty;
        string badge = pos switch
        {
            "G" or "GK" => "GK",
            "D" or "DEF" or "DF" => "DEF",
            "M" or "MID" or "MF" => "MID",
            "F" or "FWD" or "FW" or "ATT" => "ATT",
            _ => string.IsNullOrEmpty(pos) ? "-" : pos
        };

        return new LineupPlayerItemViewModel
        {
            Number = p.Number.HasValue ? p.Number.Value.ToString() : "-",
            Name = p.Name,
            Position = pos,
            PositionBadge = badge
        };
    }

    private void PopulateStatistics(MatchDetailDto match)
    {
        MatchStats.Clear();

        if (match.Statistics == null || match.Statistics.Count == 0)
        {
            HasStatistics = false;
            return;
        }

        string homeId = match.HomeTeam?.Id ?? string.Empty;
        string homeLegacyId = match.HomeTeam?.LegacyId ?? string.Empty;
        string awayId = match.AwayTeam?.Id ?? string.Empty;
        string awayLegacyId = match.AwayTeam?.LegacyId ?? string.Empty;

        TeamStatisticsDto? homeStats = match.Statistics.FirstOrDefault(s =>
            (!string.IsNullOrEmpty(homeId) && s.TeamId == homeId) ||
            (!string.IsNullOrEmpty(homeLegacyId) && s.TeamId == homeLegacyId));

        TeamStatisticsDto? awayStats = match.Statistics.FirstOrDefault(s =>
            (!string.IsNullOrEmpty(awayId) && s.TeamId == awayId) ||
            (!string.IsNullOrEmpty(awayLegacyId) && s.TeamId == awayLegacyId));

        // Fallback by order if TeamId matching is inconclusive
        if (homeStats == null && match.Statistics.Count > 0)
        {
            homeStats = match.Statistics[0];
        }
        if (awayStats == null && match.Statistics.Count > 1)
        {
            awayStats = match.Statistics[1];
        }

        if (homeStats == null && awayStats == null)
        {
            HasStatistics = false;
            return;
        }

        // Possession
        int? homePoss = homeStats?.PossessionPercent ?? homeStats?.Possession;
        int? awayPoss = awayStats?.PossessionPercent ?? awayStats?.Possession;
        if (homePoss.HasValue || awayPoss.HasValue)
        {
            int hp = homePoss ?? (awayPoss.HasValue ? 100 - awayPoss.Value : 50);
            int ap = awayPoss ?? (100 - hp);
            AddStatItem("Ball Possession", $"{hp}%", $"{ap}%", hp, ap);
        }

        // Total Shots
        AddNumericStatItem("Total Shots", homeStats?.TotalShots, awayStats?.TotalShots);

        // Shots on Goal
        AddNumericStatItem("Shots on Target", homeStats?.ShotsOnGoal, awayStats?.ShotsOnGoal);

        // Shots off Goal
        AddNumericStatItem("Shots off Target", homeStats?.ShotsOffGoal, awayStats?.ShotsOffGoal);

        // Total Passes
        AddNumericStatItem("Total Passes", homeStats?.TotalPasses, awayStats?.TotalPasses);

        // Passes Accurate
        AddNumericStatItem("Accurate Passes", homeStats?.PassesAccurate, awayStats?.PassesAccurate);

        // Corner Kicks
        int? homeCorners = homeStats?.CornerKicks ?? homeStats?.Corners;
        int? awayCorners = awayStats?.CornerKicks ?? awayStats?.Corners;
        AddNumericStatItem("Corner Kicks", homeCorners, awayCorners);

        // Fouls
        AddNumericStatItem("Fouls", homeStats?.Fouls, awayStats?.Fouls);

        // Yellow Cards
        AddNumericStatItem("Yellow Cards", homeStats?.YellowCards, awayStats?.YellowCards);

        // Red Cards
        AddNumericStatItem("Red Cards", homeStats?.RedCards, awayStats?.RedCards);

        // Goalkeeper Saves
        AddNumericStatItem("Goalkeeper Saves", homeStats?.GoalkeeperSaves, awayStats?.GoalkeeperSaves);

        // Offsides
        AddNumericStatItem("Offsides", homeStats?.Offsides, awayStats?.Offsides);

        HasStatistics = MatchStats.Count > 0;
    }

    private void AddNumericStatItem(string name, int? home, int? away)
    {
        if (!home.HasValue && !away.HasValue) return;

        int hVal = home ?? 0;
        int aVal = away ?? 0;
        double hRatio = 50.0;
        double aRatio = 50.0;

        int total = hVal + aVal;
        if (total > 0)
        {
            hRatio = Math.Round(((double)hVal / total) * 100.0, 1);
            aRatio = 100.0 - hRatio;
        }

        AddStatItem(name, hVal.ToString(), aVal.ToString(), hRatio, aRatio);
    }

    private void AddStatItem(string name, string homeDisplay, string awayDisplay, double homeRatio, double awayRatio)
    {
        MatchStats.Add(new StatComparisonViewModel
        {
            Name = name,
            HomeValue = homeDisplay,
            AwayValue = awayDisplay,
            HomeRatio = Math.Clamp(homeRatio, 0.0, 100.0),
            AwayRatio = Math.Clamp(awayRatio, 0.0, 100.0)
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        return false;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
