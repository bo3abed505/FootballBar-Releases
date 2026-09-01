using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model representing a team available for following/unfollowing.
/// </summary>
public sealed class FollowedTeamItemViewModel : INotifyPropertyChanged
{
    private bool _isFollowed;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string? LogoUrl { get; init; }

    public bool IsFollowed
    {
        get => _isFollowed;
        set => SetProperty(ref _isFollowed, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FollowedTeam ToDomain() => new(Id, Name, ShortName, Country, LogoUrl);

    public static FollowedTeamItemViewModel FromDomain(FollowedTeam team, bool isFollowed)
    {
        ArgumentNullException.ThrowIfNull(team);
        return new FollowedTeamItemViewModel
        {
            Id = team.Id ?? string.Empty,
            Name = team.Name ?? string.Empty,
            ShortName = team.ShortName ?? string.Empty,
            Country = team.Country ?? string.Empty,
            LogoUrl = team.LogoUrl,
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
