using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client.Views.Controls;

public sealed partial class MatchLineupPitchControl : UserControl
{
    private INotifyCollectionChanged? _currentPitchPlayers;

    public MatchLineupPitchControl()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderPitchPlayers();
        DataContextChanged += (_, _) => OnDataContextChanged();
    }

    private void OnDataContextChanged()
    {
        if (_currentPitchPlayers != null)
        {
            _currentPitchPlayers.CollectionChanged -= OnPitchPlayersCollectionChanged;
            _currentPitchPlayers = null;
        }

        if (DataContext is MatchCenterViewModel vm && vm.PitchPlayers != null)
        {
            _currentPitchPlayers = vm.PitchPlayers;
            _currentPitchPlayers.CollectionChanged += OnPitchPlayersCollectionChanged;
        }

        RenderPitchPlayers();
    }

    private void OnPitchPlayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderPitchPlayers();
    }

    public void Update(MatchCenterViewModel vm)
    {
        DataContext = vm;
        NoLineupsBorder.Visibility = vm.HasLineups ? Visibility.Collapsed : Visibility.Visible;
        LineupsContentContainer.Visibility = vm.HasLineups ? Visibility.Visible : Visibility.Collapsed;

        RenderPitchPlayers();
    }

    public void ApplyPitchCanvasCoordinates()
    {
        RenderPitchPlayers();
    }

    private void RenderPitchPlayers()
    {
        if (PitchCanvas == null)
            return;

        PitchCanvas.Children.Clear();

        if (DataContext is not MatchCenterViewModel vm || vm.PitchPlayers == null)
            return;

        foreach (var node in vm.PitchPlayers)
        {
            var element = CreatePlayerElement(node);
            Canvas.SetLeft(element, node.CanvasLeft);
            Canvas.SetTop(element, node.CanvasTop);
            PitchCanvas.Children.Add(element);
        }
    }

    private static UIElement CreatePlayerElement(PitchPlayerNodeViewModel node)
    {
        var rootGrid = new Grid
        {
            Width = 70,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        var stackPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 2
        };

        var badgeGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 26,
            Height = 26
        };

        Color circleColor = node.IsGoalkeeper
            ? Color.FromArgb(255, 0xD9, 0x77, 0x06)  // Gold/Amber
            : (node.IsHomeTeam
                ? Color.FromArgb(255, 0x00, 0x78, 0xD4)  // Home Blue
                : Color.FromArgb(255, 0xD1, 0x34, 0x38)); // Away Red

        var ellipse = new Ellipse
        {
            Width = 26,
            Height = 26,
            Fill = new SolidColorBrush(circleColor),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5
        };

        var numberText = new TextBlock
        {
            Text = node.Number,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        badgeGrid.Children.Add(ellipse);
        badgeGrid.Children.Add(numberText);

        var pillBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x17, 0x2A)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 70
        };

        var nameText = new TextBlock
        {
            Text = node.ShortDisplayName,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF8, 0xFA, 0xFC)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 70,
            MaxLines = 1,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        pillBorder.Child = nameText;
        stackPanel.Children.Add(badgeGrid);
        stackPanel.Children.Add(pillBorder);
        rootGrid.Children.Add(stackPanel);

        return rootGrid;
    }
}
