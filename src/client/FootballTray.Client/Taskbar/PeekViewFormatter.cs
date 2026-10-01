using System;
using System.Collections.Generic;
using System.Linq;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Taskbar;

public sealed class PeekViewMatchData
{
    public string HomeHeader { get; set; } = string.Empty;
    public string AwayHeader { get; set; } = string.Empty;
    public string HomeScoreDisplay { get; set; } = string.Empty;
    public string AwayScoreDisplay { get; set; } = string.Empty;
    public string ScoreDisplay { get; set; } = "vs";
    public string HomeEventsText { get; set; } = string.Empty;
    public string AwayEventsText { get; set; } = string.Empty;
    public bool HasEvents => !string.IsNullOrWhiteSpace(HomeEventsText) || !string.IsNullOrWhiteSpace(AwayEventsText);
    public string FormattedToolTipText { get; set; } = string.Empty;
}

public static class PeekViewFormatter
{
    public static bool IsMissedPenalty(TrayEventDto? ev)
    {
        if (ev == null) return false;
        string t = (ev.Type ?? string.Empty).ToLowerInvariant();
        string d = (ev.Detail ?? string.Empty).ToLowerInvariant();
        string desc = (ev.Description ?? string.Empty).ToLowerInvariant();

        if (t.Contains("missed") && t.Contains("pen")) return true;
        if (t is "missed_penalty" or "missedpenalty" or "penalty_missed") return true;
        if ((t is "penalty" || t.Contains("pen")) && (d.Contains("missed") || desc.Contains("missed"))) return true;
        if (d.Contains("missed penalty") || d.Contains("penalty missed") || desc.Contains("missed penalty") || desc.Contains("penalty missed")) return true;

        return false;
    }

    public static bool IsGoal(TrayEventDto? ev)
    {
        if (ev == null) return false;
        if (IsMissedPenalty(ev)) return false;
        string t = (ev.Type ?? string.Empty).ToLowerInvariant();
        return t.Contains("goal") || t == "penalty" || t == "owngoal";
    }

    public static bool IsRedCard(TrayEventDto? ev)
    {
        if (ev == null) return false;
        string t = (ev.Type ?? string.Empty).ToLowerInvariant();
        return t.Contains("red") || t is "redcard" or "red_card" or "secondyellow" or "yellowred";
    }

