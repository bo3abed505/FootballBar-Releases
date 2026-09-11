using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Service contract for acquiring and verifying local application update packages.
/// </summary>
public interface IUpdatePackageAcquisitionService : IDisposable
{
    /// <summary>
    /// Current acquisition state.
    /// </summary>
    UpdateAcquisitionState CurrentState { get; }

    /// <summary>
    /// Staged path of the current verified package, if verified.
    /// </summary>
    string? CurrentVerifiedPackagePath { get; }

    /// <summary>
    /// Last error or failure reason, if any.
    /// </summary>
    string? LastFailureReason { get; }

    /// <summary>
    /// Asynchronously downloads and verifies the specified release package.
    /// Revalidates existing package if present on disk without network download.
    /// Single-flight: concurrent requests for the same package share the in-flight download.
    /// Requesting a different package while one is in-flight returns a safe busy/failed outcome.
    /// </summary>
    Task<UpdateAcquisitionResult> AcquirePackageAsync(
        ReleaseManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
