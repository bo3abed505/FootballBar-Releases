using System;
using System.Text;
using FootballTray.Client.Services;
using FootballTray.Core;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Point-in-time diagnostic snapshot of the FootballTray client.
/// Contains zero secrets, passwords, or raw auth headers.
/// </summary>
public record ClientHealthSnapshot
{
    public string Version { get; init; } = AppVersionInfo.Version;
    public string Platform { get; init; } = "Windows 11 / Windows 10 (x64)";
    public string Architecture { get; init; } = "x64";
    public DateTimeOffset StartupTime { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSuccessfulServerResponseTime { get; init; }
    public DateTimeOffset? LastRefreshAttempt { get; init; }
    public DataFreshnessState FreshnessState { get; init; } = DataFreshnessState.Empty;
    public ApplicationHealthState HealthState { get; init; } = ApplicationHealthState.Healthy;
    public DateTimeOffset? CachedPayloadTimestamp { get; init; }
    public bool HasETag { get; init; }
    public int FollowedTeamsCount { get; init; }
    public int FollowedCompetitionsCount { get; init; }
    public bool NotificationsGloballyEnabled { get; init; } = true;
    public string TaskbarHostStatus { get; init; } = "Unknown";
    public string NotificationRegistrationStatus { get; init; } = "Unknown";
    public string? LastErrorSummary { get; init; }
    public string BackendEndpoint { get; init; } = FootballApiClient.DefaultBaseUrl;

    /// <summary>
    /// Generates a standardized, sanitized text report suitable for clipboard copying and troubleshooting.
    /// </summary>
    public string GenerateSanitizedReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FootballTray Diagnostics");
        sb.AppendLine("============================================================");
        sb.AppendLine($"Version: {Version}");
        sb.AppendLine($"Platform: {Platform}");
        sb.AppendLine($"Architecture: {Architecture}");
        sb.AppendLine($"Health State: {HealthState}");
        sb.AppendLine($"Data State: {FreshnessState}");
        sb.AppendLine($"Uptime: {DateTimeOffset.UtcNow - StartupTime:hh\\:mm\\:ss}");
        sb.AppendLine($"Backend Endpoint: {AppLogger.Sanitize(BackendEndpoint)}");
        sb.AppendLine($"Last Refresh Attempt: {(LastRefreshAttempt.HasValue ? LastRefreshAttempt.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "None")}");
        sb.AppendLine($"Last Successful Sync: {(LastSuccessfulServerResponseTime.HasValue ? LastSuccessfulServerResponseTime.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "None")}");
        sb.AppendLine($"Cached Data Timestamp: {(CachedPayloadTimestamp.HasValue ? CachedPayloadTimestamp.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "None")}");
        sb.AppendLine($"ETag Available: {(HasETag ? "Yes" : "No")}");
        sb.AppendLine($"Followed Entities: {FollowedTeamsCount} teams, {FollowedCompetitionsCount} competitions");
        sb.AppendLine($"Notifications: {(NotificationsGloballyEnabled ? "Enabled" : "Disabled")} ({NotificationRegistrationStatus})");
        sb.AppendLine($"Taskbar Host: {TaskbarHostStatus}");
        sb.AppendLine();
        sb.AppendLine("Subsystem Status:");
        sb.AppendLine($"- Local Cache: {(CachedPayloadTimestamp.HasValue ? "Active" : "Empty")}");
        sb.AppendLine($"- Taskbar Receiver: {TaskbarHostStatus}");
        sb.AppendLine($"- Notification Engine: {NotificationRegistrationStatus}");
        sb.AppendLine();
        sb.AppendLine("Errors / Warnings:");
        if (string.IsNullOrWhiteSpace(LastErrorSummary))
        {
            sb.AppendLine("- None");
        }
        else
        {
            sb.AppendLine($"- {AppLogger.Sanitize(LastErrorSummary)}");
        }
        sb.AppendLine("============================================================");

        return sb.ToString();
    }
}
