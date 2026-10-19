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
    private bool _isHomeTeamFollowed;
    private bool _isAwayTeamFollowed;
    private bool _isCompetitionFollowed;
    private IReadOnlyList<string> _relevanceReasons = Array.Empty<string>();
    private string _relevanceDisplay = string.Empty;

    private int? _homeScore;
    private int? _awayScore;
    private string _scoreDisplay = string.Empty;
    private string _state = string.Empty;
    private string _stateDisplay = string.Empty;
    private int _elapsedMinute;
    private int _extraMinute;
    private string _kickoffDisplay = string.Empty;
    private bool _isLive;
    private bool _isFinished;
    private bool _isPrematch;

    public string Id { get; init; } = string.Empty;
    public string HomeTeamId { get; init; } = string.Empty;
    public string AwayTeamId { get; init; } = string.Empty;
    public string HomeTeamName { get; init; } = string.Empty;
    public string AwayTeamName { get; init; } = string.Empty;
    public string HomeTeamShortName { get; init; } = string.Empty;
    public string AwayTeamShortName { get; init; } = string.Empty;
    public string? HomeTeamLogoUrl { get; init; }
    public string? AwayTeamLogoUrl { get; init; }

    public int? HomeScore
    {
        get => _homeScore;
        private set => SetProperty(ref _homeScore, value);
    }

    public int? AwayScore
    {
        get => _awayScore;
        private set => SetProperty(ref _awayScore, value);
    }

    public string ScoreDisplay
    {
        get => _scoreDisplay;
        private set => SetProperty(ref _scoreDisplay, value);
    }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string StateDisplay
    {
        get => _stateDisplay;
        private set => SetProperty(ref _stateDisplay, value);
    }

    public int ElapsedMinute
    {
        get => _elapsedMinute;
        private set => SetProperty(ref _elapsedMinute, value);
    }

    public int ExtraMinute
    {
        get => _extraMinute;
        private set => SetProperty(ref _extraMinute, value);
    }

    public DateTimeOffset KickoffTime { get; init; }

    public string KickoffDisplay
    {
        get => _kickoffDisplay;
        private set => SetProperty(ref _kickoffDisplay, value);
    }

    public string CompetitionId { get; init; } = string.Empty;
    public string CompetitionName { get; init; } = string.Empty;
    public string CompetitionCode { get; init; } = string.Empty;
    public string? CompetitionCountry { get; init; }
    public string? CompetitionLogoUrl { get; init; }

    public bool IsLive
    {
        get => _isLive;
        private set
        {
            if (SetProperty(ref _isLive, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLiveBadgeVisible)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNormalBadgeVisible)));
            }
        }
    }

    public bool IsFinished
    {
        get => _isFinished;
        private set => SetProperty(ref _isFinished, value);
    }

    public bool IsPrematch
    {
        get => _isPrematch;
        private set => SetProperty(ref _isPrematch, value);
    }

    public Microsoft.UI.Xaml.Visibility IsLiveBadgeVisible =>
        IsLive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility IsNormalBadgeVisible =>
        IsLive ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public bool IsActiveTaskbarMatch
    {
        get => _isActiveTaskbarMatch;
        set
        {
            if (SetProperty(ref _isActiveTaskbarMatch, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinToolTipText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinButtonOpacity)));
            }
        }
    }

    public string PinGlyph => _isActiveTaskbarMatch ? "\uE840" : "\uE718";
    public string PinText => _isActiveTaskbarMatch ? "Pinned" : "Pin";
    public string PinToolTipText => _isActiveTaskbarMatch ? "Pinned to taskbar. Click to unpin." : "Pin this match to the taskbar.";
    public double PinButtonOpacity => _isActiveTaskbarMatch ? 1.0 : 0.7;

    public bool IsHomeTeamFollowed
    {
        get => _isHomeTeamFollowed;
        set
        {
            if (SetProperty(ref _isHomeTeamFollowed, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HomeStarGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HomeStarOpacity)));
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AwayStarGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AwayStarOpacity)));
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompetitionStarGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompetitionStarOpacity)));
            }
        }
    }

    public string HomeStarGlyph => _isHomeTeamFollowed ? "\uE735" : "\uE734";
    public string AwayStarGlyph => _isAwayTeamFollowed ? "\uE735" : "\uE734";
    public string CompetitionStarGlyph => _isCompetitionFollowed ? "\uE735" : "\uE734";

    public double HomeStarOpacity => _isHomeTeamFollowed ? 1.0 : 0.45;
    public double AwayStarOpacity => _isAwayTeamFollowed ? 1.0 : 0.45;
    public double CompetitionStarOpacity => _isCompetitionFollowed ? 1.0 : 0.45;

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

    public void UpdateFollowStates(bool homeFollowed, bool awayFollowed, bool compFollowed)
    {
        IsHomeTeamFollowed = homeFollowed;
        IsAwayTeamFollowed = awayFollowed;
        IsCompetitionFollowed = compFollowed;
        FollowedByTeam = homeFollowed || awayFollowed;
        FollowedByCompetition = compFollowed;
        IsRelevant = FollowedByTeam || FollowedByCompetition;
    }

    /// <summary>
    /// Updates presentation state in-place from another MatchItemViewModel.
    /// Only triggers PropertyChanged for values that have genuinely mutated, preventing UI flicker.
    /// </summary>
    public bool UpdateFromSource(MatchItemViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        bool changed = false;

        if (SetProperty(ref _homeScore, source.HomeScore, nameof(HomeScore))) changed = true;
        if (SetProperty(ref _awayScore, source.AwayScore, nameof(AwayScore))) changed = true;
        if (SetProperty(ref _scoreDisplay, source.ScoreDisplay, nameof(ScoreDisplay))) changed = true;
        if (SetProperty(ref _state, source.State, nameof(State))) changed = true;
        if (SetProperty(ref _stateDisplay, source.StateDisplay, nameof(StateDisplay))) changed = true;
        if (SetProperty(ref _elapsedMinute, source.ElapsedMinute, nameof(ElapsedMinute))) changed = true;
        if (SetProperty(ref _extraMinute, source.ExtraMinute, nameof(ExtraMinute))) changed = true;
        if (SetProperty(ref _kickoffDisplay, source.KickoffDisplay, nameof(KickoffDisplay))) changed = true;

        if (SetProperty(ref _isLive, source.IsLive, nameof(IsLive)))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLiveBadgeVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNormalBadgeVisible)));
            changed = true;
        }

        if (SetProperty(ref _isFinished, source.IsFinished, nameof(IsFinished))) changed = true;
        if (SetProperty(ref _isPrematch, source.IsPrematch, nameof(IsPrematch))) changed = true;

        if (IsActiveTaskbarMatch != source.IsActiveTaskbarMatch)
        {
            IsActiveTaskbarMatch = source.IsActiveTaskbarMatch;
            changed = true;
        }

        if (IsHomeTeamFollowed != source.IsHomeTeamFollowed)
        {
            IsHomeTeamFollowed = source.IsHomeTeamFollowed;
            changed = true;
        }

        if (IsAwayTeamFollowed != source.IsAwayTeamFollowed)
        {
            IsAwayTeamFollowed = source.IsAwayTeamFollowed;
            changed = true;
        }

        if (IsCompetitionFollowed != source.IsCompetitionFollowed)
        {
            IsCompetitionFollowed = source.IsCompetitionFollowed;
            changed = true;
        }

        if (IsRelevant != source.IsRelevant)
        {
            IsRelevant = source.IsRelevant;
            changed = true;
        }

        if (FollowedByTeam != source.FollowedByTeam)
        {
            FollowedByTeam = source.FollowedByTeam;
            changed = true;
        }

        if (FollowedByCompetition != source.FollowedByCompetition)
        {
            FollowedByCompetition = source.FollowedByCompetition;
            changed = true;
        }

        if (SetProperty(ref _relevanceDisplay, source.RelevanceDisplay, nameof(RelevanceDisplay))) changed = true;

        return changed;
    }

    public static MatchItemViewModel FromDto(
        TrayMatchDto match,
        FixtureRelevanceResult relevance,
        bool isActiveTaskbar,
        bool use24HourFormat = true,
        bool homeFollowed = false,
        bool awayFollowed = false,
        bool compFollowed = false)
    {
        ArgumentNullException.ThrowIfNull(match);

        string homeName = match.HomeTeam?.Name ?? "Home";
        string awayName = match.AwayTeam?.Name ?? "Away";
        string homeShort = match.HomeTeam?.ShortName ?? (homeName.Length <= 3 ? homeName.ToUpperInvariant() : homeName[..3].ToUpperInvariant());
        string awayShort = match.AwayTeam?.ShortName ?? (awayName.Length <= 3 ? awayName.ToUpperInvariant() : awayName[..3].ToUpperInvariant());

        string upperState = (match.State ?? "PREMATCH").Trim().ToUpperInvariant();

        // API-Football short status codes:
        // 1H (First Half), 2H (Second Half), HT (Halftime), ET (Extra Time), BT (Break Time),
        // P (Penalty in progress), SUSP (Suspended), INT (Interrupted)
        // Also support verbose variants LIVE, IN_PLAY, HALFTIME, EXTRA_TIME, PENALTIES
        bool isExplicitLiveCode = upperState is "LIVE" or "FIRST_HALF" or "SECOND_HALF" or "1H" or "2H" or "HT" or "ET" or "BT" or "P" or "SUSP" or "INT"
            or "IN_PLAY" or "HALFTIME" or "EXTRA_TIME" or "PENALTIES";

        bool isFinished = upperState is "FT" or "AET" or "PEN" or "FINISHED";
        bool isCancelledOrPostponed = upperState is "POSTP" or "POSTPONED" or "CANC" or "CANCELLED" or "ABD" or "AWD" or "WO";

        bool hasActiveScore = match.Score != null && (match.Score.Home > 0 || match.Score.Away > 0);

        // Safe check: Active live state codes OR active scores past/at kickoff time when not finished
        bool isLive = !isFinished && !isCancelledOrPostponed &&
            (isExplicitLiveCode || (hasActiveScore && match.KickoffTime <= DateTimeOffset.UtcNow.AddMinutes(15)));

        bool isPrematch = !isLive && !isFinished && !isCancelledOrPostponed &&
            (upperState is "PREMATCH" or "NS" or "TBD" || (!hasActiveScore && match.KickoffTime > DateTimeOffset.UtcNow));

        string timeFormat = use24HourFormat ? "HH:mm" : "h:mm tt";

        string stateDisplay;
        if (upperState == "HT" || upperState == "HALFTIME")
        {
            stateDisplay = "Half Time (HT)";
        }
        else if (upperState is "SUSP")
        {
            stateDisplay = "LIVE (SUSP)";
        }
        else if (upperState is "INT")
        {
            stateDisplay = "LIVE (INT)";
        }
        else if (upperState is "BT")
        {
            stateDisplay = "LIVE (BT)";
        }
        else if (upperState is "P" or "PENALTIES")
        {
            stateDisplay = "LIVE (PEN)";
        }
        else if (isLive)
        {
            stateDisplay = match.ElapsedMinute > 0
                ? (match.ExtraMinute > 0 ? $"LIVE {match.ElapsedMinute}+{match.ExtraMinute}'" : $"LIVE {match.ElapsedMinute}'")
                : "LIVE";
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
            stateDisplay = match.KickoffTime.ToLocalTime().ToString(timeFormat);
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

        string kickoffDisplay = match.KickoffTime.ToLocalTime().ToString($"ddd, MMM d • {timeFormat}");

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
            CompetitionCode = (!string.IsNullOrWhiteSpace(match.Competition?.Code) && !string.Equals(match.Competition.Code, "COMP", StringComparison.OrdinalIgnoreCase))
                ? match.Competition.Code
                : (!string.IsNullOrWhiteSpace(match.Competition?.Country) && !string.Equals(match.Competition.Country, "COMP", StringComparison.OrdinalIgnoreCase) ? match.Competition.Country : string.Empty),
            CompetitionCountry = (!string.IsNullOrWhiteSpace(match.Competition?.Country) && !string.Equals(match.Competition.Country, "COMP", StringComparison.OrdinalIgnoreCase))
                ? match.Competition.Country
                : null,
            CompetitionLogoUrl = match.Competition?.LogoUrl,
            IsLive = isLive,
            IsFinished = isFinished,
            IsPrematch = isPrematch
        };

        vm.UpdateRelevance(relevance, isActiveTaskbar);
        vm.UpdateFollowStates(homeFollowed, awayFollowed, compFollowed);
        return vm;
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
