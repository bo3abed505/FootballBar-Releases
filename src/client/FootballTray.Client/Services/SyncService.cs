using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;

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

public class SyncService : ISyncService
{
    private readonly FootballApiClient _apiClient;
    private readonly LocalCacheService _cacheService;
    private readonly NotificationService? _notificationService;
    private readonly IPreferencesService? _preferencesService;
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
    private DateTimeOffset? _lastSuccessfulServerResponseTime;
    private DateTimeOffset? _lastRefreshAttempt;
    private long _lastRefreshDurationMs;

    public event EventHandler<TrayUpdateEventArgs>? TrayUpdated;

    public TrayDataDto? CurrentTrayData => _currentTrayData;
    public DataFreshnessState FreshnessState => _freshnessState;
    public DataFreshnessState CurrentFreshness => _freshnessState;
    public TickerConnectionState CurrentState => MapToConnectionState(_freshnessState);
    public int LastKnownVersion => _lastKnownVersion;
    public string? LastKnownETag => _lastKnownEtag;
    public DateTimeOffset? LastValidatedAt => _lastValidatedAt;
    public DateTimeOffset? LastSuccessfulServerResponseTime => _lastSuccessfulServerResponseTime;
    public DateTimeOffset? LastRefreshAttempt => _lastRefreshAttempt;
    public long LastRefreshDurationMs => _lastRefreshDurationMs;
    public NotificationService? NotificationService => _notificationService;
    public IPreferencesService? PreferencesService => _preferencesService;

    private DateTime _targetDate = DateTime.Today;

    public DateTime TargetDate
    {
        get => _targetDate;
        set
        {
            if (_targetDate.Date != value.Date)
            {
                _targetDate = value.Date;
                _lastKnownEtag = null; // Invalidate previous date ETag to force fresh fetch
            }
        }
    }

