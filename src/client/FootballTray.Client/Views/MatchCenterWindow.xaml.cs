using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using FootballTray.Client.Services;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Views;

public sealed partial class MatchCenterWindow : Window
{
    public MatchCenterViewModel ViewModel { get; } = new();

    public ObservableCollection<MatchTimelineItem> TimelineItems => ViewModel.TimelineItems;

    public MatchCenterWindow()
    {
        InitializeComponent();
        RootGrid.DataContext = ViewModel;
        EventsList.ItemsSource = ViewModel.TimelineItems;

        try
        {
            AppWindow?.Resize(new Windows.Graphics.SizeInt32(960, 720));
        }
        catch
        {
            // Ignore resize failure on unsupported platforms/runtimes
        }

        Closed += OnWindowClosed;
        SetupSplitterCursor();
    }

    public void Populate(MatchDetailDto match)
    {
        ViewModel.Load(match);

        Title = ViewModel.Title;

        // Sync header text
        CompetitionText.Text = ViewModel.CompetitionName;
        StatusText.Text = ViewModel.StatusText;
        HomeTeamName.Text = ViewModel.HomeTeamName;
        AwayTeamName.Text = ViewModel.AwayTeamName;
        ScoreText.Text = ViewModel.ScoreText;

        // Explicitly refresh incident and timeline collections on UI
        HomeIncidentsList.ItemsSource = ViewModel.HomeIncidents;
        AwayIncidentsList.ItemsSource = ViewModel.AwayIncidents;
        EventsList.ItemsSource = ViewModel.TimelineItems;

        if (ViewModel.HasHalfTime)
        {
            HalfTimeText.Text = ViewModel.HalfTimeText;
            HalfTimeText.Visibility = Visibility.Visible;
        }
        else
        {
            HalfTimeText.Visibility = Visibility.Collapsed;
        }

        // Sync aggregate score badge
        if (ViewModel.HasAggregateScore)
        {
            AggregateText.Text = ViewModel.AggregateText;
            AggregateBadge.Visibility = Visibility.Visible;
        }
        else
        {
            AggregateBadge.Visibility = Visibility.Collapsed;
        }

        // Empty states toggles
        NoEventsText.Visibility = ViewModel.TimelineItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Delegate panel state and empty states to extracted UserControls
        StatisticsControl?.Update(ViewModel);
        LineupControl?.Update(ViewModel);

        UpdateStarAndPinVisuals();

        // Asynchronously load team crests on background thread without blocking UI
        _ = LoadLogosAsync(match);
    }

