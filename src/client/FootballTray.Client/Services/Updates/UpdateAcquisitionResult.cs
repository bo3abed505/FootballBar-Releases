using System;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Immutable outcome of an update package acquisition attempt.
/// </summary>
public sealed record UpdateAcquisitionResult
{
    public required UpdateAcquisitionState State { get; init; }
    public string? Version { get; init; }
    public string? LocalPackagePath { get; init; }
    public long BytesReceived { get; init; }
    public long TotalExpectedBytes { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset CompletedAt { get; init; }

    public bool IsVerified => State == UpdateAcquisitionState.Verified;

    public static UpdateAcquisitionResult Verified(
        string version,
        string localPackagePath,
        long sizeBytes,
        DateTimeOffset completedAt) =>
        new()
        {
            State = UpdateAcquisitionState.Verified,
            Version = version,
            LocalPackagePath = localPackagePath,
            BytesReceived = sizeBytes,
            TotalExpectedBytes = sizeBytes,
            Message = "Update downloaded and verified.",
            CompletedAt = completedAt
        };

    public static UpdateAcquisitionResult Failed(
        string failureReason,
        DateTimeOffset completedAt,
        string? version = null,
        long bytesReceived = 0,
        long totalExpectedBytes = 0) =>
        new()
        {
            State = UpdateAcquisitionState.Failed,
            Version = version,
            BytesReceived = bytesReceived,
            TotalExpectedBytes = totalExpectedBytes,
            Message = failureReason,
            CompletedAt = completedAt
        };

    public static UpdateAcquisitionResult Cancelled(
        DateTimeOffset completedAt,
        string? version = null,
        long bytesReceived = 0,
        long totalExpectedBytes = 0) =>
        new()
        {
            State = UpdateAcquisitionState.Cancelled,
            Version = version,
            BytesReceived = bytesReceived,
            TotalExpectedBytes = totalExpectedBytes,
            Message = "Update download was cancelled.",
            CompletedAt = completedAt
        };
}
