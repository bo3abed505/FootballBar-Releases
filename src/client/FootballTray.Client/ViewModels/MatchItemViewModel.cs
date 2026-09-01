using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FootballTray.Core.Dto;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model for an individual match item in the Match Discovery view.
/// Encapsulates formatted display fields, live state badges, and relevance reasoning.
/// </summary>
public sealed class MatchItemViewModel : INotifyPropertyChanged
{
    private bool _isActiveTaskbarMatch;
    private bool _isRelevant;
    private bool _followedByTeam;
    private bool _followedByCompetition;
    private IReadOnlyList<string> _relevanceReasons = Array.Empty<string>();
    private string _relevanceDisplay = string.Empty;

    public string Id { get; init; } = string.Empty;
    public string HomeTeamId { get; init; } = string.Empty;
    public string AwayTeamId { get; init; } = string.Empty;
    public string HomeTeamName { get; init; } = string.Empty;
    public string AwayTeamName { get; init; } = string.Empty;
    public string HomeTeamShortName { get; init; } = string.Empty;
    public string AwayTeamShortName { get; init; } = string.Empty;
    public string? HomeTeamLogoUrl { get; init; }
    public string? AwayTeamLogoUrl { get; init; }
    public int? HomeScore { get; init; }
    public int? AwayScore { get; init; }
    public string ScoreDisplay { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string StateDisplay { get; init; } = string.Empty;
    public int ElapsedMinute { get; init; }
    public int ExtraMinute { get; init; }
    public DateTimeOffset KickoffTime { get; init; }
    public string KickoffDisplay { get; init; } = string.Empty;
    public string CompetitionId { get; init; } = string.Empty;
    public string CompetitionName { get; init; } = string.Empty;
    public string CompetitionCode { get; init; } = string.Empty;
    public string? CompetitionLogoUrl { get; init; }
    public bool IsLive { get; init; }
    public bool IsFinished { get; init; }
    public bool IsPrematch { get; init; }

    public bool IsActiveTaskbarMatch
    {
        get => _isActiveTaskbarMatch;
        set => SetProperty(ref _isActiveTaskbarMatch, value);
    }

    public bool IsRelevant
    {
        get => _isRelevant;
        private set => SetProperty(ref _isRelevant, value);
    }

    public bool FollowedByTeam
    {
        get => _followedByTeam;
        private set => SetProperty(ref _followedByTeam, value);
    }

    public bool FollowedByCompetition
    {
        get => _followedByCompetition;
        private set => SetProperty(ref _followedByCompetition, value);
    }

    public IReadOnlyList<string> RelevanceReasons
    {
        get => _relevanceReasons;
        private set => SetProperty(ref _relevanceReasons, value);
    }

    public string RelevanceDisplay
    {
        get => _relevanceDisplay;
        private set => SetProperty(ref _relevanceDisplay, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateRelevance(FixtureRelevanceResult relevance, bool isActiveTaskbar)
    {
        IsRelevant = relevance.IsRelevant;
        FollowedByTeam = relevance.FollowedByTeam;
        FollowedByCompetition = relevance.FollowedByCompetition;
        RelevanceReasons = relevance.Reasons ?? Array.Empty<string>();
        IsActiveTaskbarMatch = isActiveTaskbar;

        if (relevance.Reasons != null && relevance.Reasons.Count > 0)
        {
            RelevanceDisplay = string.Join(" • ", relevance.Reasons);
        }
        else
        {
            RelevanceDisplay = string.Empty;
        }
    }

    public static MatchItemViewModel FromDto(TrayMatchDto match, FixtureRelevanceResult relevance, bool isActiveTaskbar)
    {
        ArgumentNullException.ThrowIfNull(match);

        string homeName = match.HomeTeam?.Name ?? "Home";
        string awayName = match.AwayTeam?.Name ?? "Away";
        string homeShort = match.HomeTeam?.ShortName ?? (homeName.Length <= 3 ? homeName.ToUpperInvariant() : homeName[..3].ToUpperInvariant());
        string awayShort = match.AwayTeam?.ShortName ?? (awayName.Length <= 3 ? awayName.ToUpperInvariant() : awayName[..3].ToUpperInvariant());

        string upperState = (match.State ?? "PREMATCH").ToUpperInvariant();
        bool isLive = upperState is "LIVE" or "1H" or "2H" or "HT" or "ET" or "P";
        bool isFinished = upperState is "FT" or "AET" or "PEN" or "FINISHED";
        bool isPrematch = upperState is "PREMATCH" or "NS" or "TBD";

        string stateDisplay;
        if (upperState == "HT")
        {
            stateDisplay = "Half Time (HT)";
        }
        else if (isLive)
        {
            stateDisplay = match.ExtraMinute > 0
                ? $"LIVE {match.ElapsedMinute}+{match.ExtraMinute}'"
                : $"LIVE {match.ElapsedMinute}'";
        }
        else if (isFinished)
        {
            stateDisplay = "Full Time (FT)";
        }
        else if (upperState is "POSTP" or "POSTPONED")
        {
            stateDisplay = "Postponed";
        }
        else if (upperState is "CANC" or "CANCELLED")
        {
            stateDisplay = "Cancelled";
        }
        else
        {
            stateDisplay = match.KickoffTime.ToLocalTime().ToString("HH:mm");
        }

        string scoreDisplay;
        if (isPrematch)
        {
            scoreDisplay = "vs";
        }
        else
        {
            scoreDisplay = $"{match.Score?.Home ?? 0} – {match.Score?.Away ?? 0}";
        }

        string kickoffDisplay = match.KickoffTime.ToLocalTime().ToString("ddd, MMM d • HH:mm");

        var vm = new MatchItemViewModel
        {
            Id = match.Id,
            HomeTeamId = match.HomeTeam?.Id ?? string.Empty,
            AwayTeamId = match.AwayTeam?.Id ?? string.Empty,
            HomeTeamName = homeName,
            AwayTeamName = awayName,
            HomeTeamShortName = homeShort,
            AwayTeamShortName = awayShort,
            HomeTeamLogoUrl = match.HomeTeam?.LogoUrl,
            AwayTeamLogoUrl = match.AwayTeam?.LogoUrl,
            HomeScore = match.Score?.Home,
            AwayScore = match.Score?.Away,
            ScoreDisplay = scoreDisplay,
            State = upperState,
            StateDisplay = stateDisplay,
            ElapsedMinute = match.ElapsedMinute,
            ExtraMinute = match.ExtraMinute,
            KickoffTime = match.KickoffTime,
            KickoffDisplay = kickoffDisplay,
            CompetitionId = match.Competition?.Id ?? string.Empty,
            CompetitionName = match.Competition?.Name ?? "Competition",
            CompetitionCode = match.Competition?.Code ?? "COMP",
            CompetitionLogoUrl = null,
            IsLive = isLive,
            IsFinished = isFinished,
            IsPrematch = isPrematch
        };

        vm.UpdateRelevance(relevance, isActiveTaskbar);
        return vm;
    }

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
