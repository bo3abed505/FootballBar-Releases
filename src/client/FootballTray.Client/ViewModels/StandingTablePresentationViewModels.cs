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

    public StandingRowItemViewModel(StandingRowDto dto, bool isFollowed = false)
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
        IsHomeTeam = dto.IsHomeTeam;
        IsAwayTeam = dto.IsAwayTeam;
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
