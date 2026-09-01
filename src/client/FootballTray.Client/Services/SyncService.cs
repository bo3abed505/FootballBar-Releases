using System;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public class TrayUpdateEventArgs : EventArgs
{
    public TrayDataDto? TrayData { get; init; }
    public DataFreshnessState FreshnessState { get; init; }
    public TickerConnectionState ConnectionState { get; init; }
    public string FormattedText { get; init; } = string.Empty;
    public int Version { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
}

public class SyncService : IDisposable
{
    private readonly FootballApiClient _apiClient;
    private readonly LocalCacheService _cacheService;
    private readonly TimeSpan _pollingInterval;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private Task<DataFreshnessState>? _inFlightPollTask;

    private string? _lastKnownEtag;
    private int _lastKnownVersion;
    private TrayDataDto? _currentTrayData;
    private DataFreshnessState _freshnessState = DataFreshnessState.Empty;
    private DateTimeOffset? _lastValidatedAt;

    public event EventHandler<TrayUpdateEventArgs>? TrayUpdated;

    public TrayDataDto? CurrentTrayData => _currentTrayData;
    public DataFreshnessState FreshnessState => _freshnessState;
    public TickerConnectionState CurrentState => MapToConnectionState(_freshnessState);
    public int LastKnownVersion => _lastKnownVersion;
    public string? LastKnownETag => _lastKnownEtag;
    public DateTimeOffset? LastValidatedAt => _lastValidatedAt;

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
    /// Starts the initial sync and launches the background periodic polling loop.
    /// Immediately restores valid local cache to avoid blocking UI initialization.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 1. Immediately restore local cache for instant UI presentation
        var cached = await _cacheService.LoadCacheAsync(_cts.Token);
        if (cached?.Envelope?.Data != null)
        {
            _currentTrayData = cached.Envelope.Data;
            _lastKnownVersion = cached.Envelope.Version;
            _lastKnownEtag = cached.ETag;
            _lastValidatedAt = cached.LastValidatedAt != default ? cached.LastValidatedAt : cached.CachedAt;
            _freshnessState = DataFreshnessState.Stale;

            PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
        }
        else
        {
            _freshnessState = DataFreshnessState.Empty;
            PublishUpdate(null, _freshnessState, 0, null, null);
        }

        // 2. Perform initial immediate network sync
        await PollOnceAsync(_cts.Token);

        // 3. Start background periodic polling loop
        _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Executes a single polling iteration with single-flight concurrency protection.
    /// Multiple concurrent callers share the same active in-flight request.
    /// </summary>
    public Task<DataFreshnessState> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        lock (_refreshLock)
        {
            if (_inFlightPollTask != null && !_inFlightPollTask.IsCompleted)
            {
                return _inFlightPollTask;
            }

            _inFlightPollTask = ExecutePollInternalAsync(cancellationToken);
            return _inFlightPollTask;
        }
    }

    private async Task<DataFreshnessState> ExecutePollInternalAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var response = await _apiClient.GetTrayAsync(_lastKnownEtag, cancellationToken);

            if (response.IsNotModified)
            {
                // 304 Not Modified: Server confirms cached representation is still current
                _freshnessState = DataFreshnessState.NotModified;
                _lastValidatedAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(response.ETag))
                {
                    _lastKnownEtag = response.ETag;
                }

                // Update cache validation timestamp
                await _cacheService.TouchLastValidatedAsync(_lastKnownEtag, cancellationToken);

                PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
                return _freshnessState;
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
                    _lastValidatedAt = DateTimeOffset.UtcNow;
                    _freshnessState = DataFreshnessState.Fresh;

                    // Atomically persist to disk
                    await _cacheService.SaveCacheAsync(envelope, response.ETag, cancellationToken);

                    PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
                    return _freshnessState;
                }
            }

            // Server returned error (4xx/5xx), network failure, or malformed payload
            return HandleOfflineFallback();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return _freshnessState;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SYNC] Poll error: {ex.Message}");
            return HandleOfflineFallback();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private DataFreshnessState HandleOfflineFallback()
    {
        if (_currentTrayData != null)
        {
            // Valid cached data exists: preserve payload and mark state STALE
            _freshnessState = DataFreshnessState.Stale;
            PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
        }
        else
        {
            // No valid cached data: mark EMPTY
            _freshnessState = DataFreshnessState.Empty;
            PublishUpdate(null, _freshnessState, 0, null, null);
        }
        return _freshnessState;
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
                HandleOfflineFallback();
            }
        }
    }

    private void PublishUpdate(TrayDataDto? data, DataFreshnessState freshness, int version, string? etag, DateTimeOffset? lastValidated)
    {
        var connectionState = MapToConnectionState(freshness);
        var formatted = TrayTickerFormatter.FormatTicker(data, freshness);
        TrayUpdated?.Invoke(this, new TrayUpdateEventArgs
        {
            TrayData = data,
            FreshnessState = freshness,
            ConnectionState = connectionState,
            FormattedText = formatted,
            Version = version,
            ETag = etag,
            LastValidatedAt = lastValidated,
        });
    }

    private static TickerConnectionState MapToConnectionState(DataFreshnessState freshness) =>
        freshness switch
        {
            DataFreshnessState.Fresh => TickerConnectionState.OnlineFresh,
            DataFreshnessState.NotModified => TickerConnectionState.OnlineFresh,
            DataFreshnessState.Stale => TickerConnectionState.OfflineCached,
            DataFreshnessState.Empty => TickerConnectionState.NoCacheOffline,
            _ => TickerConnectionState.NoCacheOffline,
        };

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        _refreshLock.Dispose();
    }
}

