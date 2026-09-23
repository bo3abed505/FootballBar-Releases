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
    private MatchCenterWindow? _matchCenterWindow;

    public TaskbarTicker()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Updates the ticker text and click identity atomically on the UI thread.
    /// </summary>
    public void UpdateDisplay(string? fixtureId, string formattedText)
    {
        UpdateDisplay(fixtureId, formattedText, null, null);
    }

    /// <summary>
    /// Updates the ticker text, click identity, and home/away team logos asynchronously without blocking the UI thread.
    /// </summary>
    public void UpdateDisplay(string? fixtureId, string formattedText, string? homeLogoUrl, string? awayLogoUrl)
    {
        void ApplyText()
        {
            _currentDisplayedMatchId = fixtureId;
            TickerText.Text = formattedText;
        }

        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            ApplyText();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ApplyText);
        }

        // Asynchronously retrieve logos on a background thread without blocking the UI
        _ = LoadLogosAsync(fixtureId, homeLogoUrl, awayLogoUrl);
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

    private async void RootBorder_Tapped(object sender, TappedRoutedEventArgs e)
    {
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
}
