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
        if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
        {
            _currentDisplayedMatchId = fixtureId;
            TickerText.Text = formattedText;
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _currentDisplayedMatchId = fixtureId;
                TickerText.Text = formattedText;
            });
        }
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
