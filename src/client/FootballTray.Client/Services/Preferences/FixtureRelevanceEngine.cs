using System;
using System.Collections.Generic;
using System.Linq;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Relevance result describing whether a match is followed by team or competition with human-readable rationale.
/// </summary>
public sealed record FixtureRelevanceResult
{
    public bool IsRelevant => FollowedByTeam || FollowedByCompetition;
    public bool FollowedByTeam { get; init; }
    public bool FollowedByCompetition { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure domain engine evaluating fixture relevance against user preferences and prioritizing presentation ordering.
/// </summary>
public static class FixtureRelevanceEngine
{
    private static readonly HashSet<string> LiveStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIVE", "1H", "2H", "ET", "P", "BT", "IN_PLAY", "HALFTIME", "HT", "EXTRA_TIME", "PENALTIES", "SUSP", "INT"
    };

    private static readonly HashSet<string> FinishedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "FT", "AET", "PEN", "FINISHED"
    };

    private static readonly HashSet<string> PrematchStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "PREMATCH", "NS", "TBD"
    };

    /// <summary>
    /// Evaluates if a given match is relevant based on followed teams or followed competitions.
    /// Uses O(1) set lookups on stable IDs.
    /// </summary>
    public static FixtureRelevanceResult EvaluateRelevance(TrayMatchDto match, UserPreferences? preferences)
    {
        if (match == null || preferences == null)
        {
            return new FixtureRelevanceResult();
        }

        var teamIdSet = preferences.GetFollowedTeamIdSet();
        var compIdSet = preferences.GetFollowedCompetitionIdSet();

        return EvaluateRelevance(match, teamIdSet, compIdSet);
    }

    /// <summary>
    /// Evaluates relevance using pre-allocated lookup sets for high-throughput batch evaluation.
    /// </summary>
    public static FixtureRelevanceResult EvaluateRelevance(
        TrayMatchDto match,
        HashSet<string> followedTeamIds,
        HashSet<string> followedCompetitionIds)
    {
        if (match == null)
        {
            return new FixtureRelevanceResult();
        }

        bool homeFollowed = (!string.IsNullOrWhiteSpace(match.HomeTeam?.Id) && followedTeamIds.Contains(match.HomeTeam.Id))
            || (!string.IsNullOrWhiteSpace(match.HomeTeam?.LegacyId) && followedTeamIds.Contains(match.HomeTeam.LegacyId))
            || (!string.IsNullOrWhiteSpace(match.HomeTeam?.Name) && followedTeamIds.Contains(match.HomeTeam.Name))
            || (CanonicalEntityIds.TryTranslateLegacyId(match.HomeTeam?.Id, out var homeCanon) && followedTeamIds.Contains(homeCanon))
            || (CanonicalEntityIds.TryTranslateLegacyId(match.HomeTeam?.LegacyId, out var homeLegCanon) && followedTeamIds.Contains(homeLegCanon));

        bool awayFollowed = (!string.IsNullOrWhiteSpace(match.AwayTeam?.Id) && followedTeamIds.Contains(match.AwayTeam.Id))
            || (!string.IsNullOrWhiteSpace(match.AwayTeam?.LegacyId) && followedTeamIds.Contains(match.AwayTeam.LegacyId))
            || (!string.IsNullOrWhiteSpace(match.AwayTeam?.Name) && followedTeamIds.Contains(match.AwayTeam.Name))
            || (CanonicalEntityIds.TryTranslateLegacyId(match.AwayTeam?.Id, out var awayCanon) && followedTeamIds.Contains(awayCanon))
            || (CanonicalEntityIds.TryTranslateLegacyId(match.AwayTeam?.LegacyId, out var awayLegCanon) && followedTeamIds.Contains(awayLegCanon));

        bool compFollowed = (!string.IsNullOrWhiteSpace(match.Competition?.Id) && followedCompetitionIds.Contains(match.Competition.Id))
            || (!string.IsNullOrWhiteSpace(match.Competition?.LegacyId) && followedCompetitionIds.Contains(match.Competition.LegacyId))
            || (!string.IsNullOrWhiteSpace(match.Competition?.Name) && followedCompetitionIds.Contains(match.Competition.Name))
            || (CanonicalEntityIds.TryTranslateLegacyId(match.Competition?.Id, out var compCanon) && followedCompetitionIds.Contains(compCanon))
            || (CanonicalEntityIds.TryTranslateLegacyId(match.Competition?.LegacyId, out var compLegCanon) && followedCompetitionIds.Contains(compLegCanon));

        bool followedByTeam = homeFollowed || awayFollowed;
        var reasons = new List<string>();

        if (homeFollowed && match.HomeTeam != null)
        {
            reasons.Add($"Followed team: {match.HomeTeam.Name}");
        }

        if (awayFollowed && match.AwayTeam != null)
        {
            reasons.Add($"Followed team: {match.AwayTeam.Name}");
        }

        if (compFollowed && match.Competition != null)
        {
            reasons.Add($"Followed competition: {match.Competition.Name}");
        }

        return new FixtureRelevanceResult
        {
            FollowedByTeam = followedByTeam,
            FollowedByCompetition = compFollowed,
            Reasons = reasons
        };
    }

    /// <summary>
    /// Prioritizes matches for presentation and taskbar ticker selection based on following status and live state.
    /// </summary>
    public static IReadOnlyList<TrayMatchDto> PrioritizeMatches(
        IEnumerable<TrayMatchDto>? matches,
        UserPreferences? preferences)
    {
        if (matches == null)
        {
            return Array.Empty<TrayMatchDto>();
        }

        var list = matches.ToList();
        if (list.Count <= 1)
        {
            return list;
        }

        var teamIds = preferences?.GetFollowedTeamIdSet() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compIds = preferences?.GetFollowedCompetitionIdSet() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return list
            .OrderBy(m => GetMatchPriorityRank(m, teamIds, compIds))
            .ThenByDescending(m => IsLive(m) ? m.ElapsedMinute : 0)
            .ThenBy(m => m.KickoffTime)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static int GetMatchPriorityRank(
        TrayMatchDto match,
        HashSet<string> teamIds,
        HashSet<string> compIds)
    {
        var rel = EvaluateRelevance(match, teamIds, compIds);
        bool live = IsLive(match);
        bool prematch = IsPrematch(match);

        // Tier 1: Followed team LIVE match
        if (rel.FollowedByTeam && live) return 1;

        // Tier 2: Followed team upcoming match
        if (rel.FollowedByTeam && prematch) return 2;

        // Tier 3: Followed competition LIVE match
        if (rel.FollowedByCompetition && live) return 3;

        // Tier 4: Followed competition upcoming match
        if (rel.FollowedByCompetition && prematch) return 4;

        // Tier 5: Any other LIVE match
        if (live) return 5;

        // Tier 6: Any other upcoming match
        if (prematch) return 6;

        // Tier 7: Followed team finished match
        if (rel.FollowedByTeam) return 7;

        // Tier 8: Followed competition finished match
        if (rel.FollowedByCompetition) return 8;

        // Tier 9: All other matches
        return 9;
    }

    public static bool IsLive(TrayMatchDto? match)
    {
        if (match == null) return false;
        string state = match.State?.Trim() ?? string.Empty;
        if (LiveStates.Contains(state)) return true;
        if (FinishedStates.Contains(state)) return false;
        bool hasActiveScore = match.Score != null && (match.Score.Home > 0 || match.Score.Away > 0);
        return hasActiveScore && match.KickoffTime <= DateTimeOffset.UtcNow.AddMinutes(15);
    }

    public static bool IsPrematch(TrayMatchDto? match)
    {
        return match != null && !string.IsNullOrWhiteSpace(match.State) && PrematchStates.Contains(match.State);
    }
}
