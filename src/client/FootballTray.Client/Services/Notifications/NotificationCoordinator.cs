using System;
using System.Collections.Generic;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Audio;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Coordinates background match reminders for followed clubs and competitions,
/// dispatching native Windows toast notifications when fixtures approach kickoff.
/// </summary>
public sealed class NotificationCoordinator
{
    private readonly IPreferencesService _preferencesService;
    private readonly INotificationRenderer _renderer;
    private readonly HashSet<string> _firedReminders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public NotificationCoordinator(
        IPreferencesService preferencesService,
        INotificationRenderer? renderer = null)
    {
        _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        _renderer = renderer ?? new WindowsAppNotificationRenderer();
    }

    /// <summary>
    /// Evaluates scheduled matches in the tray payload against user reminder preferences.
    /// Fires a Windows toast notification if a followed match is within the kickoff offset window.
    /// </summary>
    public void ProcessKickoffReminders(TrayDataDto? trayData, UserPreferences? preferences = null)
    {
        try
        {
            if (trayData?.Matches == null || trayData.Matches.Count == 0) return;

            var prefs = preferences ?? _preferencesService.GetPreferences();
            if (!prefs.IsKickoffReminderEnabled) return;
            if (prefs.Notifications != null && !prefs.Notifications.IsTypeEnabled(NotificationType.Kickoff)) return;

            int offsetMinutes = prefs.KickoffReminderOffsetMinutes;
            var offsetSpan = TimeSpan.FromMinutes(offsetMinutes);
            var now = DateTimeOffset.UtcNow;
            var followedTeamIds = prefs.GetFollowedTeamIdSet();
            var followedCompIds = prefs.GetFollowedCompetitionIdSet();

            foreach (var match in trayData.Matches)
            {
                if (match == null || match.KickoffTime == default) continue;

                // Only evaluate prematch fixtures
                string stateUpper = (match.State ?? string.Empty).Trim().ToUpperInvariant();
                bool isPrematch = stateUpper is "PREMATCH" or "NS" or "TBD";
                if (!isPrematch) continue;

                // Check whether fixture involves a followed team or competition
                var relevance = FixtureRelevanceEngine.EvaluateRelevance(match, followedTeamIds, followedCompIds);
                if (!relevance.IsRelevant) continue;

                var kickoffTime = match.KickoffTime;
                var timeUntilKickoff = kickoffTime - now;

                // Trigger if kickoff is within offset, but not more than 5 minutes overdue
                if (timeUntilKickoff <= offsetSpan && timeUntilKickoff >= -TimeSpan.FromMinutes(5))
                {
                    string matchKey = !string.IsNullOrWhiteSpace(match.Id) ? match.Id : $"{match.HomeTeam?.Name}_{match.AwayTeam?.Name}_{kickoffTime.ToUnixTimeSeconds()}";
                    string dedupeKey = $"{matchKey}_{offsetMinutes}";

                    lock (_lock)
                    {
                        if (_firedReminders.Contains(dedupeKey)) continue;
                        _firedReminders.Add(dedupeKey);
                    }

                    string homeName = match.HomeTeam?.Name ?? "Home";
                    string awayName = match.AwayTeam?.Name ?? "Away";
                    string localKickoff = kickoffTime.ToLocalTime().ToString("t");

                    string title = offsetMinutes switch
                    {
                        <= 0 => $"⏰ Kickoff Now: {homeName} vs {awayName}",
                        _ => $"⏰ Kickoff in {offsetMinutes}m: {homeName} vs {awayName}"
                    };

                    string body = offsetMinutes switch
                    {
                        <= 0 => $"{homeName} vs {awayName} is kicking off now ({localKickoff})!",
                        _ => $"{homeName} vs {awayName} kicks off in {offsetMinutes} minutes ({localKickoff})."
                    };

                    var payload = new NotificationPayload
                    {
                        Title = title,
                        Body = body,
                        ActivationArguments = $"action=view_match&matchId={match.Id ?? string.Empty}",
                        DeduplicationKey = dedupeKey,
                        Type = NotificationType.Kickoff,
                        FixtureId = match.Id ?? string.Empty
                    };

                    AppLogger.Info("Notifications", "KickoffReminderTriggered", $"Dispatching kickoff reminder for {homeName} vs {awayName} (offset: {offsetMinutes}m)");

                    try
                    {
                        bool rendered = _renderer.RenderNotification(payload);
                        if (!rendered)
                        {
                            AppLogger.Warn("Notifications", "ToastNotRendered", $"Toast renderer returned false for {homeName} vs {awayName} (permission or environment limitation).");
                        }

                        if (prefs.IsAudioEnabled)
                        {
                            AudioPlaybackService.Instance.PlaySound(NotificationType.Kickoff);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Gracefully fail if OS notifications are denied or system toast service is unavailable
                        AppLogger.Warn("Notifications", "ToastDispatchException", $"Failed to display Windows notification: {ex.Message}", ex: ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Notifications", "CoordinatorExecutionError", $"Error in kickoff reminder check: {ex.Message}", ex: ex);
        }
    }
}
