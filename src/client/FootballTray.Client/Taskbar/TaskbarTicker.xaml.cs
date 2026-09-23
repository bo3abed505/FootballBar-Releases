using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FootballTray.Core.Dto;
using FootballTray.Client.Services;
using FootballTray.Client.Views;

namespace FootballTray.Client.Taskbar;

public sealed partial class TaskbarTicker : UserControl
{
    private string? _currentDisplayedMatchId;
    private TrayMatchDto? _currentMatch;
    private bool _isPinned;
    private MatchCenterWindow? _matchCenterWindow;

    public event EventHandler<string?>? PinToggled;

    public TaskbarTicker()
    {
        InitializeComponent();
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
    /// Updates ticker text, click identity, team logos, flyout details, and pin state asynchronously without blocking UI thread.
    /// </summary>
    public void UpdateDisplay(
        string? fixtureId,
        string formattedText,
        string? homeLogoUrl,
        string? awayLogoUrl,
        TrayMatchDto? match,
        bool isPinned)
    {
        void ApplyState()
        {
            _currentDisplayedMatchId = fixtureId;
            _currentMatch = match;
            _isPinned = isPinned;
            TickerText.Text = formattedText;

            // Update Pin toggle button appearance
            if (PinButton != null)
            {
                PinButton.Visibility = string.IsNullOrEmpty(fixtureId) ? Visibility.Collapsed : Visibility.Visible;
                if (PinIcon != null)
                {
                    PinIcon.Glyph = isPinned ? "\uE840" : "\uE718";
                    if (Application.Current?.Resources != null)
                    {
                        PinIcon.Foreground = isPinned
                            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                            : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                    }
                }
                ToolTipService.SetToolTip(PinButton, isPinned ? "Unpin match to resume rotation" : "Pin match to pause rotation");
            }

            // Update Hover Flyout (ToolTip) contents
            UpdateFlyoutContent(match, formattedText, isPinned);
        }

        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            ApplyState();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyState);
        }

        // Asynchronously retrieve logos on a background thread without blocking the UI
        _ = LoadLogosAsync(fixtureId, homeLogoUrl, awayLogoUrl);
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
                FlyoutStatusText.Text = match.KickoffTime.ToLocalTime().ToString("HH:mm");
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

            // Full team names
            FlyoutHomeTeamText.Text = !string.IsNullOrWhiteSpace(match.HomeTeam?.Name)
                ? match.HomeTeam.Name
                : match.HomeTeam?.ShortName ?? "Home";

            FlyoutAwayTeamText.Text = !string.IsNullOrWhiteSpace(match.AwayTeam?.Name)
                ? match.AwayTeam.Name
                : match.AwayTeam?.ShortName ?? "Away";

            // Score
            if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
            {
                FlyoutScoreText.Text = "vs";
            }
            else
            {
                FlyoutScoreText.Text = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";
            }

            // Recent Goalscorers
            var scorers = match.FormattedGoalscorers;
            if (scorers != null && scorers.Count > 0)
            {
                FlyoutEventsPanel.Visibility = Visibility.Visible;
                FlyoutGoalscorersText.Text = string.Join("  •  ", scorers);
            }
            else
            {
                FlyoutEventsPanel.Visibility = Visibility.Collapsed;
            }

            FlyoutPinHintText.Text = isPinned
                ? "📌 Match is pinned (click pin to resume rotation)"
                : "Click 📌 to pause ticker carousel";
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
        if (string.IsNullOrEmpty(homeLogoUrl) && string.IsNullOrEmpty(awayLogoUrl))
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (HomeTeamLogo != null) HomeTeamLogo.Visibility = Visibility.Collapsed;
                if (AwayTeamLogo != null) AwayTeamLogo.Visibility = Visibility.Collapsed;
            });
            return;
        }

        Uri? homeUri = null;
        Uri? awayUri = null;

        try
        {
            if (!string.IsNullOrEmpty(homeLogoUrl))
            {
                homeUri = await ImageCacheService.Instance.GetCachedImageUriAsync(homeLogoUrl).ConfigureAwait(false);
            }
            if (!string.IsNullOrEmpty(awayLogoUrl))
            {
                awayUri = await ImageCacheService.Instance.GetCachedImageUriAsync(awayLogoUrl).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort
        }

        // WinUI 3 BitmapImage MUST strictly be instantiated on the UI thread inside DispatcherQueue
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_currentDisplayedMatchId != fixtureId)
            {
                // Discard stale logos if ticker rotated to another match during download
                return;
            }

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

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        var matchId = _currentDisplayedMatchId;
        PinToggled?.Invoke(this, matchId);
    }

    private void PinButton_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private async void RootBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject orig && IsDescendantOf(orig, PinButton))
        {
            e.Handled = true;
            return;
        }

        var matchId = _currentDisplayedMatchId;
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return;
        }

        try
        {
            using var apiClient = new FootballApiClient();
            var response = await apiClient.GetMatchDetailsAsync(matchId);

            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                if (_matchCenterWindow == null)
                {
                    _matchCenterWindow = new MatchCenterWindow();
                    _matchCenterWindow.Closed += (s, args) => _matchCenterWindow = null;
                }

                _matchCenterWindow.Populate(response.Envelope.Data);
                _matchCenterWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Failed to open MatchCenterWindow on ticker tap.", ex);
        }
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject? parent)
    {
        if (parent == null) return false;
        while (element != null)
        {
            if (ReferenceEquals(element, parent)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
