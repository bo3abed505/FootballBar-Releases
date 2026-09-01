using System;
using System.Linq;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public enum TickerConnectionState
{
    OnlineFresh,
    OnlineStale,
    OfflineCached,
    NoCacheOffline,
}

public static class TrayTickerFormatter
{
    /// <summary>
    /// Identifies the single active/primary fixture represented by the taskbar ticker.
    /// Priority order:
    /// 1. First LIVE / HALFTIME / in-play match.
    /// 2. Nearest upcoming PREMATCH match by kickoff time.
    /// 3. First available match (e.g. FINISHED).
    /// 4. Null if empty.
    /// </summary>
    public static TrayMatchDto? GetActiveMatch(TrayDataDto? trayData)
    {
        if (trayData == null || trayData.Matches == null || trayData.Matches.Count == 0)
        {
            return null;
        }

        // 1. Prioritize in-play / Live matches
        var liveMatch = trayData.Matches.FirstOrDefault(m =>
            string.Equals(m.State, "LIVE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.State, "HALFTIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.State, "EXTRA_TIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.State, "PENALTIES", StringComparison.OrdinalIgnoreCase));

        if (liveMatch != null)
        {
            return liveMatch;
        }

        // 2. Nearest upcoming match (PREMATCH)
        var upcomingMatch = trayData.Matches
            .Where(m => string.Equals(m.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.KickoffTime)
            .FirstOrDefault();

        if (upcomingMatch != null)
        {
            return upcomingMatch;
        }

        // 3. Fallback to first available match
        return trayData.Matches.FirstOrDefault();
    }

    /// <summary>
    /// Formats the current TrayDataDto into a compact taskbar ticker string based on data freshness state.
    /// </summary>
    public static string FormatTicker(TrayDataDto? trayData, DataFreshnessState freshnessState)
    {
        var connectionState = freshnessState switch
        {
            DataFreshnessState.Fresh => TickerConnectionState.OnlineFresh,
            DataFreshnessState.NotModified => TickerConnectionState.OnlineFresh,
            DataFreshnessState.Stale => TickerConnectionState.OfflineCached,
            DataFreshnessState.Empty => TickerConnectionState.NoCacheOffline,
            _ => TickerConnectionState.NoCacheOffline,
        };
        return FormatTicker(trayData, connectionState);
    }

    /// <summary>
    /// Formats the current TrayDataDto into a compact taskbar ticker string.
    /// </summary>
    public static string FormatTicker(TrayDataDto? trayData, TickerConnectionState connectionState)
    {
        if (connectionState == TickerConnectionState.NoCacheOffline)
        {
            return "FootballTray · offline";
        }

        var activeMatch = GetActiveMatch(trayData);

        if (activeMatch == null)
        {
            return connectionState == TickerConnectionState.OfflineCached
                ? "FootballTray · offline"
                : "FootballTray";
        }

        return FormatMatch(activeMatch, connectionState);
    }

    private static string FormatMatch(TrayMatchDto match, TickerConnectionState connectionState)
    {
        var homeCode = GetTeamCode(match.HomeTeam);
        var awayCode = GetTeamCode(match.AwayTeam);

        string mainText;

        if (string.Equals(match.State, "LIVE", StringComparison.OrdinalIgnoreCase))
        {
            var minuteText = match.ExtraMinute > 0
                ? $"{match.ElapsedMinute}+{match.ExtraMinute}'"
                : $"{match.ElapsedMinute}'";
            mainText = $"⚽ {homeCode} {match.Score.Home}–{match.Score.Away} {awayCode} {minuteText}";
        }
        else if (string.Equals(match.State, "HALFTIME", StringComparison.OrdinalIgnoreCase))
        {
            mainText = $"⚽ {homeCode} {match.Score.Home}–{match.Score.Away} {awayCode} HT";
        }
        else if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
        {
            var localTime = match.KickoffTime.ToLocalTime().ToString("HH:mm");
            mainText = $"{homeCode} vs {awayCode} · {localTime}";
        }
        else if (string.Equals(match.State, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(match.State, "POSTMATCH", StringComparison.OrdinalIgnoreCase))
        {
            mainText = $"{homeCode} {match.Score.Home}–{match.Score.Away} {awayCode} FT";
        }
        else
        {
            mainText = $"{homeCode} {match.Score.Home}–{match.Score.Away} {awayCode}";
        }

        // Append connection/cache indicator if not fresh
        return connectionState switch
        {
            TickerConnectionState.OfflineCached => $"{mainText} · offline",
            TickerConnectionState.OnlineStale => $"{mainText} · cached",
            _ => mainText,
        };
    }

    private static string GetTeamCode(TeamSummaryDto team)
    {
        if (!string.IsNullOrWhiteSpace(team.ShortName))
        {
            return team.ShortName.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(team.Name))
        {
            var name = team.Name.Trim();
            return name.Length <= 3 ? name.ToUpperInvariant() : name.Substring(0, 3).ToUpperInvariant();
        }

        return "???";
    }
}
