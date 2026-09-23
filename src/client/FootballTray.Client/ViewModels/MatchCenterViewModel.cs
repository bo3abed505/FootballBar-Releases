using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using FootballTray.Core.Dto;

namespace FootballTray.Client.ViewModels;

public class MatchTimelineItem
{
    public string MinuteDisplay { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PlayerInfo { get; set; }
    public bool HasPlayerInfo => !string.IsNullOrWhiteSpace(PlayerInfo);
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
        Title = $"{HomeTeamName} vs {AwayTeamName} - Match Center";
        CompetitionName = match.Competition?.Name ?? "Football";

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

        // 1. Populate Timeline
        TimelineItems.Clear();
        if (match.Events != null)
        {
            foreach (var ev in match.Events.OrderBy(e => e.Minute))
            {
                string icon = ev.Type switch
                {
                    "Goal" => "⚽",
                    "YellowCard" => "🟨",
                    "RedCard" => "🟥",
                    "Substitution" => "🔄",
                    "PeriodStart" => "⏱️",
                    "PeriodEnd" => "🏁",
                    _ => "📌",
                };

                TimelineItems.Add(new MatchTimelineItem
                {
                    MinuteDisplay = $"{ev.Minute}'",
                    IconGlyph = icon,
                    Description = ev.Description ?? ev.Type,
                    PlayerInfo = !string.IsNullOrWhiteSpace(ev.PlayerName) ? ev.PlayerName : null,
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
