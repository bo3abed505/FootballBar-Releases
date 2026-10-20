using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using FootballTray.Client.Services;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Views;

public sealed partial class MatchCenterWindow : Window
{
    public MatchCenterViewModel ViewModel { get; } = new();

    public ObservableCollection<MatchTimelineItem> TimelineItems => ViewModel.TimelineItems;

    private readonly FootballApiClient _apiClient = new();
    private string? _lastEtag;
    private DispatcherTimer? _liveRefreshTimer;
    private bool _isRefreshing;

    public DispatcherTimer? LiveRefreshTimer => _liveRefreshTimer;
    public bool IsLiveRefreshTimerRunning => _liveRefreshTimer?.IsEnabled == true;

    public MatchCenterWindow()
    {
        InitializeComponent();
        RootGrid.DataContext = ViewModel;
        KeyEventsList.ItemsSource = ViewModel.KeyEvents;
        CommentaryList.ItemsSource = ViewModel.CommentaryItems;

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
        UpdateTabStyles();
    }


    public void Populate(MatchDetailDto match)
    {
        if (ViewModel.MatchId != match.Id)
        {
            _lastEtag = null;
        }

        ViewModel.Load(match);

        Title = ViewModel.Title;

        // Sync header text
        CompetitionText.Text = ViewModel.CompetitionName;
        StatusText.Text = ViewModel.StatusText;
        HomeTeamName.Text = ViewModel.HomeTeamName;
        AwayTeamName.Text = ViewModel.AwayTeamName;
        ScoreText.Text = ViewModel.ScoreText;

        // Explicitly refresh incident, key events, and commentary collections on UI if not already bound
        if (HomeIncidentsList.ItemsSource != ViewModel.HomeIncidents)
        {
            HomeIncidentsList.ItemsSource = ViewModel.HomeIncidents;
        }
        if (AwayIncidentsList.ItemsSource != ViewModel.AwayIncidents)
        {
            AwayIncidentsList.ItemsSource = ViewModel.AwayIncidents;
        }
        if (KeyEventsList.ItemsSource != ViewModel.KeyEvents)
        {
            KeyEventsList.ItemsSource = ViewModel.KeyEvents;
        }
        if (CommentaryList.ItemsSource != ViewModel.CommentaryItems)
        {
            CommentaryList.ItemsSource = ViewModel.CommentaryItems;
        }

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
        NoKeyEventsText.Visibility = ViewModel.KeyEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCommentaryText.Visibility = ViewModel.CommentaryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Delegate panel state and empty states to extracted UserControls
        StatisticsControl?.Update(ViewModel);
        LineupControl?.Update(ViewModel);

        UpdateStarAndPinVisuals();
        UpdateTabStyles();

        // Immediate clock and score sync to taskbar ticker
        App.CurrentTickerCoordinator?.UpdateMatchFromDetails(match);

        // Check terminal state upon load and after every refresh to prevent quota drain
        string stateUpper = (match.State ?? string.Empty).Trim().ToUpperInvariant();
        bool isTerminalState = stateUpper is "FT" or "FINISHED" or "AET" or "PEN" or "PST" or "CANC" or "POSTMATCH";
        if (isTerminalState)
        {
            StopLiveRefreshTimer();
        }
        else
        {
            // Start active live refresh timer (~12s) while Match Center window remains open for in-flight matches
            StartLiveRefreshTimer();
        }

        // Asynchronously load team crests on background thread without blocking UI
        _ = LoadLogosAsync(match);
    }

    public void StartLiveRefreshTimer()
    {
        if (_liveRefreshTimer == null)
        {
            _liveRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(12)
            };
            _liveRefreshTimer.Tick += OnLiveRefreshTick;
        }

