namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Immutable outcome of an installer handoff attempt.
/// </summary>
public sealed record UpdateInstallationResult
{
    public UpdateInstallationState State { get; init; }
    public string TargetVersion { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public string? FailureReason { get; init; }

    public bool Success => State == UpdateInstallationState.HandoffStarted;
    public string? Message => FailureReason;

    public static UpdateInstallationResult HandoffStarted(string targetVersion, int? processId) =>
        new()
        {
            State = UpdateInstallationState.HandoffStarted,
            TargetVersion = targetVersion,
            ProcessId = processId
        };

    public static UpdateInstallationResult Failed(string targetVersion, string reason) =>
        new()
        {
            State = UpdateInstallationState.Failed,
            TargetVersion = targetVersion,
            FailureReason = reason
        };

    public static UpdateInstallationResult Cancelled(string targetVersion) =>
        new()
        {
            State = UpdateInstallationState.NotReady,
            TargetVersion = targetVersion,
            FailureReason = "Installation preparation was cancelled."
        };
}
