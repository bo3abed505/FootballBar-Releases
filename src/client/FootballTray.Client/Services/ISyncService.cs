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
    TickerConnectionState CurrentState { get; }
    Task<DataFreshnessState> PollOnceAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    void RefreshTickerOnPreferenceChange();
}
