using System.IO;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using FootballTray.Core.Dto;
using FootballTray.Client.Diagnostics;
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
    private DispatcherTimer? _clickResolutionTimer;
    private string? _pendingClickMatchId;
    private bool _isUpdatingMenu;
    private readonly SemaphoreSlim _menuActionLock = new(1, 1);

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
        EnsureFallbackLogoLoaded();
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
                UpdatePinAndAutoRotateMenuState();
            };
        }
    }

    /// <summary>
    /// Explicitly switches taskbar ticker pill to idle/fallback state with logo.
    /// </summary>
    public void ShowFallback(string text = "FootballBar")
    {
        UpdateDisplay(null, text, null, null, null, false);
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
            try
            {
                _currentDisplayedMatchId = fixtureId;
            _currentMatch = match;
            _isPinned = isPinned;

            if (match != null)
            {
                if (FallbackPanel != null) FallbackPanel.Visibility = Visibility.Collapsed;
                if (FallbackText != null) FallbackText.Visibility = Visibility.Collapsed;

                if (HomeTeamName != null)
                {
                    HomeTeamName.Text = FormatTickerTeamName(match.HomeTeam?.ShortName, match.HomeTeam?.Name, "Home");
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
                    AwayTeamName.Text = FormatTickerTeamName(match.AwayTeam?.ShortName, match.AwayTeam?.Name, "Away");
                    AwayTeamName.Visibility = Visibility.Visible;
                }

                if (StatusText != null)
                {
                    string status = FormatTickerStatusText(match);
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

                if (FallbackPanel != null)
                {
                    EnsureFallbackLogoLoaded();
                    if (FallbackLogo != null) FallbackLogo.Visibility = Visibility.Visible;
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
                        FallbackText.Text = !string.IsNullOrWhiteSpace(cleanText) ? cleanText : "FootballBar";
                        FallbackText.Visibility = Visibility.Visible;
                    }
                    FallbackPanel.Visibility = Visibility.Visible;
                }
                else if (FallbackText != null)
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
                    FallbackText.Text = !string.IsNullOrWhiteSpace(cleanText) ? cleanText : "FootballBar";
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
            }
            catch (System.Runtime.InteropServices.COMException comEx) when ((uint)comEx.HResult == 0x8000FFFF)
            {
                // Ignored: window or tooltip closing
            }
            catch (Exception ex)
            {
                Diagnostics.AppLogger.Debug("TaskbarTicker", "UpdateTickerException", ex.Message);
            }
        });
    }

    private void UpdateFlyoutContent(TrayMatchDto? match, string fallbackText, bool isPinned)
    {
        try
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
                    : "FootballBar";

                // Status and timing
                if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(match.State, "NS", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(match.State, "TIMED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(match.State, "SCHEDULED", StringComparison.OrdinalIgnoreCase))
                {
                    var use24h = App.CurrentMainViewModel?.PreferencesService?.Current.Use24HourFormat ?? true;
                    FlyoutStatusText.Text = match.KickoffTime != default
                        ? match.KickoffTime.ToLocalTime().ToString(use24h ? "HH:mm" : "h:mm tt")
                        : "Upcoming";
                }
                else
                {
                    FlyoutStatusText.Text = TrayTickerFormatter.FormatPeriodStatus(match.State, match.ElapsedMinute, match.ExtraMinute);
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

                if ((match.Events == null || match.Events.Count == 0 || match.RecentEvents == null || match.RecentEvents.Count == 0) &&
                    (match.Score?.Home > 0 || match.Score?.Away > 0))
                {
                    _ = EnrichFlyoutEventsAsync(match.Id);
                }

                FlyoutPinHintText.Text = isPinned
                    ? "📌 Match is pinned (right-click to unpin)"
                    : "Right-click to pin match or open details";
            }
            else
            {
                FlyoutCompetitionText.Text = "FootballBar";
                FlyoutStatusText.Text = "";
                FlyoutHomeTeamText.Text = "";
                FlyoutScoreText.Text = fallbackText;
                FlyoutAwayTeamText.Text = "";
                FlyoutEventsPanel.Visibility = Visibility.Collapsed;
                FlyoutPinHintText.Text = "";
            }
        }
        catch (System.Runtime.InteropServices.COMException comEx) when ((uint)comEx.HResult == 0x8000FFFF)
        {
            // Tooltip or window closing
        }
        catch (Exception ex)
        {
            Diagnostics.AppLogger.Debug("TaskbarTicker", "UpdateFlyoutException", ex.Message);
        }
    }

    private async void EnsureFallbackLogoLoaded()
    {
        if (FallbackLogo == null || FallbackLogo.Source != null) return;
        try
        {
            string? logoPath = GetAppLogoFilePath();
            if (!string.IsNullOrEmpty(logoPath) && System.IO.File.Exists(logoPath))
            {
                using var stream = System.IO.File.OpenRead(logoPath);
                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = 28,
                    DecodePixelHeight = 28
                };
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                FallbackLogo.Source = bitmap;
                FallbackLogo.Visibility = Visibility.Visible;
                return;
            }

            var uri = GetAppLogoUri();
            if (uri != null)
            {
                FallbackLogo.Source = new BitmapImage(uri)
                {
                    DecodePixelWidth = 28,
                    DecodePixelHeight = 28
                };
                FallbackLogo.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static string? GetAppLogoFilePath()
    {
        string[] candidates =
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-24_altform-unplated.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "StoreLogo.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.png")
        };

        foreach (var path in candidates)
        {
            if (System.IO.File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static Uri GetAppLogoUri()
    {
        string? path = GetAppLogoFilePath();
        if (!string.IsNullOrEmpty(path))
        {
            return new Uri(path);
        }

        return ImageCacheService.DefaultFallbackShieldUri;
    }

    private async Task EnrichFlyoutEventsAsync(string? matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId) || _currentMatch == null || _currentMatch.Id != matchId)
            return;

        try
        {
            var apiClient = App.CurrentApiClient;
            if (apiClient == null) return;

            var response = await apiClient.GetMatchDetailsAsync(matchId).ConfigureAwait(false);
            var matchDetail = response?.Envelope?.Data;
            if (matchDetail != null && matchDetail.Events != null && matchDetail.Events.Count > 0)
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_currentMatch != null && _currentMatch.Id == matchId)
                    {
                        _currentMatch.Events = matchDetail.Events;
                        _currentMatch.RecentEvents = matchDetail.Events;
                        var peekData = PeekViewFormatter.Format(_currentMatch);
                        if (peekData.HasEvents && FlyoutEventsPanel != null)
                        {
                            FlyoutEventsPanel.Visibility = Visibility.Visible;
                            if (FlyoutHomeEventsHeader != null) FlyoutHomeEventsHeader.Text = peekData.HomeHeader;
                            if (FlyoutHomeEventsText != null) FlyoutHomeEventsText.Text = peekData.HomeEventsText;
                            if (FlyoutAwayEventsHeader != null) FlyoutAwayEventsHeader.Text = peekData.AwayHeader;
                            if (FlyoutAwayEventsText != null) FlyoutAwayEventsText.Text = peekData.AwayEventsText;
                            if (FlyoutGoalscorersText != null) FlyoutGoalscorersText.Text = peekData.FormattedToolTipText;
                        }
                    }
                });
            }
        }
        catch
        {
            // Best effort enrichment
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
                HomeCrest.Source = CreateCrestBitmap(homeUri);
                HomeCrest.Visibility = Visibility.Visible;
            }
            else if (HomeCrest != null)
            {
                HomeCrest.Source = CreateCrestBitmap(ImageCacheService.DefaultFallbackShieldUri);
                HomeCrest.Visibility = Visibility.Visible;
            }

            if (awayUri != null && AwayCrest != null)
            {
                AwayCrest.Source = CreateCrestBitmap(awayUri);
                AwayCrest.Visibility = Visibility.Visible;
            }
            else if (AwayCrest != null)
            {
                AwayCrest.Source = CreateCrestBitmap(ImageCacheService.DefaultFallbackShieldUri);
                AwayCrest.Visibility = Visibility.Visible;
            }
        });
    }

    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage CreateCrestBitmap(Uri uri)
    {
        return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
        {
            DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
            DecodePixelWidth = 36,
            UriSource = uri
        };
    }

    private static string FormatTickerTeamName(string? shortName, string? fullName, string fallback)
    {
        string val = !string.IsNullOrWhiteSpace(shortName) ? shortName.Trim() : (!string.IsNullOrWhiteSpace(fullName) ? fullName.Trim() : fallback);
        if (val.Length > 4)
        {
            return val.Substring(0, 3).ToUpperInvariant();
        }
        return val.ToUpperInvariant();
    }

    private static string FormatTickerStatusText(TrayMatchDto match)
    {
        string state = (match.State ?? "").Trim().ToUpperInvariant();
        if (state is "HALFTIME" or "HT")
        {
            return "HT";
        }
        if (state is "FINISHED" or "FT" or "POSTMATCH")
        {
            return "FT";
        }
        if (state is "FINISHED_AET" or "AET")
        {
            return "AET";
        }
        if (state is "FINISHED_PEN" or "PEN")
        {
            return "PEN";
        }
        if (state is "FIRST_HALF" or "SECOND_HALF" or "EXTRA_TIME" or "LIVE" or "IN_PLAY" or "1H" or "2H" or "ET" or "BT" or "P" or "SUSP" or "INT")
        {
            if (match.ElapsedMinute > 0)
            {
                return match.ExtraMinute > 0
                    ? $"{match.ElapsedMinute}+{match.ExtraMinute}'"
                    : $"{match.ElapsedMinute}'";
            }
            if (state is "FIRST_HALF" or "1H") return "1H";
            if (state is "SECOND_HALF" or "2H") return "2H";
            if (state is "EXTRA_TIME" or "ET") return "ET";
            return "LIVE";
        }
        if (state is "PREMATCH" or "NS" or "TBD")
        {
            var use24h = App.CurrentMainViewModel?.PreferencesService?.Current.Use24HourFormat ?? true;
            return match.KickoffTime.ToLocalTime().ToString(use24h ? "HH:mm" : "h:mm tt");
        }
        return state;
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
        if (_currentMatch != null && (_currentMatch.Events == null || _currentMatch.Events.Count == 0 || _currentMatch.RecentEvents == null || _currentMatch.RecentEvents.Count == 0))
        {
            _ = EnrichFlyoutEventsAsync(_currentMatch.Id);
        }
    }

    private void RootBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        RootBorder.Opacity = 1.0;
    }

    private long _lastWheelTicks;

    private void RootBorder_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootBorder).Properties;
        int delta = props.MouseWheelDelta;
        if (delta == 0) return;

        long now = Environment.TickCount64;
        if (now - _lastWheelTicks < 40)
        {
            e.Handled = true;
            return;
        }
        _lastWheelTicks = now;

        int direction = delta > 0 ? -1 : 1; // Scroll UP = Previous, Scroll DOWN = Next
        var coordinator = App.CurrentTickerCoordinator;
        if (coordinator != null && coordinator.StepByUserScroll(direction))
        {
            e.Handled = true;
        }
    }

    public void OnHostMouseWheel(int delta)
    {
        if (delta == 0) return;
        long now = Environment.TickCount64;
        if (now - _lastWheelTicks < 40) return;
        _lastWheelTicks = now;

        int direction = delta > 0 ? -1 : 1; // Scroll UP = Previous, Scroll DOWN = Next
        App.CurrentTickerCoordinator?.StepByUserScroll(direction);
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

    private void UpdatePinAndAutoRotateMenuState()
    {
        _isUpdatingMenu = true;
        try
        {
            bool hasCurrentMatch = !string.IsNullOrEmpty(_currentDisplayedMatchId);
            bool isPinned = (App.CurrentTickerCoordinator?.IsPinned == true) ||
                            (!string.IsNullOrEmpty(_currentDisplayedMatchId) && string.Equals(App.CurrentMainViewModel?.ManualPinnedMatchId, _currentDisplayedMatchId, StringComparison.Ordinal));

            if (MenuPinMatch != null)
            {
                MenuPinMatch.Visibility = (hasCurrentMatch && !isPinned) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MenuUnpinMatch != null)
            {
                MenuUnpinMatch.Visibility = (hasCurrentMatch && isPinned) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MenuAutoRotateToggle != null)
            {
                var carouselEnabled = App.CurrentMainViewModel?.PreferencesService?.Current.EnableTickerCarousel ?? true;
                MenuAutoRotateToggle.IsChecked = carouselEnabled;
                var scope = App.CurrentMainViewModel?.PreferencesService?.Current.TickerRotationScope ?? "FollowedLive";
                if (MenuModeFollowedLive != null)
                {
                    MenuModeFollowedLive.IsChecked = string.Equals(scope, "FollowedLive", StringComparison.OrdinalIgnoreCase);
                    MenuModeFollowedLive.IsEnabled = carouselEnabled;
                }
                if (MenuModeAllFollowed != null)
                {
                    MenuModeAllFollowed.IsChecked = string.Equals(scope, "AllFollowed", StringComparison.OrdinalIgnoreCase);
                    MenuModeAllFollowed.IsEnabled = carouselEnabled;
                }
                if (MenuModeAllLive != null)
                {
                    MenuModeAllLive.IsChecked = string.Equals(scope, "AllLive", StringComparison.OrdinalIgnoreCase);
                    MenuModeAllLive.IsEnabled = carouselEnabled;
                }
                if (MenuModeCustomQueue != null)
                {
                    MenuModeCustomQueue.IsChecked = string.Equals(scope, "CustomQueue", StringComparison.OrdinalIgnoreCase);
                    MenuModeCustomQueue.IsEnabled = carouselEnabled;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("TaskbarTicker", "UpdatePinAndAutoRotateMenuStateFailed", "Failed updating pin and auto-rotate menu state", ex: ex);
        }
        finally
        {
            _isUpdatingMenu = false;
        }
    }

    private void OnPinMatchClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_currentDisplayedMatchId))
            {
                App.CurrentMainViewModel?.PinMatch(_currentDisplayedMatchId);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("TaskbarTicker", "PinMatchFailed", "Failed pinning match from menu", ex: ex);
        }
    }

    private async void OnUnpinMatchClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var prefs = App.CurrentMainViewModel?.PreferencesService?.Current;
            if (string.Equals(prefs?.TickerRotationScope, "CustomQueue", StringComparison.OrdinalIgnoreCase))
            {
                var q = (prefs?.CustomTickerQueueMatchIds ?? new List<string>()).ToList();
                if (!string.IsNullOrEmpty(_currentDisplayedMatchId) && q.Any(id => string.Equals(id, _currentDisplayedMatchId, StringComparison.OrdinalIgnoreCase)))
                {
                    q.RemoveAll(id => string.Equals(id, _currentDisplayedMatchId, StringComparison.OrdinalIgnoreCase));
                    if (App.CurrentMainViewModel?.PreferencesService != null)
                    {
                        await App.CurrentMainViewModel.PreferencesService.SetCustomTickerQueueMatchIdsAsync(q);
                    }
                    return;
                }
            }
            App.CurrentMainViewModel?.UnpinMatch(_currentDisplayedMatchId);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TaskbarTicker", "UnpinMatchFailed", "Failed unpinning match from menu", ex: ex);
        }
    }

    private void DummyUnpinOld()
    {
        try
        {
            App.CurrentMainViewModel?.UnpinMatch(_currentDisplayedMatchId);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TaskbarTicker", "UnpinMatchFailed", "Failed unpinning match from menu", ex: ex);
        }
    }

    private void MenuAutoRotateToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMenu) return;
        DispatcherQueue?.TryEnqueue(async () =>
        {
            await _menuActionLock.WaitAsync();
            try
            {
                var prefs = App.CurrentMainViewModel?.PreferencesService;
                if (prefs == null) return;
                bool isChecked = MenuAutoRotateToggle?.IsChecked ?? true;
                await prefs.SetEnableTickerCarouselAsync(isChecked);
                if (App.CurrentMainViewModel != null)
                {
                    await App.CurrentMainViewModel.SetEnableTickerCarouselAsync(isChecked);
                }
                else
                {
                    App.CurrentTickerCoordinator?.SetCarouselEnabled(isChecked);
                }
                UpdatePinAndAutoRotateMenuState();
            }
            catch (Exception ex)
            {
                AppLogger.Error("TaskbarTicker", "AutoRotateToggleFailed", "Failed toggling auto-rotate from taskbar menu", ex: ex);
            }
            finally
            {
                _menuActionLock.Release();
            }
        });
    }

    private void OnAutoRotateModeSelected(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMenu) return;
        if (sender is not ToggleMenuFlyoutItem item || item.Tag is not string scope) return;

        DispatcherQueue?.TryEnqueue(async () =>
        {
            await _menuActionLock.WaitAsync();
            try
            {
                var prefs = App.CurrentMainViewModel?.PreferencesService;
                if (prefs != null)
                {
                    await prefs.SetTickerRotationScopeAsync(scope);
                    UpdatePinAndAutoRotateMenuState();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("TaskbarTicker", "AutoRotateModeChangeFailed", "Failed setting auto-rotate mode", ex: ex);
            }
            finally
            {
                _menuActionLock.Release();
            }
        });
    }

    private void OnTogglePinClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var matchId = _currentDisplayedMatchId;
            PinToggled?.Invoke(this, matchId);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TaskbarTicker", "TogglePinFailed", "Failed toggling pin from menu", ex: ex);
        }
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
        if (_clickResolutionTimer != null && _clickResolutionTimer.IsEnabled)
        {
            _clickResolutionTimer.Stop();
        }
        _pendingClickMatchId = null;
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
            if (item is ToggleMenuFlyoutItem toggleItem &&
                toggleItem != MenuMuteToggle &&
                toggleItem != MenuAutoRotateToggle &&
                toggleItem != MenuModeFollowedLive &&
                toggleItem != MenuModeAllFollowed &&
                toggleItem != MenuModeAllLive &&
                toggleItem != MenuModeCustomQueue)
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

        _pendingClickMatchId = _currentDisplayedMatchId;

        if (_clickResolutionTimer == null)
        {
            _clickResolutionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _clickResolutionTimer.Tick += (s, args) =>
            {
                _clickResolutionTimer.Stop();
                var id = _pendingClickMatchId;
                _pendingClickMatchId = null;
                OpenMatchCenterForMatch(id);
            };
        }
        else
        {
            _clickResolutionTimer.Stop();
        }

        _clickResolutionTimer.Start();
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
