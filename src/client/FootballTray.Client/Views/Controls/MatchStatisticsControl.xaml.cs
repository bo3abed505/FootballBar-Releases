using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client.Views.Controls;

public sealed partial class MatchStatisticsControl : UserControl
{
    public MatchStatisticsControl()
    {
        InitializeComponent();
    }

    public void Update(MatchCenterViewModel vm)
    {
        DataContext = vm;
        NoStatsBorder.Visibility = vm.HasStatistics ? Visibility.Collapsed : Visibility.Visible;
        StatsItemsControl.Visibility = vm.HasStatistics ? Visibility.Visible : Visibility.Collapsed;
    }
}
