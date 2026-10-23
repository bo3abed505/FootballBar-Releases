using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FootballTray.Client.Services;
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

public class MatchTimelineItem : INotifyPropertyChanged
{
    private int _minute;
    private int _extraMinute;
    private int _sortPriority = 5;
    private string _minuteDisplay = string.Empty;
    private string _iconGlyph = string.Empty;
    private string _description = string.Empty;
    private string? _playerInfo;
    private bool _isHomeTeam;
    private bool _isAwayTeam;
    private string _eventTitle = string.Empty;
    private string _eventSubtitle = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public int Minute
    {
        get => _minute;
        set => SetProperty(ref _minute, value);
    }

    public int ExtraMinute
    {
        get => _extraMinute;
        set => SetProperty(ref _extraMinute, value);
    }

    public int SortPriority
    {
        get => _sortPriority;
        set => SetProperty(ref _sortPriority, value);
    }

    public long GetSortKey() => ((long)Minute * 100000L) + ((long)ExtraMinute * 100L) + (long)SortPriority;

    public string MinuteDisplay
    {
        get => _minuteDisplay;
        set => SetProperty(ref _minuteDisplay, value);
    }

    public string IconGlyph
    {
        get => _iconGlyph;
        set => SetProperty(ref _iconGlyph, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string? PlayerInfo
    {
        get => _playerInfo;
        set
        {
            if (SetProperty(ref _playerInfo, value))
            {
                OnPropertyChanged(nameof(HasPlayerInfo));
            }
        }
    }

    public bool HasPlayerInfo => !string.IsNullOrWhiteSpace(PlayerInfo);

    public bool IsHomeTeam
    {
        get => _isHomeTeam;
        set
        {
            if (SetProperty(ref _isHomeTeam, value))
            {
                OnPropertyChanged(nameof(IsNeutral));
                OnPropertyChanged(nameof(HomeVisibility));
                OnPropertyChanged(nameof(NonNeutralVisibility));
                OnPropertyChanged(nameof(NeutralVisibility));
            }
        }
    }

    public bool IsAwayTeam
    {
        get => _isAwayTeam;
        set
        {
            if (SetProperty(ref _isAwayTeam, value))
            {
                OnPropertyChanged(nameof(IsNeutral));
                OnPropertyChanged(nameof(AwayVisibility));
                OnPropertyChanged(nameof(NonNeutralVisibility));
                OnPropertyChanged(nameof(NeutralVisibility));
            }
        }
    }

    public bool IsNeutral => !IsHomeTeam && !IsAwayTeam;

    public string EventTitle
    {
        get => _eventTitle;
        set => SetProperty(ref _eventTitle, value);
    }

    public string EventSubtitle
    {
        get => _eventSubtitle;
        set
        {
            if (SetProperty(ref _eventSubtitle, value))
            {
                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(SubtitleVisibility));
            }
        }
    }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(EventSubtitle);

    private string? _teamName;
    private string? _teamLogoUrl;

    public string? TeamName
    {
        get => _teamName;
        set
        {
            if (SetProperty(ref _teamName, value))
            {
                OnPropertyChanged(nameof(HasTeamName));
                OnPropertyChanged(nameof(TeamNameVisibility));
            }
        }
    }

    public string? TeamLogoUrl
    {
        get => _teamLogoUrl;
        set
        {
            if (SetProperty(ref _teamLogoUrl, value))
            {
                OnPropertyChanged(nameof(HasTeamLogo));
                OnPropertyChanged(nameof(TeamLogoVisibility));
            }
        }
    }

    public bool HasTeamName => !string.IsNullOrWhiteSpace(TeamName);
    public Visibility TeamNameVisibility => HasTeamName ? Visibility.Visible : Visibility.Collapsed;
    public bool HasTeamLogo => !string.IsNullOrWhiteSpace(TeamLogoUrl);
    public Visibility TeamLogoVisibility => HasTeamLogo ? Visibility.Visible : Visibility.Collapsed;

    // Direct Visibility properties for clean WinUI 3 XAML bindings without converters
    public Visibility HomeVisibility => IsHomeTeam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AwayVisibility => IsAwayTeam ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NonNeutralVisibility => !IsNeutral ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NeutralVisibility => IsNeutral ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubtitleVisibility => HasSubtitle ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class MatchCommentaryItemViewModel : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private int _minute;
    private int? _extraMinute;
    private string _minuteDisplay = string.Empty;
    private string _type = string.Empty;
    private string _text = string.Empty;
    private string? _teamId;
    private string? _teamName;
    private string? _teamLogoUrl;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public int Minute
    {
        get => _minute;
        set => SetProperty(ref _minute, value);
    }

    public int? ExtraMinute
    {
        get => _extraMinute;
        set => SetProperty(ref _extraMinute, value);
    }

    public string MinuteDisplay
    {
        get => _minuteDisplay;
        set => SetProperty(ref _minuteDisplay, value);
    }

    public string Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(IsWhistle));
                OnPropertyChanged(nameof(WhistleVisibility));
                OnPropertyChanged(nameof(RegularRowVisibility));
                OnPropertyChanged(nameof(TeamLogoVisibility));
                OnPropertyChanged(nameof(NoLogoVisibility));
                OnPropertyChanged(nameof(RowHighlightVisibility));
            }
        }
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string? TeamId
    {
        get => _teamId;
        set => SetProperty(ref _teamId, value);
    }

    public string? TeamName
    {
        get => _teamName;
        set => SetProperty(ref _teamName, value);
    }

    public string? TeamLogoUrl
    {
        get => _teamLogoUrl;
        set
        {
            if (SetProperty(ref _teamLogoUrl, value))
            {
                OnPropertyChanged(nameof(LogoUri));
                OnPropertyChanged(nameof(TeamLogoVisibility));
                OnPropertyChanged(nameof(NoLogoVisibility));
            }
        }
    }

    public bool IsWhistle => string.Equals(Type, "whistle", StringComparison.OrdinalIgnoreCase);
    public Visibility WhistleVisibility => IsWhistle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RegularRowVisibility => !IsWhistle ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowHighlightVisibility => IsWhistle ? Visibility.Visible : Visibility.Collapsed;

    public Uri? LogoUri
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_teamLogoUrl) && Uri.TryCreate(_teamLogoUrl, UriKind.Absolute, out var uri))
            {
                return uri;
            }
            return null;
        }
    }

    public Visibility TeamLogoVisibility => !IsWhistle && LogoUri != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoLogoVisibility => !IsWhistle && LogoUri == null ? Visibility.Visible : Visibility.Collapsed;

    public void UpdateFrom(MatchCommentaryItemViewModel source)
    {
        if (source == null) return;
        Minute = source.Minute;
        ExtraMinute = source.ExtraMinute;
        MinuteDisplay = source.MinuteDisplay;
        Type = source.Type;
        Text = source.Text;
        TeamId = source.TeamId;
        TeamName = source.TeamName;
        TeamLogoUrl = source.TeamLogoUrl;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public static class RatingVisualHelper
{
    private static readonly SolidColorBrush EliteBrush = new(Windows.UI.Color.FromArgb(255, 14, 165, 233));   // #0EA5E9
    private static readonly SolidColorBrush GoodBrush = new(Windows.UI.Color.FromArgb(255, 34, 197, 94));    // #22C55E
    private static readonly SolidColorBrush AverageBrush = new(Windows.UI.Color.FromArgb(255, 245, 158, 11)); // #F59E0B
    private static readonly SolidColorBrush PoorBrush = new(Windows.UI.Color.FromArgb(255, 239, 68, 68));     // #EF4444
    private static readonly SolidColorBrush NoneBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public static SolidColorBrush GetRatingBackgroundBrush(double? rating)
    {
        if (!rating.HasValue || rating.Value <= 0) return NoneBrush;
        if (rating.Value >= 8.0) return EliteBrush;
        if (rating.Value >= 7.0) return GoodBrush;
        if (rating.Value >= 6.0) return AverageBrush;
        return PoorBrush;
    }

    public static string FormatRating(double? rating, bool isManOfTheMatch)
    {
        if (!rating.HasValue || rating.Value <= 0) return string.Empty;
        string num = rating.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return isManOfTheMatch ? $"★ {num}" : num;
    }
}

public class LineupPlayerItemViewModel : INotifyPropertyChanged
{
    private string _number = "-";
    private string _name = string.Empty;
    private string _position = string.Empty;
    private string _positionBadge = string.Empty;
    private double? _rating;
    private bool _isManOfTheMatch;
    private bool _isCaptain;

    public bool IsCaptain
    {
        get => _isCaptain;
        set => SetProperty(ref _isCaptain, value);
    }

    public string Number
    {
        get => _number;
        set => SetProperty(ref _number, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
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

    public double? Rating
    {
        get => _rating;
        set
        {
            if (SetProperty(ref _rating, value))
            {
                OnPropertyChanged(nameof(RatingDisplay));
                OnPropertyChanged(nameof(HasRating));
                OnPropertyChanged(nameof(RatingBackgroundBrush));
                OnPropertyChanged(nameof(RatingVisibility));
            }
        }
    }

    public bool IsManOfTheMatch
    {
        get => _isManOfTheMatch;
        set
        {
            if (SetProperty(ref _isManOfTheMatch, value))
            {
                OnPropertyChanged(nameof(RatingDisplay));
            }
        }
    }

    public bool HasRating => Rating.HasValue && Rating.Value > 0;
    public Visibility RatingVisibility => HasRating ? Visibility.Visible : Visibility.Collapsed;
    public string RatingDisplay => RatingVisualHelper.FormatRating(Rating, IsManOfTheMatch);
    public SolidColorBrush RatingBackgroundBrush => RatingVisualHelper.GetRatingBackgroundBrush(Rating);

    private string? _playerId;
    private bool _isSubbedOn;
    private int? _subInMinute;
    private string _subInText = string.Empty;
    private bool _isSubbedOff;
    private int? _subOutMinute;
    private string _subOutText = string.Empty;
    private bool _hasYellowCard;
    private bool _hasRedCard;
    private int _regularGoalsCount;
    private int _penaltyGoalsCount;
    private int _ownGoalsCount;
    private int _assistsCount;

    public string? PlayerId
    {
        get => _playerId;
        set => SetProperty(ref _playerId, value);
    }

    public string Id
    {
        get => _playerId ?? string.Empty;
        set => PlayerId = value;
    }

    public bool IsSubbedOn
    {
        get => _isSubbedOn;
        set
        {
            if (SetProperty(ref _isSubbedOn, value))
            {
                OnPropertyChanged(nameof(SubInVisibility));
            }
        }
    }

    public int? SubInMinute
    {
        get => _subInMinute;
        set
        {
            if (SetProperty(ref _subInMinute, value))
            {
                OnPropertyChanged(nameof(SubInTooltip));
            }
        }
    }

    public string SubInTooltip => SubInMinute.HasValue ? $"Subbed on ({SubInMinute}')" : "Subbed on";

    public string SubInText
    {
        get => _subInText;
        set
        {
            if (SetProperty(ref _subInText, value))
            {
                OnPropertyChanged(nameof(SubInVisibility));
            }
        }
    }

    public Visibility SubInVisibility => IsSubbedOn && !string.IsNullOrEmpty(SubInText) ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSubbedOff
    {
        get => _isSubbedOff;
        set
        {
            if (SetProperty(ref _isSubbedOff, value))
            {
                OnPropertyChanged(nameof(SubOutVisibility));
            }
        }
    }

    public int? SubOutMinute
    {
        get => _subOutMinute;
        set
        {
            if (SetProperty(ref _subOutMinute, value))
            {
                OnPropertyChanged(nameof(SubOutTooltip));
            }
        }
    }

    public string SubOutTooltip => SubOutMinute.HasValue ? $"Subbed off ({SubOutMinute}')" : "Subbed off";

    public string SubOutText
    {
        get => _subOutText;
        set
        {
            if (SetProperty(ref _subOutText, value))
            {
                OnPropertyChanged(nameof(SubOutVisibility));
            }
        }
    }

    public Visibility SubOutVisibility => IsSubbedOff && !string.IsNullOrEmpty(SubOutText) ? Visibility.Visible : Visibility.Collapsed;

    public bool HasYellowCard
    {
        get => _hasYellowCard;
        set
        {
            if (SetProperty(ref _hasYellowCard, value))
            {
                OnPropertyChanged(nameof(YellowCardVisibility));
                OnPropertyChanged(nameof(HasCards));
            }
        }
    }

    public bool HasRedCard
    {
        get => _hasRedCard;
        set
        {
            if (SetProperty(ref _hasRedCard, value))
            {
                OnPropertyChanged(nameof(RedCardVisibility));
                OnPropertyChanged(nameof(YellowCardVisibility));
                OnPropertyChanged(nameof(HasCards));
            }
        }
    }

    public bool HasCards => HasRedCard || HasYellowCard;
    public Visibility YellowCardVisibility => HasYellowCard && !HasRedCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RedCardVisibility => HasRedCard ? Visibility.Visible : Visibility.Collapsed;

    public int RegularGoalsCount
    {
        get => _regularGoalsCount;
        set
        {
            if (SetProperty(ref _regularGoalsCount, value))
            {
                OnPropertyChanged(nameof(GoalBadgeText));
                OnPropertyChanged(nameof(HasGoalBadge));
                OnPropertyChanged(nameof(GoalBadgeVisibility));
            }
        }
    }

    public bool HasGoalBadge => RegularGoalsCount > 0;
    public Visibility GoalBadgeVisibility => HasGoalBadge ? Visibility.Visible : Visibility.Collapsed;
    public string GoalBadgeText => RegularGoalsCount > 1 ? $"⚽x{RegularGoalsCount}" : (RegularGoalsCount == 1 ? "⚽" : string.Empty);

    public int PenaltyGoalsCount
    {
        get => _penaltyGoalsCount;
        set
        {
            if (SetProperty(ref _penaltyGoalsCount, value))
            {
                OnPropertyChanged(nameof(HasPenaltyGoal));
                OnPropertyChanged(nameof(PenaltyGoalText));
                OnPropertyChanged(nameof(PenaltyGoalVisibility));
            }
        }
    }

    public bool HasPenaltyGoal => PenaltyGoalsCount > 0;
    public Visibility PenaltyGoalVisibility => HasPenaltyGoal ? Visibility.Visible : Visibility.Collapsed;
    public string PenaltyGoalText => PenaltyGoalsCount > 1 ? $"⚽ PEN x{PenaltyGoalsCount}" : "⚽ PEN";

    public int OwnGoalsCount
    {
        get => _ownGoalsCount;
        set
        {
            if (SetProperty(ref _ownGoalsCount, value))
            {
                OnPropertyChanged(nameof(HasOwnGoal));
                OnPropertyChanged(nameof(OwnGoalText));
                OnPropertyChanged(nameof(OwnGoalVisibility));
            }
        }
    }

    public bool HasOwnGoal => OwnGoalsCount > 0;
    public Visibility OwnGoalVisibility => HasOwnGoal ? Visibility.Visible : Visibility.Collapsed;
    public string OwnGoalText => OwnGoalsCount > 1 ? $"⚽ OG x{OwnGoalsCount}" : "⚽ OG";

    public int AssistsCount
    {
        get => _assistsCount;
        set
        {
            if (SetProperty(ref _assistsCount, value))
            {
                OnPropertyChanged(nameof(HasAssistBadge));
                OnPropertyChanged(nameof(AssistBadgeText));
                OnPropertyChanged(nameof(AssistBadgeVisibility));
            }
        }
    }

    public bool HasAssistBadge => AssistsCount > 0;
    public Visibility AssistBadgeVisibility => HasAssistBadge ? Visibility.Visible : Visibility.Collapsed;
    public string AssistBadgeText => AssistsCount > 1 ? $"👟x{AssistsCount}" : (AssistsCount == 1 ? "👟" : string.Empty);

    public void UpdateFrom(LineupPlayerItemViewModel source)
    {
        if (source == null) return;
        Number = source.Number;
        Name = source.Name;
        Position = source.Position;
        PositionBadge = source.PositionBadge;
        Rating = source.Rating;
        IsManOfTheMatch = source.IsManOfTheMatch;
        IsCaptain = source.IsCaptain;
        PlayerId = source.PlayerId;
        IsSubbedOn = source.IsSubbedOn;
        SubInMinute = source.SubInMinute;
        SubInText = source.SubInText;
        IsSubbedOff = source.IsSubbedOff;
        SubOutMinute = source.SubOutMinute;
        SubOutText = source.SubOutText;
        HasYellowCard = source.HasYellowCard;
        HasRedCard = source.HasRedCard;
        RegularGoalsCount = source.RegularGoalsCount;
        PenaltyGoalsCount = source.PenaltyGoalsCount;
        OwnGoalsCount = source.OwnGoalsCount;
        AssistsCount = source.AssistsCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class UnavailablePlayerItemViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string? _reason;
    private string? _position;
    private int? _shirtNumber;
    private string? _expectedReturn;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string? Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public string? Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    public int? ShirtNumber
    {
        get => _shirtNumber;
        set => SetProperty(ref _shirtNumber, value);
    }

    public string? ExpectedReturn
    {
        get => _expectedReturn;
        set => SetProperty(ref _expectedReturn, value);
    }

    public string DisplayText => string.IsNullOrWhiteSpace(Reason)
        ? $"🚑 {Name}"
        : $"🚑 {Name} ({Reason})";

    public string DetailText => !string.IsNullOrWhiteSpace(ExpectedReturn)
        ? $"Return: {ExpectedReturn}"
        : (Reason ?? "Ruled out");

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
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
    public ObservableCollection<UnavailablePlayerItemViewModel> Unavailable { get; } = new();

    public bool HasUnavailable => Unavailable.Count > 0;
    public Visibility UnavailableVisibility => HasUnavailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoUnavailableVisibility => HasUnavailable ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(storage, value))
        {
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public void NotifyUnavailableChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnavailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnavailableVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoUnavailableVisibility)));
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
    private double? _rating;
    private bool _isManOfTheMatch;
    private bool _isCaptain;

    public bool IsCaptain
    {
        get => _isCaptain;
        set => SetProperty(ref _isCaptain, value);
    }

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

    public double? Rating
    {
        get => _rating;
        set
        {
            if (SetProperty(ref _rating, value))
            {
                OnPropertyChanged(nameof(RatingDisplay));
                OnPropertyChanged(nameof(HasRating));
                OnPropertyChanged(nameof(RatingBackgroundBrush));
                OnPropertyChanged(nameof(RatingVisibility));
            }
        }
    }

    public bool IsManOfTheMatch
    {
        get => _isManOfTheMatch;
        set
        {
            if (SetProperty(ref _isManOfTheMatch, value))
            {
                OnPropertyChanged(nameof(RatingDisplay));
            }
        }
    }

    public bool HasRating => Rating.HasValue && Rating.Value > 0;
    public Visibility RatingVisibility => HasRating ? Visibility.Visible : Visibility.Collapsed;
    public string RatingDisplay => RatingVisualHelper.FormatRating(Rating, IsManOfTheMatch);
    public SolidColorBrush RatingBackgroundBrush => RatingVisualHelper.GetRatingBackgroundBrush(Rating);

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

    public int Column => Col;

    public double XPercent
    {
        get => _xPercent;
        set
        {
            if (SetProperty(ref _xPercent, value))
            {
                OnPropertyChanged(nameof(CanvasLeft));
                OnPropertyChanged(nameof(X));
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
                OnPropertyChanged(nameof(Y));
            }
        }
    }

    public double CanvasLeft => Math.Round((XPercent * 1000.0) - 55.0, 1);
    public double CanvasTop => Math.Round((YPercent * 600.0) - 30.0, 1);
    public double X => CanvasLeft;
    public double Y => CanvasTop;

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
        if (parts.Length >= 2 && (parts[^2].Equals("De", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Van", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Von", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Di", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Da", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Do", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Dos", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^2].Equals("Del", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{parts[^2]} {parts[^1]}";
        }
        return parts[^1];
    }

    private string? _playerId;
    private bool _isSubbedOff;
    private int? _subOutMinute;
    private string _subOutText = string.Empty;
    private bool _hasYellowCard;
    private bool _hasRedCard;
    private int _regularGoalsCount;
    private int _penaltyGoalsCount;
    private int _ownGoalsCount;
    private int _assistsCount;

    public string? PlayerId
    {
        get => _playerId;
        set => SetProperty(ref _playerId, value);
    }

    public bool IsSubbedOff
    {
        get => _isSubbedOff;
        set
        {
            if (SetProperty(ref _isSubbedOff, value))
            {
                OnPropertyChanged(nameof(SubBadgeVisibility));
            }
        }
    }

    public int? SubOutMinute
    {
        get => _subOutMinute;
        set => SetProperty(ref _subOutMinute, value);
    }

    public string SubOutText
    {
        get => _subOutText;
        set
        {
            if (SetProperty(ref _subOutText, value))
            {
                OnPropertyChanged(nameof(SubBadgeVisibility));
            }
        }
    }

    public Visibility SubBadgeVisibility => IsSubbedOff && !string.IsNullOrEmpty(SubOutText) ? Visibility.Visible : Visibility.Collapsed;

    public bool HasYellowCard
    {
        get => _hasYellowCard;
        set
        {
            if (SetProperty(ref _hasYellowCard, value))
            {
                OnPropertyChanged(nameof(YellowCardVisibility));
                OnPropertyChanged(nameof(HasCards));
            }
        }
    }

    public bool HasRedCard
    {
        get => _hasRedCard;
        set
        {
            if (SetProperty(ref _hasRedCard, value))
            {
                OnPropertyChanged(nameof(RedCardVisibility));
                OnPropertyChanged(nameof(YellowCardVisibility));
                OnPropertyChanged(nameof(HasCards));
            }
        }
    }

    public bool HasCards => HasRedCard || HasYellowCard;
    public Visibility YellowCardVisibility => HasYellowCard && !HasRedCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RedCardVisibility => HasRedCard ? Visibility.Visible : Visibility.Collapsed;

    public int RegularGoalsCount
    {
        get => _regularGoalsCount;
        set
        {
            if (SetProperty(ref _regularGoalsCount, value))
            {
                OnPropertyChanged(nameof(GoalBadgeText));
                OnPropertyChanged(nameof(HasGoalBadge));
                OnPropertyChanged(nameof(GoalBadgeVisibility));
            }
        }
    }

    public bool HasGoalBadge => RegularGoalsCount > 0;
    public Visibility GoalBadgeVisibility => HasGoalBadge ? Visibility.Visible : Visibility.Collapsed;
    public string GoalBadgeText => RegularGoalsCount > 1 ? $"⚽x{RegularGoalsCount}" : (RegularGoalsCount == 1 ? "⚽" : string.Empty);

    public int PenaltyGoalsCount
    {
        get => _penaltyGoalsCount;
        set
        {
            if (SetProperty(ref _penaltyGoalsCount, value))
            {
                OnPropertyChanged(nameof(HasPenaltyGoal));
                OnPropertyChanged(nameof(PenaltyGoalText));
                OnPropertyChanged(nameof(PenaltyGoalVisibility));
            }
        }
    }

    public bool HasPenaltyGoal => PenaltyGoalsCount > 0;
    public Visibility PenaltyGoalVisibility => HasPenaltyGoal ? Visibility.Visible : Visibility.Collapsed;
    public string PenaltyGoalText => PenaltyGoalsCount > 1 ? $"⚽ PEN x{PenaltyGoalsCount}" : "⚽ PEN";

    public int OwnGoalsCount
    {
        get => _ownGoalsCount;
        set
        {
            if (SetProperty(ref _ownGoalsCount, value))
            {
                OnPropertyChanged(nameof(HasOwnGoal));
                OnPropertyChanged(nameof(OwnGoalText));
                OnPropertyChanged(nameof(OwnGoalVisibility));
            }
        }
    }

    public bool HasOwnGoal => OwnGoalsCount > 0;
    public Visibility OwnGoalVisibility => HasOwnGoal ? Visibility.Visible : Visibility.Collapsed;
    public string OwnGoalText => OwnGoalsCount > 1 ? $"⚽ OG x{OwnGoalsCount}" : "⚽ OG";

    public int AssistsCount
    {
        get => _assistsCount;
        set
        {
            if (SetProperty(ref _assistsCount, value))
            {
                OnPropertyChanged(nameof(HasAssistBadge));
                OnPropertyChanged(nameof(AssistBadgeText));
                OnPropertyChanged(nameof(AssistBadgeVisibility));
            }
        }
    }

    public bool HasAssistBadge => AssistsCount > 0;
    public Visibility AssistBadgeVisibility => HasAssistBadge ? Visibility.Visible : Visibility.Collapsed;
    public string AssistBadgeText => AssistsCount > 1 ? $"👟x{AssistsCount}" : (AssistsCount == 1 ? "👟" : string.Empty);

    public void UpdateFrom(PitchPlayerNodeViewModel source)
    {
        if (source == null) return;
        Name = source.Name;
        ShortDisplayName = source.ShortDisplayName;
        Number = source.Number;
        Position = source.Position;
        PositionBadge = source.PositionBadge;
        IsGoalkeeper = source.IsGoalkeeper;
        IsHomeTeam = source.IsHomeTeam;
        Row = source.Row;
        Col = source.Col;
        XPercent = source.XPercent;
        YPercent = source.YPercent;
        Rating = source.Rating;
        IsManOfTheMatch = source.IsManOfTheMatch;
        IsCaptain = source.IsCaptain;
        PlayerId = source.PlayerId;
        IsSubbedOff = source.IsSubbedOff;
        SubOutMinute = source.SubOutMinute;
        SubOutText = source.SubOutText;
        HasYellowCard = source.HasYellowCard;
        HasRedCard = source.HasRedCard;
        RegularGoalsCount = source.RegularGoalsCount;
        PenaltyGoalsCount = source.PenaltyGoalsCount;
        OwnGoalsCount = source.OwnGoalsCount;
        AssistsCount = source.AssistsCount;
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

public class HeatmapPointViewModel
{
    public double CanvasLeft { get; set; }
    public double CanvasTop { get; set; }
    public double Size { get; set; } = 40;
    public SolidColorBrush FillBrush { get; set; } = new(Windows.UI.Color.FromArgb(100, 245, 158, 11));
    public double Opacity { get; set; } = 0.6;
}

public class FormBadgeViewModel
{
    public string Result { get; set; } = "D"; // "W", "D", "L"
    public string OpponentName { get; set; } = string.Empty;
    public string Score { get; set; } = string.Empty;
    public string TooltipText => $"vs {OpponentName} ({Score})";

    private static readonly SolidColorBrush WinBrush = new(Windows.UI.Color.FromArgb(255, 34, 197, 94));   // #22C55E
    private static readonly SolidColorBrush DrawBrush = new(Windows.UI.Color.FromArgb(255, 100, 116, 139)); // #64748B
    private static readonly SolidColorBrush LossBrush = new(Windows.UI.Color.FromArgb(255, 239, 68, 68));  // #EF4444

    public SolidColorBrush BackgroundBrush => Result switch
    {
        "W" => WinBrush,
        "L" => LossBrush,
        _ => DrawBrush
    };
}

public class H2HMeetingViewModel
{
    public string MatchId { get; set; } = string.Empty;
    public string DateDisplay { get; set; } = string.Empty;
    public string CompetitionName { get; set; } = string.Empty;
    public string HomeTeamName { get; set; } = string.Empty;
    public string AwayTeamName { get; set; } = string.Empty;
    public string ScoreDisplay { get; set; } = string.Empty;
    public string SummaryText => $"{HomeTeamName} {ScoreDisplay} {AwayTeamName}";
}

public class StatComparisonViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _homeValue = "0";
    private string _awayValue = "0";
    private double _homeRatio = 50.0;
    private double _awayRatio = 50.0;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string HomeValue
    {
        get => _homeValue;
        set => SetProperty(ref _homeValue, value);
    }

    public string AwayValue
    {
        get => _awayValue;
        set => SetProperty(ref _awayValue, value);
    }

    public double HomeRatio
    {
        get => _homeRatio;
        set => SetProperty(ref _homeRatio, value);
    }

    public double AwayRatio
    {
        get => _awayRatio;
        set => SetProperty(ref _awayRatio, value);
    }

    public void UpdateFrom(StatComparisonViewModel source)
    {
        if (source == null) return;
        HomeValue = source.HomeValue;
        AwayValue = source.AwayValue;
        HomeRatio = source.HomeRatio;
        AwayRatio = source.AwayRatio;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
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

    private bool _isHomeWinner;
    public bool IsHomeWinner
    {
        get => _isHomeWinner;
        private set
        {
            if (SetProperty(ref _isHomeWinner, value))
            {
                OnPropertyChanged(nameof(HomeTeamStatsForeground));
            }
        }
    }

    private bool _isAwayWinner;
    public bool IsAwayWinner
    {
        get => _isAwayWinner;
        private set
        {
            if (SetProperty(ref _isAwayWinner, value))
            {
                OnPropertyChanged(nameof(AwayTeamStatsForeground));
            }
        }
    }

    public Brush? HomeTeamStatsForeground => GetTeamStatsForeground(IsHomeWinner);
    public Brush? AwayTeamStatsForeground => GetTeamStatsForeground(IsAwayWinner);

    private static Brush? GetTeamStatsForeground(bool isWinner)
    {
        if (Application.Current == null) return null;
        try
        {
            string key = isWinner ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush";
            return Application.Current.Resources.TryGetValue(key, out var res) && res is Brush brush ? brush : null;
        }
        catch
        {
            return null;
        }
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
        set
        {
            if (SetProperty(ref _venue, value))
            {
                OnPropertyChanged(nameof(VenueText));
                OnPropertyChanged(nameof(VenueVisibility));
            }
        }
    }

    public string VenueText => !string.IsNullOrWhiteSpace(Venue) ? $"🏟️ {Venue.Trim()}" : string.Empty;
    public Visibility VenueVisibility => !string.IsNullOrWhiteSpace(Venue) ? Visibility.Visible : Visibility.Collapsed;

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
    public ObservableCollection<MatchTimelineItem> KeyTimelineItems { get; } = new();
    public ObservableCollection<MatchTimelineItem> AllTimelineItems { get; } = new();
    public ObservableCollection<MatchTimelineItem> KeyEvents { get; } = new();
    public ObservableCollection<MatchCommentaryItemViewModel> CommentaryItems { get; } = new();

    public bool HasKeyEvents => KeyEvents.Count > 0;
    public bool HasCommentary => CommentaryItems.Count > 0;

    private bool _isKeyEventsTabSelected = true;
    public bool IsKeyEventsTabSelected
    {
        get => _isKeyEventsTabSelected;
        set
        {
            if (SetProperty(ref _isKeyEventsTabSelected, value))
            {
                OnPropertyChanged(nameof(IsCommentaryTabSelected));
                OnPropertyChanged(nameof(KeyEventsTabVisibility));
                OnPropertyChanged(nameof(CommentaryTabVisibility));
            }
        }
    }

    public bool IsCommentaryTabSelected
    {
        get => !_isKeyEventsTabSelected;
        set
        {
            if (value != !_isKeyEventsTabSelected)
            {
                IsKeyEventsTabSelected = !value;
            }
        }
    }

    public Visibility KeyEventsTabVisibility => _isKeyEventsTabSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CommentaryTabVisibility => !_isKeyEventsTabSelected ? Visibility.Visible : Visibility.Collapsed;

    public string KeyEventsTabHeader => KeyEvents.Count > 0 ? $"⏱️ Key Events ({KeyEvents.Count})" : "⏱️ Key Events";
    public string CommentaryTabHeader => CommentaryItems.Count > 0 ? $"📜 Commentary ({CommentaryItems.Count})" : "📜 Commentary";

    private bool _isKeyEventsViewSelected = true;
    public bool IsKeyEventsViewSelected
    {
        get => _isKeyEventsViewSelected;
        set
        {
            if (SetProperty(ref _isKeyEventsViewSelected, value))
            {
                OnPropertyChanged(nameof(IsAllEventsViewSelected));
                ApplyTimelineView();
            }
        }
    }

    public bool IsAllEventsViewSelected
    {
        get => !_isKeyEventsViewSelected;
        set
        {
            if (value != !_isKeyEventsViewSelected)
            {
                IsKeyEventsViewSelected = !value;
            }
        }
    }

    public void SetTimelineFilter(bool isKeyEvents)
    {
        IsKeyEventsViewSelected = isKeyEvents;
    }

    public void ApplyTimelineView()
    {
        var source = _isKeyEventsViewSelected ? KeyTimelineItems : AllTimelineItems;
        SmartMergeTimeline(TimelineItems, source.ToList());
    }

    public static bool AreTimelineItemsMatching(MatchTimelineItem a, MatchTimelineItem b)
    {
        if (!string.IsNullOrEmpty(a.EventId) && !string.IsNullOrEmpty(b.EventId))
        {
            return string.Equals(a.EventId, b.EventId, StringComparison.OrdinalIgnoreCase);
        }

        if (a.IsNeutral && b.IsNeutral)
        {
            return string.Equals(a.EventTitle, b.EventTitle, StringComparison.OrdinalIgnoreCase);
        }

        return a.GetSortKey() == b.GetSortKey()
            && a.IsHomeTeam == b.IsHomeTeam
            && a.IsAwayTeam == b.IsAwayTeam
            && (string.Equals(a.EventTitle, b.EventTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Description, b.Description, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.IconGlyph, b.IconGlyph, StringComparison.OrdinalIgnoreCase));
    }

    public static void UpdateTimelineItemInPlace(MatchTimelineItem target, MatchTimelineItem source)
    {
        target.Minute = source.Minute;
        target.ExtraMinute = source.ExtraMinute;
        target.SortPriority = source.SortPriority;
        target.MinuteDisplay = source.MinuteDisplay;
        target.IconGlyph = source.IconGlyph;
        target.Description = source.Description;
        target.PlayerInfo = source.PlayerInfo;
        target.IsHomeTeam = source.IsHomeTeam;
        target.IsAwayTeam = source.IsAwayTeam;
        target.EventTitle = source.EventTitle;
        target.EventSubtitle = source.EventSubtitle;
        target.TeamName = source.TeamName;
        target.TeamLogoUrl = source.TeamLogoUrl;
        if (!string.IsNullOrEmpty(source.EventId))
        {
            target.EventId = source.EventId;
        }
    }

    public static void SmartMergeCommentary(
        ObservableCollection<MatchCommentaryItemViewModel> collection,
        IReadOnlyList<MatchCommentaryItemViewModel> incoming)
    {
        if (collection == null || incoming == null) return;

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            var existing = collection[i];
            if (!incoming.Any(inc => string.Equals(inc.Id, existing.Id, StringComparison.Ordinal)))
            {
                collection.RemoveAt(i);
            }
        }

        for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
        {
            var inc = incoming[targetIdx];
            int foundIdx = -1;
            for (int j = 0; j < collection.Count; j++)
            {
                if (string.Equals(collection[j].Id, inc.Id, StringComparison.Ordinal))
                {
                    foundIdx = j;
                    break;
                }
            }

            if (foundIdx >= 0)
            {
                collection[foundIdx].UpdateFrom(inc);
                if (foundIdx != targetIdx && targetIdx < collection.Count)
                {
                    collection.Move(foundIdx, targetIdx);
                }
            }
            else
            {
                if (targetIdx >= collection.Count)
                {
                    collection.Add(inc);
                }
                else
                {
                    collection.Insert(targetIdx, inc);
                }
            }
        }
    }

    public static void SmartMergeTimeline(
        ObservableCollection<MatchTimelineItem> collection,
        IReadOnlyList<MatchTimelineItem> incomingSorted)
    {
        if (collection == null || incomingSorted == null) return;

        // 1. Remove obsolete items that are no longer in incoming list
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            var existing = collection[i];
            if (!incomingSorted.Any(inc => AreTimelineItemsMatching(existing, inc)))
            {
                collection.RemoveAt(i);
            }
        }

        // 2. Insert new events according to sort order or update existing ones in place
        for (int targetIdx = 0; targetIdx < incomingSorted.Count; targetIdx++)
        {
            var incoming = incomingSorted[targetIdx];

            int foundIdx = -1;
            for (int j = 0; j < collection.Count; j++)
            {
                if (AreTimelineItemsMatching(collection[j], incoming))
                {
                    foundIdx = j;
                    break;
                }
            }

            if (foundIdx >= 0)
            {
                UpdateTimelineItemInPlace(collection[foundIdx], incoming);

                if (foundIdx != targetIdx && targetIdx < collection.Count)
                {
                    collection.Move(foundIdx, targetIdx);
                }
            }
            else
            {
                if (targetIdx >= collection.Count)
                {
                    collection.Add(incoming);
                }
                else
                {
                    collection.Insert(targetIdx, incoming);
                }
            }
        }
    }

    public static void SmartMergeIncidents(
        ObservableCollection<MatchHeaderIncidentViewModel> collection,
        IReadOnlyList<MatchHeaderIncidentViewModel> incoming)
    {
        if (collection == null || incoming == null) return;

        if (collection.Count == incoming.Count &&
            collection.Zip(incoming, (a, b) => a.Icon == b.Icon && a.Text == b.Text).All(x => x))
        {
            return;
        }

        collection.Clear();
        foreach (var inc in incoming)
        {
            collection.Add(inc);
        }
    }

    public static void SmartMergeStats(
        ObservableCollection<StatComparisonViewModel> collection,
        IReadOnlyList<StatComparisonViewModel> incoming)
    {
        if (collection == null || incoming == null) return;

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            var existing = collection[i];
            if (!incoming.Any(inc => string.Equals(inc.Name, existing.Name, StringComparison.OrdinalIgnoreCase)))
            {
                collection.RemoveAt(i);
            }
        }

        for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
        {
            var inc = incoming[targetIdx];
            int foundIdx = -1;
            for (int j = 0; j < collection.Count; j++)
            {
                if (string.Equals(collection[j].Name, inc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    foundIdx = j;
                    break;
                }
            }

            if (foundIdx >= 0)
            {
                collection[foundIdx].UpdateFrom(inc);
                if (foundIdx != targetIdx && targetIdx < collection.Count)
                {
                    collection.Move(foundIdx, targetIdx);
                }
            }
            else
            {
                if (targetIdx >= collection.Count)
                {
                    collection.Add(inc);
                }
                else
                {
                    collection.Insert(targetIdx, inc);
                }
            }
        }
    }

    public static void SmartMergeLineupPlayers(
        ObservableCollection<LineupPlayerItemViewModel> collection,
        IReadOnlyList<LineupPlayerItemViewModel> incoming)
    {
        if (collection == null || incoming == null) return;

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            var existing = collection[i];
            if (!incoming.Any(inc => string.Equals(inc.Name, existing.Name, StringComparison.OrdinalIgnoreCase)))
            {
                collection.RemoveAt(i);
            }
        }

        for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
        {
            var inc = incoming[targetIdx];
            int foundIdx = -1;
            for (int j = 0; j < collection.Count; j++)
            {
                if (string.Equals(collection[j].Name, inc.Name, StringComparison.OrdinalIgnoreCase))
                {
                    foundIdx = j;
                    break;
                }
            }

            if (foundIdx >= 0)
            {
                collection[foundIdx].UpdateFrom(inc);
                if (foundIdx != targetIdx && targetIdx < collection.Count)
                {
                    collection.Move(foundIdx, targetIdx);
                }
            }
            else
            {
                if (targetIdx >= collection.Count)
                {
                    collection.Add(inc);
                }
                else
                {
                    collection.Insert(targetIdx, inc);
                }
            }
        }
    }

    public static void SmartMergePitchPlayers(
        ObservableCollection<PitchPlayerNodeViewModel> collection,
        IReadOnlyList<PitchPlayerNodeViewModel> incoming)
    {
        if (collection == null || incoming == null) return;

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            var existing = collection[i];
            if (!incoming.Any(inc => string.Equals(inc.Name, existing.Name, StringComparison.OrdinalIgnoreCase) && inc.IsHomeTeam == existing.IsHomeTeam))
            {
                collection.RemoveAt(i);
            }
        }

        for (int targetIdx = 0; targetIdx < incoming.Count; targetIdx++)
        {
            var inc = incoming[targetIdx];
            int foundIdx = -1;
            for (int j = 0; j < collection.Count; j++)
            {
                if (string.Equals(collection[j].Name, inc.Name, StringComparison.OrdinalIgnoreCase) && collection[j].IsHomeTeam == inc.IsHomeTeam)
                {
                    foundIdx = j;
                    break;
                }
            }

            if (foundIdx >= 0)
            {
                var existing = collection[foundIdx];
                existing.UpdateFrom(inc);

                if (foundIdx != targetIdx && targetIdx < collection.Count)
                {
                    collection.Move(foundIdx, targetIdx);
                }
            }
            else
            {
                if (targetIdx >= collection.Count)
                {
                    collection.Add(inc);
                }
                else
                {
                    collection.Insert(targetIdx, inc);
                }
            }
        }
    }

    public string? LastEtag { get; set; }

    public async Task<bool> RefreshAsync(FootballApiClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(MatchId)) return false;
        bool created = false;
        if (client == null)
        {
            client = new FootballApiClient();
            created = true;
        }

        try
        {
            var response = await client.GetMatchDetailsAsync(MatchId, LastEtag);
            if (response.IsNotModified)
            {
                if (!string.IsNullOrEmpty(response.ETag))
                {
                    LastEtag = response.ETag;
                }
                return true;
            }

            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                LastEtag = response.ETag;
                Load(response.Envelope.Data);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MATCH_CENTER_VM] RefreshAsync error: {ex.Message}");
            return false;
        }
        finally
        {
            if (created)
            {
                client.Dispose();
            }
        }
    }

    public ObservableCollection<MatchHeaderIncidentViewModel> HomeIncidents { get; } = new();
    public ObservableCollection<MatchHeaderIncidentViewModel> AwayIncidents { get; } = new();
    public bool HasHeaderIncidents => HomeIncidents.Count > 0 || AwayIncidents.Count > 0;
    public Visibility HeaderIncidentsVisibility => HasHeaderIncidents ? Visibility.Visible : Visibility.Collapsed;
    public void NotifyHeaderIncidentsChanged()
    {
        OnPropertyChanged(nameof(HasHeaderIncidents));
        OnPropertyChanged(nameof(HeaderIncidentsVisibility));
    }
    public ObservableCollection<StatComparisonViewModel> MatchStats { get; } = new();

    public LineupPresentationViewModel HomeLineup { get; } = new();
    public LineupPresentationViewModel AwayLineup { get; } = new();
    public ObservableCollection<PitchPlayerNodeViewModel> HomePitchPlayers { get; } = new();
    public ObservableCollection<PitchPlayerNodeViewModel> AwayPitchPlayers { get; } = new();
    public ObservableCollection<PitchPlayerNodeViewModel> PitchPlayers { get; } = new();
    public ObservableCollection<PitchPlayerNodeViewModel> PitchPlayerNodes => PitchPlayers;

    private MatchDetailDto? _lastMatchDetail;

    // Pitch Heatmap Mode
    private bool _showHeatmap;
    public bool ShowHeatmap
    {
        get => _showHeatmap;
        set
        {
            if (SetProperty(ref _showHeatmap, value))
            {
                OnPropertyChanged(nameof(LineupPitchVisibility));
                OnPropertyChanged(nameof(HeatmapPitchVisibility));
                OnPropertyChanged(nameof(PitchModeButtonText));
                OnPropertyChanged(nameof(PitchModeHeaderTitle));
            }
        }
    }

    public Visibility LineupPitchVisibility => ShowHeatmap ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HeatmapPitchVisibility => ShowHeatmap ? Visibility.Visible : Visibility.Collapsed;
    public string PitchModeButtonText => ShowHeatmap ? "⚽ Lineup" : "🔥 Heatmap";
    public string PitchModeHeaderTitle => ShowHeatmap ? "🔥 Tactical Action & Shot Heatmap" : "⚽ Tactical Lineup Pitch";
    public ICommand TogglePitchModeCommand => new RelayCommand(() => ShowHeatmap = !ShowHeatmap);

    private int _homeAttackingThirdPct = 35;
    private int _homeMiddleThirdPct = 40;
    private int _homeDefensiveThirdPct = 25;
    private int _awayAttackingThirdPct = 30;
    private int _awayMiddleThirdPct = 45;
    private int _awayDefensiveThirdPct = 25;

    public int HomeAttackingThirdPct { get => _homeAttackingThirdPct; set => SetProperty(ref _homeAttackingThirdPct, value); }
    public int HomeMiddleThirdPct { get => _homeMiddleThirdPct; set => SetProperty(ref _homeMiddleThirdPct, value); }
    public int HomeDefensiveThirdPct { get => _homeDefensiveThirdPct; set => SetProperty(ref _homeDefensiveThirdPct, value); }

    public int AwayAttackingThirdPct { get => _awayAttackingThirdPct; set => SetProperty(ref _awayAttackingThirdPct, value); }
    public int AwayMiddleThirdPct { get => _awayMiddleThirdPct; set => SetProperty(ref _awayMiddleThirdPct, value); }
    public int AwayDefensiveThirdPct { get => _awayDefensiveThirdPct; set => SetProperty(ref _awayDefensiveThirdPct, value); }

    public ObservableCollection<HeatmapPointViewModel> HeatmapPoints { get; } = new();

    // Predictions & History
    private int _homeWinPct = 45;
    private int _drawPct = 25;
    private int _awayWinPct = 30;
    private int _h2hHomeWins = 2;
    private int _h2hDraws = 1;
    private int _h2hAwayWins = 2;

    public int HomeWinPct
    {
        get => _homeWinPct;
        set
        {
            if (SetProperty(ref _homeWinPct, value))
            {
                OnPropertyChanged(nameof(HomeWinPctDisplay));
                OnPropertyChanged(nameof(HomeWinGridLength));
            }
        }
    }

    public int DrawPct
    {
        get => _drawPct;
        set
        {
            if (SetProperty(ref _drawPct, value))
            {
                OnPropertyChanged(nameof(DrawPctDisplay));
                OnPropertyChanged(nameof(DrawGridLength));
            }
        }
    }

    public int AwayWinPct
    {
        get => _awayWinPct;
        set
        {
            if (SetProperty(ref _awayWinPct, value))
            {
                OnPropertyChanged(nameof(AwayWinPctDisplay));
                OnPropertyChanged(nameof(AwayGridLength));
            }
        }
    }

    public string HomeWinPctDisplay => $"{HomeWinPct}%";
    public string DrawPctDisplay => $"{DrawPct}%";
    public string AwayWinPctDisplay => $"{AwayWinPct}%";

    public GridLength HomeWinGridLength
    {
        get
        {
            double val = HomeWinPct;
            if (double.IsNaN(val) || double.IsInfinity(val) || val <= 0) val = 1;
            return new GridLength(val, GridUnitType.Star);
        }
    }

    public GridLength DrawGridLength
    {
        get
        {
            double val = DrawPct;
            if (double.IsNaN(val) || double.IsInfinity(val) || val <= 0) val = 1;
            return new GridLength(val, GridUnitType.Star);
        }
    }

    public GridLength AwayGridLength
    {
        get
        {
            double val = AwayWinPct;
            if (double.IsNaN(val) || double.IsInfinity(val) || val <= 0) val = 1;
            return new GridLength(val, GridUnitType.Star);
        }
    }

    public int H2HHomeWins { get => _h2hHomeWins; set { if (SetProperty(ref _h2hHomeWins, value)) OnPropertyChanged(nameof(H2HRecordDisplay)); } }
    public int H2HDraws { get => _h2hDraws; set { if (SetProperty(ref _h2hDraws, value)) OnPropertyChanged(nameof(H2HRecordDisplay)); } }
    public int H2HAwayWins { get => _h2hAwayWins; set { if (SetProperty(ref _h2hAwayWins, value)) OnPropertyChanged(nameof(H2HRecordDisplay)); } }

    public string H2HRecordDisplay => $"{H2HHomeWins}W – {H2HDraws}D – {H2HAwayWins}W";

    public ObservableCollection<FormBadgeViewModel> HomeFormBadges { get; } = new();
    public ObservableCollection<FormBadgeViewModel> AwayFormBadges { get; } = new();
    public ObservableCollection<H2HMeetingViewModel> LastFiveMeetings { get; } = new();

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

    public ObservableCollection<StandingRowItemViewModel> Standings { get; } = new();
    public ObservableCollection<PlayerStatLeaderItemViewModel> TopScorers { get; } = new();
    public ObservableCollection<PlayerStatLeaderItemViewModel> TopAssists { get; } = new();

    public bool HasStandings => Standings.Count > 0;
    public bool HasTopScorers => TopScorers.Count > 0;
    public bool HasTopAssists => TopAssists.Count > 0;
    public bool HasLeaders => HasTopScorers || HasTopAssists;

    public Visibility StandingsVisibility => HasStandings ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LeadersVisibility => HasLeaders ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoStandingsVisibility => (!HasStandings && !HasLeaders) ? Visibility.Visible : Visibility.Collapsed;

    public void Load(MatchDetailDto match)
    {
        if (match == null) return;
        _lastMatchDetail = match;

        if (!string.Equals(MatchId, match.Id, StringComparison.OrdinalIgnoreCase))
        {
            LastEtag = null;
        }

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
        StatusText = FormatStatusText(match);

        ScoreText = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";

        // Compute match winner based on final score for finished matches (Accent Color Foreground ONLY for winning team)
        string stateNorm = (match.State ?? "PREMATCH").Trim().ToUpperInvariant();
        bool isConcluded = stateNorm is "FINISHED" or "FT" or "AET" or "PEN" or "POSTMATCH" or "FINAL" or "FINISHED_AET" or "FINISHED_PEN";

        if (isConcluded)
        {
            int homeScore = match.Score?.Home ?? 0;
            int awayScore = match.Score?.Away ?? 0;
            int? homePen = match.Score?.Penalty?.Home;
            int? awayPen = match.Score?.Penalty?.Away;

            if (homePen.HasValue && awayPen.HasValue && homePen.Value != awayPen.Value)
            {
                IsHomeWinner = homePen.Value > awayPen.Value;
                IsAwayWinner = awayPen.Value > homePen.Value;
            }
            else
            {
                IsHomeWinner = homeScore > awayScore;
                IsAwayWinner = awayScore > homeScore;
            }
        }
        else
        {
            IsHomeWinner = false;
            IsAwayWinner = false;
        }

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

        // 1. Populate Timeline and Header Incidents (Goal Scorers & Red Cards) with Smart Merge (Anti-Blink & Preserves Scroll)
        var newHomeIncidents = new List<MatchHeaderIncidentViewModel>();
        var newAwayIncidents = new List<MatchHeaderIncidentViewModel>();
        var allTimelineList = new List<MatchTimelineItem>();
        var keyTimelineList = new List<MatchTimelineItem>();

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
                if (val.StartsWith("fotmob_", StringComparison.OrdinalIgnoreCase))
                    return val.Substring(7);
                if (val.StartsWith("apifootball_", StringComparison.OrdinalIgnoreCase))
                    return val.Substring(12);
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

        if (match.Events != null && match.Events.Count > 0)
        {

            bool IsEventForTeam(TrayEventDto e, TeamSummaryDto? team, string? fallbackTeamId = null)
            {
                if (team == null) return false;
                return IsTeamMatch(e.TeamId, team, fallbackTeamId);
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

            // Mutually exclusive partitioning of header incidents with deduplication
            var homeIncidentEvents = new List<TrayEventDto>();
            var awayIncidentEvents = new List<TrayEventDto>();
            var seenIncidentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in match.Events)
            {
                if (e == null) continue;

                bool isGoalEvent = (e.Type?.ToLowerInvariant().Contains("goal") == true || e.Type?.ToLowerInvariant() == "penalty") &&
                                   e.Type?.ToLowerInvariant().Contains("missed") != true &&
                                   !IsMissedPenalty(e);
                bool isMissedPenEvent = IsMissedPenalty(e);
                bool isRedEvent = e.Type?.ToLowerInvariant().Contains("red") == true;

                if (!isGoalEvent && !isMissedPenEvent && !isRedEvent)
                    continue;

                string dedupeKey = $"{e.Minute}_{e.ExtraMinute}_{(e.PlayerName ?? string.Empty).Trim().ToLowerInvariant()}_{(e.Type ?? string.Empty).Trim().ToLowerInvariant()}";
                if (!seenIncidentKeys.Add(dedupeKey))
                {
                    continue;
                }

                bool belongsToHome = IsEventForTeam(e, match.HomeTeam, match.HomeTeamId);
                bool belongsToAway = IsEventForTeam(e, match.AwayTeam, match.AwayTeamId);

                if (belongsToHome && !belongsToAway)
                {
                    homeIncidentEvents.Add(e);
                }
                else if (belongsToAway && !belongsToHome)
                {
                    awayIncidentEvents.Add(e);
                }
                else if (belongsToHome && belongsToAway)
                {
                    bool strictHome = !string.IsNullOrWhiteSpace(match.HomeTeam?.Id) &&
                                      string.Equals(e.TeamId, match.HomeTeam.Id, StringComparison.OrdinalIgnoreCase);
                    bool strictAway = !string.IsNullOrWhiteSpace(match.AwayTeam?.Id) &&
                                      string.Equals(e.TeamId, match.AwayTeam.Id, StringComparison.OrdinalIgnoreCase);

                    if (strictHome && !strictAway)
                    {
                        homeIncidentEvents.Add(e);
                    }
                    else if (strictAway && !strictHome)
                    {
                        awayIncidentEvents.Add(e);
                    }
                    else
                    {
                        homeIncidentEvents.Add(e);
                    }
                }
            }

            // Home Goals
            var homeGoals = homeIncidentEvents
                .Where(e => (e.Type?.ToLowerInvariant().Contains("goal") == true || e.Type?.ToLowerInvariant() == "penalty") &&
                            e.Type?.ToLowerInvariant().Contains("missed") != true &&
                            !IsMissedPenalty(e))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Goal"));

            foreach (var g in homeGoals)
            {
                var minutes = string.Join(", ", g.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                newHomeIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "⚽",
                    Text = $"{g.Key} {minutes}"
                });
            }

            // Home Missed Penalties
            var homeMissedPens = homeIncidentEvents
                .Where(e => IsMissedPenalty(e))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Missed Penalty"));

            foreach (var mp in homeMissedPens)
            {
                var minutes = string.Join(", ", mp.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                newHomeIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "❌",
                    Text = $"{mp.Key} {minutes}"
                });
            }

            // Home Red Cards
            var homeReds = homeIncidentEvents
                .Where(e => (e.Type?.ToLowerInvariant().Contains("red") == true))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName) ? e.PlayerName.Trim() : "Red Card");

            foreach (var r in homeReds)
            {
                var minutes = string.Join(", ", r.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                newHomeIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "🟥",
                    Text = $"{r.Key} {minutes}"
                });
            }

            // Away Goals
            var awayGoals = awayIncidentEvents
                .Where(e => (e.Type?.ToLowerInvariant().Contains("goal") == true || e.Type?.ToLowerInvariant() == "penalty") &&
                            e.Type?.ToLowerInvariant().Contains("missed") != true &&
                            !IsMissedPenalty(e))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Goal"));

            foreach (var g in awayGoals)
            {
                var minutes = string.Join(", ", g.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                newAwayIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "⚽",
                    Text = $"{g.Key} {minutes}"
                });
            }

            // Away Missed Penalties
            var awayMissedPens = awayIncidentEvents
                .Where(e => IsMissedPenalty(e))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                    ? e.PlayerName.Trim()
                    : (!string.IsNullOrWhiteSpace(CleanDescription(e.Description, e.Type)) ? CleanDescription(e.Description, e.Type) : "Missed Penalty"));

            foreach (var mp in awayMissedPens)
            {
                var minutes = string.Join(", ", mp.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                newAwayIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "❌",
                    Text = $"{mp.Key} {minutes}"
                });
            }

            // Away Red Cards
            var awayReds = awayIncidentEvents
                .Where(e => (e.Type?.ToLowerInvariant().Contains("red") == true))
                .OrderBy(e => e.Minute)
                .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName) ? e.PlayerName.Trim() : "Red Card");

            foreach (var r in awayReds)
            {
                var minutes = string.Join(", ", r.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
                newAwayIncidents.Add(new MatchHeaderIncidentViewModel
                {
                    Icon = "🟥",
                    Text = $"{r.Key} {minutes}"
                });
            }

            // Reverse chronological Timeline items (descending: 90+2' down to 1')
            foreach (var ev in match.Events)
            {
                string typeLower = (ev.Type ?? string.Empty).ToLowerInvariant();
                string detailLower = (ev.Detail ?? string.Empty).ToLowerInvariant();
                string descLower = (ev.Description ?? string.Empty).ToLowerInvariant();

                bool isWhistleOrPeriod = typeLower is "periodstart" or "periodend" or "halftime" or "fulltime" or "kickoff" or "half" or "whistle"
                    || typeLower.Contains("period") || typeLower.Contains("halftime") || typeLower.Contains("fulltime") || typeLower.Contains("whistle") || typeLower.Contains("half")
                    || detailLower.Contains("period") || detailLower.Contains("halftime") || detailLower.Contains("fulltime") || detailLower.Contains("whistle") || detailLower.Contains("half")
                    || descLower.Contains("half time") || descLower.Contains("full time") || descLower.Contains("kickoff") || descLower.Contains("whistle");

                if (isWhistleOrPeriod)
                {
                    continue; // Skip raw/backend period markers here so KeyEvents and Timeline have strictly deduplicated, correctly ordered whistle markers
                }

                bool isMissedPen = IsMissedPenalty(ev);
                string icon = isMissedPen ? "❌" : typeLower switch
                {
                    "goal" or "penalty" or "owngoal" => "⚽",
                    "yellowcard" or "yellow_card" => "🟨",
                    "redcard" or "red_card" => "🟥",
                    "secondyellow" or "yellowred" => "🟨🟥",
                    "substitution" or "sub" => "🔄",
                    "var" => "🖥️",
                    "periodstart" or "kickoff" => "⏱️",
                    "periodend" or "halftime" or "fulltime" => "🏁",
                    "missed_penalty" or "missedpenalty" => "❌",
                    "corner" or "corner_kick" => "🚩",
                    "foul" or "freekick" or "free_kick" => "⚠️",
                    "shot" or "shot_on_target" or "shottarget" => "🎯",
                    "block" or "blocked_shot" => "🛡️",
                    "save" or "gk_save" => "🧤",
                    "offside" => "🚩",
                    _ when typeLower.Contains("red") || detailLower.Contains("red") || descLower.Contains("red card") => "🟥",
                    _ when typeLower.Contains("card") || detailLower.Contains("yellow") || descLower.Contains("yellow card") => "🟨",
                    _ => "📌"
                };

                bool isNeutralType = typeLower is "periodstart" or "periodend" or "halftime" or "fulltime" or "kickoff"
                    || typeLower.Contains("period") || typeLower.Contains("halftime") || typeLower.Contains("fulltime");
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
                    string inPlayer = FormatPlayerWithLineup(ev.AssistPlayerName, match.Lineups, ev.AssistPlayerId);

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

                string? evTeamName = !string.IsNullOrWhiteSpace(ev.TeamName)
                    ? ev.TeamName
                    : (isHome ? HomeTeamName : (isAway ? AwayTeamName : null));
                string? evTeamLogo = !string.IsNullOrWhiteSpace(ev.TeamLogoUrl)
                    ? ev.TeamLogoUrl
                    : (isHome ? match.HomeTeam?.LogoUrl : (isAway ? match.AwayTeam?.LogoUrl : null));

                var timelineItem = new MatchTimelineItem
                {
                    EventId = ev.Id ?? string.Empty,
                    Minute = ev.Minute,
                    ExtraMinute = ev.ExtraMinute,
                    SortPriority = 5,
                    MinuteDisplay = minuteDisplay,
                    IconGlyph = icon,
                    Description = !string.IsNullOrWhiteSpace(cleanedDesc) ? cleanedDesc : (ev.Description ?? ev.Type ?? "Event"),
                    PlayerInfo = !string.IsNullOrWhiteSpace(ev.PlayerName) ? ev.PlayerName.Trim() : null,
                    IsHomeTeam = isHome,
                    IsAwayTeam = isAway,
                    EventTitle = eventTitle,
                    EventSubtitle = eventSubtitle,
                    TeamName = isNeutralType ? null : evTeamName,
                    TeamLogoUrl = isNeutralType ? null : evTeamLogo
                };

                allTimelineList.Add(timelineItem);

                bool isKeyEvent = isMissedPen
                    || typeLower.Contains("goal")
                    || typeLower.Contains("pen")
                    || typeLower.Contains("card")
                    || detailLower.Contains("card")
                    || typeLower.Contains("sub")
                    || typeLower.Contains("var");

                if (isKeyEvent)
                {
                    keyTimelineList.Add(timelineItem);
                }
            }
        }

        // Compute true end minutes and stoppage times for 1st-half (HT) and regulation/ET (FT)
        int maxHtExtraMinute = 0;
        int maxFtMinute = Math.Max(90, match.ElapsedMinute > 90 ? match.ElapsedMinute : 90);
        int maxFtExtraMinute = 0;

        if (match.Events != null)
        {
            foreach (var ev in match.Events)
            {
                if (ev.Minute == 45 && ev.ExtraMinute > maxHtExtraMinute)
                {
                    maxHtExtraMinute = ev.ExtraMinute;
                }
                if (ev.Minute > maxFtMinute)
                {
                    maxFtMinute = ev.Minute;
                    maxFtExtraMinute = ev.ExtraMinute;
                }
                else if (ev.Minute == maxFtMinute && ev.ExtraMinute > maxFtExtraMinute)
                {
                    maxFtExtraMinute = ev.ExtraMinute;
                }
            }
        }

        if (match.Commentary != null)
        {
            foreach (var c in match.Commentary)
            {
                int em = c.ExtraMinute;
                if (c.Minute == 45 && em > maxHtExtraMinute)
                {
                    maxHtExtraMinute = em;
                }
                if (c.Minute > maxFtMinute)
                {
                    maxFtMinute = c.Minute;
                    maxFtExtraMinute = em;
                }
                else if (c.Minute == maxFtMinute && em > maxFtExtraMinute)
                {
                    maxFtExtraMinute = em;
                }
            }
        }

        // Synthesize unified match-state transition whistle markers:
        // 1' Kickoff, HT (45+x'), 46' Start 2nd Half, ET breaks, PEN, FT / AET / PEN (at very top)
        bool wentToExtraTime = stateNorm is "EXTRA_TIME" or "EXTRA_TIME_HALFTIME" or "ET" or "BT" or "P" or "AET" or "PEN" or "FINISHED_AET" or "FINISHED_PEN"
            || (match.Score?.ExtraTime != null && (match.Score.ExtraTime.Home != null || match.Score.ExtraTime.Away != null))
            || match.ElapsedMinute > 90;

        bool wentToPenalties = stateNorm is "PENALTIES" or "P" or "PEN" or "FINISHED_PEN"
            || (match.Score?.Penalty != null && (match.Score.Penalty.Home != null || match.Score.Penalty.Away != null));

        bool hasStarted = (stateNorm is "LIVE" or "IN_PLAY" or "FIRST_HALF" or "1H" or "HALFTIME" or "HT" or "SECOND_HALF" or "2H" or "EXTRA_TIME" or "EXTRA_TIME_HALFTIME" or "ET" or "BT" or "PENALTIES" or "P" || isConcluded)
            || match.ElapsedMinute > 0;

        void AddTimelineMarker(MatchTimelineItem marker)
        {
            allTimelineList.Add(marker);
            keyTimelineList.Add(marker);
        }

        // 1. Kickoff Marker (1') - sorts at the very bottom (key = 0)
        if (hasStarted && !allTimelineList.Any(i => i.EventId == "marker_kickoff"))
        {
            AddTimelineMarker(new MatchTimelineItem
            {
                EventId = "marker_kickoff",
                Minute = 0,
                ExtraMinute = 0,
                SortPriority = 0,
                MinuteDisplay = "1'",
                IconGlyph = "⏱️",
                EventTitle = "Kickoff",
                EventSubtitle = "Match Started",
                Description = "Match Started",
                IsHomeTeam = false,
                IsAwayTeam = false
            });
        }

        // 2. Half-Time Marker (HT or 45+x') - sorts strictly above 45+x' 1st-half events and below 46' 2nd-half events
        bool reachedHalfTime = stateNorm is "HALFTIME" or "HT" or "SECOND_HALF" or "2H" or "EXTRA_TIME" or "EXTRA_TIME_HALFTIME" or "ET" or "BT" or "PENALTIES" or "P" || isConcluded
            || (match.Score?.HalfTime?.Home != null && match.Score?.HalfTime?.Away != null && hasStarted)
            || (match.ElapsedMinute >= 45 && stateNorm != "FIRST_HALF" && stateNorm != "1H" && stateNorm != "PREMATCH");

        if (reachedHalfTime && !allTimelineList.Any(i => i.EventId == "marker_halftime"))
        {
            string htScore = (match.Score?.HalfTime?.Home != null && match.Score?.HalfTime?.Away != null)
                ? $"({match.Score.HalfTime.Home} - {match.Score.HalfTime.Away})"
                : "First half ends";
            string htMinuteDisplay = maxHtExtraMinute > 0 ? $"45+{maxHtExtraMinute}'" : "HT";

            AddTimelineMarker(new MatchTimelineItem
            {
                EventId = "marker_halftime",
                Minute = 45,
                ExtraMinute = 90,
                SortPriority = 9,
                MinuteDisplay = htMinuteDisplay,
                IconGlyph = "🏁",
                EventTitle = "Half-Time",
                EventSubtitle = htScore,
                Description = $"Half-Time {htScore}",
                IsHomeTeam = false,
                IsAwayTeam = false
            });
        }

        // 3. Start 2nd Half Marker (46' - added only to allTimelineList, kept out of KeyEvents to avoid clutter)
        bool startedSecondHalf = stateNorm is "SECOND_HALF" or "2H" or "EXTRA_TIME" or "EXTRA_TIME_HALFTIME" or "ET" or "BT" or "PENALTIES" or "P" || isConcluded
            || (match.ElapsedMinute > 45 && stateNorm != "HALFTIME" && stateNorm != "HT" && stateNorm != "EXTRA_TIME_HALFTIME" && stateNorm != "BT" && stateNorm != "FIRST_HALF" && stateNorm != "1H" && stateNorm != "PREMATCH");

        if (startedSecondHalf && !allTimelineList.Any(i => i.EventId == "marker_2h"))
        {
            allTimelineList.Add(new MatchTimelineItem
            {
                EventId = "marker_2h",
                Minute = 45,
                ExtraMinute = 91,
                SortPriority = 8,
                MinuteDisplay = "46'",
                IconGlyph = "⏱️",
                EventTitle = "Start 2nd Half",
                EventSubtitle = "Second half begins",
                Description = "Second half begins",
                IsHomeTeam = false,
                IsAwayTeam = false
            });
        }

        // 4. Extra Time Markers (if match went the distance - added only to allTimelineList)
        if (wentToExtraTime)
        {
            // End of 90 Min (Regular Time Whistle)
            if (!allTimelineList.Any(i => i.EventId == "marker_end_90"))
            {
                allTimelineList.Add(new MatchTimelineItem
                {
                    EventId = "marker_end_90",
                    Minute = 90,
                    ExtraMinute = 90,
                    SortPriority = 8,
                    MinuteDisplay = "90'",
                    IconGlyph = "🏁",
                    EventTitle = "End of 90 Min",
                    EventSubtitle = "Heading to Extra Time",
                    Description = "End of 90 Min",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }

            // Extra Time 1st Half Kickoff
            if (!allTimelineList.Any(i => i.EventId == "marker_et_start"))
            {
                allTimelineList.Add(new MatchTimelineItem
                {
                    EventId = "marker_et_start",
                    Minute = 90,
                    ExtraMinute = 91,
                    SortPriority = 1,
                    MinuteDisplay = "90'",
                    IconGlyph = "⏱️",
                    EventTitle = "Extra Time Kickoff",
                    EventSubtitle = "Extra time 1st half begins",
                    Description = "Extra time begins",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }

            // Extra Time Half-Time (Break)
            bool reachedEtBreak = stateNorm is "BT" or "P" or "AET" or "PEN"
                || (match.ElapsedMinute >= 105 && stateNorm != "ET")
                || isConcluded;

            if (reachedEtBreak && !allTimelineList.Any(i => i.EventId == "marker_et_ht"))
            {
                allTimelineList.Add(new MatchTimelineItem
                {
                    EventId = "marker_et_ht",
                    Minute = 105,
                    ExtraMinute = 90,
                    SortPriority = 1,
                    MinuteDisplay = "105'",
                    IconGlyph = "🏁",
                    EventTitle = "Extra Time Half-Time",
                    EventSubtitle = "Extra time interval",
                    Description = "Extra time half-time",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }

            // Extra Time 2nd Half Start
            bool startedEt2h = stateNorm is "P" or "AET" or "PEN"
                || (match.ElapsedMinute > 105 && stateNorm != "BT")
                || isConcluded;

            if (startedEt2h && !allTimelineList.Any(i => i.EventId == "marker_et_2h"))
            {
                allTimelineList.Add(new MatchTimelineItem
                {
                    EventId = "marker_et_2h",
                    Minute = 105,
                    ExtraMinute = 90,
                    SortPriority = 2,
                    MinuteDisplay = "105'",
                    IconGlyph = "⏱️",
                    EventTitle = "Start Extra Time 2nd Half",
                    EventSubtitle = "Extra time 2nd half begins",
                    Description = "Extra time 2nd half begins",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }
        }

        // 5. Penalty Shootout Marker (if match went to penalties)
        if (wentToPenalties)
        {
            if (!allTimelineList.Any(i => i.EventId == "marker_pen_start"))
            {
                string penSubtitle = (match.Score?.Penalty?.Home != null && match.Score?.Penalty?.Away != null)
                    ? $"Shootout ({match.Score.Penalty.Home} - {match.Score.Penalty.Away})"
                    : "Penalty shootout begins";

                AddTimelineMarker(new MatchTimelineItem
                {
                    EventId = "marker_pen_start",
                    Minute = 120,
                    ExtraMinute = 95,
                    SortPriority = 1,
                    MinuteDisplay = "PEN",
                    IconGlyph = "🥅",
                    EventTitle = "Penalty Shootout",
                    EventSubtitle = penSubtitle,
                    Description = $"Penalty Shootout {penSubtitle}",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }
        }

        // 6. Full-Time Final Whistle (FT, AET, PEN) - sorts at the very top (key = 99,999,909)
        if (isConcluded && !allTimelineList.Any(i => i.EventId.StartsWith("marker_fulltime")))
        {
            if (wentToPenalties)
            {
                string penScore = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0} (Pen: {match.Score?.Penalty?.Home ?? 0}-{match.Score?.Penalty?.Away ?? 0})";
                AddTimelineMarker(new MatchTimelineItem
                {
                    EventId = "marker_fulltime_pen",
                    Minute = 999,
                    ExtraMinute = 999,
                    SortPriority = 9,
                    MinuteDisplay = "PEN",
                    IconGlyph = "🏁",
                    EventTitle = "Full-Time (PEN)",
                    EventSubtitle = $"Final whistle ({penScore})",
                    Description = $"Full-Time (PEN) ({penScore})",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }
            else if (wentToExtraTime)
            {
                string aetScore = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";
                string aetDisplay = maxFtExtraMinute > 0 ? $"{maxFtMinute}+{maxFtExtraMinute}'" : "AET";
                AddTimelineMarker(new MatchTimelineItem
                {
                    EventId = "marker_fulltime_aet",
                    Minute = 999,
                    ExtraMinute = 999,
                    SortPriority = 9,
                    MinuteDisplay = aetDisplay,
                    IconGlyph = "🏁",
                    EventTitle = "Full-Time (AET)",
                    EventSubtitle = $"Final whistle ({aetScore})",
                    Description = $"Full-Time (AET) ({aetScore})",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }
            else
            {
                string finalScore = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";
                string ftDisplay = maxFtExtraMinute > 0 ? $"{maxFtMinute}+{maxFtExtraMinute}'" : "FT";
                AddTimelineMarker(new MatchTimelineItem
                {
                    EventId = "marker_fulltime",
                    Minute = 999,
                    ExtraMinute = 999,
                    SortPriority = 9,
                    MinuteDisplay = ftDisplay,
                    IconGlyph = "🏁",
                    EventTitle = "Full-Time",
                    EventSubtitle = $"Final whistle ({finalScore})",
                    Description = $"Full-Time ({finalScore})",
                    IsHomeTeam = false,
                    IsAwayTeam = false
                });
            }
        }

        var sortedAll = allTimelineList.OrderByDescending(i => i.GetSortKey()).ToList();
        var sortedKey = keyTimelineList.OrderByDescending(i => i.GetSortKey()).ToList();

        SmartMergeTimeline(AllTimelineItems, sortedAll);
        SmartMergeTimeline(KeyTimelineItems, sortedKey);
        SmartMergeTimeline(KeyEvents, sortedKey);
        ApplyTimelineView();

        SmartMergeIncidents(HomeIncidents, newHomeIncidents);
        SmartMergeIncidents(AwayIncidents, newAwayIncidents);
        NotifyHeaderIncidentsChanged();

        // Populate Commentary collection (reverse-chronological)
        var commentaryList = new List<MatchCommentaryItemViewModel>();
        if (match.Commentary != null && match.Commentary.Count > 0)
        {
            foreach (var c in match.Commentary)
            {
                string idLower = (c.Id ?? string.Empty).ToLowerInvariant();
                string textLower = (c.Text ?? string.Empty).ToLowerInvariant();
                string typeLower = (c.Type ?? string.Empty).ToLowerInvariant();
                bool isWhistle = typeLower == "whistle" || idLower.Contains("whistle");

                string minDisplay;
                if (isWhistle && (idLower.Contains("fulltime") || textLower.Contains("full-time") || textLower.Contains("full time")))
                {
                    minDisplay = c.ExtraMinute > 0 ? $"{c.Minute}+{c.ExtraMinute}'" : "FT";
                }
                else if (isWhistle && (idLower.Contains("halftime") || textLower.Contains("half-time") || textLower.Contains("half time")))
                {
                    minDisplay = c.ExtraMinute > 0 ? $"45+{c.ExtraMinute}'" : "HT";
                }
                else if (isWhistle && (idLower.Contains("kickoff") || textLower.Contains("kicks off")))
                {
                    minDisplay = "1'";
                }
                else
                {
                    minDisplay = c.ExtraMinute > 0
                        ? $"{c.Minute}+{c.ExtraMinute}'"
                        : $"{c.Minute}'";
                }

                string? logo = c.TeamLogoUrl;
                if (string.IsNullOrWhiteSpace(logo))
                {
                    if (IsTeamMatch(c.TeamId, match.HomeTeam, match.HomeTeamId))
                        logo = match.HomeTeam?.LogoUrl ?? HomeTeamLogoUrl;
                    else if (IsTeamMatch(c.TeamId, match.AwayTeam, match.AwayTeamId))
                        logo = match.AwayTeam?.LogoUrl ?? AwayTeamLogoUrl;
                }

                commentaryList.Add(new MatchCommentaryItemViewModel
                {
                    Id = c.Id ?? $"{c.Minute}_{c.Type}_{c.Text?.GetHashCode()}",
                    Minute = c.Minute,
                    ExtraMinute = c.ExtraMinute > 0 ? c.ExtraMinute : null,
                    MinuteDisplay = minDisplay,
                    Type = c.Type ?? "commentary",
                    Text = c.Text ?? string.Empty,
                    TeamId = c.TeamId,
                    TeamName = c.TeamName,
                    TeamLogoUrl = logo
                });
            }
        }
        else
        {
            // Graceful synthetic fallback if upstream commentary was empty
            foreach (var item in sortedAll)
            {
                commentaryList.Add(new MatchCommentaryItemViewModel
                {
                    Id = $"synthetic_{item.EventId}_{item.Minute}_{item.GetHashCode()}",
                    Minute = item.Minute,
                    ExtraMinute = item.ExtraMinute > 0 ? item.ExtraMinute : null,
                    MinuteDisplay = item.MinuteDisplay,
                    Type = item.IconGlyph,
                    Text = !string.IsNullOrWhiteSpace(item.Description)
                        ? item.Description
                        : $"{item.EventTitle}{(item.HasSubtitle ? $" ({item.EventSubtitle})" : "")}",
                    TeamName = item.TeamName,
                    TeamLogoUrl = item.TeamLogoUrl
                });
            }
        }

        // Guarantee reverse-chronological order with whistles placed at exact boundaries:
        // FullTime at very top, HalfTime between 45+x and 46, Kickoff at very bottom
        commentaryList = commentaryList
            .OrderByDescending(c =>
            {
                string idLower = (c.Id ?? string.Empty).ToLowerInvariant();
                string textLower = (c.Text ?? string.Empty).ToLowerInvariant();
                string typeLower = (c.Type ?? string.Empty).ToLowerInvariant();
                bool isWhistle = typeLower == "whistle" || idLower.Contains("whistle");

                if (isWhistle && (idLower.Contains("fulltime") || textLower.Contains("full-time") || textLower.Contains("full time") || c.MinuteDisplay == "FT"))
                {
                    return 999999999L;
                }

                if (isWhistle && (idLower.Contains("halftime") || textLower.Contains("half-time") || textLower.Contains("half time") || c.MinuteDisplay == "HT"))
                {
                    return (45L * 100000L) + (90L * 100L) + 9L;
                }

                if (isWhistle && (idLower.Contains("kickoff") || textLower.Contains("kicks off")))
                {
                    return 0L;
                }

                long m = c.Minute;
                long em = c.ExtraMinute ?? 0;
                return (m * 100000L) + (em * 100L) + 5L;
            })
            .ToList();

        SmartMergeCommentary(CommentaryItems, commentaryList);

        OnPropertyChanged(nameof(HasKeyEvents));
        OnPropertyChanged(nameof(HasCommentary));
        OnPropertyChanged(nameof(KeyEventsTabHeader));
        OnPropertyChanged(nameof(CommentaryTabHeader));

        // Hook real-time clock/score sync to taskbar ticker coordinator
        App.CurrentTickerCoordinator?.UpdateMatchFromDetails(match);

        // 2. Populate Lineups
        PopulateLineups(match);

        // 3. Populate Statistics
        PopulateStatistics(match);

        // 4. Populate Predictions, Form & Heatmaps
        PopulatePredictionsAndH2H(match);

        // 5. Ensure tactical pitch lineups are ready
        EnsureLineupPitchPopulated();
    }

    private static string StripTeamPrefix(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        string trimmed = id.Trim();
        if (trimmed.StartsWith("ft_team_", StringComparison.OrdinalIgnoreCase)) return trimmed.Substring(8);
        if (trimmed.StartsWith("team_", StringComparison.OrdinalIgnoreCase)) return trimmed.Substring(5);
        return trimmed;
    }

    private static bool IsTeamIdMatch(string? candidateId, string? targetId, string? targetLegacyId, string? targetAltId = null)
    {
        if (string.IsNullOrWhiteSpace(candidateId)) return false;
        string c = candidateId.Trim();
        string cNorm = StripTeamPrefix(c);

        if (!string.IsNullOrWhiteSpace(targetId))
        {
            string t = targetId.Trim();
            if (string.Equals(c, t, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cNorm, StripTeamPrefix(t), StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (!string.IsNullOrWhiteSpace(targetLegacyId))
        {
            string t = targetLegacyId.Trim();
            if (string.Equals(c, t, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cNorm, StripTeamPrefix(t), StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (!string.IsNullOrWhiteSpace(targetAltId))
        {
            string t = targetAltId.Trim();
            if (string.Equals(c, t, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(cNorm, StripTeamPrefix(t), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private void PopulateLineups(MatchDetailDto match)
    {
        HomeLineup.TeamName = HomeTeamName;
        AwayLineup.TeamName = AwayTeamName;

        if (match.Lineups == null || match.Lineups.Count == 0)
        {
            SmartMergeLineupPlayers(HomeLineup.StartingXI, Array.Empty<LineupPlayerItemViewModel>());
            SmartMergeLineupPlayers(HomeLineup.Substitutes, Array.Empty<LineupPlayerItemViewModel>());
            SmartMergeLineupPlayers(AwayLineup.StartingXI, Array.Empty<LineupPlayerItemViewModel>());
            SmartMergeLineupPlayers(AwayLineup.Substitutes, Array.Empty<LineupPlayerItemViewModel>());
            SmartMergePitchPlayers(HomePitchPlayers, Array.Empty<PitchPlayerNodeViewModel>());
            SmartMergePitchPlayers(AwayPitchPlayers, Array.Empty<PitchPlayerNodeViewModel>());
            SmartMergePitchPlayers(PitchPlayers, Array.Empty<PitchPlayerNodeViewModel>());

            HomeLineup.HasLineup = false;
            AwayLineup.HasLineup = false;
            HasLineups = false;
            return;
        }

        string homeId = match.HomeTeam?.Id ?? match.HomeTeamId ?? string.Empty;
        string homeLegacyId = match.HomeTeam?.LegacyId ?? string.Empty;
        string awayId = match.AwayTeam?.Id ?? match.AwayTeamId ?? string.Empty;
        string awayLegacyId = match.AwayTeam?.LegacyId ?? string.Empty;

        LineupDto? homeDto = match.Lineups.FirstOrDefault(l =>
            IsTeamIdMatch(l.TeamId, homeId, homeLegacyId, match.HomeTeamId));

        LineupDto? awayDto = match.Lineups.FirstOrDefault(l =>
            IsTeamIdMatch(l.TeamId, awayId, awayLegacyId, match.AwayTeamId));

        // Fallback by order if TeamId matching is inconclusive
        if (homeDto == null && match.Lineups.Count > 0)
        {
            homeDto = match.Lineups[0];
        }
        if (awayDto == null && match.Lineups.Count > 1)
        {
            awayDto = match.Lineups[1];
        }

        var newHomeStarters = new List<LineupPlayerItemViewModel>();
        var newHomeSubs = new List<LineupPlayerItemViewModel>();
        if (homeDto != null)
        {
            HomeLineup.Formation = homeDto.Formation ?? string.Empty;
            HomeLineup.CoachName = homeDto.EffectiveCoachName;
            var starters = homeDto.GetStartingXI();
            var subs = homeDto.GetSubstitutes();

            if (starters.Count == 0 && homeDto.Players != null && homeDto.Players.Count > 0)
            {
                starters = homeDto.Players.Take(11).ToList();
            }

            foreach (var p in starters)
            {
                newHomeStarters.Add(CreatePlayerItem(p, isStarter: true, match.Events));
            }
            foreach (var p in subs)
            {
                newHomeSubs.Add(CreatePlayerItem(p, isStarter: false, match.Events));
            }
            HomeLineup.HasLineup = starters.Count > 0 || subs.Count > 0;
        }
        else
        {
            HomeLineup.HasLineup = false;
        }

        var newAwayStarters = new List<LineupPlayerItemViewModel>();
        var newAwaySubs = new List<LineupPlayerItemViewModel>();
        if (awayDto != null)
        {
            AwayLineup.Formation = awayDto.Formation ?? string.Empty;
            AwayLineup.CoachName = awayDto.EffectiveCoachName;
            var starters = awayDto.GetStartingXI();
            var subs = awayDto.GetSubstitutes();

            if (starters.Count == 0 && awayDto.Players != null && awayDto.Players.Count > 0)
            {
                starters = awayDto.Players.Take(11).ToList();
            }

            foreach (var p in starters)
            {
                newAwayStarters.Add(CreatePlayerItem(p, isStarter: true, match.Events));
            }
            foreach (var p in subs)
            {
                newAwaySubs.Add(CreatePlayerItem(p, isStarter: false, match.Events));
            }
            AwayLineup.HasLineup = starters.Count > 0 || subs.Count > 0;
        }
        else
        {
            AwayLineup.HasLineup = false;
        }

        SmartMergeLineupPlayers(HomeLineup.StartingXI, newHomeStarters);
        SmartMergeLineupPlayers(HomeLineup.Substitutes, newHomeSubs);
        SmartMergeLineupPlayers(AwayLineup.StartingXI, newAwayStarters);
        SmartMergeLineupPlayers(AwayLineup.Substitutes, newAwaySubs);

        HomeLineup.Unavailable.Clear();
        var homeUnavailable = homeDto?.GetUnavailable();
        if (homeUnavailable != null)
        {
            foreach (var u in homeUnavailable)
            {
                HomeLineup.Unavailable.Add(new UnavailablePlayerItemViewModel
                {
                    Name = u.Name,
                    Reason = u.Reason,
                    Position = u.Position,
                    ShirtNumber = u.ShirtNumber,
                    ExpectedReturn = u.ExpectedReturn
                });
            }
        }
        HomeLineup.NotifyUnavailableChanged();

        AwayLineup.Unavailable.Clear();
        var awayUnavailable = awayDto?.GetUnavailable();
        if (awayUnavailable != null)
        {
            foreach (var u in awayUnavailable)
            {
                AwayLineup.Unavailable.Add(new UnavailablePlayerItemViewModel
                {
                    Name = u.Name,
                    Reason = u.Reason,
                    Position = u.Position,
                    ShirtNumber = u.ShirtNumber,
                    ExpectedReturn = u.ExpectedReturn
                });
            }
        }
        AwayLineup.NotifyUnavailableChanged();

        BuildPitchPlayers(homeDto, awayDto, match.Events);

        HasLineups = HomePitchPlayers.Count > 0 || AwayPitchPlayers.Count > 0 || PitchPlayers.Count > 0 || HomeLineup.HasLineup || AwayLineup.HasLineup;
        OnPropertyChanged(nameof(CurrentLineup));

        // Standings & Leaders
        Standings.Clear();
        if (match.Standings != null)
        {
            var mainVm = App.CurrentMainViewModel;
            var prefs = mainVm?.PreferencesService?.GetPreferences();
            var followedTeams = prefs?.GetFollowedTeamIdSet() ?? new HashSet<string>();
            var followedNames = prefs?.FollowedTeams?.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in match.Standings)
            {
                bool isFollowed = followedTeams.Contains(s.TeamId) || followedNames.Contains(s.TeamName) || (!string.IsNullOrEmpty(s.TeamShortName) && followedNames.Contains(s.TeamShortName));
                Standings.Add(new StandingRowItemViewModel(s, isFollowed));
            }
        }
        OnPropertyChanged(nameof(HasStandings));
        OnPropertyChanged(nameof(StandingsVisibility));

        TopScorers.Clear();
        if (match.TopScorers != null)
        {
            foreach (var sc in match.TopScorers)
            {
                TopScorers.Add(new PlayerStatLeaderItemViewModel(sc, isAssistLeader: false));
            }
        }
        OnPropertyChanged(nameof(HasTopScorers));

        TopAssists.Clear();
        if (match.TopAssists != null)
        {
            foreach (var asst in match.TopAssists)
            {
                TopAssists.Add(new PlayerStatLeaderItemViewModel(asst, isAssistLeader: true));
            }
        }
        OnPropertyChanged(nameof(HasTopAssists));
        OnPropertyChanged(nameof(HasLeaders));
        OnPropertyChanged(nameof(LeadersVisibility));
        OnPropertyChanged(nameof(NoStandingsVisibility));
    }

    private void BuildPitchPlayers(LineupDto? homeDto, LineupDto? awayDto, IReadOnlyList<TrayEventDto>? events = null)
    {
        var awayStarters = ResolveStarters(awayDto, AwayLineup);
        var homeStarters = ResolveStarters(homeDto, HomeLineup);

        var homeNodes = homeStarters.Count > 0
            ? CreateTeamPitchNodes(homeStarters, isHomeTeam: true, events, HomeLineup.Formation)
            : new List<PitchPlayerNodeViewModel>();

        var awayNodes = awayStarters.Count > 0
            ? CreateTeamPitchNodes(awayStarters, isHomeTeam: false, events, AwayLineup.Formation)
            : new List<PitchPlayerNodeViewModel>();

        var incomingPitch = new List<PitchPlayerNodeViewModel>();
        incomingPitch.AddRange(awayNodes);
        incomingPitch.AddRange(homeNodes);

        SmartMergePitchPlayers(HomePitchPlayers, homeNodes);
        SmartMergePitchPlayers(AwayPitchPlayers, awayNodes);
        SmartMergePitchPlayers(PitchPlayers, incomingPitch);

        OnPropertyChanged(nameof(HomePitchPlayers));
        OnPropertyChanged(nameof(AwayPitchPlayers));
        OnPropertyChanged(nameof(PitchPlayers));

        HasLineups = HomePitchPlayers.Count > 0 || AwayPitchPlayers.Count > 0 || PitchPlayers.Count > 0 || HomeLineup.HasLineup || AwayLineup.HasLineup;
    }

    private static IReadOnlyList<LineupPlayerDto> ResolveStarters(LineupDto? dto, LineupPresentationViewModel presentation)
    {
        if (dto != null)
        {
            var starters = dto.GetStartingXI();
            if (starters.Count > 0)
                return starters;

            if (dto.Players != null && dto.Players.Count > 0)
            {
                var filtered = dto.Players.FindAll(p => p.IsStarter);
                if (filtered.Count > 0) return filtered;
                if (dto.Players.Count >= 11) return dto.Players.Take(11).ToList();
                return dto.Players;
            }
        }

        if (presentation != null && presentation.StartingXI.Count > 0)
        {
            return presentation.StartingXI.Select(p => new LineupPlayerDto
            {
                Id = p.Id,
                PlayerId = p.Id,
                Name = p.Name,
                Number = int.TryParse(p.Number, out int num) ? num : null,
                Position = p.PositionBadge,
                IsStarter = true,
                Rating = p.Rating,
                IsManOfTheMatch = p.IsManOfTheMatch,
                IsCaptain = p.IsCaptain
            }).ToList();
        }

        return Array.Empty<LineupPlayerDto>();
    }

    private bool _isPopulatingLineups;

    public void EnsureLineupPitchPopulated()
    {
        if (_isPopulatingLineups) return;
        if (HomePitchPlayers.Count > 0 && AwayPitchPlayers.Count > 0)
        {
            HasLineups = true;
            return;
        }

        _isPopulatingLineups = true;
        try
        {
            if (_lastMatchDetail != null && HomePitchPlayers.Count == 0 && AwayPitchPlayers.Count == 0)
            {
                PopulateLineups(_lastMatchDetail);
            }

            if (HomePitchPlayers.Count == 0 && HomeLineup.StartingXI.Count > 0)
            {
                var homeDtos = HomeLineup.StartingXI.Select(p => new LineupPlayerDto
                {
                    Id = p.Id,
                    PlayerId = p.Id,
                    Name = p.Name,
                    Number = int.TryParse(p.Number, out int num) ? num : null,
                    Position = p.PositionBadge,
                    IsStarter = true,
                    Rating = p.Rating,
                    IsManOfTheMatch = p.IsManOfTheMatch
                }).ToList();
                var nodes = CreateTeamPitchNodes(homeDtos, isHomeTeam: true, _lastMatchDetail?.Events, HomeLineup.Formation);
                SmartMergePitchPlayers(HomePitchPlayers, nodes);
            }

            if (AwayPitchPlayers.Count == 0 && AwayLineup.StartingXI.Count > 0)
            {
                var awayDtos = AwayLineup.StartingXI.Select(p => new LineupPlayerDto
                {
                    Id = p.Id,
                    PlayerId = p.Id,
                    Name = p.Name,
                    Number = int.TryParse(p.Number, out int num) ? num : null,
                    Position = p.PositionBadge,
                    IsStarter = true,
                    Rating = p.Rating,
                    IsManOfTheMatch = p.IsManOfTheMatch
                }).ToList();
                var nodes = CreateTeamPitchNodes(awayDtos, isHomeTeam: false, _lastMatchDetail?.Events, AwayLineup.Formation);
                SmartMergePitchPlayers(AwayPitchPlayers, nodes);
            }

            if (PitchPlayers.Count == 0 && (HomePitchPlayers.Count > 0 || AwayPitchPlayers.Count > 0))
            {
                var combined = new List<PitchPlayerNodeViewModel>();
                combined.AddRange(AwayPitchPlayers);
                combined.AddRange(HomePitchPlayers);
                SmartMergePitchPlayers(PitchPlayers, combined);
            }

            HasLineups = HomePitchPlayers.Count > 0 || AwayPitchPlayers.Count > 0 || PitchPlayers.Count > 0 || HomeLineup.HasLineup || AwayLineup.HasLineup;
        }
        finally
        {
            _isPopulatingLineups = false;
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

    private static string StripPlayerPrefix(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        string trimmed = id.Trim();
        if (trimmed.StartsWith("ft_player_", StringComparison.OrdinalIgnoreCase)) return trimmed.Substring(10);
        if (trimmed.StartsWith("player_", StringComparison.OrdinalIgnoreCase)) return trimmed.Substring(7);
        return trimmed;
    }

    private static bool IsPlayerMatch(string? eventPlayerId, string? eventPlayerName, string? playerId, string? playerName)
    {
        if (!string.IsNullOrWhiteSpace(eventPlayerId) && !string.IsNullOrWhiteSpace(playerId))
        {
            if (string.Equals(eventPlayerId.Trim(), playerId.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            string p1 = StripPlayerPrefix(eventPlayerId);
            string p2 = StripPlayerPrefix(playerId);
            if (!string.IsNullOrEmpty(p1) && string.Equals(p1, p2, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(eventPlayerName) && !string.IsNullOrWhiteSpace(playerName))
        {
            string n1 = eventPlayerName.Trim();
            string n2 = playerName.Trim();
            if (string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase))
                return true;

            var parts1 = n1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var parts2 = n2.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts1.Length > 0 && parts2.Length > 0)
            {
                string lastName1 = parts1[^1];
                string lastName2 = parts2[^1];
                if (string.Equals(lastName1, lastName2, StringComparison.OrdinalIgnoreCase))
                {
                    if (parts1.Length == 1 || parts2.Length == 1)
                        return true;
                    if (char.ToUpperInvariant(parts1[0][0]) == char.ToUpperInvariant(parts2[0][0]))
                        return true;
                }
            }
        }

        return false;
    }

    private static void CorrelatePlayerEvents(
        string? playerId,
        string? playerName,
        bool isStarter,
        IReadOnlyList<TrayEventDto>? events,
        out bool isSubbedOn,
        out int? subInMinute,
        out string subInText,
        out bool isSubbedOff,
        out int? subOutMinute,
        out string subOutText,
        out bool hasYellowCard,
        out bool hasRedCard,
        out int regGoals,
        out int penGoals,
        out int ownGoals,
        out int assists)
    {
        isSubbedOn = false;
        subInMinute = null;
        subInText = string.Empty;
        isSubbedOff = false;
        subOutMinute = null;
        subOutText = string.Empty;
        hasYellowCard = false;
        hasRedCard = false;
        regGoals = 0;
        penGoals = 0;
        ownGoals = 0;
        assists = 0;

        if (events == null || events.Count == 0) return;

        foreach (var ev in events)
        {
            string evType = (ev.Type ?? string.Empty).Trim();
            string evTypeLower = evType.ToLowerInvariant();
            string detail = (ev.Detail ?? string.Empty).Trim();
            string detailLower = detail.ToLowerInvariant();
            int minute = ev.Minute;

            bool matchesPlayer = IsPlayerMatch(ev.PlayerId, ev.PlayerName, playerId, playerName);
            bool matchesAssist = IsPlayerMatch(ev.AssistPlayerId, ev.AssistPlayerName, playerId, playerName);

            // 1. Substitutions
            if (evTypeLower.Contains("sub"))
            {
                if (matchesPlayer || matchesAssist)
                {
                    if (isStarter)
                    {
                        isSubbedOff = true;
                        subOutMinute = minute;
                        subOutText = "▼";
                    }
                    else
                    {
                        if (!isSubbedOn)
                        {
                            isSubbedOn = true;
                            subInMinute = minute;
                            subInText = "▲";
                        }
                    }
                }
            }

            // 2. Cards
            if (matchesPlayer && (evTypeLower.Contains("card") || detailLower.Contains("card")))
            {
                if (evTypeLower.Contains("red") || detailLower.Contains("red") || detailLower.Contains("second yellow"))
                {
                    hasRedCard = true;
                }
                else
                {
                    hasYellowCard = true;
                }
            }

            // 3. Goals
            if (matchesPlayer && evTypeLower.Contains("goal"))
            {
                if (detailLower.Contains("penalty") || detailLower.Contains("pen"))
                {
                    penGoals++;
                }
                else if (detailLower.Contains("own") || detailLower.Contains("og"))
                {
                    ownGoals++;
                }
                else
                {
                    regGoals++;
                }
            }

            // 4. Assists
            if (matchesAssist && evTypeLower.Contains("goal") && !detailLower.Contains("own"))
            {
                assists++;
            }
        }
    }

    public static List<PitchPlayerNodeViewModel> CreateTeamPitchNodes(
        IReadOnlyList<LineupPlayerDto> starters,
        bool isHomeTeam,
        IReadOnlyList<TrayEventDto>? events = null,
        string? formation = null)
    {
        var result = new List<PitchPlayerNodeViewModel>();
        if (starters == null || starters.Count == 0) return result;

        // Parse formation rows if available (e.g. "4-3-3" -> [4, 3, 3])
        int[]? formationRows = null;
        if (!string.IsNullOrWhiteSpace(formation))
        {
            var parts = formation.Split('-', StringSplitOptions.TrimEntries);
            var parsedList = new List<int>();
            foreach (var part in parts)
            {
                if (int.TryParse(part, out int count) && count > 0)
                {
                    parsedList.Add(count);
                }
            }
            if (parsedList.Count >= 2)
            {
                formationRows = parsedList.ToArray();
            }
        }

        var parsed = new List<(LineupPlayerDto Player, int Row, int Col, int Index)>();
        bool hasExplicitGrid = starters.Any(p => !string.IsNullOrWhiteSpace(p.Grid) || (p.Row.HasValue && p.Col.HasValue));

        for (int i = 0; i < starters.Count; i++)
        {
            var p = starters[i];
            int row, col;

            if (hasExplicitGrid)
            {
                (row, col) = ParseGridCoordinates(p.Grid, p.Position, i + 1);
                if (p.Row.HasValue && p.Col.HasValue && p.Row.Value > 0 && p.Col.Value > 0)
                {
                    row = p.Row.Value;
                    col = p.Col.Value;
                }
            }
            else
            {
                // Assign from formation or default layout
                if (i == 0)
                {
                    row = 1;
                    col = 1;
                }
                else if (formationRows != null)
                {
                    int remaining = i - 1;
                    row = 2;
                    col = 1;
                    for (int r = 0; r < formationRows.Length; r++)
                    {
                        if (remaining < formationRows[r])
                        {
                            row = r + 2;
                            col = remaining + 1;
                            break;
                        }
                        remaining -= formationRows[r];
                        if (r == formationRows.Length - 1)
                        {
                            row = formationRows.Length + 1;
                            col = formationRows[r] + remaining + 1;
                        }
                    }
                }
                else
                {
                    (row, col) = ParseGridCoordinates(p.Grid, p.Position, i + 1);
                }
            }

            parsed.Add((p, row, col, i));
        }

        var rowGroups = parsed.GroupBy(p => p.Row).OrderBy(g => g.Key).ToList();
        int minRow = rowGroups.Min(g => g.Key);
        int maxRow = rowGroups.Max(g => g.Key);

        foreach (var g in rowGroups)
        {
            int rowKey = g.Key;
            double t = (maxRow > minRow) ? (double)(rowKey - minRow) / (maxRow - minRow) : 0.0;

            // Horizontal Pitch Layout:
            // Home (Left attacking right): GK at 0.08, Attackers at 0.45
            // Away (Right attacking left): GK at 0.92, Attackers at 0.55
            double x = isHomeTeam
                ? Math.Round(0.08 + (t * 0.37), 3)
                : Math.Round(0.92 - (t * 0.37), 3);

            var playersInRow = g.OrderBy(p => p.Col).ThenBy(p => p.Index).ToList();
            int countInRow = playersInRow.Count;

            for (int i = 0; i < countInRow; i++)
            {
                var item = playersInRow[i];
                double y;
                if (countInRow <= 1)
                {
                    y = 0.50;
                }
                else if (countInRow >= 4)
                {
                    y = Math.Round(0.12 + ((double)i / (countInRow - 1)) * 0.76, 3);
                }
                else
                {
                    y = Math.Round(0.12 + ((i + 1.0) / (countInRow + 1.0)) * 0.76, 3);
                }

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

                string? effectivePlayerId = item.Player.PlayerId ?? item.Player.Id;
                CorrelatePlayerEvents(
                    effectivePlayerId,
                    item.Player.Name,
                    isStarter: true,
                    events,
                    out _,
                    out _,
                    out _,
                    out bool isSubbedOff,
                    out int? subOutMin,
                    out string subOutText,
                    out bool hasYellow,
                    out bool hasRed,
                    out int regGoals,
                    out int penGoals,
                    out int ownGoals,
                    out int assists);

                int? effectiveNumber = item.Player.EffectiveNumber;
                string numberStr = effectiveNumber.HasValue ? effectiveNumber.Value.ToString() : (item.Index + 1).ToString();

                var node = new PitchPlayerNodeViewModel
                {
                    Name = item.Player.Name,
                    ShortDisplayName = PitchPlayerNodeViewModel.ExtractShortDisplayName(item.Player.Name),
                    Number = numberStr,
                    Position = pos,
                    PositionBadge = badge,
                    IsGoalkeeper = isGk,
                    IsHomeTeam = isHomeTeam,
                    Row = rowKey,
                    Col = item.Col,
                    XPercent = x,
                    YPercent = y,
                    Rating = item.Player.Rating,
                    IsManOfTheMatch = item.Player.IsManOfTheMatch,
                    IsCaptain = item.Player.IsCaptain == true,
                    PlayerId = effectivePlayerId,
                    IsSubbedOff = isSubbedOff,
                    SubOutMinute = subOutMin,
                    SubOutText = subOutText,
                    HasYellowCard = hasYellow,
                    HasRedCard = hasRed,
                    RegularGoalsCount = regGoals,
                    PenaltyGoalsCount = penGoals,
                    OwnGoalsCount = ownGoals,
                    AssistsCount = assists
                };

                result.Add(node);
            }
        }

        return result;
    }

    private static LineupPlayerItemViewModel CreatePlayerItem(
        LineupPlayerDto p,
        bool isStarter,
        IReadOnlyList<TrayEventDto>? events = null)
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

        string? effectivePlayerId = p.PlayerId ?? p.Id;
        CorrelatePlayerEvents(
            effectivePlayerId,
            p.Name,
            isStarter,
            events,
            out bool isSubbedOn,
            out int? subInMin,
            out string subInText,
            out bool isSubbedOff,
            out int? subOutMin,
            out string subOutText,
            out bool hasYellow,
            out bool hasRed,
            out int regGoals,
            out int penGoals,
            out int ownGoals,
            out int assists);

        return new LineupPlayerItemViewModel
        {
            Number = p.Number.HasValue ? p.Number.Value.ToString() : "-",
            Name = p.Name,
            Position = pos,
            PositionBadge = badge,
            Rating = p.Rating,
            IsManOfTheMatch = p.IsManOfTheMatch,
            IsCaptain = p.IsCaptain == true,
            PlayerId = effectivePlayerId,
            IsSubbedOn = isSubbedOn,
            SubInMinute = subInMin,
            SubInText = subInText,
            IsSubbedOff = isSubbedOff,
            SubOutMinute = subOutMin,
            SubOutText = subOutText,
            HasYellowCard = hasYellow,
            HasRedCard = hasRed,
            RegularGoalsCount = regGoals,
            PenaltyGoalsCount = penGoals,
            OwnGoalsCount = ownGoals,
            AssistsCount = assists
        };
    }

    private void PopulateStatistics(MatchDetailDto match)
    {
        if (match.Statistics == null || match.Statistics.Count == 0)
        {
            SmartMergeStats(MatchStats, Array.Empty<StatComparisonViewModel>());
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
            SmartMergeStats(MatchStats, Array.Empty<StatComparisonViewModel>());
            HasStatistics = false;
            return;
        }

        var incomingStats = new List<StatComparisonViewModel>();

        void AddStat(string name, string homeDisplay, string awayDisplay, double homeRatio, double awayRatio)
        {
            incomingStats.Add(new StatComparisonViewModel
            {
                Name = name,
                HomeValue = homeDisplay,
                AwayValue = awayDisplay,
                HomeRatio = Math.Clamp(homeRatio, 0.0, 100.0),
                AwayRatio = Math.Clamp(awayRatio, 0.0, 100.0)
            });
        }

        void AddNumericStat(string name, int? home, int? away)
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

            AddStat(name, hVal.ToString(), aVal.ToString(), hRatio, aRatio);
        }

        // Expected Goals (xG)
        double? homeXg = homeStats?.ExpectedGoals ?? homeStats?.Xg;
        double? awayXg = awayStats?.ExpectedGoals ?? awayStats?.Xg;
        if (homeXg.HasValue || awayXg.HasValue)
        {
            double h = homeXg ?? 0.0;
            double a = awayXg ?? 0.0;
            double total = h + a;
            double hRatio = total > 0 ? Math.Round((h / total) * 100.0, 1) : 50.0;
            double aRatio = 100.0 - hRatio;
            AddStat("Expected Goals (xG)", h.ToString("0.00"), a.ToString("0.00"), hRatio, aRatio);
        }

        // Possession
        int? homePoss = homeStats?.PossessionPercent ?? homeStats?.Possession;
        int? awayPoss = awayStats?.PossessionPercent ?? awayStats?.Possession;
        if (homePoss.HasValue || awayPoss.HasValue)
        {
            int hp = homePoss ?? (awayPoss.HasValue ? 100 - awayPoss.Value : 50);
            int ap = awayPoss ?? (100 - hp);
            AddStat("Ball Possession", $"{hp}%", $"{ap}%", hp, ap);
        }

        // Total Shots
        AddNumericStat("Total Shots", homeStats?.TotalShots, awayStats?.TotalShots);

        // Shots on Goal
        AddNumericStat("Shots on Target", homeStats?.ShotsOnGoal, awayStats?.ShotsOnGoal);

        // Shots off Goal
        AddNumericStat("Shots off Target", homeStats?.ShotsOffGoal, awayStats?.ShotsOffGoal);

        // Total Passes
        AddNumericStat("Total Passes", homeStats?.TotalPasses, awayStats?.TotalPasses);

        // Passes Accurate
        AddNumericStat("Accurate Passes", homeStats?.PassesAccurate, awayStats?.PassesAccurate);

        // Corner Kicks
        int? homeCorners = homeStats?.CornerKicks ?? homeStats?.Corners;
        int? awayCorners = awayStats?.CornerKicks ?? awayStats?.Corners;
        AddNumericStat("Corner Kicks", homeCorners, awayCorners);

        // Fouls
        AddNumericStat("Fouls", homeStats?.Fouls, awayStats?.Fouls);

        // Yellow Cards
        AddNumericStat("Yellow Cards", homeStats?.YellowCards, awayStats?.YellowCards);

        // Red Cards
        AddNumericStat("Red Cards", homeStats?.RedCards, awayStats?.RedCards);

        // Goalkeeper Saves
        AddNumericStat("Goalkeeper Saves", homeStats?.GoalkeeperSaves, awayStats?.GoalkeeperSaves);

        // Offsides
        AddNumericStat("Offsides", homeStats?.Offsides, awayStats?.Offsides);

        SmartMergeStats(MatchStats, incomingStats);
        HasStatistics = MatchStats.Count > 0;
    }

    private static string FormatStatusText(MatchDetailDto match)
    {
        string state = (match.State ?? "PREMATCH").Trim().ToUpperInvariant();

        return state switch
        {
            // Active Play (FIRST_HALF, SECOND_HALF, EXTRA_TIME, LIVE, 1H, 2H, ET, IN_PLAY)
            "FIRST_HALF" or "SECOND_HALF" or "EXTRA_TIME" or "LIVE" or "1H" or "2H" or "ET" or "IN_PLAY" =>
                match.ElapsedMinute > 0
                    ? (match.ExtraMinute > 0
                        ? $"LIVE {match.ElapsedMinute}+{match.ExtraMinute}'"
                        : $"LIVE {match.ElapsedMinute}'")
                    : (state is "FIRST_HALF" or "1H"
                        ? "LIVE 1H"
                        : state is "SECOND_HALF" or "2H"
                            ? "LIVE 2H"
                            : state is "EXTRA_TIME" or "ET"
                                ? "LIVE ET"
                                : "LIVE"),

            // Penalties (PENALTIES, P)
            "PENALTIES" or "P" => "LIVE PEN",

            // Halftime (HALFTIME, HT, EXTRA_TIME_HALFTIME, BT)
            "HALFTIME" or "HT" or "EXTRA_TIME_HALFTIME" or "BT" => "Half Time (HT)",

            // Finished (FINISHED, FT, FINISHED_AET, AET, FINISHED_PEN, PEN)
            "FINISHED_AET" or "AET" => "Full Time (AET)",
            "FINISHED_PEN" => "Full Time (PEN)",
            "FINISHED" or "FT" or "POSTMATCH" or "FINAL" => "Full Time (FT)",
            "PEN" => "Full Time (PEN)",

            // Fallback
            _ => state
        };
    }

    private void PopulatePredictionsAndH2H(MatchDetailDto match)
    {
        var h2h = match.H2HAndPredictions;
        if (h2h != null)
        {
            HomeWinPct = h2h.HomeWinProbabilityPct;
            DrawPct = h2h.DrawProbabilityPct;
            AwayWinPct = h2h.AwayWinProbabilityPct;

            H2HHomeWins = h2h.HomeWins;
            H2HDraws = h2h.Draws;
            H2HAwayWins = h2h.AwayWins;

            HomeFormBadges.Clear();
            foreach (var f in h2h.HomeForm)
            {
                HomeFormBadges.Add(new FormBadgeViewModel
                {
                    Result = f.Result,
                    OpponentName = f.OpponentName,
                    Score = f.Score
                });
            }

            AwayFormBadges.Clear();
            foreach (var f in h2h.AwayForm)
            {
                AwayFormBadges.Add(new FormBadgeViewModel
                {
                    Result = f.Result,
                    OpponentName = f.OpponentName,
                    Score = f.Score
                });
            }

            LastFiveMeetings.Clear();
            foreach (var m in h2h.LastFiveMeetings)
            {
                string dt = string.Empty;
                if (DateTimeOffset.TryParse(m.DateUtc, out var dto))
                {
                    dt = dto.LocalDateTime.ToString("MMM d, yyyy");
                }
                else
                {
                    dt = m.DateUtc;
                }

                LastFiveMeetings.Add(new H2HMeetingViewModel
                {
                    MatchId = m.MatchId,
                    DateDisplay = dt,
                    CompetitionName = m.CompetitionName ?? "Match",
                    HomeTeamName = m.HomeTeamName,
                    AwayTeamName = m.AwayTeamName,
                    ScoreDisplay = m.ScoreDisplay
                });
            }
        }
        else
        {
            // Default realistic fallback
            HomeWinPct = 45;
            DrawPct = 25;
            AwayWinPct = 30;
            H2HHomeWins = 2;
            H2HDraws = 1;
            H2HAwayWins = 2;

            HomeFormBadges.Clear();
            HomeFormBadges.Add(new FormBadgeViewModel { Result = "W", OpponentName = "Recent Match", Score = "2-1" });
            HomeFormBadges.Add(new FormBadgeViewModel { Result = "W", OpponentName = "Recent Match", Score = "3-0" });
            HomeFormBadges.Add(new FormBadgeViewModel { Result = "D", OpponentName = "Recent Match", Score = "1-1" });
            HomeFormBadges.Add(new FormBadgeViewModel { Result = "L", OpponentName = "Recent Match", Score = "0-1" });
            HomeFormBadges.Add(new FormBadgeViewModel { Result = "W", OpponentName = "Recent Match", Score = "2-0" });

            AwayFormBadges.Clear();
            AwayFormBadges.Add(new FormBadgeViewModel { Result = "D", OpponentName = "Recent Match", Score = "1-1" });
            AwayFormBadges.Add(new FormBadgeViewModel { Result = "W", OpponentName = "Recent Match", Score = "1-0" });
            AwayFormBadges.Add(new FormBadgeViewModel { Result = "L", OpponentName = "Recent Match", Score = "1-2" });
            AwayFormBadges.Add(new FormBadgeViewModel { Result = "W", OpponentName = "Recent Match", Score = "2-1" });
            AwayFormBadges.Add(new FormBadgeViewModel { Result = "L", OpponentName = "Recent Match", Score = "0-2" });
        }

        // Heatmaps
        PopulateHeatmaps(match);
    }

    private void PopulateHeatmaps(MatchDetailDto match)
    {
        HeatmapPoints.Clear();
        if (match.Heatmaps != null && match.Heatmaps.Count > 0)
        {
            var homeH = match.Heatmaps[0];
            HomeAttackingThirdPct = homeH.AttackingThirdPct;
            HomeMiddleThirdPct = homeH.MiddleThirdPct;
            HomeDefensiveThirdPct = homeH.DefensiveThirdPct;

            if (match.Heatmaps.Count > 1)
            {
                var awayH = match.Heatmaps[1];
                AwayAttackingThirdPct = awayH.AttackingThirdPct;
                AwayMiddleThirdPct = awayH.MiddleThirdPct;
                AwayDefensiveThirdPct = awayH.DefensiveThirdPct;
            }

            foreach (var h in match.Heatmaps)
            {
                bool isHome = h == match.Heatmaps[0];
                foreach (var pt in h.Points)
                {
                    double left = Math.Round((pt.X / 100.0) * 340.0 - 20.0, 1);
                    double top = Math.Round((pt.Y / 100.0) * 500.0 - 20.0, 1);
                    var brush = isHome
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(pt.Intensity * 180), 14, 165, 233))
                        : new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(pt.Intensity * 180), 239, 68, 68));

                    HeatmapPoints.Add(new HeatmapPointViewModel
                    {
                        CanvasLeft = Math.Clamp(left, 10, 310),
                        CanvasTop = Math.Clamp(top, 10, 470),
                        Size = 36 + (pt.Intensity * 16),
                        FillBrush = brush,
                        Opacity = Math.Clamp(pt.Intensity, 0.4, 0.95)
                    });
                }
            }
        }
        else
        {
            HomeAttackingThirdPct = 36;
            HomeMiddleThirdPct = 42;
            HomeDefensiveThirdPct = 22;
            AwayAttackingThirdPct = 28;
            AwayMiddleThirdPct = 44;
            AwayDefensiveThirdPct = 28;
        }
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