    public void TriggerImmediateSync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PollOnceAsync(_cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Sync", "TriggerImmediateSyncFailed", $"Out-of-band sync failed: {ex.Message}");
            }
        });
    }

    public SyncService(
        FootballApiClient apiClient,
        LocalCacheService cacheService,
        NotificationService? notificationService = null,
        IPreferencesService? preferencesService = null,
        TimeSpan? pollingInterval = null)
    {
        _apiClient = apiClient;
        _cacheService = cacheService;
        _notificationService = notificationService;
        _preferencesService = preferencesService;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(15);

        if (_preferencesService != null)
        {
            _preferencesService.PreferencesChanged += OnPreferencesChanged;
        }
    }

    public SyncService(
        FootballApiClient apiClient,
        LocalCacheService cacheService,
        NotificationService? notificationService,
        TimeSpan? pollingInterval)
        : this(apiClient, cacheService, notificationService, null, pollingInterval)
    {
    }

    public SyncService(
        FootballApiClient apiClient,
        LocalCacheService cacheService,
        TimeSpan? pollingInterval)
        : this(apiClient, cacheService, null, null, pollingInterval)
    {
    }

    public void RefreshTickerOnPreferenceChange()
    {
        PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
    }

    private void OnPreferencesChanged(object? sender, UserPreferences preferences)
    {
        // Immediate UI refresh when following or notification preferences are updated
        RefreshTickerOnPreferenceChange();

        // Push updated followed competitions and teams to the daemon
        _ = PushPreferencesToDaemonAsync(preferences, _cts?.Token ?? CancellationToken.None);

        // Trigger immediate out-of-band poll with newly followed teams
        TriggerImmediateSync();
    }

    /// <summary>
    /// Starts the initial sync and launches the background periodic polling loop.
    /// Immediately restores valid local cache to avoid blocking UI initialization.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 0. Initialize preferences and notification deduplication history
        if (_preferencesService != null)
        {
            var prefs = await _preferencesService.LoadAsync(_cts.Token);
            _ = PushPreferencesToDaemonAsync(prefs, _cts.Token);
        }

        if (_notificationService != null)
        {
            await _notificationService.InitializeAsync(_cts.Token);
        }

        // 1. Immediately restore local cache for instant UI presentation
        var cached = await _cacheService.LoadCacheAsync(_cts.Token);
        if (cached?.Envelope?.Data != null)
        {
            _currentTrayData = cached.Envelope.Data;
            _lastKnownVersion = cached.Envelope.Version;
            _lastKnownEtag = cached.ETag;
            _lastValidatedAt = cached.LastValidatedAt != default ? cached.LastValidatedAt : cached.CachedAt;
            _freshnessState = DataFreshnessState.Stale;

            // Establish baseline in notification engine so cached historical data is never replayed
            _notificationService?.Engine.EvaluateUpdates(null, _currentTrayData);

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lastRefreshAttempt = DateTimeOffset.UtcNow;
        try
        {
            var prefs = _preferencesService?.Current ?? _preferencesService?.GetPreferences();
            var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (prefs != null)
            {
                // Followed teams: canonical IDs, legacy IDs, names, numeric IDs
                foreach (var id in prefs.GetFollowedTeamIdSet())
                {
                    if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
                    {
                        targetIds.Add(id.Trim());
                    }
                }
                if (prefs.FollowedTeams != null)
                {
                    foreach (var t in prefs.FollowedTeams)
                    {
                        if (!string.IsNullOrWhiteSpace(t.Id)) targetIds.Add(t.Id.Trim());
                    }
                }

                // Followed competitions: canonical IDs, legacy IDs, codes
                foreach (var id in prefs.GetFollowedCompetitionIdSet())
                {
                    if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
                    {
                        targetIds.Add(id.Trim());
                    }
                }
                if (prefs.FollowedCompetitions != null)
                {
                    foreach (var c in prefs.FollowedCompetitions)
                    {
                        if (!string.IsNullOrWhiteSpace(c.Id)) targetIds.Add(c.Id.Trim());
                    }
                }
            }

            var targetIdList = targetIds.ToList();

            AppLogger.Debug("Sync", "PollStarted", $"Checking backend sync (Date: {_targetDate:yyyy-MM-dd}, Targets: {targetIdList.Count}, ETag: '{_lastKnownEtag ?? "none"}')...");
            var response = await _apiClient.GetTrayAsync(
                _lastKnownEtag,
                _targetDate,
                targetIdList,
                cancellationToken);
            sw.Stop();
            _lastRefreshDurationMs = sw.ElapsedMilliseconds;

            if (response.IsNotModified)
            {
                // 304 Not Modified: Server confirms cached representation is still current
                _freshnessState = DataFreshnessState.NotModified;
                _lastValidatedAt = DateTimeOffset.UtcNow;
                _lastSuccessfulServerResponseTime = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(response.ETag))
                {
                    _lastKnownEtag = response.ETag;
                }

                // Update cache validation timestamp (secondary non-critical persistence)
                try
                {
                    await _cacheService.TouchLastValidatedAsync(_lastKnownEtag, cancellationToken);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Sync", "CacheTouchFailed", $"Failed to update cache validation timestamp on 304: [{ex.GetType().Name}] {ex.Message}", ex: ex);
                }

                AppLogger.Info("Sync", "PollNotModified", $"Backend returned 304 Not Modified ({_lastRefreshDurationMs}ms). Cache revalidated.");
                PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
                return _freshnessState;
            }

            if (response.IsSuccess && response.Envelope?.Data != null)
            {
                var envelope = response.Envelope;
                var serverVersion = envelope.Version;

                if (_currentTrayData != null && serverVersion < _lastKnownVersion)
                {
                    AppLogger.Info("Sync", "LowerVersionAccepted", $"Server version v{serverVersion} is lower than cached v{_lastKnownVersion}. Accepting authoritative server payload.");
                }

                var previousData = _currentTrayData;
                _currentTrayData = envelope.Data;
                _lastKnownVersion = serverVersion;
                _lastKnownEtag = response.ETag;
                _lastValidatedAt = DateTimeOffset.UtcNow;
                _lastSuccessfulServerResponseTime = DateTimeOffset.UtcNow;
                _freshnessState = DataFreshnessState.Fresh;

                // Atomically persist to disk
                await _cacheService.SaveCacheAsync(envelope, response.ETag, cancellationToken);

                // Evaluate and dispatch notifications for newly observed events/transitions
                if (_notificationService != null)
                {
                    var preferences = _preferencesService?.GetPreferences();
                    await _notificationService.ProcessTrayUpdateAsync(previousData, _currentTrayData, preferences, cancellationToken);
                }

                AppLogger.Info("Sync", "PollSuccess", $"Backend payload received (v{serverVersion}, {_currentTrayData.Matches?.Count ?? 0} matches, {_lastRefreshDurationMs}ms).");
                PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
                return _freshnessState;
            }

            // Server returned error (4xx/5xx), network failure, or malformed payload
            AppLogger.Warn("Sync", "PollFailed", $"Backend returned unsuccessful response: {response.ErrorMessage ?? "Unknown"}. Fallback to cached state.");
            return HandleOfflineFallback();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return _freshnessState;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _lastRefreshDurationMs = sw.ElapsedMilliseconds;
            string hresultHex = $"0x{ex.HResult:X8}";
            string messageDetail = !string.IsNullOrWhiteSpace(ex.Message) ? $"{ex.Message} " : string.Empty;
            AppLogger.Warn("Sync", "PollException", $"Network sync failed ({_lastRefreshDurationMs}ms): [{ex.GetType().Name}] {messageDetail}({hresultHex}). Falling back to offline cache.", ex: ex);
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
                AppLogger.Error("Sync", "PeriodicLoopError", $"Periodic polling loop encountered unexpected error: [{ex.GetType().Name}] {ex.Message}", ex: ex);
                HandleOfflineFallback();
            }
        }
    }

    private void PublishUpdate(TrayDataDto? data, DataFreshnessState freshness, int version, string? etag, DateTimeOffset? lastValidated)
    {
        var connectionState = MapToConnectionState(freshness);
        var preferences = _preferencesService?.GetPreferences();
        var formatted = TrayTickerFormatter.FormatTicker(data, freshness, preferences);
        var args = new TrayUpdateEventArgs
        {
            TrayData = data,
            FreshnessState = freshness,
            ConnectionState = connectionState,
            FormattedText = formatted,
            Version = version,
            ETag = etag,
            LastValidatedAt = lastValidated,
        };

        var handler = TrayUpdated;
        if (handler != null)
        {
            foreach (var del in handler.GetInvocationList())
            {
                try
                {
                    if (del is EventHandler<TrayUpdateEventArgs> typedHandler)
                    {
                        typedHandler(this, args);
                    }
                    else
                    {
                        del.DynamicInvoke(this, args);
                    }
                }
                catch (Exception ex)
                {
                    var actualEx = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    AppLogger.Error("Sync", "SubscriberError", $"TrayUpdated subscriber '{del.Method.DeclaringType?.Name}.{del.Method.Name}' failed: [{actualEx.GetType().Name}] {actualEx.Message}", ex: actualEx);
                }
            }
        }
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

    private async Task PushPreferencesToDaemonAsync(UserPreferences preferences, CancellationToken cancellationToken)
    {
        try
        {
            var compIds = preferences.FollowedCompetitions?.Select(c => c.Id).Where(id => !string.IsNullOrWhiteSpace(id)) ?? Enumerable.Empty<string>();
            var teamIds = preferences.FollowedTeams?.Select(t => t.Id).Where(id => !string.IsNullOrWhiteSpace(id)) ?? Enumerable.Empty<string>();

            await _apiClient.SyncPreferencesAsync(compIds, teamIds, cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncService] Failed to push preferences to daemon: {ex.Message}");
        }
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
        if (_preferencesService != null)
        {
            _preferencesService.PreferencesChanged -= OnPreferencesChanged;
        }
        _refreshLock.Dispose();
    }
}
