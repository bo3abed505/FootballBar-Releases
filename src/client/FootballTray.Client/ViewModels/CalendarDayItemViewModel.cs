using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model representing an individual day in the 3-day calendar navigation bar.
/// Encapsulates relative date labeling ("Yesterday", "Today", "Tomorrow"), active selection,
/// and live match counts.
/// </summary>
public sealed class CalendarDayItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _matchCount;

    public DateTime Date { get; }
    public bool IsToday { get; }
    public bool IsNotToday => !IsToday;
    public string DayOfWeekName { get; }
    public string DayNumber { get; }
    public string MonthName { get; }
    public string RelativeLabel { get; }
    public string DateDisplay { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonStyle)));
            }
        }
    }

    public int MatchCount
    {
        get => _matchCount;
        set
        {
            if (SetProperty(ref _matchCount, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMatches)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MatchCountText)));
            }
        }
    }

    public bool HasMatches => _matchCount > 0;
    public string MatchCountText => $"{_matchCount} {(_matchCount == 1 ? "match" : "matches")}";

    /// <summary>
    /// Dynamic button style resolving to AccentButtonStyle when selected, or DefaultButtonStyle otherwise.
    /// Safe for headless test runners where Application.Current may be null.
    /// </summary>
    public Style? ButtonStyle
    {
        get
        {
            if (Application.Current == null) return null;
            try
            {
                string key = IsSelected ? "AccentButtonStyle" : "DefaultButtonStyle";
                return Application.Current.Resources.TryGetValue(key, out var style) ? style as Style : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public CalendarDayItemViewModel(DateTime date, bool isToday, bool isSelected = false, int matchCount = 0)
    {
        Date = date.Date;
        IsToday = isToday;
        _isSelected = isSelected;
        _matchCount = matchCount;

        DayOfWeekName = date.ToString("ddd");
        DayNumber = date.ToString("%d");
        MonthName = date.ToString("MMM");
        DateDisplay = $"{MonthName} {DayNumber}";

        var today = DateTime.Today;
        if (date.Date == today)
        {
            RelativeLabel = "Today";
        }
        else if (date.Date == today.AddDays(-1))
        {
            RelativeLabel = "Yesterday";
        }
        else if (date.Date == today.AddDays(1))
        {
            RelativeLabel = "Tomorrow";
        }
        else
        {
            RelativeLabel = date.ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
