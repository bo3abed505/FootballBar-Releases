using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly FootballApiClient? _apiClient;

    public NotificationEngine Engine => _engine;
    public NotificationDeduplicationStore Store => _store;
    public INotificationRenderer Renderer => _renderer;
    public IAudioPlaybackService? AudioService => _audioService;
    public FootballApiClient? ApiClient => _apiClient;

    public event Action<NotificationPayload>? NotificationDispatched;

    public NotificationService(
        NotificationEngine? engine = null,
        INotificationRenderer? renderer = null,
        NotificationDeduplicationStore? store = null,
        IAudioPlaybackService? audioService = null,
        FootballApiClient? apiClient = null)
    {
        _store = store ?? engine?.DeduplicationStore ?? new NotificationDeduplicationStore();
        _engine = engine ?? new NotificationEngine(_store);
        _renderer = renderer ?? new WindowsAppNotificationRenderer();
        _audioService = audioService;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Loads deduplication history from disk at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Backward-compatible overload without preferences or pinned match.
    /// </summary>
    public Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        CancellationToken cancellationToken = default)
    {
        return ProcessTrayUpdateAsync(previous, current, null, null, cancellationToken);
    }

    /// <summary>
    /// Backward-compatible overload with preferences but without pinned match.
    /// </summary>
    public Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        UserPreferences? preferences,
        CancellationToken cancellationToken = default)
    {
        return ProcessTrayUpdateAsync(previous, current, preferences, null, cancellationToken);
    }

    /// <summary>
    /// Processes snapshot transitions, filters against user following preferences & category settings,
    /// emits alerts for eligible new events/transitions, and persists deduplication state.
    /// Pinned matches are unconditionally eligible for notifications.
    /// </summary>
    public async Task<List<NotificationPayload>> ProcessTrayUpdateAsync(
        TrayDataDto? previous,
        TrayDataDto? current,
        UserPreferences? preferences,
        string? pinnedMatchId,
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

            bool isPinnedMatch = false;
            if (!string.IsNullOrWhiteSpace(pinnedMatchId) && !string.IsNullOrWhiteSpace(candidate.FixtureId))
            {
                isPinnedMatch = string.Equals(candidate.FixtureId, pinnedMatchId, StringComparison.OrdinalIgnoreCase)
                    || candidate.FixtureId.EndsWith($"_{pinnedMatchId}", StringComparison.OrdinalIgnoreCase)
                    || pinnedMatchId.EndsWith($"_{candidate.FixtureId}", StringComparison.OrdinalIgnoreCase);
            }

            bool isInCustomQueue = preferences?.CustomTickerQueueMatchIds != null
                && !string.IsNullOrWhiteSpace(candidate.FixtureId)
                && preferences.CustomTickerQueueMatchIds.Contains(candidate.FixtureId);

            bool isDirectlyTargeted = isPinnedMatch || isInCustomQueue;

            // 1. Check Entity Alert Profiles (Muted / Custom / InheritGlobal)
            bool isEntityMuted = false;
            bool isEntityCustom = false;
            bool isEntityCustomAllowed = false;
            string? soundOverride = null;

            if (preferences != null)
            {
                var matchingProfiles = new List<FootballTray.Core.Dto.EntityAlertProfileDto>();
                if (preferences.TeamAlertProfiles != null)
                {
                    if (!string.IsNullOrWhiteSpace(candidate.HomeTeamId) && preferences.TeamAlertProfiles.TryGetValue(candidate.HomeTeamId, out var hp)) matchingProfiles.Add(hp);
                    else if (!string.IsNullOrWhiteSpace(candidate.HomeTeamLegacyId) && preferences.TeamAlertProfiles.TryGetValue(candidate.HomeTeamLegacyId, out var hlp)) matchingProfiles.Add(hlp);

                    if (!string.IsNullOrWhiteSpace(candidate.AwayTeamId) && preferences.TeamAlertProfiles.TryGetValue(candidate.AwayTeamId, out var ap)) matchingProfiles.Add(ap);
                    else if (!string.IsNullOrWhiteSpace(candidate.AwayTeamLegacyId) && preferences.TeamAlertProfiles.TryGetValue(candidate.AwayTeamLegacyId, out var alp)) matchingProfiles.Add(alp);
                }

                if (preferences.CompetitionAlertProfiles != null)
                {
                    if (!string.IsNullOrWhiteSpace(candidate.CompetitionId) && preferences.CompetitionAlertProfiles.TryGetValue(candidate.CompetitionId, out var cp)) matchingProfiles.Add(cp);
                    else if (!string.IsNullOrWhiteSpace(candidate.CompetitionLegacyId) && preferences.CompetitionAlertProfiles.TryGetValue(candidate.CompetitionLegacyId, out var clp)) matchingProfiles.Add(clp);
                }

                if (matchingProfiles.Count > 0)
                {
                    if (matchingProfiles.All(p => string.Equals(p.Mode, "Muted", StringComparison.OrdinalIgnoreCase)))
                    {
                        isEntityMuted = true;
                    }
                    else
                    {
                        var customProfiles = matchingProfiles.Where(p => string.Equals(p.Mode, "Custom", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (customProfiles.Count > 0)
                        {
                            isEntityCustom = true;
                            isEntityCustomAllowed = customProfiles.Any(p => p.IsTypeEnabled(candidate.Type.ToString()));
                        }
                    }

                    var profileWithSound = matchingProfiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.SoundOverride) && !string.Equals(p.SoundOverride, "InheritGlobal", StringComparison.OrdinalIgnoreCase));
                    if (profileWithSound != null)
                    {
                        soundOverride = profileWithSound.SoundOverride;
                    }
                }
            }

            if (isEntityMuted)
            {
                AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Muted by entity alert profile.");
                continue;
            }

            if (isEntityCustom && !isEntityCustomAllowed)
            {
                AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Custom entity profile disabled category '{candidate.Type}'.");
                continue;
            }

            if (!isEntityCustom)
            {
                // Check Global Enablement
                if (preferences != null && !preferences.Notifications.GlobalEnabled)
                {
                    AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Notifications globally disabled.");
                    continue;
                }

                // Check Category / Type Enablement
                if (preferences != null && !preferences.Notifications.IsTypeEnabled(candidate.Type))
                {
                    AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Category '{candidate.Type}' disabled.");
                    continue;
                }
            }

            // 3. Check Following Relevance (unconditionally eligible if pinned match or in custom queue)
            if (!isDirectlyTargeted && preferences != null)
            {
                bool hasFollowPreferences = (followedTeamIds != null && followedTeamIds.Count > 0) ||
                                            (followedCompIds != null && followedCompIds.Count > 0);

                if (hasFollowPreferences)
                {
                    bool homeFollowed = followedTeamIds != null && (
                        (!string.IsNullOrWhiteSpace(candidate.HomeTeamId) && followedTeamIds.Contains(candidate.HomeTeamId)) ||
                        (!string.IsNullOrWhiteSpace(candidate.HomeTeamLegacyId) && followedTeamIds.Contains(candidate.HomeTeamLegacyId)) ||
                        (!string.IsNullOrWhiteSpace(candidate.HomeTeamName) && followedTeamIds.Contains(candidate.HomeTeamName)) ||
                        (CanonicalEntityIds.TryTranslateLegacyId(candidate.HomeTeamId, out var homeCanon) && followedTeamIds.Contains(homeCanon)) ||
                        (CanonicalEntityIds.TryTranslateLegacyId(candidate.HomeTeamLegacyId, out var homeLegCanon) && followedTeamIds.Contains(homeLegCanon)));

                    bool awayFollowed = followedTeamIds != null && (
                        (!string.IsNullOrWhiteSpace(candidate.AwayTeamId) && followedTeamIds.Contains(candidate.AwayTeamId)) ||
                        (!string.IsNullOrWhiteSpace(candidate.AwayTeamLegacyId) && followedTeamIds.Contains(candidate.AwayTeamLegacyId)) ||
                        (!string.IsNullOrWhiteSpace(candidate.AwayTeamName) && followedTeamIds.Contains(candidate.AwayTeamName)) ||
                        (CanonicalEntityIds.TryTranslateLegacyId(candidate.AwayTeamId, out var awayCanon) && followedTeamIds.Contains(awayCanon)) ||
                        (CanonicalEntityIds.TryTranslateLegacyId(candidate.AwayTeamLegacyId, out var awayLegCanon) && followedTeamIds.Contains(awayLegCanon)));

                    bool compFollowed = followedCompIds != null && (
                        (!string.IsNullOrWhiteSpace(candidate.CompetitionId) && followedCompIds.Contains(candidate.CompetitionId)) ||
                        (!string.IsNullOrWhiteSpace(candidate.CompetitionLegacyId) && followedCompIds.Contains(candidate.CompetitionLegacyId)) ||
                        (CanonicalEntityIds.TryTranslateLegacyId(candidate.CompetitionId, out var compCanon) && followedCompIds.Contains(compCanon)) ||
                        (CanonicalEntityIds.TryTranslateLegacyId(candidate.CompetitionLegacyId, out var compLegCanon) && followedCompIds.Contains(compLegCanon)));

                    if (!homeFollowed && !awayFollowed && !compFollowed)
                    {
                        AppLogger.Debug("Notifications", "CandidateFiltered", $"Candidate '{candidate.DeduplicationKey}' suppressed: Match entities not followed.");
                        continue; // Suppress notification for unfollowed match
                    }
                }
            }

            // 4. On-demand goalscorer enrichment if goalscorer name is missing from candidate
            if (candidate.Type == NotificationType.Goal &&
                string.IsNullOrWhiteSpace(candidate.EventPlayerName) &&
                _apiClient != null &&
                !string.IsNullOrWhiteSpace(candidate.FixtureId))
            {
                int clockMinute = candidate.ElapsedMinute;
                try
                {
                    using var detailCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    detailCts.CancelAfter(TimeSpan.FromSeconds(3));

                    var detailResp = await _apiClient.GetMatchDetailsAsync(candidate.FixtureId, detailCts.Token);
                    var matchDetail = detailResp?.Envelope?.Data;
                    if (matchDetail?.Events != null && matchDetail.Events.Count > 0)
                    {
                        var latestGoal = matchDetail.Events
                            .Where(NotificationEngine.IsGoalEvent)
                            .OrderByDescending(e => e.Minute + (e.ExtraMinute > 0 ? e.ExtraMinute : 0))
                            .FirstOrDefault();

                        if (latestGoal != null)
                        {
                            if (!string.IsNullOrWhiteSpace(latestGoal.PlayerName))
                            {
                                candidate.EventPlayerName = latestGoal.PlayerName;
                            }
                            if (!string.IsNullOrWhiteSpace(latestGoal.AssistPlayerName))
                            {
                                candidate.AssistPlayerName = latestGoal.AssistPlayerName;
                            }
                            if (!string.IsNullOrWhiteSpace(latestGoal.Detail))
                            {
                                candidate.EventDetail = latestGoal.Detail;
                            }
                            if (string.IsNullOrWhiteSpace(candidate.ScoringTeamName) && !string.IsNullOrWhiteSpace(latestGoal.TeamId))
                            {
                                if (string.Equals(latestGoal.TeamId, candidate.HomeTeamId, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(latestGoal.TeamId, candidate.HomeTeamLegacyId, StringComparison.OrdinalIgnoreCase))
                                {
                                    candidate.ScoringTeamName = candidate.HomeTeamName;
                                }
                                else if (string.Equals(latestGoal.TeamId, candidate.AwayTeamId, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(latestGoal.TeamId, candidate.AwayTeamLegacyId, StringComparison.OrdinalIgnoreCase))
                                {
                                    candidate.ScoringTeamName = candidate.AwayTeamName;
                                }
                            }
                            if (latestGoal.Minute > 0)
                            {
                                candidate.ElapsedMinute = latestGoal.Minute;
                                candidate.ExtraMinute = latestGoal.ExtraMinute;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("Notifications", "ScorerEnrichmentFallback", $"On-demand scorer enrichment for {candidate.FixtureId} timed out or failed: {ex.Message}");
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

            if (_audioService != null && !string.Equals(soundOverride, "Mute", StringComparison.OrdinalIgnoreCase))
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
