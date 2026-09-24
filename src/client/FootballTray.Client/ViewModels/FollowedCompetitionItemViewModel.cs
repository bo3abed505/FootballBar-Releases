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
    private Uri? _cachedLogoUri;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
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

    public static FollowedCompetitionItemViewModel FromCatalogDto(FootballTray.Core.Dto.V2CatalogCompetitionDto dto, bool isFollowed)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new FollowedCompetitionItemViewModel
        {
            Id = dto.Id ?? string.Empty,
            Name = dto.Name ?? string.Empty,
            Code = dto.Code ?? string.Empty,
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
