using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FootballTray.Core.Dto;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Views;

namespace FootballTray.Client.Taskbar;

public sealed partial class TaskbarTicker : UserControl
{
    private string? _currentDisplayedMatchId;
    private TrayMatchDto? _currentMatch;
    private bool _isPinned;
    private bool _isManualPositionMode = false;
    private bool _isDragging = false;
    private int _dragStartScreenX = 0;
    private int _initialOffsetX = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public event EventHandler<string?>? PinToggled;
    public event EventHandler<string>? MatchCenterRequested;

    public TaskbarTicker()
    {
        InitializeComponent();
        BuildDisplayMenu();
        if (TickerContextMenu != null)
        {
            TickerContextMenu.Opening += (s, e) =>
            {
                UpdateDisplayMenuState();
                if (MenuMuteToggle != null)
                {
                    var currentMode = App.CurrentMainViewModel?.PreferencesService?.Current.SoundMode ?? NotificationSoundMode.Custom;
                    MenuMuteToggle.IsChecked = (currentMode == NotificationSoundMode.Off);
                }
            };
        }
    }

    /// <summary>
    /// Updates the ticker text and click identity atomically on the UI thread.
    /// </summary>
    public void UpdateDisplay(string? fixtureId, string formattedText)
    {
        UpdateDisplay(fixtureId, formattedText, null, null, null, false);
    }

    /// <summary>
    /// Updates the ticker text, click identity, and home/away team logos asynchronously without blocking the UI thread.
    /// </summary>
    public void UpdateDisplay(string? fixtureId, string formattedText, string? homeLogoUrl, string? awayLogoUrl)
    {
        UpdateDisplay(fixtureId, formattedText, homeLogoUrl, awayLogoUrl, _currentMatch, _isPinned);
    }

    /// <summary>
    /// Updates ticker explicit elements, click identity, crests, flyout details, and pin state asynchronously without blocking UI thread.
    /// Populates [HomeCrest] [HomeTeamName] [HomeRedCard] [Score] [AwayRedCard] [AwayTeamName] [AwayCrest] [StatusText] directly from _currentMatch.
    /// </summary>
    public void UpdateDisplay(
        string? fixtureId,
        string formattedText,
        string? homeLogoUrl,
        string? awayLogoUrl,
        TrayMatchDto? match,
        bool isPinned)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _currentDisplayedMatchId = fixtureId;
            _currentMatch = match;
            _isPinned = isPinned;

