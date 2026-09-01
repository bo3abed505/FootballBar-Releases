using System;
using System.Collections.Generic;
using System.Linq;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Core business logic engine for football match alert generation and change detection.
/// Evaluates state transitions and event arrival between consecutive data snapshots.
/// </summary>
public sealed class NotificationEngine
{
    // High defensive ceiling to guard against corrupted or pathological payloads while allowing all legitimate concurrent match events
    private const int MaxDefensiveThreshold = 50;
    private readonly NotificationDeduplicationStore _deduplicationStore;

    private static readonly HashSet<string> LiveStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIVE", "1H", "2H", "ET", "P", "BT", "IN_PLAY"
    };

    private static readonly HashSet<string> HalftimeStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "HALFTIME", "HT"
    };

    private static readonly HashSet<string> FinishedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "FINISHED", "FT", "AET", "PEN", "POSTMATCH"
    };

    private static readonly HashSet<string> PostponedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "POSTPONED", "SUSP", "INT"
    };

    private static readonly HashSet<string> CancelledStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "CANCELLED", "ABD", "AWD", "WO"
    };

    private static readonly HashSet<string> PrematchStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "PREMATCH", "NS", "TBD"
    };

    public NotificationDeduplicationStore DeduplicationStore => _deduplicationStore;

    public NotificationEngine(NotificationDeduplicationStore? deduplicationStore = null)
    {
        _deduplicationStore = deduplicationStore ?? new NotificationDeduplicationStore();
    }

    /// <summary>
    /// Evaluates previous vs current tray snapshots and derives non-duplicate notification candidates.
    /// When previous is null (startup baseline), records initial state without emitting alerts.
    /// </summary>
    public List<NotificationCandidate> EvaluateUpdates(TrayDataDto? previous, TrayDataDto? current)
    {
        if (current?.Matches == null || current.Matches.Count == 0)
        {
            return [];
        }

        // 1. Startup Baseline: populate deduplication store to prevent historical replay
        if (previous?.Matches == null)
        {
            EstablishBaseline(current);
            return [];
        }

        var candidates = new List<NotificationCandidate>();
        var prevMap = previous.Matches.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var curr in current.Matches)
        {
            prevMap.TryGetValue(curr.Id, out var prev);

            // A. State Transition Evaluation
            EvaluateStateTransitions(prev, curr, candidates);

            // B. Event-Based Goal Evaluation
            EvaluateGoalEvents(prev, curr, candidates);
        }

        // Defensive threshold cap specifically for pathological/corrupt payload storms
        if (candidates.Count > MaxDefensiveThreshold)
        {
            // Mark excess in deduplication store to avoid repeated loops, but cap immediate dispatch
            foreach (var excess in candidates.Skip(MaxDefensiveThreshold))
            {
                _deduplicationStore.MarkNotified(excess.DeduplicationKey);
            }
            return candidates.Take(MaxDefensiveThreshold).ToList();
        }

        return candidates;
    }

    private void EstablishBaseline(TrayDataDto current)
    {
        foreach (var match in current.Matches)
        {
            string state = (match.State ?? string.Empty).ToUpperInvariant();

            if (LiveStates.Contains(state) || HalftimeStates.Contains(state) || FinishedStates.Contains(state))
            {
                _deduplicationStore.MarkNotified($"match:{match.Id}:started");
            }
            if (HalftimeStates.Contains(state))
            {
                _deduplicationStore.MarkNotified($"match:{match.Id}:halftime");
            }
            if (FinishedStates.Contains(state))
            {
                _deduplicationStore.MarkNotified($"match:{match.Id}:finished");
            }
            if (PostponedStates.Contains(state))
            {
                _deduplicationStore.MarkNotified($"match:{match.Id}:postponed");
            }
            if (CancelledStates.Contains(state))
            {
                _deduplicationStore.MarkNotified($"match:{match.Id}:cancelled");
            }

            if (match.RecentEvents != null)
            {
                foreach (var ev in match.RecentEvents)
                {
                    if (IsGoalEvent(ev) && !string.IsNullOrWhiteSpace(ev.Id))
                    {
                        _deduplicationStore.MarkNotified($"match:{match.Id}:event:{ev.Id}");
                    }
                }
            }
        }
    }

    private void EvaluateStateTransitions(TrayMatchDto? prev, TrayMatchDto curr, List<NotificationCandidate> candidates)
    {
        string prevState = prev?.State?.ToUpperInvariant() ?? string.Empty;
        string currState = curr.State?.ToUpperInvariant() ?? string.Empty;

        if (string.Equals(prevState, currState, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 1. MATCH_STARTED: (PREMATCH / uninitialized) -> LIVE
        if ((PrematchStates.Contains(prevState) || string.IsNullOrEmpty(prevState)) && LiveStates.Contains(currState))
        {
            string key = $"match:{curr.Id}:started";
            if (!_deduplicationStore.IsAlreadyNotified(key))
            {
                candidates.Add(CreateCandidate(curr, NotificationType.MatchStarted, key, prevState, curr.State ?? "LIVE"));
            }
        }

        // 2. HALF_TIME: LIVE -> HALFTIME
        if (LiveStates.Contains(prevState) && HalftimeStates.Contains(currState))
        {
            string key = $"match:{curr.Id}:halftime";
            if (!_deduplicationStore.IsAlreadyNotified(key))
            {
                candidates.Add(CreateCandidate(curr, NotificationType.HalfTime, key, prevState, curr.State ?? "HALFTIME"));
            }
        }

        // 3. MATCH_FINISHED: (LIVE / HALFTIME / PREMATCH) -> FINISHED
        if (!FinishedStates.Contains(prevState) && FinishedStates.Contains(currState))
        {
            string key = $"match:{curr.Id}:finished";
            if (!_deduplicationStore.IsAlreadyNotified(key))
            {
                candidates.Add(CreateCandidate(curr, NotificationType.MatchFinished, key, prevState, curr.State ?? "FINISHED"));
            }
        }

        // 4. MATCH_POSTPONED: Non-postponed -> POSTPONED
        if (!PostponedStates.Contains(prevState) && PostponedStates.Contains(currState))
        {
            string key = $"match:{curr.Id}:postponed";
            if (!_deduplicationStore.IsAlreadyNotified(key))
            {
                candidates.Add(CreateCandidate(curr, NotificationType.MatchPostponed, key, prevState, curr.State ?? "POSTPONED"));
            }
        }

        // 5. MATCH_CANCELLED: Non-cancelled -> CANCELLED
        if (!CancelledStates.Contains(prevState) && CancelledStates.Contains(currState))
        {
            string key = $"match:{curr.Id}:cancelled";
            if (!_deduplicationStore.IsAlreadyNotified(key))
            {
                candidates.Add(CreateCandidate(curr, NotificationType.MatchCancelled, key, prevState, curr.State ?? "CANCELLED"));
            }
        }

        // 6. MATCH_RESUMED: POSTPONED -> LIVE / PREMATCH
        if (PostponedStates.Contains(prevState) && (LiveStates.Contains(currState) || PrematchStates.Contains(currState)))
        {
            string key = $"match:{curr.Id}:resumed";
            if (!_deduplicationStore.IsAlreadyNotified(key))
            {
                candidates.Add(CreateCandidate(curr, NotificationType.MatchResumed, key, prevState, curr.State ?? "LIVE"));
            }
        }
    }

    private void EvaluateGoalEvents(TrayMatchDto? prev, TrayMatchDto curr, List<NotificationCandidate> candidates)
    {
        if (curr.RecentEvents == null || curr.RecentEvents.Count == 0)
        {
            return;
        }

        var prevEventIds = prev?.RecentEvents != null
            ? new HashSet<string>(prev.RecentEvents.Select(e => e.Id), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in curr.RecentEvents)
        {
            if (!IsGoalEvent(ev))
            {
                continue;
            }

            // Event must be genuinely newly observed
            if (prevEventIds.Contains(ev.Id))
            {
                continue;
            }

            string key = $"match:{curr.Id}:event:{ev.Id}";
            if (_deduplicationStore.IsAlreadyNotified(key))
            {
                continue;
            }

            candidates.Add(new NotificationCandidate
            {
                FixtureId = curr.Id,
                Type = NotificationType.Goal,
                DeduplicationKey = key,
                EventUid = ev.Id,
                PreviousState = prev?.State,
                NewState = curr.State ?? "LIVE",
                HomeTeamName = curr.HomeTeam?.Name ?? curr.HomeTeam?.ShortName ?? "Home",
                AwayTeamName = curr.AwayTeam?.Name ?? curr.AwayTeam?.ShortName ?? "Away",
                CompetitionId = curr.Competition?.Id,
                HomeTeamId = curr.HomeTeam?.Id,
                AwayTeamId = curr.AwayTeam?.Id,
                HomeScore = curr.Score?.Home ?? 0,
                AwayScore = curr.Score?.Away ?? 0,
                ElapsedMinute = ev.Minute > 0 ? ev.Minute : curr.ElapsedMinute,
                ExtraMinute = ev.ExtraMinute > 0 ? ev.ExtraMinute : curr.ExtraMinute,
                EventPlayerName = ev.PlayerName,
                EventDescription = ev.Description
            });
        }
    }

    private static bool IsGoalEvent(TrayEventDto ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Type)) return false;
        var t = ev.Type.Trim();
        return t.Equals("Goal", StringComparison.OrdinalIgnoreCase)
            || t.Equals("OwnGoal", StringComparison.OrdinalIgnoreCase)
            || t.Equals("PenaltyGoal", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Goal", StringComparison.OrdinalIgnoreCase);
    }

    private static NotificationCandidate CreateCandidate(
        TrayMatchDto match,
        NotificationType type,
        string key,
        string? prevState,
        string newState)
    {
        return new NotificationCandidate
        {
            FixtureId = match.Id,
            Type = type,
            DeduplicationKey = key,
            PreviousState = prevState,
            NewState = newState,
            HomeTeamName = match.HomeTeam?.Name ?? match.HomeTeam?.ShortName ?? "Home",
            AwayTeamName = match.AwayTeam?.Name ?? match.AwayTeam?.ShortName ?? "Away",
            CompetitionId = match.Competition?.Id,
            HomeTeamId = match.HomeTeam?.Id,
            AwayTeamId = match.AwayTeam?.Id,
            HomeScore = match.Score?.Home ?? 0,
            AwayScore = match.Score?.Away ?? 0,
            ElapsedMinute = match.ElapsedMinute,
            ExtraMinute = match.ExtraMinute
        };
    }
}
