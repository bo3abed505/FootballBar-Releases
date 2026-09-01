using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Views;

public class MatchTimelineItem
{
    public string MinuteDisplay { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PlayerInfo { get; set; }
    public Visibility PlayerInfoVisibility => string.IsNullOrWhiteSpace(PlayerInfo) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class MatchCenterWindow : Window
{
    public ObservableCollection<MatchTimelineItem> TimelineItems { get; } = new();

    public MatchCenterWindow()
    {
        InitializeComponent();
        EventsList.ItemsSource = TimelineItems;
    }

    public void Populate(MatchDetailDto match)
    {
        Title = $"{match.HomeTeam.Name} vs {match.AwayTeam.Name} - Match Center";
        CompetitionText.Text = match.Competition?.Name ?? "Football";

        // Status badge
        StatusText.Text = match.State == "LIVE"
            ? $"LIVE {match.ElapsedMinute}'"
            : match.State;

        HomeTeamName.Text = match.HomeTeam.Name;
        AwayTeamName.Text = match.AwayTeam.Name;
        ScoreText.Text = $"{match.Score.Home} - {match.Score.Away}";

        if (match.Score.HalfTime != null && match.Score.HalfTime.Home.HasValue && match.Score.HalfTime.Away.HasValue)
        {
            HalfTimeText.Text = $"(HT {match.Score.HalfTime.Home}-{match.Score.HalfTime.Away})";
            HalfTimeText.Visibility = Visibility.Visible;
        }
        else
        {
            HalfTimeText.Visibility = Visibility.Collapsed;
        }

        // Timeline events
        TimelineItems.Clear();
        foreach (var ev in match.Events.OrderBy(e => e.Minute))
        {
            string icon = ev.Type switch
            {
                "Goal" => "⚽",
                "YellowCard" => "🟨",
                "RedCard" => "🟥",
                "Substitution" => "🔄",
                "PeriodStart" => "⏱️",
                "PeriodEnd" => "🏁",
                _ => "📌",
            };

            string? playerInfo = null;
            if (!string.IsNullOrWhiteSpace(ev.PlayerName))
            {
                playerInfo = ev.PlayerName;
            }

            TimelineItems.Add(new MatchTimelineItem
            {
                MinuteDisplay = $"{ev.Minute}'",
                IconGlyph = icon,
                Description = ev.Description ?? ev.Type,
                PlayerInfo = playerInfo,
            });
        }
    }
}
