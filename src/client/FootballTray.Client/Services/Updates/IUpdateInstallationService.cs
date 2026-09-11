using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Service responsible for preparing and initiating user-approved update installer handoff.
/// </summary>
public interface IUpdateInstallationService : IDisposable
{
    UpdateInstallationState CurrentState { get; }
    string? LastFailureReason { get; }

    /// <summary>
    /// Phase 1: Validates integrity, extracts payload securely, copies trusted installer, and prepares process start info.
    /// </summary>
    Task<PreparedUpdateInstall> PrepareInstallAsync(
        ReleaseManifest manifest,
        string verifiedPackagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Phase 2: Synchronously launches the prepared installer process and transfers directory ownership.
    /// Must be invoked immediately after verifying the offer is still current.
    /// </summary>
    UpdateInstallationResult StartPreparedHandoff(PreparedUpdateInstall prepared);

    /// <summary>
    /// Discards and cleans up a prepared installation when an offer changes or user cancels.
    /// </summary>
    void DiscardPreparedInstall(PreparedUpdateInstall prepared);
}
