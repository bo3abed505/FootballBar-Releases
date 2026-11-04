using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FootballTray.Core.Dto;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model for a single recent match result form badge ('W', 'D', 'L').
/// </summary>
public sealed class FormBadgeItemViewModel
{
    private static readonly SolidColorBrush WinBrush = new(Color.FromArgb(255, 16, 124, 65));   // #107C41
    private static readonly SolidColorBrush DrawBrush = new(Color.FromArgb(255, 100, 116, 139)); // #64748B
    private static readonly SolidColorBrush LossBrush = new(Color.FromArgb(255, 239, 68, 68));  // #EF4444
    private static readonly SolidColorBrush TextBrush = new(Color.FromArgb(255, 255, 255, 255));

    public char Result { get; init; } = 'D';

    public SolidColorBrush BackgroundBrush => Result switch
    {
        'W' => WinBrush,
        'L' => LossBrush,
        _ => DrawBrush
    };

    public SolidColorBrush ForegroundBrush => TextBrush;

    public string Text => Result.ToString();

    public string TooltipText => Result switch
    {
        'W' => "Won",
        'L' => "Lost",
        _ => "Drew"
    };
}

/// <summary>
/// Presentation model for a single row in the league standings table.
/// Encapsulates rich formatting, home/away/followed highlighting, and form badges.
/// </summary>
public sealed class StandingRowItemViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush HomeBg = new(Color.FromArgb(40, 0, 120, 212));       // #280078D4
    private static readonly SolidColorBrush AwayBg = new(Color.FromArgb(40, 16, 124, 65));       // #28107C41
    private static readonly SolidColorBrush FollowedBg = new(Color.FromArgb(45, 56, 189, 248));   // #2D38BDF8
    private static readonly SolidColorBrush NormalBg = new(Color.FromArgb(0, 0, 0, 0));           // Transparent

    private static readonly SolidColorBrush HomeBorder = new(Color.FromArgb(140, 0, 120, 212));
    private static readonly SolidColorBrush AwayBorder = new(Color.FromArgb(140, 16, 124, 65));
    private static readonly SolidColorBrush FollowedBorder = new(Color.FromArgb(160, 56, 189, 248));
    private static readonly SolidColorBrush NormalBorder = new(Color.FromArgb(25, 255, 255, 255));

    private static readonly Thickness HighlightThickness = new(1.5);
    private static readonly Thickness NormalThickness = new(0.5);

    public int Rank { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public string TeamName { get; init; } = string.Empty;
    public string? TeamShortName { get; init; }
    public string? TeamLogoUrl { get; init; }
    public int Played { get; init; }
    public int Wins { get; init; }
    public int Draws { get; init; }
    public int Losses { get; init; }
    public int GoalsFor { get; init; }
    public int GoalsAgainst { get; init; }
    public int GoalDiff { get; init; }
    public int Points { get; init; }
    public string? Form { get; init; }
    public string? QualificationColor { get; init; }
    public bool IsHomeTeam { get; init; }
    public bool IsAwayTeam { get; init; }
    public bool IsFollowedTeam { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(TeamShortName) ? TeamShortName : TeamName;
    public string GoalDiffDisplay => GoalDiff > 0 ? $"+{GoalDiff}" : GoalDiff.ToString(CultureInfo.InvariantCulture);

    public Visibility HomeBadgeVisibility => IsHomeTeam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayBadgeVisibility => IsAwayTeam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LogoVisibility => !string.IsNullOrWhiteSpace(TeamLogoUrl) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FollowedStarVisibility => IsFollowedTeam ? Visibility.Visible : Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.ImageSource? TeamLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TeamLogoUrl)) return null;
            if (Uri.TryCreate(TeamLogoUrl, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public SolidColorBrush RowBackgroundBrush =>
        IsHomeTeam ? HomeBg : (IsAwayTeam ? AwayBg : (IsFollowedTeam ? FollowedBg : NormalBg));

    public SolidColorBrush RowBorderBrush =>
        IsHomeTeam ? HomeBorder : (IsAwayTeam ? AwayBorder : (IsFollowedTeam ? FollowedBorder : NormalBorder));

    public Thickness RowBorderThickness =>
        (IsHomeTeam || IsAwayTeam || IsFollowedTeam) ? HighlightThickness : NormalThickness;

    public IReadOnlyList<FormBadgeItemViewModel> FormBadges { get; }

    public StandingRowItemViewModel(StandingRowDto dto, bool isFollowed = false, bool isHomeTeam = false, bool isAwayTeam = false)
    {
        Rank = dto.Rank;
        TeamId = dto.TeamId;
        TeamName = dto.TeamName;
        TeamShortName = dto.TeamShortName;
        TeamLogoUrl = dto.TeamLogoUrl;
        Played = dto.Played;
        Wins = dto.Wins;
        Draws = dto.Draws;
        Losses = dto.Losses;
        GoalsFor = dto.GoalsFor;
        GoalsAgainst = dto.GoalsAgainst;
        GoalDiff = dto.GoalDiff;
        Points = dto.Points;
        Form = dto.Form;
        QualificationColor = dto.QualificationColor;
        IsHomeTeam = isHomeTeam || dto.IsHomeTeam;
        IsAwayTeam = isAwayTeam || dto.IsAwayTeam;
        IsFollowedTeam = isFollowed || dto.IsFollowedTeam;

        var badges = new List<FormBadgeItemViewModel>();
        if (!string.IsNullOrWhiteSpace(dto.Form))
        {
            var letters = dto.Form.Where(char.IsLetter).Select(char.ToUpperInvariant).TakeLast(5);
            foreach (var c in letters)
            {
                badges.Add(new FormBadgeItemViewModel { Result = c });
            }
        }
        FormBadges = badges;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Presentation model for a top scorer or top assist leader.
/// </summary>
public sealed class PlayerStatLeaderItemViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush GoalBrush = new(Color.FromArgb(255, 56, 189, 248));   // #38BDF8
    private static readonly SolidColorBrush AssistBrush = new(Color.FromArgb(255, 16, 185, 129)); // #10B981

    public int Rank { get; init; }
    public string PlayerId { get; init; } = string.Empty;
    public string PlayerName { get; init; } = string.Empty;
    public string TeamId { get; init; } = string.Empty;
    public string TeamName { get; init; } = string.Empty;
    public string? TeamLogoUrl { get; init; }
    public int? Goals { get; init; }
    public int? Assists { get; init; }
    public int? Penalties { get; init; }
    public int? MatchesPlayed { get; init; }
    public bool IsAssistLeader { get; init; }

    public string RankDisplay => $"#{Rank}";

    public string MainStatValue => IsAssistLeader
        ? (Assists?.ToString(CultureInfo.InvariantCulture) ?? "0")
        : (Goals?.ToString(CultureInfo.InvariantCulture) ?? "0");

    public string MainStatLabel => IsAssistLeader ? "assists" : "goals";

    public string SubStatDisplay
    {
        get
        {
            if (IsAssistLeader)
            {
                return MatchesPlayed.HasValue ? $"{MatchesPlayed.Value} matches" : string.Empty;
            }

            if (Penalties.HasValue && Penalties.Value > 0)
            {
                string penText = $"({Penalties.Value} pen)";
                return MatchesPlayed.HasValue ? $"{penText} • {MatchesPlayed.Value} matches" : penText;
            }

            return MatchesPlayed.HasValue ? $"{MatchesPlayed.Value} matches" : string.Empty;
        }
    }

    public Visibility SubStatVisibility => !string.IsNullOrEmpty(SubStatDisplay) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LogoVisibility => !string.IsNullOrWhiteSpace(TeamLogoUrl) ? Visibility.Visible : Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.ImageSource? TeamLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TeamLogoUrl)) return null;
            if (Uri.TryCreate(TeamLogoUrl, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public SolidColorBrush StatBrush => IsAssistLeader ? AssistBrush : GoalBrush;

    public PlayerStatLeaderItemViewModel(PlayerStatLeaderDto dto, bool isAssistLeader = false)
    {
        Rank = dto.Rank;
        PlayerId = dto.PlayerId;
        PlayerName = dto.PlayerName;
        TeamId = dto.TeamId;
        TeamName = dto.TeamName;
        TeamLogoUrl = dto.TeamLogoUrl;
        Goals = dto.Goals;
        Assists = dto.Assists;
        Penalties = dto.Penalties;
        MatchesPlayed = dto.MatchesPlayed;
        IsAssistLeader = isAssistLeader;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class CompetitionFixtureViewModel : INotifyPropertyChanged
{
    private static SolidColorBrush? _fixtureHighlightBg;
    private static SolidColorBrush? _fixtureNormalBg;
    private static SolidColorBrush? _fixtureHighlightBorder;
    private static SolidColorBrush? _fixtureNormalBorder;
    private static SolidColorBrush? _fixtureHighlightText;
    private static SolidColorBrush? _fixtureNormalText;

    private static SolidColorBrush? _statusLiveBrush;
    private static SolidColorBrush? _statusFinishedBrush;
    private static SolidColorBrush? _statusUpcomingBrush;

    private readonly CompetitionFixtureDto _dto;
    private readonly Action<string>? _onNavigate;

    private static SolidColorBrush? SafeCreateBrush(byte a, byte r, byte g, byte b)
    {
        try
        {
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch
        {
            return null;
        }
    }

    public bool IsHomeTeamHighlighted { get; set; }
    public bool IsAwayTeamHighlighted { get; set; }
    public bool IsRowHighlighted => IsHomeTeamHighlighted || IsAwayTeamHighlighted;

    public SolidColorBrush? RowBackgroundBrush =>
        IsRowHighlighted
            ? (_fixtureHighlightBg ??= SafeCreateBrush(30, 56, 189, 248))
            : (_fixtureNormalBg ??= SafeCreateBrush(0, 0, 0, 0));

    public SolidColorBrush? RowBorderBrush =>
        IsRowHighlighted
            ? (_fixtureHighlightBorder ??= SafeCreateBrush(255, 56, 189, 248))
            : (_fixtureNormalBorder ??= SafeCreateBrush(25, 255, 255, 255));

    public Thickness RowBorderThickness => IsRowHighlighted ? new Thickness(1.5) : new Thickness(0.5);

    public SolidColorBrush? HomeTeamForegroundBrush =>
        IsHomeTeamHighlighted
            ? (_fixtureHighlightText ??= SafeCreateBrush(255, 56, 189, 248))
            : (_fixtureNormalText ??= SafeCreateBrush(255, 255, 255, 255));

    public SolidColorBrush? AwayTeamForegroundBrush =>
        IsAwayTeamHighlighted
            ? (_fixtureHighlightText ??= SafeCreateBrush(255, 56, 189, 248))
            : (_fixtureNormalText ??= SafeCreateBrush(255, 255, 255, 255));

    public CompetitionFixtureViewModel(
        CompetitionFixtureDto dto,
        Action<string>? onNavigate = null,
        bool isHomeHighlighted = false,
        bool isAwayHighlighted = false)
    {
        _dto = dto ?? new CompetitionFixtureDto();
        _onNavigate = onNavigate;
        IsHomeTeamHighlighted = isHomeHighlighted;
        IsAwayTeamHighlighted = isAwayHighlighted;
    }

    public string Id => !string.IsNullOrWhiteSpace(_dto?.Id) ? _dto.Id : string.Empty;
    public string MatchId => Id;
    public string RoundName => !string.IsNullOrWhiteSpace(_dto?.RoundName) ? _dto.RoundName : "Fixture";
    public string? CompetitionId => _dto?.CompetitionId;
    public string? CompetitionName => _dto?.CompetitionName;
    public string? CompetitionLogoUrl => _dto?.CompetitionLogo;
    public string HomeTeamId => _dto?.HomeTeamId ?? string.Empty;
    public string AwayTeamId => _dto?.AwayTeamId ?? string.Empty;
    public string HomeTeamName => !string.IsNullOrWhiteSpace(_dto?.HomeTeamName) ? _dto.HomeTeamName : "Home";
    public string AwayTeamName => !string.IsNullOrWhiteSpace(_dto?.AwayTeamName) ? _dto.AwayTeamName : "Away";
    public string? HomeTeamLogo => _dto?.HomeTeamLogo;
    public string? AwayTeamLogo => _dto?.AwayTeamLogo;
    private bool? _use24HourTime;
    public bool? Use24HourTime
    {
        get => _use24HourTime;
        set
        {
            if (_use24HourTime != value)
            {
                _use24HourTime = value;
                OnPropertyChanged(nameof(ScoreOrTime));
                OnPropertyChanged(nameof(ScoreOrTimeDisplay));
            }
        }
    }

    public string DateDisplay
    {
        get
        {
            if (DateTimeOffset.TryParse(_dto?.KickoffUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt.ToLocalTime().ToString("MMM d");
            }
            return string.Empty;
        }
    }

    public string ScoreOrTime => ScoreOrTimeDisplay;

    public string ScoreOrTimeDisplay
    {
        get
        {
            if (_dto == null) return "–";
            if (!string.IsNullOrWhiteSpace(_dto.CustomScoreOrTime)) return _dto.CustomScoreOrTime;
            if (_dto.IsFinished || _dto.IsLive)
            {
                if (_dto.HomeScore.HasValue && _dto.AwayScore.HasValue) return $"{_dto.HomeScore.Value} - {_dto.AwayScore.Value}";
                if (!string.IsNullOrWhiteSpace(_dto.StatusText)) return _dto.StatusText;
            }
            if (DateTimeOffset.TryParse(_dto.KickoffUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                var local = dt.ToLocalTime();
                bool use24 = Use24HourTime ?? App.CurrentMainViewModel?.PreferencesService?.Current.Use24HourFormat ?? true;
                string timeFmt = use24 ? "HH:mm" : "h:mm tt";
                return $"{local:MMM d} • {local.ToString(timeFmt)}";
            }
            if (!string.IsNullOrWhiteSpace(_dto.ScoreOrTime) && _dto.ScoreOrTime != "–")
            {
                if (DateTime.TryParseExact(_dto.ScoreOrTime, new[] { "HH:mm", "H:mm", "h:mm tt", "hh:mm tt" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pt))
                {
                    bool use24 = Use24HourTime ?? App.CurrentMainViewModel?.PreferencesService?.Current.Use24HourFormat ?? true;
                    return pt.ToString(use24 ? "HH:mm" : "h:mm tt");
                }
                return _dto.ScoreOrTime;
            }
            return !string.IsNullOrWhiteSpace(_dto.StatusText) ? _dto.StatusText : "Upcoming";
        }
    }

    public TrayMatchDto ToTrayMatchDto()
    {
        var localTime = DateTimeOffset.TryParse(_dto?.KickoffUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : DateTimeOffset.UtcNow;

        return new TrayMatchDto
        {
            Id = !string.IsNullOrWhiteSpace(Id) ? (Id.StartsWith("fotmob_") ? Id : $"fotmob_{Id}") : $"fixture_{Guid.NewGuid():N}",
            LegacyId = Id,
            Competition = !string.IsNullOrWhiteSpace(CompetitionId) ? new CompetitionDto
            {
                Id = CompetitionId,
                LegacyId = CompetitionId,
                Name = CompetitionName ?? RoundName,
                LogoUrl = CompetitionLogoUrl
            } : null,
            HomeTeam = new TeamDto
            {
                Id = HomeTeamId,
                LegacyId = HomeTeamId,
                Name = HomeTeamName,
                ShortName = HomeTeamName,
                LogoUrl = HomeTeamLogo
            },
            AwayTeam = new TeamDto
            {
                Id = AwayTeamId,
                LegacyId = AwayTeamId,
                Name = AwayTeamName,
                ShortName = AwayTeamName,
                LogoUrl = AwayTeamLogo
            },
            Score = new ScoreDto
            {
                Home = _dto?.HomeScore ?? 0,
                Away = _dto?.AwayScore ?? 0,
                Display = (_dto?.HomeScore.HasValue == true && _dto?.AwayScore.HasValue == true)
                    ? $"{_dto.HomeScore.Value} - {_dto.AwayScore.Value}"
                    : "–"
            },
            State = _dto?.IsLive == true ? "LIVE" : (_dto?.IsFinished == true ? "FT" : "PREMATCH"),
            KickoffTime = localTime,
            Trust = new V2TrustMetadataDto
            {
                Provider = "live-telemetry",
                FreshnessState = "FRESH",
                DataAgeSeconds = 0,
                SourceObservedAt = DateTimeOffset.UtcNow
            }
        };
    }

    public System.Windows.Input.ICommand OpenHomeTeamHubCommand => new RelayCommand(() =>
    {
        if (!string.IsNullOrWhiteSpace(HomeTeamId))
        {
            App.OpenTeamHub(HomeTeamId, HomeTeamName, HomeTeamLogo);
        }
    });

    public System.Windows.Input.ICommand OpenAwayTeamHubCommand => new RelayCommand(() =>
    {
        if (!string.IsNullOrWhiteSpace(AwayTeamId))
        {
            App.OpenTeamHub(AwayTeamId, AwayTeamName, AwayTeamLogo);
        }
    });
    public string StatusText => _dto?.StatusText ?? string.Empty;
    public bool IsFinished => _dto?.IsFinished ?? false;
    public bool IsLive => _dto?.IsLive ?? false;

    public Visibility HomeLogoVisibility => !string.IsNullOrWhiteSpace(HomeTeamLogo) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayLogoVisibility => !string.IsNullOrWhiteSpace(AwayTeamLogo) ? Visibility.Visible : Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.ImageSource? HomeLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HomeTeamLogo)) return null;
            if (Uri.TryCreate(HomeTeamLogo, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public Microsoft.UI.Xaml.Media.ImageSource? AwayLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AwayTeamLogo)) return null;
            if (Uri.TryCreate(AwayTeamLogo, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public SolidColorBrush? StatusBrush =>
        IsLive
            ? (_statusLiveBrush ??= SafeCreateBrush(255, 239, 68, 68))
            : (IsFinished
                ? (_statusFinishedBrush ??= SafeCreateBrush(255, 148, 163, 184))
                : (_statusUpcomingBrush ??= SafeCreateBrush(255, 56, 189, 248)));

    public System.Windows.Input.ICommand NavigateCommand => new RelayCommand(() =>
    {
        if (!string.IsNullOrWhiteSpace(MatchId) && _onNavigate != null)
        {
            _onNavigate(MatchId);
        }
    });

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class KnockoutTieViewModel : INotifyPropertyChanged
{
    private readonly KnockoutTieDto _dto;

    public KnockoutTieViewModel(KnockoutTieDto dto)
    {
        _dto = dto;
    }

    public string StageName => _dto.StageName;
    public string HomeTeamName => !string.IsNullOrWhiteSpace(_dto.HomeTeamName) ? _dto.HomeTeamName : "TBD";
    public string AwayTeamName => !string.IsNullOrWhiteSpace(_dto.AwayTeamName) ? _dto.AwayTeamName : "TBD";
    public string? HomeTeamLogo => _dto.HomeTeamLogo;
    public string? AwayTeamLogo => _dto.AwayTeamLogo;
    public string? Leg1Score => _dto.Leg1Score;
    public string? Leg2Score => _dto.Leg2Score;
    public string? AggregateScore => _dto.AggregateScore;
    public string ScoreOrStatus => !string.IsNullOrWhiteSpace(_dto.ScoreOrStatus) && _dto.ScoreOrStatus != "vs" ? _dto.ScoreOrStatus : (!string.IsNullOrWhiteSpace(_dto.AggregateScore) ? _dto.AggregateScore : (!string.IsNullOrWhiteSpace(_dto.Leg1Score) ? _dto.Leg1Score : "vs"));
    public string? WinnerTeamName => _dto.WinnerTeamName;
    public bool IsFinished => _dto.IsFinished;
    public bool HasAggregateScore => !string.IsNullOrWhiteSpace(AggregateScore);
    public Visibility AggregateScoreVisibility => HasAggregateScore ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HomeLogoVisibility => !string.IsNullOrWhiteSpace(HomeTeamLogo) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayLogoVisibility => !string.IsNullOrWhiteSpace(AwayTeamLogo) ? Visibility.Visible : Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.ImageSource? HomeLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(HomeTeamLogo)) return null;
            if (Uri.TryCreate(HomeTeamLogo, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public Microsoft.UI.Xaml.Media.ImageSource? AwayLogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AwayTeamLogo)) return null;
            if (Uri.TryCreate(AwayTeamLogo, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public bool IsHomeWinner => !string.IsNullOrWhiteSpace(WinnerTeamName) &&
        (string.Equals(WinnerTeamName, HomeTeamName, StringComparison.OrdinalIgnoreCase) ||
         WinnerTeamName.Contains(HomeTeamName, StringComparison.OrdinalIgnoreCase));

    public bool IsAwayWinner => !string.IsNullOrWhiteSpace(WinnerTeamName) &&
        (string.Equals(WinnerTeamName, AwayTeamName, StringComparison.OrdinalIgnoreCase) ||
         WinnerTeamName.Contains(AwayTeamName, StringComparison.OrdinalIgnoreCase));

    public Windows.UI.Text.FontWeight HomeFontWeight => IsHomeWinner ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
    public Windows.UI.Text.FontWeight AwayFontWeight => IsAwayWinner ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;

    public SolidColorBrush HomeWinnerBrush => IsHomeWinner
        ? new SolidColorBrush(Color.FromArgb(255, 56, 189, 248))
        : new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));

    public SolidColorBrush AwayWinnerBrush => IsAwayWinner
        ? new SolidColorBrush(Color.FromArgb(255, 56, 189, 248))
        : new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class KnockoutStageViewModel : INotifyPropertyChanged
{
    public string StageName { get; set; } = string.Empty;
    public ObservableCollection<KnockoutTieViewModel> Ties { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