        if (!_liveRefreshTimer.IsEnabled)
        {
            _liveRefreshTimer.Start();
        }
    }

    public void StopLiveRefreshTimer()
    {
        if (_liveRefreshTimer != null)
        {
            _liveRefreshTimer.Tick -= OnLiveRefreshTick;
            _liveRefreshTimer.Stop();
            _liveRefreshTimer = null;
        }
    }

    private async void OnLiveRefreshTick(object? sender, object e)
    {
        await RefreshMatchDetailsAsync();
    }

    public async Task<bool> RefreshMatchDetailsAsync(FootballApiClient? client = null)
    {
        if (_isRefreshing || string.IsNullOrWhiteSpace(ViewModel.MatchId))
        {
            return false;
        }

        _isRefreshing = true;
        FootballApiClient effectiveClient = client ?? _apiClient;

        try
        {
            var response = await effectiveClient.GetMatchDetailsAsync(ViewModel.MatchId, _lastEtag);
            if (response.IsNotModified)
            {
                if (!string.IsNullOrEmpty(response.ETag))
                {
                    _lastEtag = response.ETag;
                }
                return true;
            }

            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                _lastEtag = response.ETag;
                Populate(response.Envelope.Data);
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MATCH_CENTER] Live refresh tick error: {ex.Message}");
        }
        finally
        {
            _isRefreshing = false;
        }

        return false;
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

    private void OnKeyEventsTabClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsKeyEventsTabSelected = true;
        UpdateTabStyles();
    }

    private void OnCommentaryTabClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsKeyEventsTabSelected = false;
        UpdateTabStyles();
    }

    private static readonly SolidColorBrush ActiveTabBg = new(Windows.UI.Color.FromArgb(255, 30, 41, 59));       // #1E293B
    private static readonly SolidColorBrush ActiveTabBorder = new(Windows.UI.Color.FromArgb(255, 51, 65, 85));    // #334155
    private static readonly SolidColorBrush ActiveTabFg = new(Windows.UI.Color.FromArgb(255, 248, 250, 252));    // #F8FAFC
    private static readonly SolidColorBrush InactiveTabBg = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));           // Transparent
    private static readonly SolidColorBrush InactiveTabBorder = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));       // Transparent
    private static readonly SolidColorBrush InactiveTabFg = new(Windows.UI.Color.FromArgb(255, 148, 163, 184));  // #94A3B8

    private static readonly SolidColorBrush SplitterAccent = new(Windows.UI.Color.FromArgb(255, 56, 189, 248));   // #38BDF8
    private static readonly SolidColorBrush SplitterGripNormal = new(Windows.UI.Color.FromArgb(255, 148, 163, 184)); // #94A3B8
    private static readonly SolidColorBrush SplitterLineNormal = new(Windows.UI.Color.FromArgb(255, 51, 65, 85));    // #334155

    private void UpdateTabStyles()
    {
        try
        {
            if (KeyEventsTabButton == null || CommentaryTabButton == null) return;

            bool isKeyEvents = ViewModel.IsKeyEventsTabSelected;

            if (isKeyEvents)
            {
                KeyEventsTabButton.Background = ActiveTabBg;
                KeyEventsTabButton.BorderBrush = ActiveTabBorder;
                KeyEventsTabButton.BorderThickness = new Thickness(1);
                if (KeyEventsTabText != null) KeyEventsTabText.Foreground = ActiveTabFg;

                CommentaryTabButton.Background = InactiveTabBg;
                CommentaryTabButton.BorderBrush = InactiveTabBorder;
                CommentaryTabButton.BorderThickness = new Thickness(0);
                if (CommentaryTabText != null) CommentaryTabText.Foreground = InactiveTabFg;

                if (KeyEventsPanel != null) KeyEventsPanel.Visibility = Visibility.Visible;
                if (CommentaryPanel != null) CommentaryPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                CommentaryTabButton.Background = ActiveTabBg;
                CommentaryTabButton.BorderBrush = ActiveTabBorder;
                CommentaryTabButton.BorderThickness = new Thickness(1);
                if (CommentaryTabText != null) CommentaryTabText.Foreground = ActiveTabFg;

                KeyEventsTabButton.Background = InactiveTabBg;
                KeyEventsTabButton.BorderBrush = InactiveTabBorder;
                KeyEventsTabButton.BorderThickness = new Thickness(0);
                if (KeyEventsTabText != null) KeyEventsTabText.Foreground = InactiveTabFg;

                if (KeyEventsPanel != null) KeyEventsPanel.Visibility = Visibility.Collapsed;
                if (CommentaryPanel != null) CommentaryPanel.Visibility = Visibility.Visible;
            }

            // Sync empty states
            if (NoKeyEventsText != null)
            {
                NoKeyEventsText.Visibility = isKeyEvents && ViewModel.KeyEvents.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (NoCommentaryText != null)
            {
                NoCommentaryText.Visibility = !isKeyEvents && ViewModel.CommentaryItems.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AppLogger.Error("MatchCenter", "UpdateTabStylesError", $"Error updating tab styles: {ex.Message}", ex: ex);
        }
    }


    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnWindowClosed;
        StopLiveRefreshTimer();
        _apiClient.Dispose();

        if (HomeTeamLogo != null)
        {
            HomeTeamLogo.Source = null;
        }
        if (AwayTeamLogo != null)
        {
            AwayTeamLogo.Source = null;
        }

        if (KeyEventsList != null)
        {
            KeyEventsList.ItemsSource = null;
        }
        if (CommentaryList != null)
        {
            CommentaryList.ItemsSource = null;
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
        if (!_isDraggingSplitter && SplitterGrip != null && SplitterLine != null)
        {
            SplitterGrip.Background = SplitterAccent;
            SplitterLine.Background = SplitterAccent;
        }
    }

    private void OnSplitterPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplitter && SplitterGrip != null && SplitterLine != null)
        {
            SplitterGrip.Background = SplitterGripNormal;
            SplitterLine.Background = SplitterLineNormal;
        }
    }

    private void OnSplitterPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isDraggingSplitter = true;
        TimelineSplitter.CapturePointer(e.Pointer);

        var pt = e.GetCurrentPoint(MainContentGrid);
        _dragStartPointerX = pt.Position.X;
        _dragStartLeftColumnWidth = LeftColumnDefinition.ActualWidth;

        if (SplitterGrip != null && SplitterLine != null)
        {
            SplitterGrip.Background = SplitterAccent;
            SplitterLine.Background = SplitterAccent;
        }

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

            if (SplitterGrip != null && SplitterLine != null)
            {
                SplitterGrip.Background = SplitterGripNormal;
                SplitterLine.Background = SplitterLineNormal;
            }

            e.Handled = true;
        }
    }

    private void OnSplitterPointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        OnSplitterPointerReleased(sender, e);
    }
}
