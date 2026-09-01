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
    private TrayDataDto? _currentTrayData;
    private MatchCenterWindow? _matchCenterWindow;

    public TaskbarTicker()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Updates the ticker text and stored TrayData on the UI thread.
    /// </summary>
    public void Update(TrayDataDto? data, string formattedText)
    {
        _currentTrayData = data;
        DispatcherQueue.TryEnqueue(() =>
        {
            TickerText.Text = formattedText;
        });
    }

    /// <summary>
    /// Updates the ticker text directly on the UI thread.
    /// </summary>
    public void UpdateText(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TickerText.Text = text;
        });
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
        var primaryMatch = TrayTickerFormatter.GetActiveMatch(_currentTrayData);
        if (primaryMatch == null)
        {
            return;
        }

        try
        {
            using var apiClient = new FootballApiClient();
            var response = await apiClient.GetMatchDetailsAsync(primaryMatch.Id);

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
