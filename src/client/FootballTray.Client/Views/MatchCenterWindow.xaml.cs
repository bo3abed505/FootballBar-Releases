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

        if (ViewModel.HasHalfTime)
        {
            HalfTimeText.Text = ViewModel.HalfTimeText;
            HalfTimeText.Visibility = Visibility.Visible;
        }
        else
        {
            HalfTimeText.Visibility = Visibility.Collapsed;
        }

        // Sync team labels on lineups tab
        HomeLineupRadio.Content = ViewModel.HomeTeamName;
        AwayLineupRadio.Content = ViewModel.AwayTeamName;

        // Empty states toggles
        NoEventsText.Visibility = ViewModel.TimelineItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoStatsBorder.Visibility = ViewModel.HasStatistics ? Visibility.Collapsed : Visibility.Visible;
        StatsItemsControl.Visibility = ViewModel.HasStatistics ? Visibility.Visible : Visibility.Collapsed;
        NoLineupsBorder.Visibility = ViewModel.HasLineups ? Visibility.Collapsed : Visibility.Visible;

        UpdateLineupContainerVisibility();

        // Asynchronously load team crests on background thread without blocking UI
        _ = LoadLogosAsync(match);
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
        if (StatisticsPanel == null || LineupsPanel == null) return;

        if (StatsTabRadio?.IsChecked == true)
        {
            StatisticsPanel.Visibility = Visibility.Visible;
            LineupsPanel.Visibility = Visibility.Collapsed;
        }
        else if (LineupsTabRadio?.IsChecked == true)
        {
            StatisticsPanel.Visibility = Visibility.Collapsed;
            LineupsPanel.Visibility = Visibility.Visible;
            UpdateLineupContainerVisibility();
        }
    }

    private void OnLineupTeamChecked(object sender, RoutedEventArgs e)
    {
        UpdateLineupContainerVisibility();
    }

    private void UpdateLineupContainerVisibility()
    {
        if (HomeLineupContainer == null || AwayLineupContainer == null) return;

        if (!ViewModel.HasLineups)
        {
            HomeLineupContainer.Visibility = Visibility.Collapsed;
            AwayLineupContainer.Visibility = Visibility.Collapsed;
            return;
        }

        if (HomeLineupRadio?.IsChecked == true)
        {
            HomeLineupContainer.Visibility = Visibility.Visible;
            AwayLineupContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            HomeLineupContainer.Visibility = Visibility.Collapsed;
            AwayLineupContainer.Visibility = Visibility.Visible;
        }
    }
}