    private async void OnToggleHomeStarClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleFollowHomeTeamAsync();
        UpdateStarAndPinVisuals();
    }

    private async void OnToggleAwayStarClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleFollowAwayTeamAsync();
        UpdateStarAndPinVisuals();
    }

    private async void OnToggleCompetitionStarClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleFollowCompetitionAsync();
        UpdateStarAndPinVisuals();
    }

    private void OnTogglePinClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePin();
        UpdateStarAndPinVisuals();
    }

    private void UpdateStarAndPinVisuals()
    {
        if (CompetitionStarIcon != null)
        {
            CompetitionStarIcon.Glyph = ViewModel.CompetitionStarGlyph;
            CompetitionStarIcon.Opacity = ViewModel.CompetitionStarOpacity;
        }
        if (HomeStarIcon != null)
        {
            HomeStarIcon.Glyph = ViewModel.HomeStarGlyph;
            HomeStarIcon.Opacity = ViewModel.HomeStarOpacity;
        }
        if (AwayStarIcon != null)
        {
            AwayStarIcon.Glyph = ViewModel.AwayStarGlyph;
            AwayStarIcon.Opacity = ViewModel.AwayStarOpacity;
        }
        if (PinIcon != null)
        {
            PinIcon.Glyph = ViewModel.PinGlyph;
            PinIcon.Opacity = ViewModel.PinGlyphOpacity;
        }
    }

    private async Task LoadLogosAsync(MatchDetailDto match)
    {
        string? homeLogoUrl = match.HomeTeam?.LogoUrl;
        string? awayLogoUrl = match.AwayTeam?.LogoUrl;
        string matchId = match.Id;

        Uri? homeUri = null;
        Uri? awayUri = null;

        try
        {
            if (!string.IsNullOrEmpty(homeLogoUrl))
            {
                homeUri = await ImageCacheService.Instance.GetCachedImageUriAsync(homeLogoUrl, returnFallbackOnFailure: false).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(awayLogoUrl))
            {
                awayUri = await ImageCacheService.Instance.GetCachedImageUriAsync(awayLogoUrl, returnFallbackOnFailure: false).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort
        }

        // WinUI 3 BitmapImage MUST strictly be instantiated on the UI thread inside DispatcherQueue
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ViewModel.MatchId != matchId) return;

            if (homeUri != null && HomeTeamLogo != null)
            {
                HomeTeamLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(homeUri);
                HomeTeamLogo.Visibility = Visibility.Visible;
            }
            else if (HomeTeamLogo != null)
            {
                HomeTeamLogo.Visibility = Visibility.Collapsed;
            }

            if (awayUri != null && AwayTeamLogo != null)
            {
                AwayTeamLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(awayUri);
                AwayTeamLogo.Visibility = Visibility.Visible;
            }
            else if (AwayTeamLogo != null)
            {
                AwayTeamLogo.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (StatisticsControl == null || LineupControl == null) return;

        if (StatsTabRadio?.IsChecked == true)
        {
            StatisticsControl.Visibility = Visibility.Visible;
            LineupControl.Visibility = Visibility.Collapsed;
        }
        else if (LineupsTabRadio?.IsChecked == true)
        {
            StatisticsControl.Visibility = Visibility.Collapsed;
            LineupControl.Visibility = Visibility.Visible;
            LineupControl.Update(ViewModel);
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;

        if (HomeTeamLogo != null)
        {
            HomeTeamLogo.Source = null;
        }
        if (AwayTeamLogo != null)
        {
            AwayTeamLogo.Source = null;
        }

        if (EventsList != null)
        {
            EventsList.ItemsSource = null;
        }
        if (RootGrid != null)
        {
            RootGrid.DataContext = null;
        }

        TimelineItems.Clear();
        ViewModel.HomeIncidents.Clear();
        ViewModel.AwayIncidents.Clear();
        ViewModel.PitchPlayers.Clear();
        ViewModel.HomeLineup.StartingXI.Clear();
        ViewModel.HomeLineup.Substitutes.Clear();
        ViewModel.AwayLineup.StartingXI.Clear();
        ViewModel.AwayLineup.Substitutes.Clear();
        ViewModel.MatchStats.Clear();
    }

    private bool _isDraggingSplitter;
    private double _dragStartPointerX;
    private double _dragStartLeftColumnWidth;

    private void SetupSplitterCursor()
    {
        try
        {
            var cursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
            typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.SetValue(TimelineSplitter, cursor);
        }
        catch
        {
            // Defensive fallback for runtimes without InputSystemCursor support
        }
    }

    private void OnSplitterPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter)
        {
            SplitterGrip.Background = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? SplitterGrip.Background;
            SplitterLine.Background = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? SplitterLine.Background;
        }
    }

    private void OnSplitterPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter)
        {
            SplitterGrip.Background = Application.Current.Resources["TextFillColorTertiaryBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? SplitterGrip.Background;
            SplitterLine.Background = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? SplitterLine.Background;
        }
    }

    private void OnSplitterPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isDraggingSplitter = true;
        TimelineSplitter.CapturePointer(e.Pointer);

        var pt = e.GetCurrentPoint(MainContentGrid);
        _dragStartPointerX = pt.Position.X;
        _dragStartLeftColumnWidth = LeftColumnDefinition.ActualWidth;

        SplitterGrip.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush
            ?? SplitterGrip.Background;
        SplitterLine.Background = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush
            ?? SplitterLine.Background;

        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter) return;

        var pt = e.GetCurrentPoint(MainContentGrid);
        double deltaX = pt.Position.X - _dragStartPointerX;
        double proposedWidth = _dragStartLeftColumnWidth + deltaX;

        double minLeft = LeftColumnDefinition.MinWidth;
        double minRight = RightColumnDefinition.MinWidth;
        double totalAvailable = MainContentGrid.ActualWidth - 16; // 16px is splitter column width

        if (totalAvailable > minLeft + minRight)
        {
            double maxLeft = totalAvailable - minRight;
            proposedWidth = Math.Clamp(proposedWidth, minLeft, maxLeft);
        }
        else
        {
            proposedWidth = Math.Max(minLeft, proposedWidth);
        }

        LeftColumnDefinition.Width = new GridLength(proposedWidth, GridUnitType.Pixel);
        e.Handled = true;
    }

    private void OnSplitterPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isDraggingSplitter)
        {
            _isDraggingSplitter = false;
            TimelineSplitter.ReleasePointerCapture(e.Pointer);

            SplitterGrip.Background = Application.Current.Resources["TextFillColorTertiaryBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? SplitterGrip.Background;
            SplitterLine.Background = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? SplitterLine.Background;

            e.Handled = true;
        }
    }

    private void OnSplitterPointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        OnSplitterPointerReleased(sender, e);
    }
}