    public static bool IsTeamMatch(string? eventTeamId, TeamSummaryDto? team, string? fallbackTeamId = null)
    {
        if (string.IsNullOrWhiteSpace(eventTeamId)) return false;

        string evId = eventTeamId.Trim();
        var candidates = new List<string>();

        if (team != null)
        {
            if (!string.IsNullOrWhiteSpace(team.Id)) candidates.Add(team.Id.Trim());
            if (!string.IsNullOrWhiteSpace(team.LegacyId)) candidates.Add(team.LegacyId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(fallbackTeamId))
        {
            candidates.Add(fallbackTeamId.Trim());
        }

        if (candidates.Count == 0) return false;

        foreach (var c in candidates)
        {
            if (string.Equals(evId, c, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        static string StripPrefix(string val)
        {
            if (val.StartsWith("ft_team_", StringComparison.OrdinalIgnoreCase))
                return val.Substring(8);
            if (val.StartsWith("team_", StringComparison.OrdinalIgnoreCase))
                return val.Substring(5);
            return val;
        }

        string evStripped = StripPrefix(evId);
        foreach (var c in candidates)
        {
            string cStripped = StripPrefix(c);
            if (string.Equals(evStripped, cStripped, StringComparison.OrdinalIgnoreCase))
                return true;

            if (long.TryParse(evStripped, out long evNum) && long.TryParse(cStripped, out long cNum))
            {
                if (evNum == cNum)
                    return true;
            }
        }

        return false;
    }

    public static bool IsEventForTeam(TrayEventDto ev, TeamSummaryDto? team, string? fallbackTeamId = null)
    {
        if (team == null) return false;
        if (IsTeamMatch(ev.TeamId, team, fallbackTeamId)) return true;

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            if (!string.IsNullOrWhiteSpace(team.Name) && ev.Description.Contains(team.Name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(team.ShortName) && team.ShortName.Length >= 2 && ev.Description.Contains(team.ShortName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static PeekViewMatchData Format(TrayMatchDto? match)
    {
        var result = new PeekViewMatchData();
        if (match == null) return result;

        string homeName = !string.IsNullOrWhiteSpace(match.HomeTeam?.Name)
            ? match.HomeTeam.Name
            : match.HomeTeam?.ShortName ?? "Home";

        string awayName = !string.IsNullOrWhiteSpace(match.AwayTeam?.Name)
            ? match.AwayTeam.Name
            : match.AwayTeam?.ShortName ?? "Away";

        result.HomeHeader = homeName;
        result.AwayHeader = awayName;

        var events = match.RecentEvents ?? match.Events ?? new List<TrayEventDto>();

        // 1. Red Card Indicators
        bool homeHasRed = events.Any(e => IsRedCard(e) && IsEventForTeam(e, match.HomeTeam, match.HomeTeam?.Id));
        bool awayHasRed = events.Any(e => IsRedCard(e) && IsEventForTeam(e, match.AwayTeam, match.AwayTeam?.Id));

        result.HomeScoreDisplay = homeHasRed ? $"{homeName} 🟥" : homeName;
        result.AwayScoreDisplay = awayHasRed ? $"{awayName} 🟥" : awayName;

        if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
        {
            result.ScoreDisplay = "vs";
        }
        else
        {
            result.ScoreDisplay = $"{match.Score?.Home ?? 0} - {match.Score?.Away ?? 0}";
        }

        // 2. Team-Divided Match Incidents (Goals and Red Cards, strictly excluding Missed Penalties)
        var homeIncidentsList = BuildTeamIncidentLines(events, match.HomeTeam, match.HomeTeam?.Id);
        var awayIncidentsList = BuildTeamIncidentLines(events, match.AwayTeam, match.AwayTeam?.Id);

        result.HomeEventsText = string.Join("\n", homeIncidentsList);
        result.AwayEventsText = string.Join("\n", awayIncidentsList);

        // 3. String ToolTip formatted strictly with headers and line breaks
        // e.g. [Home Team Name]\nMessi 14', 89'\n\n[Away Team Name]\nRonaldo 80'
        var tipParts = new List<string>();
        if (homeIncidentsList.Count > 0)
        {
            tipParts.Add($"[{homeName}]\n{string.Join("\n", homeIncidentsList)}");
        }
        if (awayIncidentsList.Count > 0)
        {
            tipParts.Add($"[{awayName}]\n{string.Join("\n", awayIncidentsList)}");
        }

        result.FormattedToolTipText = string.Join("\n\n", tipParts);

        return result;
    }

    private static List<string> BuildTeamIncidentLines(List<TrayEventDto> events, TeamSummaryDto? team, string? teamId)
    {
        var lines = new List<string>();
        if (events == null || events.Count == 0 || team == null) return lines;

        // Goals (strictly excluding missed penalties)
        var goals = events
            .Where(e => IsGoal(e) && !IsMissedPenalty(e) && IsEventForTeam(e, team, teamId))
            .OrderBy(e => e.Minute)
            .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName)
                ? e.PlayerName.Trim()
                : (!string.IsNullOrWhiteSpace(e.Description) ? e.Description.Trim() : "Goal"));

        foreach (var g in goals)
        {
            var minutes = string.Join(", ", g.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
            lines.Add($"{g.Key} {minutes}");
        }

        // Red Cards
        var reds = events
            .Where(e => IsRedCard(e) && IsEventForTeam(e, team, teamId))
            .OrderBy(e => e.Minute)
            .GroupBy(e => !string.IsNullOrWhiteSpace(e.PlayerName) ? e.PlayerName.Trim() : "Red Card");

        foreach (var r in reds)
        {
            var minutes = string.Join(", ", r.Select(e => e.ExtraMinute > 0 ? $"{e.Minute}+{e.ExtraMinute}'" : $"{e.Minute}'"));
            lines.Add($"🟥 {r.Key} {minutes}");
        }

        return lines;
    }

    public static string FormatToolTipString(TrayMatchDto? match)
    {
        return Format(match).FormattedToolTipText;
    }
}
