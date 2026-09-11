using System;
using FootballTray.Core;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Immutable outcome of an update discovery check.
/// </summary>
public sealed record UpdateCheckResult
{
    public required UpdateCheckState State { get; init; }
    public ReleaseManifest? OfferedManifest { get; init; }
    public SemanticVersion? CurrentVersion { get; init; }
    public SemanticVersion? TargetVersion { get; init; }
    public string? Message { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset CheckedAt { get; init; }

    public bool IsUpdateAvailable => State == UpdateCheckState.UpdateAvailable;

    public static UpdateCheckResult Available(
        SemanticVersion current,
        SemanticVersion target,
        ReleaseManifest manifest,
        string? etag,
        DateTimeOffset checkedAt) =>
        new()
        {
            State = UpdateCheckState.UpdateAvailable,
            CurrentVersion = current,
            TargetVersion = target,
            OfferedManifest = manifest,
            ETag = etag,
            CheckedAt = checkedAt,
            Message = $"A newer version v{target} is available (current: v{current})."
        };

    public static UpdateCheckResult UpToDate(
        SemanticVersion current,
        SemanticVersion? target,
        string message,
        string? etag,
        DateTimeOffset checkedAt) =>
        new()
        {
            State = UpdateCheckState.UpToDate,
            CurrentVersion = current,
            TargetVersion = target,
            ETag = etag,
            CheckedAt = checkedAt,
            Message = message
        };

    public static UpdateCheckResult Failed(
        string failureReason,
        DateTimeOffset checkedAt,
        SemanticVersion? current = null) =>
        new()
        {
            State = UpdateCheckState.CheckFailed,
            CurrentVersion = current,
            CheckedAt = checkedAt,
            Message = failureReason
        };
}
