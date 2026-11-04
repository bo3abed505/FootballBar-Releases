using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client.Views;

public sealed partial class TeamHubWindow : Window
{
    public TeamHubViewModel ViewModel { get; } = new();

    public TeamHubWindow(string? teamId = null, string? initialName = null, string? initialLogo = null)
    {
        InitializeComponent();
        RootGrid.DataContext = ViewModel;

        try
        {
            AppWindow?.Resize(new Windows.Graphics.SizeInt32(960, 840));
        }
        catch
        {
            // Ignore resize failure on unsupported platforms/runtimes
        }

        UpdateTabStyles();

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.TeamName))
            {
                Title = !string.IsNullOrWhiteSpace(ViewModel.TeamName) ? $"{ViewModel.TeamName} - Team Hub" : "Team Hub";
            }
            else if (e.PropertyName == nameof(ViewModel.SubTabIndex))
            {
                UpdateTabStyles();
            }
        };

        if (!string.IsNullOrWhiteSpace(teamId))
        {
            Title = !string.IsNullOrWhiteSpace(initialName) ? $"{initialName} - Team Hub" : "Team Hub";
            _ = ViewModel.LoadAsync(teamId, initialName, initialLogo);
        }
    }

    private void UpdateTabStyles()
    {
        var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        var defaultStyle = (Style)Application.Current.Resources["DefaultButtonStyle"];

        if (FixturesTabButton != null && StandingsTabButton != null && SquadTabButton != null)
        {
            FixturesTabButton.Style = ViewModel.IsFixturesTabSelected ? accentStyle : defaultStyle;
            StandingsTabButton.Style = ViewModel.IsStandingsTabSelected ? accentStyle : defaultStyle;
            SquadTabButton.Style = ViewModel.IsSquadTabSelected ? accentStyle : defaultStyle;
        }
    }

    private void OnFixturesTabClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectFixturesTab();
        UpdateTabStyles();
    }

    private void OnStandingsTabClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectStandingsTab();
        UpdateTabStyles();
    }

    private void OnSquadTabClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectSquadTab();
        UpdateTabStyles();
    }

    private void OnAlertsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && !string.IsNullOrWhiteSpace(ViewModel.TeamId))
        {
            var prefs = App.CurrentPreferencesService ?? App.CurrentMainViewModel?.PreferencesService;
            if (prefs != null)
            {
                FootballTray.Client.Views.Controls.EntityAlertFlyoutHelper.ShowAlertProfileFlyout(
                    fe,
                    ViewModel.TeamId,
                    ViewModel.TeamName,
                    isTeam: true,
                    prefs);
            }
        }
    }

    private void OnToggleFollowClicked(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ToggleFollowAsync();
    }

    private void OnPrimaryLeagueClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.PrimaryLeagueId))
        {
            App.OpenCompetitionStandings(ViewModel.PrimaryLeagueId, ViewModel.PrimaryLeagueName, ViewModel.PrimaryLeagueLogoUrl, ViewModel.CountryText);
        }
    }

    private void OnFixtureCompetitionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CompetitionFixtureViewModel fix)
        {
            var compId = !string.IsNullOrWhiteSpace(fix.CompetitionId) ? fix.CompetitionId : ViewModel.PrimaryLeagueId;
            var compName = !string.IsNullOrWhiteSpace(fix.CompetitionName) ? fix.CompetitionName : ViewModel.PrimaryLeagueName;
            var compLogo = !string.IsNullOrWhiteSpace(fix.CompetitionLogoUrl) ? fix.CompetitionLogoUrl : ViewModel.PrimaryLeagueLogoUrl;
            App.OpenCompetitionStandings(compId, compName, compLogo, ViewModel.CountryText);
        }
    }

    private void OnFixtureCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var parent = dep;
            while (parent != null && !ReferenceEquals(parent, sender))
            {
                if (parent is ButtonBase)
                {
                    return;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
        }

        var fixVm = ((sender as FrameworkElement)?.DataContext as CompetitionFixtureViewModel)
            ?? ((sender as FrameworkElement)?.Tag as CompetitionFixtureViewModel);

        if (fixVm != null)
        {
            App.OpenMatchCenter(fixVm.ToTrayMatchDto());
        }
    }

    private void OnFixtureHomeTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CompetitionFixtureViewModel fix && !string.IsNullOrWhiteSpace(fix.HomeTeamId))
        {
            App.OpenTeamHub(fix.HomeTeamId, fix.HomeTeamName, fix.HomeTeamLogo);
        }
    }

    private void OnFixtureAwayTeamClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CompetitionFixtureViewModel fix && !string.IsNullOrWhiteSpace(fix.AwayTeamId))
        {
            App.OpenTeamHub(fix.AwayTeamId, fix.AwayTeamName, fix.AwayTeamLogo);
        }
    }

    private void OnFixtureItemClicked(object sender, RoutedEventArgs e)
    {
        var fixVm = ((sender as FrameworkElement)?.DataContext as CompetitionFixtureViewModel)
            ?? ((sender as FrameworkElement)?.Tag as CompetitionFixtureViewModel);

        string? matchId = (sender as FrameworkElement)?.Tag as string
            ?? fixVm?.Id
            ?? ((sender as FrameworkElement)?.DataContext as FootballTray.Core.Dto.CompetitionFixtureDto)?.Id;

        if (fixVm != null)
        {
            App.OpenMatchCenter(fixVm.ToTrayMatchDto());
        }
        else if (!string.IsNullOrWhiteSpace(matchId))
        {
            App.OpenMatchCenter(matchId);
        }
    }

    private void OnStandingClubClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string clubId && !string.IsNullOrWhiteSpace(clubId))
        {
            App.OpenTeamHub(clubId);
        }
    }

    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadAsync(ViewModel.TeamId, ViewModel.TeamName, ViewModel.TeamLogoUrl);
    }
}

