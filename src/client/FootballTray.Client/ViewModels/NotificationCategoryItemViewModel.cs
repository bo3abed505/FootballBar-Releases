using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model representing an individual notification category toggle.
/// </summary>
public sealed class NotificationCategoryItemViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;

    public NotificationType Type { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static NotificationCategoryItemViewModel Create(NotificationType type, bool isEnabled)
    {
        (string name, string desc) = type switch
        {
            NotificationType.MatchStarted => ("Match Started", "Alert when a followed match kicks off"),
            NotificationType.Goal => ("Goal Alert", "Alert on goals scored with scorer details"),
            NotificationType.HalfTime => ("Half-Time (HT)", "Alert when the first half concludes"),
            NotificationType.MatchFinished => ("Full-Time (FT)", "Alert when a match reaches final whistle"),
            NotificationType.MatchPostponed => ("Match Postponed", "Alert if a fixture is rescheduled/postponed"),
            NotificationType.MatchCancelled => ("Match Cancelled", "Alert if a fixture is officially cancelled"),
            NotificationType.MatchResumed => ("Match Resumed", "Alert when a suspended/delayed match restarts"),
            NotificationType.RedCard => ("Red Card", "Alert when a player is sent off"),
            NotificationType.PenaltyAwarded => ("Penalty Awarded", "Alert when a penalty kick is awarded"),
            NotificationType.SecondHalf => ("Second Half", "Alert when the second half kicks off"),
            NotificationType.Kickoff => ("Kickoff Reminder", "Alert before a followed match kicks off"),
            _ => (type.ToString(), "Match alert")
        };

        return new NotificationCategoryItemViewModel
        {
            Type = type,
            DisplayName = name,
            Description = desc,
            IsEnabled = isEnabled
        };
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
