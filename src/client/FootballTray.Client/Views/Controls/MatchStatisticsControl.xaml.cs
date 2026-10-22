using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using FootballTray.Client.ViewModels;

namespace FootballTray.Client.Views.Controls;

public sealed partial class MatchStatisticsControl : UserControl
{
    private MatchCenterViewModel? _subscribedVm;

    public MatchStatisticsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderHeatmap();
        DataContextChanged += (_, _) => OnDataContextChanged();
    }

    private void OnDataContextChanged()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }

        if (DataContext is MatchCenterViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }

        RenderHeatmap();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchCenterViewModel.HeatmapPoints) ||
            e.PropertyName == nameof(MatchCenterViewModel.HomeAttackingThirdPct) ||
            e.PropertyName == nameof(MatchCenterViewModel.AwayAttackingThirdPct))
        {
            RenderHeatmap();
        }
    }

    public void Update(MatchCenterViewModel vm)
    {
        if (DataContext != vm)
        {
            DataContext = vm;
        }
        if (NoStatsBorder != null)
        {
            NoStatsBorder.Visibility = vm.HasStatistics ? Visibility.Collapsed : Visibility.Visible;
        }
        if (StatsItemsControl != null)
        {
            StatsItemsControl.Visibility = vm.HasStatistics ? Visibility.Visible : Visibility.Collapsed;
        }
        RenderHeatmap();
    }

    public void RenderHeatmap()
    {
        if (HeatmapPitchCanvas == null)
            return;

        try
        {
            HeatmapPitchCanvas.Children.Clear();

            if (DataContext is not MatchCenterViewModel vm)
                return;

            double width = HeatmapPitchCanvas.ActualWidth > 0 ? HeatmapPitchCanvas.ActualWidth : 340;
            double height = HeatmapPitchCanvas.ActualHeight > 0 ? HeatmapPitchCanvas.ActualHeight : 420;

            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = 340;
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) height = 420;

            // 1. Render territorial third bands on the pitch canvas
            double bandHeight = height / 3.0;

            // Top third: Away attacking third / Home defending third
            var topBand = new Border
            {
                Width = width,
                Height = bandHeight,
                Background = new SolidColorBrush(Color.FromArgb(45, 209, 52, 56)), // Semi-transparent Red
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var topText = new TextBlock
            {
                Text = $"TOP THIRD: {vm.AwayAttackingThirdPct}% Away Attacking",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 0, 0)
            };
            topBand.Child = topText;
            Canvas.SetLeft(topBand, 0);
            Canvas.SetTop(topBand, 0);
            HeatmapPitchCanvas.Children.Add(topBand);

            // Middle third
            var midBand = new Border
            {
                Width = width,
                Height = bandHeight,
                Background = new SolidColorBrush(Color.FromArgb(35, 245, 158, 11)), // Semi-transparent Amber
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var midText = new TextBlock
            {
                Text = $"MIDFIELD CONTEST: {vm.HomeMiddleThirdPct}% – {vm.AwayMiddleThirdPct}%",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            midBand.Child = midText;
            Canvas.SetLeft(midBand, 0);
            Canvas.SetTop(midBand, bandHeight);
            HeatmapPitchCanvas.Children.Add(midBand);

            // Bottom third: Home attacking third / Away defending third
            var btmBand = new Border
            {
                Width = width,
                Height = bandHeight,
                Background = new SolidColorBrush(Color.FromArgb(45, 0, 120, 212)) // Semi-transparent Blue
            };
            var btmText = new TextBlock
            {
                Text = $"BOTTOM THIRD: {vm.HomeAttackingThirdPct}% Home Attacking",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14)
            };
            btmBand.Child = btmText;
            Canvas.SetLeft(btmBand, 0);
            Canvas.SetTop(btmBand, bandHeight * 2);
            HeatmapPitchCanvas.Children.Add(btmBand);

            // 2. Render radiant activity/shot nodes from HeatmapPoints
            if (vm.HeatmapPoints != null)
            {
                foreach (var pt in vm.HeatmapPoints)
                {
                    double ptLeft = pt.CanvasLeft;
                    double ptTop = pt.CanvasTop;
                    if (double.IsNaN(ptLeft) || double.IsInfinity(ptLeft)) ptLeft = width / 2;
                    if (double.IsNaN(ptTop) || double.IsInfinity(ptTop)) ptTop = height / 2;

                    double scaledTop = Math.Clamp(ptTop * (height / 500.0), 10, Math.Max(10, height - 30));
                    double scaledLeft = Math.Clamp(ptLeft, 10, Math.Max(10, width - 30));

                    double ptSize = pt.Size;
                    if (double.IsNaN(ptSize) || double.IsInfinity(ptSize) || ptSize <= 0) ptSize = 40;

                    var ellipse = new Ellipse
                    {
                        Width = ptSize,
                        Height = ptSize,
                        Fill = pt.FillBrush,
                        Opacity = pt.Opacity
                    };
                    Canvas.SetLeft(ellipse, scaledLeft);
                    Canvas.SetTop(ellipse, scaledTop);
                    HeatmapPitchCanvas.Children.Add(ellipse);
                }
            }
        }
        catch (Exception ex)
        {
            FootballTray.Client.Diagnostics.AppDiagnosticsLogger.LogError("MatchStatisticsControl", "RenderHeatmapFailed", ex.Message, ex);
        }
    }
}
