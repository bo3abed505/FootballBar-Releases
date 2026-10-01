using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

/// <summary>
/// Contract for the client football data synchronization lifecycle and update event stream.
/// </summary>
public interface ISyncService : IDisposable
{
    event EventHandler<TrayUpdateEventArgs>? TrayUpdated;
    TrayDataDto? CurrentTrayData { get; }
    DataFreshnessState FreshnessState { get; }
    DataFreshnessState CurrentFreshness => FreshnessState;
    TickerConnectionState CurrentState { get; }
    DateTimeOffset? LastSuccessfulServerResponseTime { get; }
    DateTimeOffset? LastRefreshAttempt { get; }
    string? LastKnownETag { get; }
    DateTimeOffset? LastValidatedAt { get; }
    Task<DataFreshnessState> PollOnceAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    DateTime TargetDate { get; set; }
    void TriggerImmediateSync();
    void RefreshTickerOnPreferenceChange();
}
