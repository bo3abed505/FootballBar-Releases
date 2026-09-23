using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

        HasLineups = HomeLineup.HasLineup || AwayLineup.HasLineup;
        OnPropertyChanged(nameof(CurrentLineup));
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
