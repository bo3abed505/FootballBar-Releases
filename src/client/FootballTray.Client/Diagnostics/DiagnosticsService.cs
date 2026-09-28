using System;
using System.Collections.Generic;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Aggregates subsystem state into safe point-in-time diagnostic snapshots.
/// </summary>
public class DiagnosticsService : IDiagnosticsService
{
    private readonly ISyncService? _syncService;
    private readonly IPreferencesService? _preferencesService;
    private readonly LocalCacheService? _cacheService;
    private readonly DateTimeOffset _startupTime;

    private string _taskbarHostStatus = "Unattached";
    private string _notificationRegistrationStatus = "Unknown";
    private string? _lastErrorSummary;
    private readonly object _lock = new();

    public DateTimeOffset StartupTime => _startupTime;

    public DiagnosticsService(
        ISyncService? syncService = null,
        IPreferencesService? preferencesService = null,
        LocalCacheService? cacheService = null)
    {
        _syncService = syncService;
        _preferencesService = preferencesService;
        _cacheService = cacheService;
        _startupTime = DateTimeOffset.UtcNow;
    }

    public void RecordTaskbarStatus(string status)
    {
        lock (_lock)
        {
            _taskbarHostStatus = status;
        }
        AppLogger.Info("Taskbar", "StatusChanged", $"Taskbar host status updated: {status}");
    }

    public void RecordNotificationRegistrationStatus(string status)
    {
        lock (_lock)
        {
            _notificationRegistrationStatus = status;
        }
        AppLogger.Info("Notifications", "StatusChanged", $"Notification registration status updated: {status}");
    }

    public void RecordError(string component, string errorSummary, Exception? ex = null)
    {
        lock (_lock)
        {
            _lastErrorSummary = $"[{component}] {errorSummary}";
        }
        AppLogger.Error(component, "SubsystemError", errorSummary, null, ex);
    }

    public ClientHealthSnapshot GetCurrentSnapshot()
    {
        lock (_lock)
        {
            var prefs = _preferencesService?.GetPreferences() ?? new UserPreferences();
            var currentData = _syncService?.CurrentTrayData;

            var freshness = _syncService?.FreshnessState ??
                (currentData != null ? DataFreshnessState.Stale : DataFreshnessState.Empty);

            // Compute ApplicationHealthState
            var health = ApplicationHealthState.Healthy;
            if (_taskbarHostStatus.Contains("Recovering", StringComparison.OrdinalIgnoreCase))
            {
                health = ApplicationHealthState.Recovering;
            }
            else if (freshness == DataFreshnessState.Stale || _taskbarHostStatus.Contains("Unattached", StringComparison.OrdinalIgnoreCase))
            {
                health = ApplicationHealthState.Degraded;
            }
            else if (_taskbarHostStatus.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                     _notificationRegistrationStatus.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            {
                health = ApplicationHealthState.Degraded;
            }

            return new ClientHealthSnapshot
            {
                Version = AppVersionInfo.Version,
                Platform = "Windows 11 / Windows 10 (x64)",
                Architecture = "x64",
                StartupTime = _startupTime,
                LastSuccessfulServerResponseTime = _syncService?.LastSuccessfulServerResponseTime,
                LastRefreshAttempt = _syncService?.LastRefreshAttempt,
                FreshnessState = freshness,
                HealthState = health,
                CachedPayloadTimestamp = _syncService?.LastValidatedAt,
                HasETag = !string.IsNullOrWhiteSpace(_syncService?.LastKnownETag),
                FollowedTeamsCount = prefs.FollowedTeams.Count,
                FollowedCompetitionsCount = prefs.FollowedCompetitions.Count,
                NotificationsGloballyEnabled = prefs.Notifications.GlobalEnabled,
                TaskbarHostStatus = _taskbarHostStatus,
                NotificationRegistrationStatus = _notificationRegistrationStatus,
                LastErrorSummary = _lastErrorSummary,
                BackendEndpoint = FootballApiClient.GetDefaultBaseUrl()
            };
        }
    }

    public string GetSanitizedReportText()
    {
        return GetCurrentSnapshot().GenerateSanitizedReport();
    }

    public string GenerateReport() => GetSanitizedReportText();

    public IReadOnlyList<DiagnosticLogEntry> GetRecentLogEntries()
    {
        return AppLogger.GetRecentEntries();
    }
}
