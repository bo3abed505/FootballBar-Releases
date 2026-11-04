using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model for a date accordion group on the Following tab.
/// Contains nested CompetitionGroupViewModels for all followed matches on this date.
/// </summary>
public sealed class FollowingDayGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;

    public DateTime Date { get; }
    public string DateKey { get; }
    public string HeaderTitle { get; }
    public bool IsToday { get; }
    public ObservableCollection<CompetitionGroupViewModel> CompetitionGroups { get; } = new();

    public int MatchCount => CompetitionGroups.Sum(cg => cg.MatchCount);
    public string MatchCountBadge => MatchCount.ToString();
    public Visibility EmptyDayVisibility => MatchCount == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    public FollowingDayGroupViewModel(DateTime date, bool isToday, bool isExpanded = false)
    {
        Date = date.Date;
        DateKey = date.ToString("yyyy-MM-dd");
        IsToday = isToday;
        _isExpanded = isExpanded;

        var today = DateTime.Today;
        if (Date == today)
        {
            HeaderTitle = $"Today • {date:ddd, MMM d}";
        }
        else if (Date == today.AddDays(-1))
        {
            HeaderTitle = $"Yesterday • {date:ddd, MMM d}";
        }
        else if (Date == today.AddDays(1))
        {
            HeaderTitle = $"Tomorrow • {date:ddd, MMM d}";
        }
        else
        {
            HeaderTitle = date.ToString("ddd, MMM d");
        }
    }

    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(MatchCount));
        OnPropertyChanged(nameof(MatchCountBadge));
        OnPropertyChanged(nameof(EmptyDayVisibility));
    }

    public static List<FollowingDayGroupViewModel> Create7DayWindow(DateTime baseDate)
    {
        var groups = new List<FollowingDayGroupViewModel>();
        var today = DateTime.Today;
        for (int offset = -1; offset <= 5; offset++)
        {
            var date = baseDate.AddDays(offset).Date;
            bool isToday = (date == today);
            bool isExpanded = (date == baseDate.Date);
            groups.Add(new FollowingDayGroupViewModel(date, isToday, isExpanded));
        }
        return groups;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

