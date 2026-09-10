using System;
using FootballTray.Core.Dto;

namespace FootballTray.Core.Services;

/// <summary>
/// Status outcome of an update eligibility evaluation.
/// </summary>
public enum UpdateEvaluationStatus
{
    /// <summary>
    /// Current version is equal to or newer than the target release.
    /// </summary>
    UpToDate,

    /// <summary>
    /// A newer eligible release is available.
    /// </summary>
    UpdateAvailable,

    /// <summary>
    /// Target release is a prerelease and current application is on the stable channel.
    /// </summary>
    PrereleaseNotEligible,

    /// <summary>
    /// Remote manifest is structurally or semantically invalid.
    /// </summary>
    InvalidManifest
}

/// <summary>
/// Domain result containing update eligibility details and reasons.
/// </summary>
public sealed record UpdateEvaluationResult
{
    public UpdateEvaluationStatus Status { get; init; }
    public bool IsUpdateAvailable => Status == UpdateEvaluationStatus.UpdateAvailable;
    public string? Reason { get; init; }
    public SemanticVersion? CurrentVersion { get; init; }
    public SemanticVersion? TargetVersion { get; init; }
    public ReleaseManifest? Manifest { get; init; }

    public static UpdateEvaluationResult Available(
        SemanticVersion current,
        SemanticVersion target,
        ReleaseManifest manifest) =>
        new()
        {
            Status = UpdateEvaluationStatus.UpdateAvailable,
            CurrentVersion = current,
            TargetVersion = target,
            Manifest = manifest,
            Reason = $"A newer version v{target} is available (current: v{current})."
        };

    public static UpdateEvaluationResult UpToDate(
        SemanticVersion current,
        SemanticVersion target,
        string reason) =>
        new()
        {
            Status = UpdateEvaluationStatus.UpToDate,
            CurrentVersion = current,
            TargetVersion = target,
            Reason = reason
        };

    public static UpdateEvaluationResult PrereleaseNotEligible(
        SemanticVersion current,
        SemanticVersion target,
        string reason) =>
        new()
        {
            Status = UpdateEvaluationStatus.PrereleaseNotEligible,
            CurrentVersion = current,
            TargetVersion = target,
            Reason = reason
        };

    public static UpdateEvaluationResult Invalid(string error) =>
        new()
        {
            Status = UpdateEvaluationStatus.InvalidManifest,
            Reason = error
        };
}

/// <summary>
/// Pure domain evaluator determining whether a validated release manifest is eligible for update
/// against the currently running application version.
///
/// Channel Policy:
/// - Initial implementation enforces STABLE CHANNEL ONLY.
/// - If the currently running application version is stable, prerelease releases (e.g. 1.1.0-beta)
///   are strictly NOT offered to the user.
/// - If the running application version is itself a prerelease (e.g. 1.0.0-rc.1), a stable release (1.0.0)
///   is higher precedence and will be offered as an update.
/// </summary>
public static class UpdateVersionEvaluator
{
    /// <summary>
    /// Evaluates update eligibility of a manifest against a current version string.
    /// Validates the manifest and derives target version strictly from manifest.Version.
    /// </summary>
    public static UpdateEvaluationResult Evaluate(ReleaseManifest? manifest, string currentVersionString)
    {
        if (!SemanticVersion.TryParse(currentVersionString, out var currentVersion) || currentVersion is null)
        {
            return UpdateEvaluationResult.Invalid($"Current application version '{currentVersionString}' is not a valid semantic version.");
        }

        return Evaluate(manifest, currentVersion);
    }

    /// <summary>
    /// Evaluates update eligibility of a manifest against a current semantic version.
    /// Validates the manifest and derives target version strictly from manifest.Version.
    /// Contradictory target versions cannot be supplied by arbitrary callers.
    /// </summary>
    public static UpdateEvaluationResult Evaluate(ReleaseManifest? manifest, SemanticVersion? currentVersion)
    {
        if (currentVersion is null)
        {
            return UpdateEvaluationResult.Invalid("Current application version is null.");
        }

        var validation = ReleaseManifestValidator.Validate(manifest);
        if (!validation.IsValid || validation.ValidatedManifest is null || validation.ParsedVersion is null)
        {
            return UpdateEvaluationResult.Invalid(validation.ErrorMessage ?? "Manifest validation failed.");
        }

        return EvaluateInternal(validation.ValidatedManifest, validation.ParsedVersion, currentVersion);
    }

    private static UpdateEvaluationResult EvaluateInternal(
        ReleaseManifest manifest,
        SemanticVersion targetVersion,
        SemanticVersion currentVersion)
    {
        // 1. Stable Channel Policy: Stable client never receives prerelease updates
        if (!currentVersion.IsPrerelease && targetVersion.IsPrerelease)
        {
            return UpdateEvaluationResult.PrereleaseNotEligible(
                currentVersion,
                targetVersion,
                $"Prerelease version v{targetVersion} is not offered to stable channel (current: v{currentVersion}).");
        }

        // 2. SemVer Precedence Comparison (identical to install-client.ps1 rules)
        int comparison = targetVersion.CompareTo(currentVersion);

        if (comparison > 0)
        {
            return UpdateEvaluationResult.Available(currentVersion, targetVersion, manifest);
        }

        if (comparison == 0)
        {
            return UpdateEvaluationResult.UpToDate(
                currentVersion,
                targetVersion,
                $"Application is up to date (current: v{currentVersion}, target: v{targetVersion}).");
        }

        // comparison < 0: Downgrade rejected
        return UpdateEvaluationResult.UpToDate(
            currentVersion,
            targetVersion,
            $"Current version v{currentVersion} is newer than available version v{targetVersion} (downgrade rejected).");
    }
}
