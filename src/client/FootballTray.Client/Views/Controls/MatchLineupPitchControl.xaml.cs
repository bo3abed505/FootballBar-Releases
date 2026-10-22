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
    private INotifyCollectionChanged? _currentHomePitchPlayers;
    private INotifyCollectionChanged? _currentAwayPitchPlayers;
    private MatchCenterViewModel? _subscribedVm;
    private bool _isRendering;

    public MatchLineupPitchControl()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderPitch();
        DataContextChanged += (_, _) => OnDataContextChanged();
    }

    private void OnDataContextChanged()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }

        if (_currentPitchPlayers != null)
        {
            _currentPitchPlayers.CollectionChanged -= OnPitchPlayersCollectionChanged;
            _currentPitchPlayers = null;
        }

        if (_currentHomePitchPlayers != null)
        {
            _currentHomePitchPlayers.CollectionChanged -= OnPitchPlayersCollectionChanged;
            _currentHomePitchPlayers = null;
        }

        if (_currentAwayPitchPlayers != null)
        {
            _currentAwayPitchPlayers.CollectionChanged -= OnPitchPlayersCollectionChanged;
            _currentAwayPitchPlayers = null;
        }

        if (DataContext is MatchCenterViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;

            if (vm.PitchPlayers != null)
            {
                _currentPitchPlayers = vm.PitchPlayers;
                _currentPitchPlayers.CollectionChanged += OnPitchPlayersCollectionChanged;
            }

            if (vm.HomePitchPlayers != null)
            {
                _currentHomePitchPlayers = vm.HomePitchPlayers;
                _currentHomePitchPlayers.CollectionChanged += OnPitchPlayersCollectionChanged;
            }

            if (vm.AwayPitchPlayers != null)
            {
                _currentAwayPitchPlayers = vm.AwayPitchPlayers;
                _currentAwayPitchPlayers.CollectionChanged += OnPitchPlayersCollectionChanged;
            }
        }

        RenderPitch();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MatchCenterViewModel.PitchPlayers) ||
            e.PropertyName == nameof(MatchCenterViewModel.HomePitchPlayers) ||
            e.PropertyName == nameof(MatchCenterViewModel.AwayPitchPlayers) ||
            e.PropertyName == nameof(MatchCenterViewModel.HasLineups))
        {
            RenderPitch();
        }
    }

    private void OnPitchPlayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderPitch();
    }

    public void Update(MatchCenterViewModel vm)
    {
        if (DataContext != vm)
        {
            DataContext = vm;
        }
        if (NoLineupsBorder != null)
        {
            NoLineupsBorder.Visibility = vm.HasLineups ? Visibility.Collapsed : Visibility.Visible;
        }
        if (LineupsContentContainer != null)
        {
            LineupsContentContainer.Visibility = vm.HasLineups ? Visibility.Visible : Visibility.Collapsed;
        }

        RenderPitch();
    }

    public void ApplyPitchCanvasCoordinates()
    {
        RenderPitch();
    }

    public void RenderPitchPlayers()
    {
        RenderPitch();
    }

    public void RenderPitch()
    {
        if (_isRendering) return;
        if (DataContext is not MatchCenterViewModel vm) return;

        _isRendering = true;
        try
        {
            if (NoLineupsBorder != null)
            {
                NoLineupsBorder.Visibility = vm.HasLineups ? Visibility.Collapsed : Visibility.Visible;
            }
            if (LineupsContentContainer != null)
            {
                LineupsContentContainer.Visibility = vm.HasLineups ? Visibility.Visible : Visibility.Collapsed;
            }

            if (PitchCanvas != null)
            {
                PitchCanvas.Children.Clear();
                PitchCanvas.Visibility = Visibility.Visible;
            }

            if (HomePitchCanvas != null)
            {
                HomePitchCanvas.Children.Clear();
                HomePitchCanvas.Visibility = Visibility.Visible;
            }

            if (AwayPitchCanvas != null)
            {
                AwayPitchCanvas.Children.Clear();
                AwayPitchCanvas.Visibility = Visibility.Visible;
            }

            // 1. Render Away Pitch Players (Right Half)
            var awayNodes = (vm.AwayPitchPlayers != null && vm.AwayPitchPlayers.Count > 0)
                ? (IEnumerable<PitchPlayerNodeViewModel>)vm.AwayPitchPlayers
                : (vm.PitchPlayers != null ? vm.PitchPlayers.Where(p => !p.IsHomeTeam) : Enumerable.Empty<PitchPlayerNodeViewModel>());

            foreach (var node in awayNodes)
            {
                double left = node.CanvasLeft;
                double top = node.CanvasTop;
                if (double.IsNaN(left) || double.IsInfinity(left)) left = 0;
                if (double.IsNaN(top) || double.IsInfinity(top)) top = 0;
                left = Math.Clamp(left, 4, 1000 - 114);
                top = Math.Clamp(top, 4, 600 - 94);

                var element = CreatePlayerElement(node);
                element.Visibility = Visibility.Visible;
                Canvas.SetLeft(element, left);
                Canvas.SetTop(element, top);

                if (AwayPitchCanvas != null)
                {
                    AwayPitchCanvas.Children.Add(element);
                }
                else if (PitchCanvas != null)
                {
                    PitchCanvas.Children.Add(element);
                }
            }

            // 2. Render Home Pitch Players (Left Half)
            var homeNodes = (vm.HomePitchPlayers != null && vm.HomePitchPlayers.Count > 0)
                ? (IEnumerable<PitchPlayerNodeViewModel>)vm.HomePitchPlayers
                : (vm.PitchPlayers != null ? vm.PitchPlayers.Where(p => p.IsHomeTeam) : Enumerable.Empty<PitchPlayerNodeViewModel>());

            foreach (var node in homeNodes)
            {
                double left = node.CanvasLeft;
                double top = node.CanvasTop;
                if (double.IsNaN(left) || double.IsInfinity(left)) left = 0;
                if (double.IsNaN(top) || double.IsInfinity(top)) top = 0;
                left = Math.Clamp(left, 4, 1000 - 114);
                top = Math.Clamp(top, 4, 600 - 94);

                var element = CreatePlayerElement(node);
                element.Visibility = Visibility.Visible;
                Canvas.SetLeft(element, left);
                Canvas.SetTop(element, top);

                if (HomePitchCanvas != null)
                {
                    HomePitchCanvas.Children.Add(element);
                }
                else if (PitchCanvas != null)
                {
                    PitchCanvas.Children.Add(element);
                }
            }
        }
        catch (Exception ex)
        {
            FootballTray.Client.Diagnostics.AppDiagnosticsLogger.LogError("MatchLineupPitchControl", "RenderPitchFailed", ex.Message, ex);
        }
        finally
        {
            _isRendering = false;
        }
    }

    private static UIElement CreatePlayerElement(PitchPlayerNodeViewModel node)
    {
        var rootGrid = new Grid
        {
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Visible
        };

        var mainStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 3
        };

        // --- SECTION 1: 5-Anchor Unified Player Node (60x60 Anchor Grid) ---
        var anchorGrid = new Grid
        {
            Width = 60,
            Height = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Center: 48x48 Jersey Circle with Shirt Number in 15pt Bold
        Color circleColor = node.IsGoalkeeper
            ? Color.FromArgb(255, 0xD9, 0x77, 0x06)  // Gold/Amber
            : (node.IsHomeTeam
                ? Color.FromArgb(255, 0x00, 0x78, 0xD4)  // Home Blue
                : Color.FromArgb(255, 0xD1, 0x34, 0x38)); // Away Red

        var jerseyGrid = new Grid
        {
            Width = 48,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var ellipse = new Ellipse
        {
            Width = 48,
            Height = 48,
            Fill = new SolidColorBrush(circleColor),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2.5
        };

        var numberText = new TextBlock
        {
            Text = node.Number,
            FontSize = 15.0,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        jerseyGrid.Children.Add(ellipse);
        jerseyGrid.Children.Add(numberText);
        anchorGrid.Children.Add(jerseyGrid);

        // Anchor 1 (Upper-Right): Rating Pill
        if (node.HasRating)
        {
            var ratingBadge = new Border
            {
                Background = node.RatingBackgroundBrush,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1.5, 5, 1.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -8, 0)
            };
            var ratingText = new TextBlock
            {
                Text = node.RatingDisplay,
                FontSize = 11.0,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                VerticalAlignment = VerticalAlignment.Center
            };
            ratingBadge.Child = ratingText;
            anchorGrid.Children.Add(ratingBadge);
        }

        // Anchor 2 (Upper-Left): Subbed Off ▼
        if (node.IsSubbedOff)
        {
            var subDot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(255, 0xEF, 0x44, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x17, 0x2A)),
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-5, -5, 0, 0),
                Child = new TextBlock
                {
                    Text = "▼",
                    FontSize = 10.0,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                }
            };
            string subTip = node.SubOutMinute.HasValue ? $"Subbed off ({node.SubOutMinute}')" : "Subbed off";
            ToolTipService.SetToolTip(subDot, subTip);
            anchorGrid.Children.Add(subDot);
        }

        // Anchor 3 (Mid-Left): Yellow / Red Card
        if (node.HasRedCard)
        {
            var redCard = new Border
            {
                Width = 10,
                Height = 15,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(255, 0xEF, 0x44, 0x44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x17, 0x2A)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-6, 0, 0, 0)
            };
            ToolTipService.SetToolTip(redCard, "Red Card");
            anchorGrid.Children.Add(redCard);
        }
        else if (node.HasYellowCard)
        {
            var yellowCard = new Border
            {
                Width = 10,
                Height = 15,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(255, 0xFA, 0xCC, 0x15)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x17, 0x2A)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-6, 0, 0, 0)
            };
            ToolTipService.SetToolTip(yellowCard, "Yellow Card");
            anchorGrid.Children.Add(yellowCard);
        }

        // Anchor 4 (Bottom-Right): Goals Badge
        int totalGoals = node.RegularGoalsCount + node.PenaltyGoalsCount + node.OwnGoalsCount;
        if (totalGoals > 0)
        {
            Color bgCol = Color.FromArgb(255, 0x0F, 0x17, 0x2A);
            Color strokeCol = Color.FromArgb(255, 0x33, 0x41, 0x55);

            if (node.PenaltyGoalsCount > 0 && node.RegularGoalsCount == 0 && node.OwnGoalsCount == 0)
            {
                bgCol = Color.FromArgb(255, 0x16, 0xA3, 0x4A); // Emerald green for penalty
                strokeCol = Color.FromArgb(255, 0x22, 0xC5, 0x5E);
            }
            else if (node.OwnGoalsCount > 0 && node.RegularGoalsCount == 0 && node.PenaltyGoalsCount == 0)
            {
                bgCol = Color.FromArgb(255, 0xDC, 0x26, 0x26); // Crimson red for own goal
                strokeCol = Color.FromArgb(255, 0xEF, 0x44, 0x44);
            }

            var goalBadge = new Border
            {
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(bgCol),
                BorderBrush = new SolidColorBrush(strokeCol),
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -5, -4)
            };

            if (totalGoals == 1)
            {
                goalBadge.Width = 20;
                goalBadge.Child = new TextBlock
                {
                    Text = "⚽",
                    FontSize = 12.0,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                };
            }
            else
            {
                goalBadge.Padding = new Thickness(4, 0, 5, 0);
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(new TextBlock
                {
                    Text = "⚽",
                    FontSize = 12.0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $"x{totalGoals}",
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    VerticalAlignment = VerticalAlignment.Center
                });
                goalBadge.Child = sp;
            }

            string goalTip = totalGoals > 1 ? $"{totalGoals} Goals" : "Goal";
            if (node.PenaltyGoalsCount == 1 && totalGoals == 1) goalTip = "Penalty Goal";
            if (node.OwnGoalsCount == 1 && totalGoals == 1) goalTip = "Own Goal";
            ToolTipService.SetToolTip(goalBadge, goalTip);

            anchorGrid.Children.Add(goalBadge);
        }

        // Anchor 5 (Bottom-Left): Assists Badge
        if (node.AssistsCount > 0)
        {
            var assistBadge = new Border
            {
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x17, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x33, 0x41, 0x55)),
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(-5, 0, 0, -4)
            };

            if (node.AssistsCount == 1)
            {
                assistBadge.Width = 20;
                assistBadge.Child = new TextBlock
                {
                    Text = "👟",
                    FontSize = 12.0,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                };
            }
            else
            {
                assistBadge.Padding = new Thickness(4, 0, 5, 0);
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(new TextBlock
                {
                    Text = "👟",
                    FontSize = 12.0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                });
                sp.Children.Add(new TextBlock
                {
                    Text = $"x{node.AssistsCount}",
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    VerticalAlignment = VerticalAlignment.Center
                });
                assistBadge.Child = sp;
            }

            ToolTipService.SetToolTip(assistBadge, node.AssistsCount > 1 ? $"{node.AssistsCount} Assists" : "Assist");
            anchorGrid.Children.Add(assistBadge);
        }

        mainStack.Children.Add(anchorGrid);

        // --- SECTION 2: Surname-Only Single-Line Name Pill Directly Beneath Jersey ---
        var pillBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0F, 0x17, 0x2A)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 108,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var nameText = new TextBlock
        {
            FontSize = 12.0,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 96,
            MaxLines = 1,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (node.IsCaptain)
        {
            nameText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "C ",
                FontWeight = FontWeights.Bold,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xFB, 0xBF, 0x24)) // Bold Gold #FBBF24
            });
            nameText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = node.ShortDisplayName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.0,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF8, 0xFA, 0xFC))
            });
        }
        else
        {
            nameText.Text = node.ShortDisplayName;
            nameText.FontWeight = FontWeights.SemiBold;
            nameText.FontSize = 12.0;
            nameText.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF8, 0xFA, 0xFC));
        }

        pillBorder.Child = nameText;
        mainStack.Children.Add(pillBorder);
        rootGrid.Children.Add(mainStack);

        // --- SECTION 3: Full-Detail Hover Tooltip ---
        var tooltipLines = new List<string>();
        string header = $"#{node.Number} {node.Name}";
        if (node.IsCaptain)
        {
            header += " (C)";
        }
        if (!string.IsNullOrWhiteSpace(node.PositionBadge))
        {
            header += $" ({node.PositionBadge})";
        }
        tooltipLines.Add(header);

        if (node.HasRating)
        {
            string motm = node.IsManOfTheMatch ? " ★ MOTM" : "";
            tooltipLines.Add($"Rating: {node.RatingDisplay}{motm}");
        }

        if (node.RegularGoalsCount > 0)
        {
            tooltipLines.Add(node.RegularGoalsCount > 1 ? $"⚽ {node.RegularGoalsCount} Goals" : "⚽ 1 Goal");
        }
        if (node.PenaltyGoalsCount > 0)
        {
            tooltipLines.Add(node.PenaltyGoalsCount > 1 ? $"⚽ {node.PenaltyGoalsCount} Penalties" : "⚽ 1 Penalty");
        }
        if (node.OwnGoalsCount > 0)
        {
            tooltipLines.Add(node.OwnGoalsCount > 1 ? $"⚽ {node.OwnGoalsCount} Own Goals" : "⚽ 1 Own Goal");
        }
        if (node.AssistsCount > 0)
        {
            tooltipLines.Add(node.AssistsCount > 1 ? $"👟 {node.AssistsCount} Assists" : "👟 1 Assist");
        }
        if (node.HasRedCard)
        {
            tooltipLines.Add("🟥 Red Card");
        }
        else if (node.HasYellowCard)
        {
            tooltipLines.Add("🟨 Yellow Card");
        }
        if (node.IsSubbedOff)
        {
            string subText = node.SubOutMinute.HasValue ? $"▼ Subbed off ({node.SubOutMinute}')" : "▼ Subbed off";
            tooltipLines.Add(subText);
        }

        ToolTipService.SetToolTip(rootGrid, string.Join("\n", tooltipLines));

        return rootGrid;
    }
}
