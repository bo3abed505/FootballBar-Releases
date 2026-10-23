using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FootballTray.Client.ViewModels;

/// <summary>
/// Presentation model representing an selectable competition chip in the Standings &amp; Leaders Hub header.
/// </summary>
public sealed class HubCompetitionItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Country { get; init; }
    public string? LogoUrl { get; init; }
    public bool IsFollowed { get; init; }

    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoUrl);
    public Microsoft.UI.Xaml.Visibility LogoVisibility => HasLogo ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility FallbackVisibility => HasLogo ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Media.ImageSource? LogoSource
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LogoUrl)) return null;
            if (Uri.TryCreate(LogoUrl, UriKind.Absolute, out var uri))
            {
                return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
            }
            return null;
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BackgroundBrush));
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(ForegroundBrush));
                OnPropertyChanged(nameof(BackgroundHex));
                OnPropertyChanged(nameof(BorderBrushHex));
                OnPropertyChanged(nameof(TextForegroundHex));
            }
        }
    }

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedBg = new(Windows.UI.Color.FromArgb(38, 56, 189, 248));   // #2638BDF8
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NormalBg = new(Windows.UI.Color.FromArgb(20, 255, 255, 255));     // #14FFFFFF
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedBorder = new(Windows.UI.Color.FromArgb(255, 56, 189, 248)); // #38BDF8
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NormalBorder = new(Windows.UI.Color.FromArgb(38, 255, 255, 255));   // #26FFFFFF
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SelectedFg = new(Windows.UI.Color.FromArgb(255, 56, 189, 248));     // #38BDF8
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush NormalFg = new(Windows.UI.Color.FromArgb(255, 226, 232, 240));     // #E2E8F0

    public Microsoft.UI.Xaml.Media.SolidColorBrush BackgroundBrush => _isSelected ? SelectedBg : NormalBg;
    public Microsoft.UI.Xaml.Media.SolidColorBrush BorderBrush => _isSelected ? SelectedBorder : NormalBorder;
    public Microsoft.UI.Xaml.Media.SolidColorBrush ForegroundBrush => _isSelected ? SelectedFg : NormalFg;

    public string BackgroundHex => _isSelected ? "#2638BDF8" : "#14FFFFFF";
    public string BorderBrushHex => _isSelected ? "#38BDF8" : "#26FFFFFF";
    public string TextForegroundHex => _isSelected ? "#38BDF8" : "#E2E8F0";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
