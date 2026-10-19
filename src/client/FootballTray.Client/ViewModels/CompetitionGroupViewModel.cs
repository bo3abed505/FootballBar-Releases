using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model representing an expandable/collapsible competition accordion group card.
/// Groups match items under a clean header with match count badge, followed indicator, and chevron toggle.
/// </summary>
public sealed class CompetitionGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string CompetitionId { get; }
    public string CompetitionName { get; }
    public string? Country { get; private set; }
    public string? LogoUrl { get; private set; }
    public bool HasLogoUrl => !string.IsNullOrWhiteSpace(LogoUrl);
    public Visibility LogoVisibility => HasLogoUrl ? Visibility.Visible : Visibility.Collapsed;

    public bool HasFollowedTeams { get; private set; }
    public bool IsFollowedCompetition { get; private set; }
    public bool IsFollowed => HasFollowedTeams || IsFollowedCompetition;

    public ObservableCollection<MatchItemViewModel> Matches { get; } = new();

    public string CompetitionKey => !string.IsNullOrWhiteSpace(CompetitionId) ? CompetitionId : CompetitionName;

    public int MatchCount => Matches.Count;
    public string MatchCountText => Matches.Count.ToString();
    public Visibility FollowedStarVisibility => IsFollowed ? Visibility.Visible : Visibility.Collapsed;
    public string CountryDisplay => (!string.IsNullOrWhiteSpace(Country) && !string.Equals(Country, "COMP", StringComparison.OrdinalIgnoreCase)) ? $"• {Country}" : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(ChevronGlyph));
                OnPropertyChanged(nameof(BodyVisibility));
            }
        }
    }

    public string ChevronGlyph => _isExpanded ? "\uE70E" : "\uE70D"; // ChevronUp / ChevronDown
    public Visibility BodyVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

    public CompetitionGroupViewModel(
        string competitionId,
        string competitionName,
        string? country = null,
        string? logoUrl = null,
        bool hasFollowedTeams = false,
        bool isFollowedCompetition = false)
    {
        CompetitionId = competitionId;
        CompetitionName = competitionName;
        Country = country;
        LogoUrl = logoUrl;
        HasFollowedTeams = hasFollowedTeams;
        IsFollowedCompetition = isFollowedCompetition;
        _isExpanded = hasFollowedTeams || isFollowedCompetition;
    }

    public CompetitionGroupViewModel(
        string competitionId,
        string competitionName,
        string? country,
        string? logoUrl,
        bool isFollowed)
        : this(competitionId, competitionName, country, logoUrl, isFollowed, false)
    {
    }

    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    public void AddMatch(MatchItemViewModel match)
    {
        Matches.Add(match);
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(MatchCountText));
    }

    public void UpdateMetadata(string? country, string? logoUrl, bool hasFollowedTeams, bool isFollowedCompetition)
    {
        if (country != null && Country != country)
        {
            Country = country;
            OnPropertyChanged(nameof(Country));
            OnPropertyChanged(nameof(CountryDisplay));
        }
        if (logoUrl != null && LogoUrl != logoUrl)
        {
            LogoUrl = logoUrl;
            OnPropertyChanged(nameof(LogoUrl));
            OnPropertyChanged(nameof(HasLogoUrl));
            OnPropertyChanged(nameof(LogoVisibility));
        }
        if (HasFollowedTeams != hasFollowedTeams || IsFollowedCompetition != isFollowedCompetition)
        {
            HasFollowedTeams = hasFollowedTeams;
            IsFollowedCompetition = isFollowedCompetition;
            OnPropertyChanged(nameof(HasFollowedTeams));
            OnPropertyChanged(nameof(IsFollowedCompetition));
            OnPropertyChanged(nameof(IsFollowed));
            OnPropertyChanged(nameof(FollowedStarVisibility));
        }
    }

    /// <summary>
    /// Performs in-place smart merge on the internal Matches collection without wiping or collapsing.
    /// Preserves existing viewmodel instances and expands/contracts the collection minimally.
    /// </summary>
    public void SyncMatches(IList<MatchItemViewModel> incomingMatches)
    {
        var incomingIds = new HashSet<string>(incomingMatches.Select(m => m.Id), StringComparer.Ordinal);

        // 1. Remove deleted matches
        for (int i = Matches.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(Matches[i].Id))
            {
                Matches.RemoveAt(i);
            }
        }

        // 2. Insert or update incoming matches at matching indexes
        for (int i = 0; i < incomingMatches.Count; i++)
        {
            var incoming = incomingMatches[i];
            int existingIndex = -1;
            for (int j = 0; j < Matches.Count; j++)
            {
                if (string.Equals(Matches[j].Id, incoming.Id, StringComparison.Ordinal))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                if (i <= Matches.Count)
                {
                    Matches.Insert(i, incoming);
                }
                else
                {
                    Matches.Add(incoming);
                }
            }
            else
            {
                Matches[existingIndex] = incoming;
                if (existingIndex != i && i < Matches.Count)
                {
                    Matches.Move(existingIndex, i);
                }
            }
        }

        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(MatchCountText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
