using System;
using System.Linq;
using FootballTray.Core.Dto;
using FootballTray.Client.Services.Preferences;

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
    /// When an explicit pinnedMatchId is provided, it is returned unconditionally if present in tray data.
    /// When user preferences are provided, prioritizes followed live/upcoming matches.
    /// Priority order without preferences:
    /// 0. Explicitly pinned match (regardless of follow status or state).
    /// 1. First LIVE / HALFTIME / in-play match.
    /// 2. Nearest upcoming PREMATCH match by kickoff time.
    /// 3. First available match (e.g. FINISHED).
    /// 4. Null if empty.
    /// </summary>
    public static TrayMatchDto? GetActiveMatch(TrayDataDto? trayData, UserPreferences? preferences = null, string? pinnedMatchId = null)
    {
        if (trayData == null || trayData.Matches == null || trayData.Matches.Count == 0)
        {
            return null;
        }

        // 0. Explicitly pinned match takes absolute precedence
        if (!string.IsNullOrEmpty(pinnedMatchId))
        {
            var pinned = trayData.Matches.FirstOrDefault(m => string.Equals(m.Id, pinnedMatchId, StringComparison.Ordinal));
            if (pinned != null)
            {
                return pinned;
            }
        }

        if (preferences != null)
        {
            return FixtureRelevanceEngine.PrioritizeMatches(trayData.Matches, preferences).FirstOrDefault();
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
    /// Maps DataFreshnessState to corresponding TickerConnectionState.
    /// </summary>
    public static TickerConnectionState MapConnectionState(DataFreshnessState freshnessState) =>
        freshnessState switch
        {
            DataFreshnessState.Fresh => TickerConnectionState.OnlineFresh,
            DataFreshnessState.NotModified => TickerConnectionState.OnlineFresh,
            DataFreshnessState.Stale => TickerConnectionState.OfflineCached,
            DataFreshnessState.Empty => TickerConnectionState.NoCacheOffline,
            _ => TickerConnectionState.NoCacheOffline,
        };

    /// <summary>
    /// Evaluates candidate matches for ticker presentation and cycling.
    /// If an explicit pinnedMatchId is provided, candidate set contains only the pinned match.
    /// If >= 1 LIVE match exists:
    ///   candidate set = ALL LIVE matches in existing FixtureRelevanceEngine priority order.
    /// If 0 LIVE matches exist:
    ///   candidate set = ONLY the single top-priority match (or empty if no matches exist).
    /// </summary>
    public static IReadOnlyList<TrayMatchDto> GetTickerCandidates(TrayDataDto? trayData, UserPreferences? preferences = null, string? pinnedMatchId = null)
    {
        if (trayData?.Matches == null || trayData.Matches.Count == 0)
        {
            return Array.Empty<TrayMatchDto>();
        }

        // Explicitly pinned match is the sole candidate when set
        if (!string.IsNullOrEmpty(pinnedMatchId))
        {
            var pinned = trayData.Matches.FirstOrDefault(m => string.Equals(m.Id, pinnedMatchId, StringComparison.Ordinal));
            if (pinned != null)
            {
                return new List<TrayMatchDto> { pinned };
            }
        }

        var prioritized = FixtureRelevanceEngine.PrioritizeMatches(trayData.Matches, preferences);
        if (prioritized == null || prioritized.Count == 0)
        {
            return Array.Empty<TrayMatchDto>();
        }

        var liveCandidates = prioritized.Where(FixtureRelevanceEngine.IsLive).ToList();
        if (liveCandidates.Count > 0)
        {
            return liveCandidates;
        }

        return prioritized.Take(1).ToList();
    }

    /// <summary>
    /// Formats a single selected match into a compact ticker string based on data freshness state.
    /// </summary>
    public static string FormatMatchTicker(TrayMatchDto? match, DataFreshnessState freshnessState, UserPreferences? preferences = null)
    {
        return FormatMatchTicker(match, MapConnectionState(freshnessState), preferences);
    }

    /// <summary>
    /// Formats a single selected match into a compact ticker string based on connection state.
    /// Preserves existing fallback and offline indication behavior.
    /// </summary>
    public static string FormatMatchTicker(TrayMatchDto? match, TickerConnectionState connectionState, UserPreferences? preferences = null)
    {
        if (connectionState == TickerConnectionState.NoCacheOffline)
        {
            return "FootballTray · offline";
        }

        if (match == null)
        {
            return connectionState == TickerConnectionState.OfflineCached
                ? "FootballTray · offline"
                : "FootballTray";
        }

        return FormatMatch(match, connectionState, preferences);
    }

    /// <summary>
    /// Formats the current TrayDataDto into a compact taskbar ticker string based on data freshness state and user preferences.
    /// </summary>
    public static string FormatTicker(TrayDataDto? trayData, DataFreshnessState freshnessState, UserPreferences? preferences = null, string? pinnedMatchId = null)
    {
        return FormatTicker(trayData, MapConnectionState(freshnessState), preferences, pinnedMatchId);
    }

    /// <summary>
    /// Formats the current TrayDataDto into a compact taskbar ticker string.
    /// </summary>
    public static string FormatTicker(TrayDataDto? trayData, TickerConnectionState connectionState, UserPreferences? preferences = null, string? pinnedMatchId = null)
    {
        var activeMatch = GetActiveMatch(trayData, preferences, pinnedMatchId);
        return FormatMatchTicker(activeMatch, connectionState, preferences);
    }

    private static string FormatMatch(TrayMatchDto match, TickerConnectionState connectionState, UserPreferences? preferences = null)
    {
        var homeCode = GetTeamCode(match.HomeTeam);
        var awayCode = GetTeamCode(match.AwayTeam);

        string mainText;

        if (string.Equals(match.State, "LIVE", StringComparison.OrdinalIgnoreCase))
        {
            var minuteText = match.ExtraMinute > 0
                ? $"{match.ElapsedMinute}+{match.ExtraMinute}'"
                : $"{match.ElapsedMinute}'";
            mainText = $"{homeCode} {match.Score.Home}–{match.Score.Away} {awayCode} {minuteText}";
        }
        else if (string.Equals(match.State, "HALFTIME", StringComparison.OrdinalIgnoreCase))
        {
            mainText = $"{homeCode} {match.Score.Home}–{match.Score.Away} {awayCode} HT";
        }
        else if (string.Equals(match.State, "PREMATCH", StringComparison.OrdinalIgnoreCase))
        {
            var timeFormat = (preferences?.Use24HourFormat ?? true) ? "HH:mm" : "h:mm tt";
            var localTime = match.KickoffTime.ToLocalTime().ToString(timeFormat);
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
