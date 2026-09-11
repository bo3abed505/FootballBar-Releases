using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Contract for client remote release manifest discovery.
/// </summary>
public interface IUpdateDiscoveryService : IDisposable
{
    UpdateCheckState CurrentState { get; }
    ReleaseManifest? CurrentOfferedManifest { get; }
    DateTimeOffset? LastCheckAttempt { get; }
    DateTimeOffset? LastSuccessfulCheck { get; }
    string? LastKnownETag { get; }
    string? LastFailureReason { get; }
    string FeedUrl { get; }
    SemanticVersion CurrentVersion { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}
