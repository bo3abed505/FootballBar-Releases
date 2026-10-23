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
    public string? LegacyId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    private string? _logoUrl;
    public string? LogoUrl
    {
        get => _logoUrl;
        set
        {
            if (SetProperty(ref _logoUrl, value))
            {
                OnPropertyChanged(nameof(LogoSource));
                OnPropertyChanged(nameof(HasLogo));
                OnPropertyChanged(nameof(LogoVisibility));
                OnPropertyChanged(nameof(FallbackVisibility));
                if (!string.IsNullOrWhiteSpace(_logoUrl))
                {
                    _ = EnsureLogoCachedAsync();
                }
            }
        }
    }

    public Uri? CachedLogoUri
    {
        get => _cachedLogoUri;
        set
        {
            if (SetProperty(ref _cachedLogoUri, value))
            {
                OnPropertyChanged(nameof(LogoSource));
                OnPropertyChanged(nameof(HasLogo));
                OnPropertyChanged(nameof(LogoVisibility));
                OnPropertyChanged(nameof(FallbackVisibility));
            }
        }
    }

    public Microsoft.UI.Xaml.Media.ImageSource? LogoSource
    {
        get
        {
            if (CachedLogoUri != null)
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(CachedLogoUri);
            }
            if (!string.IsNullOrWhiteSpace(LogoUrl) && Uri.TryCreate(LogoUrl, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public bool HasLogo => CachedLogoUri != null || !string.IsNullOrWhiteSpace(LogoUrl);
    public Microsoft.UI.Xaml.Visibility LogoVisibility => HasLogo ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility FallbackVisibility => HasLogo ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

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
    public bool CanFollow => true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public FollowedCompetition ToDomain() => new(Id, Name, Code, Country, LogoUrl);

    public static FollowedCompetitionItemViewModel FromDomain(FollowedCompetition comp, bool isFollowed)
    {
        ArgumentNullException.ThrowIfNull(comp);
        var logo = comp.LogoUrl;
        if (string.IsNullOrWhiteSpace(logo))
        {
            logo = MainViewModel.ResolveCompetitionLogoUrl(comp.Id, comp.Name, null);
            comp.LogoUrl = logo;
        }

        var vm = new FollowedCompetitionItemViewModel
        {
            Id = comp.Id ?? string.Empty,
            LegacyId = null,
            Name = comp.Name ?? string.Empty,
            Code = comp.Code ?? string.Empty,
            Country = comp.Country ?? string.Empty,
            LogoUrl = logo,
            IsFollowed = isFollowed
        };
        _ = vm.EnsureLogoCachedAsync();
        return vm;
    }

    public static FollowedCompetitionItemViewModel FromCatalogDto(FootballTray.Core.Dto.V2CatalogCompetitionDto dto, bool isFollowed)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var logo = dto.LogoUrl ?? dto.Logo;
        if (string.IsNullOrWhiteSpace(logo))
        {
            logo = MainViewModel.ResolveCompetitionLogoUrl(dto.Id, dto.Name, null);
        }

        var vm = new FollowedCompetitionItemViewModel
        {
            Id = dto.Id ?? string.Empty,
            LegacyId = dto.LegacyId,
            Name = dto.Name ?? string.Empty,
            Code = dto.Code ?? string.Empty,
            Country = dto.Country ?? string.Empty,
            LogoUrl = logo,
            IsFollowed = isFollowed
        };
        _ = vm.EnsureLogoCachedAsync();
        return vm;
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
