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
    private Uri? _cachedLogoUri;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string? LogoUrl { get; init; }

    public Uri? CachedLogoUri
    {
        get => _cachedLogoUri;
        set => SetProperty(ref _cachedLogoUri, value);
    }

    public bool IsFollowed
    {
        get => _isFollowed;
        set
        {
            if (SetProperty(ref _isFollowed, value))
            {
                OnPropertyChanged(nameof(FollowButtonText));
                OnPropertyChanged(nameof(CanFollow));
            }
        }
    }

    public string FollowButtonText => _isFollowed ? "Following" : "+ Follow";
    public bool CanFollow => !_isFollowed;

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

    public static FollowedTeamItemViewModel FromCatalogDto(FootballTray.Core.Dto.V2CatalogTeamDto dto, bool isFollowed)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new FollowedTeamItemViewModel
        {
            Id = dto.Id ?? string.Empty,
            Name = dto.Name ?? string.Empty,
            ShortName = dto.ShortName ?? dto.Code ?? string.Empty,
            Country = dto.Country ?? string.Empty,
            LogoUrl = dto.LogoUrl ?? dto.Logo,
            IsFollowed = isFollowed
        };
    }

    public async System.Threading.Tasks.Task EnsureLogoCachedAsync(FootballTray.Client.Services.ImageCacheService? imageCacheService = null)
    {
        if (CachedLogoUri != null) return;
        var cache = imageCacheService ?? FootballTray.Client.Services.ImageCacheService.Instance;
        if (!string.IsNullOrWhiteSpace(LogoUrl))
        {
            try
            {
                var uri = await cache.GetCachedImageUriAsync(LogoUrl, returnFallbackOnFailure: true);
                if (uri != null)
                {
                    CachedLogoUri = uri;
                }
            }
            catch
            {
                CachedLogoUri = FootballTray.Client.Services.ImageCacheService.DefaultFallbackShieldUri;
            }
        }
        else
        {
            CachedLogoUri = FootballTray.Client.Services.ImageCacheService.DefaultFallbackShieldUri;
        }
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
