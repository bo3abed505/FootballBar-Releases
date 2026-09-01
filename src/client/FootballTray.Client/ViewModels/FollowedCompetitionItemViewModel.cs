using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model representing a competition available for following/unfollowing.
/// </summary>
public sealed class FollowedCompetitionItemViewModel : INotifyPropertyChanged
{
    private bool _isFollowed;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string? LogoUrl { get; init; }

    public bool IsFollowed
    {
        get => _isFollowed;
        set => SetProperty(ref _isFollowed, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FollowedCompetition ToDomain() => new(Id, Name, Code, Country, LogoUrl);

    public static FollowedCompetitionItemViewModel FromDomain(FollowedCompetition comp, bool isFollowed)
    {
        ArgumentNullException.ThrowIfNull(comp);
        return new FollowedCompetitionItemViewModel
        {
            Id = comp.Id ?? string.Empty,
            Name = comp.Name ?? string.Empty,
            Code = comp.Code ?? string.Empty,
            Country = comp.Country ?? string.Empty,
            LogoUrl = comp.LogoUrl,
            IsFollowed = isFollowed
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