            if (match != null)
            {
                if (FallbackText != null) FallbackText.Visibility = Visibility.Collapsed;

                if (HomeTeamName != null)
                {
                    HomeTeamName.Text = !string.IsNullOrWhiteSpace(match.HomeTeam?.ShortName)
                        ? match.HomeTeam.ShortName
                        : (!string.IsNullOrWhiteSpace(match.HomeTeam?.Name) ? match.HomeTeam.Name : "Home");
                    HomeTeamName.Visibility = Visibility.Visible;
                }

                int homeReds = 0;
                int awayReds = 0;
                if (match.RecentEvents != null)
                {
                    foreach (var ev in match.RecentEvents)
                    {
                        if (string.Equals(ev.Type, "RedCard", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(ev.Type, "Red Card", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(match.HomeTeam?.Id) &&
                                string.Equals(ev.TeamId, match.HomeTeam.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                homeReds++;
                            }
                            else if (!string.IsNullOrEmpty(match.AwayTeam?.Id) &&
                                     string.Equals(ev.TeamId, match.AwayTeam.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                awayReds++;
                            }
                        }
                    }
                }

                if (HomeRedCard != null)
                {
                    HomeRedCard.Visibility = homeReds > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (HomeRedCardText != null)
                    {
                        HomeRedCardText.Text = homeReds > 1 ? homeReds.ToString() : "";
                    }
                }

                if (Score != null)
                {
                    if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        Score.Text = "vs";
                    }
                    else
                    {
                        Score.Text = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";
                    }
                    Score.Visibility = Visibility.Visible;
                }

                if (AwayRedCard != null)
                {
                    AwayRedCard.Visibility = awayReds > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (AwayRedCardText != null)
                    {
                        AwayRedCardText.Text = awayReds > 1 ? awayReds.ToString() : "";
                    }
                }

                if (AwayTeamName != null)
                {
                    AwayTeamName.Text = !string.IsNullOrWhiteSpace(match.AwayTeam?.ShortName)
                        ? match.AwayTeam.ShortName
                        : (!string.IsNullOrWhiteSpace(match.AwayTeam?.Name) ? match.AwayTeam.Name : "Away");
                    AwayTeamName.Visibility = Visibility.Visible;
                }

                if (StatusText != null)
                {
                    string status;
                    if (string.Equals(match.State, "LIVE", StringComparison.OrdinalIgnoreCase))
                    {
                        status = match.ExtraMinute > 0
                            ? $"{match.ElapsedMinute}+{match.ExtraMinute}'"
                            : $"{match.ElapsedMinute}'";
                    }
                    else if (string.Equals(match.State, "HALFTIME", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "HT";
                    }
                    else if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        var use24h = App.CurrentMainViewModel?.PreferencesService?.Current.Use24HourFormat ?? true;
                        status = match.KickoffTime.ToLocalTime().ToString(use24h ? "HH:mm" : "h:mm tt");
                    }
                    else if (string.Equals(match.State, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(match.State, "POSTMATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "FT";
                    }
                    else
                    {
                        status = match.State ?? "";
                    }

                    StatusText.Text = status;
                    StatusText.Visibility = !string.IsNullOrWhiteSpace(status) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            else
            {
                if (HomeCrest != null) HomeCrest.Visibility = Visibility.Collapsed;
                if (HomeTeamName != null) HomeTeamName.Visibility = Visibility.Collapsed;
                if (HomeRedCard != null) HomeRedCard.Visibility = Visibility.Collapsed;
                if (Score != null) Score.Visibility = Visibility.Collapsed;
                if (AwayRedCard != null) AwayRedCard.Visibility = Visibility.Collapsed;
                if (AwayTeamName != null) AwayTeamName.Visibility = Visibility.Collapsed;
                if (AwayCrest != null) AwayCrest.Visibility = Visibility.Collapsed;
                if (StatusText != null) StatusText.Visibility = Visibility.Collapsed;

                if (FallbackText != null)
                {
                    string cleanText = formattedText;
                    if (cleanText.StartsWith("⚽ ", StringComparison.Ordinal))
                    {
                        cleanText = cleanText["⚽ ".Length..];
                    }
                    else if (cleanText.StartsWith("⚽", StringComparison.Ordinal))
                    {
                        cleanText = cleanText["⚽".Length..].TrimStart();
                    }
                    FallbackText.Text = !string.IsNullOrWhiteSpace(cleanText) ? cleanText : "FootballTray";
                    FallbackText.Visibility = Visibility.Visible;
                }
            }

            // Update Context Menu pin state text
            if (MenuTogglePin != null)
            {
                MenuTogglePin.Text = isPinned ? "Unpin Match" : "Toggle Pin";
            }

            if (MenuMuteToggle != null)
            {
                var soundMode = App.CurrentMainViewModel?.PreferencesService?.Current.SoundMode;
                MenuMuteToggle.IsChecked = (soundMode == NotificationSoundMode.Off);
            }

            // Update Hover Flyout (ToolTip) contents
            UpdateFlyoutContent(match, formattedText, isPinned);

            // Asynchronously retrieve crests on a background thread without blocking the UI
            _ = LoadLogosAsync(fixtureId, homeLogoUrl, awayLogoUrl);
        });
    }

    private void UpdateFlyoutContent(TrayMatchDto? match, string fallbackText, bool isPinned)
    {
        if (FlyoutCompetitionText == null || FlyoutStatusText == null ||
            FlyoutHomeTeamText == null || FlyoutAwayTeamText == null ||
            FlyoutScoreText == null || FlyoutEventsPanel == null ||
            FlyoutGoalscorersText == null || FlyoutPinHintText == null)
        {
            return;
        }

        if (match != null)
        {
            FlyoutCompetitionText.Text = !string.IsNullOrWhiteSpace(match.Competition?.Name)
                ? match.Competition.Name
                : "FootballTray";

            // Status and timing
            if (string.Equals(match.State, "LIVE", StringComparison.OrdinalIgnoreCase))
            {
                FlyoutStatusText.Text = match.ExtraMinute > 0
                    ? $"LIVE {match.ElapsedMinute}+{match.ExtraMinute}'"
                    : $"LIVE {match.ElapsedMinute}'";
            }
            else if (string.Equals(match.State, "HALFTIME", StringComparison.OrdinalIgnoreCase))
            {
                FlyoutStatusText.Text = "HT";
            }
            else if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
            {
                var use24h = App.CurrentMainViewModel?.PreferencesService?.Current.Use24HourFormat ?? true;
                FlyoutStatusText.Text = match.KickoffTime.ToLocalTime().ToString(use24h ? "HH:mm" : "h:mm tt");
            }
            else if (string.Equals(match.State, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(match.State, "POSTMATCH", StringComparison.OrdinalIgnoreCase))
            {
                FlyoutStatusText.Text = "FT";
            }
            else
            {
                FlyoutStatusText.Text = match.State;
            }

            var peekData = PeekViewFormatter.Format(match);

            // Red card indicators in score row (e.g. Egypt 🟥 1 - 0)
            FlyoutHomeTeamText.Text = peekData.HomeScoreDisplay;
            FlyoutAwayTeamText.Text = peekData.AwayScoreDisplay;
            FlyoutScoreText.Text = peekData.ScoreDisplay;

            // Team-divided Incidents (Multi-column Grid, excluding missed penalties)
            if (peekData.HasEvents)
            {
                FlyoutEventsPanel.Visibility = Visibility.Visible;
                if (FlyoutHomeEventsHeader != null) FlyoutHomeEventsHeader.Text = peekData.HomeHeader;
                if (FlyoutHomeEventsText != null) FlyoutHomeEventsText.Text = peekData.HomeEventsText;
                if (FlyoutAwayEventsHeader != null) FlyoutAwayEventsHeader.Text = peekData.AwayHeader;
                if (FlyoutAwayEventsText != null) FlyoutAwayEventsText.Text = peekData.AwayEventsText;
                if (FlyoutGoalscorersText != null) FlyoutGoalscorersText.Text = peekData.FormattedToolTipText;
            }
            else
            {
                FlyoutEventsPanel.Visibility = Visibility.Collapsed;
            }

            FlyoutPinHintText.Text = isPinned
                ? "📌 Match is pinned (right-click to unpin)"
                : "Right-click to pin match or open details";
        }
        else
        {
            FlyoutCompetitionText.Text = "FootballTray";
            FlyoutStatusText.Text = "";
            FlyoutHomeTeamText.Text = "";
            FlyoutScoreText.Text = fallbackText;
            FlyoutAwayTeamText.Text = "";
            FlyoutEventsPanel.Visibility = Visibility.Collapsed;
            FlyoutPinHintText.Text = "";
        }
    }

    private async Task LoadLogosAsync(string? fixtureId, string? homeLogoUrl, string? awayLogoUrl)
    {
        if (_currentMatch == null || (string.IsNullOrEmpty(homeLogoUrl) && string.IsNullOrEmpty(awayLogoUrl)))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (HomeCrest != null) HomeCrest.Visibility = Visibility.Collapsed;
                if (AwayCrest != null) AwayCrest.Visibility = Visibility.Collapsed;
            });
            return;
        }

        Uri? homeUri = null;
        Uri? awayUri = null;

        try
        {
            homeUri = await ImageCacheService.Instance.GetCachedImageUriAsync(homeLogoUrl, returnFallbackOnFailure: true).ConfigureAwait(false);
            awayUri = await ImageCacheService.Instance.GetCachedImageUriAsync(awayLogoUrl, returnFallbackOnFailure: true).ConfigureAwait(false);
        }
        catch
        {
            // Best effort
            homeUri ??= ImageCacheService.DefaultFallbackShieldUri;
            awayUri ??= ImageCacheService.DefaultFallbackShieldUri;
        }

        // WinUI 3 BitmapImage MUST strictly be instantiated on the UI thread inside DispatcherQueue
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_currentDisplayedMatchId != fixtureId || _currentMatch == null)
            {
                // Discard stale logos if ticker rotated to another match during download or if no match
                return;
            }

            if (homeUri != null && HomeCrest != null)
            {
                HomeCrest.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(homeUri);
                HomeCrest.Visibility = Visibility.Visible;
            }
            else if (HomeCrest != null)
            {
                HomeCrest.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(ImageCacheService.DefaultFallbackShieldUri);
                HomeCrest.Visibility = Visibility.Visible;
            }

            if (awayUri != null && AwayCrest != null)
            {
                AwayCrest.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(awayUri);
                AwayCrest.Visibility = Visibility.Visible;
            }
            else if (AwayCrest != null)
            {
                AwayCrest.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(ImageCacheService.DefaultFallbackShieldUri);
                AwayCrest.Visibility = Visibility.Visible;
            }
        });
    }

    /// <summary>
    /// Updates the ticker text directly on the UI thread without altering match click identity.
    /// </summary>
    public void UpdateText(string text)
    {
        UpdateDisplay(_currentDisplayedMatchId, text);
    }

    private void RootBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        RootBorder.Opacity = 0.85;
    }

    private void RootBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        RootBorder.Opacity = 1.0;
    }

    public static readonly DependencyProperty ShowDurationProperty =
        DependencyProperty.RegisterAttached(
            "ShowDuration",
            typeof(int),
            typeof(TaskbarTicker),
            new PropertyMetadata(30000));

    public static void SetShowDuration(DependencyObject element, int value) =>
        element.SetValue(ShowDurationProperty, value);

    public static int GetShowDuration(DependencyObject element) =>
        (int)element.GetValue(ShowDurationProperty);

    private void OnOpenMatchCenterClicked(object sender, RoutedEventArgs e)
    {
        OpenMatchCenterForMatch(_currentDisplayedMatchId);
    }

    private void OnTogglePinClicked(object sender, RoutedEventArgs e)
    {
        var matchId = _currentDisplayedMatchId;
        PinToggled?.Invoke(this, matchId);
    }

    private void OnOpenAppClicked(object sender, RoutedEventArgs e)
    {
        App.OpenMainWindow();
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        App.ExitApp();
    }

    private void RootBorder_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        App.OpenMainWindow();
    }

    private async void MenuHideTicker_Click(object sender, RoutedEventArgs e)
    {
        var prefService = App.CurrentMainViewModel?.PreferencesService;
        if (prefService != null)
        {
            await prefService.SetTickerVisibleAsync(false);
        }
        App.CurrentTaskbarHost?.ApplyVisibility();
    }

    private async void MenuResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _isManualPositionMode = false;
        _isDragging = false;
        ProtectedCursor = null;

        var prefService = App.CurrentMainViewModel?.PreferencesService;
        if (prefService != null)
        {
            await prefService.SetTickerOffsetXAsync(0);
        }
        App.CurrentTaskbarHost?.UpdatePosition();
    }

    private void MenuManualPosition_Click(object sender, RoutedEventArgs e)
    {
        _isManualPositionMode = true;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
    }

    private void RootBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isManualPositionMode)
        {
            _isDragging = true;
            RootBorder?.CapturePointer(e.Pointer);
            if (GetCursorPos(out POINT pt))
            {
                _dragStartScreenX = pt.X;
            }
            _initialOffsetX = App.CurrentMainViewModel?.PreferencesService?.Current.TickerOffsetX ?? 0;
            e.Handled = true;
        }
    }

    private void RootBorder_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && GetCursorPos(out POINT pt))
        {
            int deltaX = pt.X - _dragStartScreenX;
            int newOffset = _initialOffsetX - deltaX;
            App.CurrentTaskbarHost?.UpdatePosition(newOffset);
            e.Handled = true;
        }
    }

    private async void RootBorder_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            RootBorder?.ReleasePointerCapture(e.Pointer);
            int finalOffset = App.CurrentTaskbarHost?.LastAppliedOffsetX ?? 0;

            var prefService = App.CurrentMainViewModel?.PreferencesService;
            if (prefService != null)
            {
                await prefService.SetTickerOffsetXAsync(finalOffset);
            }

            _isDragging = false;
            _isManualPositionMode = false;
            ProtectedCursor = null;
            e.Handled = true;
        }
    }

    private void BuildDisplayMenu()
    {
        if (TickerContextMenu == null || MenuDisplaySeparator == null) return;

        int insertIndex = TickerContextMenu.Items.IndexOf(MenuDisplaySeparator);
        if (insertIndex < 0) return;

        insertIndex++;

        var primaryItem = new ToggleMenuFlyoutItem
        {
            Text = "Primary Display",
            Tag = null
        };
        primaryItem.Click += async (s, e) =>
        {
            var prefService = App.CurrentMainViewModel?.PreferencesService;
            if (prefService != null)
            {
                await prefService.SetTargetScreenDeviceNameAsync(null);
                await prefService.SetTickerOffsetXAsync(0);
            }
            App.RestartTaskbarHost();
        };
        TickerContextMenu.Items.Insert(insertIndex++, primaryItem);

        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string deviceName = screen.DeviceName;
                string label = screen.Primary
                    ? $"{deviceName} (Primary)"
                    : $"{deviceName} (Display {i + 1})";

                var screenItem = new ToggleMenuFlyoutItem
                {
                    Text = label,
                    Tag = deviceName
                };
                screenItem.Click += async (s, e) =>
                {
                    var prefService = App.CurrentMainViewModel?.PreferencesService;
                    if (prefService != null)
                    {
                        await prefService.SetTargetScreenDeviceNameAsync(deviceName);
                        await prefService.SetTickerOffsetXAsync(0);
                    }
                    App.RestartTaskbarHost();
                };
                TickerContextMenu.Items.Insert(insertIndex++, screenItem);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TICKER] Failed to enumerate screens: {ex.Message}");
        }
    }

    private void UpdateDisplayMenuState()
    {
        if (TickerContextMenu == null) return;

        var currentTarget = App.CurrentMainViewModel?.PreferencesService?.Current.TargetScreenDeviceName;

        foreach (var item in TickerContextMenu.Items)
        {
            if (item is ToggleMenuFlyoutItem toggleItem && toggleItem != MenuMuteToggle)
            {
                string? tag = toggleItem.Tag as string;
                if (string.IsNullOrEmpty(tag))
                {
                    toggleItem.IsChecked = string.IsNullOrEmpty(currentTarget);
                }
                else
                {
                    toggleItem.IsChecked = !string.IsNullOrEmpty(currentTarget) &&
                                           string.Equals(currentTarget, tag, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    private async void MenuMuteToggle_Click(object sender, RoutedEventArgs e)
    {
        var prefs = App.CurrentMainViewModel?.PreferencesService;
        if (prefs == null) return;

        var currentMode = prefs.Current.SoundMode;
        var newMode = (currentMode == NotificationSoundMode.Off)
            ? NotificationSoundMode.Custom
            : NotificationSoundMode.Off;

        await prefs.SetSoundModeAsync(newMode);
        if (MenuMuteToggle != null)
        {
            MenuMuteToggle.IsChecked = (newMode == NotificationSoundMode.Off);
        }
    }

    private void RootBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var matchId = _currentDisplayedMatchId;
        OpenMatchCenterForMatch(matchId);
    }

    public void OpenMatchCenterForMatch(string? matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return;
        }

        MatchCenterRequested?.Invoke(this, matchId);
    }

    private void OpenMatchCenter()
    {
        var matchId = _currentDisplayedMatchId;
        OpenMatchCenterForMatch(matchId);
    }

    private static async Task DirectFetchMatchDetailsFallbackAsync(string matchId)
    {
        using var apiClient = new FootballApiClient();
        await apiClient.GetMatchDetailsAsync(matchId);
    }
}
