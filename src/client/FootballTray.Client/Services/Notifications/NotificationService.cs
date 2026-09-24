using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Audio;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Coordinates match alert evaluation, preference filtering, deduplication, formatting, and notification dispatch.
/// Completely decoupled from network polling.
/// </summary>
public sealed class NotificationService
{
    private readonly NotificationEngine _engine;
    private readonly INotificationRenderer _renderer;
    private readonly NotificationDeduplicationStore _store;
    private readonly IAudioPlaybackService? _audioService;

    public NotificationEngine Engine => _engine;
    public NotificationDeduplicationStore Store => _store;
    public INotificationRenderer Renderer => _renderer;
    public IAudioPlaybackService? AudioService => _audioService;

    public event Action<NotificationPayload>? NotificationDispatched;

    public NotificationService(
        NotificationEngine? engine = null,
        INotificationRenderer? renderer = null,
        NotificationDeduplicationStore? store = null,
        IAudioPlaybackService? audioService = null)
    {
        _store = store ?? engine?.DeduplicationStore ?? new NotificationDeduplicationStore();
        _engine = engine ?? new NotificationEngine(_store);
        _renderer = renderer ?? new WindowsAppNotificationRenderer();
        _audioService = audioService;
    }

    /// <summary>
    /// Loads deduplication history from disk at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Backward-compatible overload without preferences.
    /// </summary>
    public Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        CancellationToken cancellationToken = default)
    {
        return ProcessTrayUpdateAsync(previous, current, null, cancellationToken);
    }

    /// <summary>
    /// Processes snapshot transitions, filters against user following preferences & category settings,
    /// emits alerts for eligible new events/transitions, and persists deduplication state.
    /// </summary>
    public async Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        UserPreferences? preferences,
        CancellationToken cancellationToken = default)
    {
        if (current?.Matches == null)
        {
            return [];
        }

        var candidates = _engine.EvaluateUpdates(previous, current);
        var dispatchedPayloads = new List<NotificationPayload>();

        HashSet<string>? followedTeamIds = preferences?.GetFollowedTeamIdSet();
        HashSet<string>? followedCompIds = preferences?.GetFollowedCompetitionIdSet();

        AppLogger.Debug("Notifications", "CandidatesEvaluated", $"Evaluated {candidates.Count} candidate alerts from snapshot transition.");

        foreach (var candidate in candidates)
        {
            // Record in deduplication store regardless of preference filtering
            // so that if a category/following is re-enabled later, old events do NOT replay.
            _store.MarkNotified(candidate.DeduplicationKey);

            // 1. Check Global Enablement
            if (preferences != null && !preferences.Notifications.GlobalEnabled)
            {
                AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Notifications globally disabled.");
                continue;
            }

            // 2. Check Category / Type Enablement
            if (preferences != null && !preferences.Notifications.IsTypeEnabled(candidate.Type))
            {
                AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Category '{candidate.Type}' disabled.");
                continue;
            }

            // 3. Check Following Relevance (team or competition)
            if (preferences != null)
            {
                bool homeFollowed = followedTeamIds != null && (
                    (!string.IsNullOrWhiteSpace(candidate.HomeTeamId) && followedTeamIds.Contains(candidate.HomeTeamId)) ||
                    (!string.IsNullOrWhiteSpace(candidate.HomeTeamLegacyId) && followedTeamIds.Contains(candidate.HomeTeamLegacyId)));

                bool awayFollowed = followedTeamIds != null && (
                    (!string.IsNullOrWhiteSpace(candidate.AwayTeamId) && followedTeamIds.Contains(candidate.AwayTeamId)) ||
                    (!string.IsNullOrWhiteSpace(candidate.AwayTeamLegacyId) && followedTeamIds.Contains(candidate.AwayTeamLegacyId)));

                bool compFollowed = followedCompIds != null && (
                    (!string.IsNullOrWhiteSpace(candidate.CompetitionId) && followedCompIds.Contains(candidate.CompetitionId)) ||
                    (!string.IsNullOrWhiteSpace(candidate.CompetitionLegacyId) && followedCompIds.Contains(candidate.CompetitionLegacyId)));

                if (!homeFollowed && !awayFollowed && !compFollowed)
                {
                    AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Match entities not followed.");
                    continue; // Suppress notification for unfollowed match
                }
            }

            var payload = NotificationPayloadFormatter.Format(candidate);

            try
            {
                // Render alert through injected renderer
                _renderer.RenderNotification(payload);
                AppLogger.Info("Notifications", "NotificationRendered", $"Dispatched alert: [{payload.Type}] '{payload.Title}' - '{payload.Body}'");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Notifications", "NotificationRenderError", $"Failed to render notification for '{candidate.DeduplicationKey}': {ex.Message}", ex: ex);
            }

            if (_audioService != null)
            {
                try
                {
                    _audioService.PlaySound(payload.Type);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("Notifications", "AudioPlaybackError", $"Audio playback failed for [{payload.Type}]: {ex.Message}");
                }
            }

            dispatchedPayloads.Add(payload);
            NotificationDispatched?.Invoke(payload);
        }

        if (candidates.Count > 0)
        {
            await _store.SaveAsync(cancellationToken);
        }

        return dispatchedPayloads;
    }
}
