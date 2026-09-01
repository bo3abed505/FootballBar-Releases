using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public class TrayUpdateEventArgs : EventArgs
{
    public TrayDataDto? TrayData { get; init; }
    public TickerConnectionState ConnectionState { get; init; }
    public string FormattedText { get; init; } = string.Empty;
    public int Version { get; init; }
    public string? ETag { get; init; }
}

public class SyncService : IDisposable
{
    private readonly FootballApiClient _apiClient;
    private readonly LocalCacheService _cacheService;
    private readonly TimeSpan _pollingInterval;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    private string? _lastKnownEtag;
    private int _lastKnownVersion;
    private TrayDataDto? _currentTrayData;
    private TickerConnectionState _currentState = TickerConnectionState.NoCacheOffline;

    public event EventHandler<TrayUpdateEventArgs>? TrayUpdated;

    public TrayDataDto? CurrentTrayData => _currentTrayData;
    public TickerConnectionState CurrentState => _currentState;
    public int LastKnownVersion => _lastKnownVersion;
    public string? LastKnownETag => _lastKnownEtag;

    public SyncService(
        FootballApiClient apiClient,
        LocalCacheService cacheService,
        TimeSpan? pollingInterval = null)
    {
        _apiClient = apiClient;
        _cacheService = cacheService;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Starts the initial sync and launches the background polling loop.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 1. Load local cache immediately
        var cached = await _cacheService.LoadCacheAsync(_cts.Token);
        if (cached?.Envelope?.Data != null)
        {
            _currentTrayData = cached.Envelope.Data;
            _lastKnownVersion = cached.Envelope.Version;
            _lastKnownEtag = cached.ETag;
            _currentState = TickerConnectionState.OnlineStale;

            PublishUpdate(_currentTrayData, _currentState, _lastKnownVersion, _lastKnownEtag);
        }
        else
        {
            _currentState = TickerConnectionState.NoCacheOffline;
            PublishUpdate(null, _currentState, 0, null);
        }

        // 2. Perform initial immediate network sync
        await PollOnceAsync(_cts.Token);

        // 3. Start background periodic polling loop
        _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Executes a single polling iteration against the backend API.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _apiClient.GetTrayAsync(_lastKnownEtag, cancellationToken);

            if (response.IsNotModified)
            {
                // 304 Not Modified: Data is unchanged on server
                if (_currentState != TickerConnectionState.OnlineFresh)
                {
                    _currentState = TickerConnectionState.OnlineFresh;
                    PublishUpdate(_currentTrayData, _currentState, _lastKnownVersion, _lastKnownEtag);
                }
                return;
            }

            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                var envelope = response.Envelope;
                var serverVersion = envelope.Version;

                // Version check: update only if version is newer or uninitialized
                if (_currentTrayData == null || serverVersion >= _lastKnownVersion)
                {
                    _currentTrayData = envelope.Data;
                    _lastKnownVersion = serverVersion;
                    _lastKnownEtag = response.ETag;
                    _currentState = TickerConnectionState.OnlineFresh;

                    // Atomically persist to disk
                    await _cacheService.SaveCacheAsync(envelope, response.ETag, cancellationToken);

                    PublishUpdate(_currentTrayData, _currentState, _lastKnownVersion, _lastKnownEtag);
                }
                return;
            }

            // Server returned error or unreachable
            HandleOfflineState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful cancellation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SYNC] Poll error: {ex.Message}");
            HandleOfflineState();
        }
    }

    private void HandleOfflineState()
    {
        if (_currentTrayData != null)
        {
            if (_currentState != TickerConnectionState.OfflineCached)
            {
                _currentState = TickerConnectionState.OfflineCached;
                PublishUpdate(_currentTrayData, _currentState, _lastKnownVersion, _lastKnownEtag);
            }
        }
        else
        {
            if (_currentState != TickerConnectionState.NoCacheOffline)
            {
                _currentState = TickerConnectionState.NoCacheOffline;
                PublishUpdate(null, _currentState, 0, null);
            }
        }
    }

    private async Task PollingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollingInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC] Periodic loop error: {ex.Message}");
                HandleOfflineState();
            }
        }
    }

    private void PublishUpdate(TrayDataDto? data, TickerConnectionState state, int version, string? etag)
    {
        var formatted = TrayTickerFormatter.FormatTicker(data, state);
        TrayUpdated?.Invoke(this, new TrayUpdateEventArgs
        {
            TrayData = data,
            ConnectionState = state,
            FormattedText = formatted,
            Version = version,
            ETag = etag,
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
