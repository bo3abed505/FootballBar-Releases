using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client.Views.Controls;

public sealed partial class MatchLineupPitchControl : UserControl
{
    public MatchLineupPitchControl()
    {
        InitializeComponent();
    }

    public void Update(MatchCenterViewModel vm)
    {
        DataContext = vm;
        NoLineupsBorder.Visibility = vm.HasLineups ? Visibility.Collapsed : Visibility.Visible;
        LineupsContentContainer.Visibility = vm.HasLineups ? Visibility.Visible : Visibility.Collapsed;
    }
}
