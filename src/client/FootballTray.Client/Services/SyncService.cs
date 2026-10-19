using System;
using System.Collections.Concurrent;
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
    public bool QuotaExhausted { get; init; }
}

public class SyncService : ISyncService
{
    private readonly FootballApiClient _apiClient;
    private readonly LocalCacheService _cacheService;
    private readonly NotificationService? _notificationService;
    private readonly FootballTray.Client.Services.Notifications.NotificationCoordinator? _notificationCoordinator;
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

    private readonly ConcurrentDictionary<string, string> _etagsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrayDataDto> _dataByKey = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastPublishedDate;

    public event EventHandler<TrayUpdateEventArgs>? TrayUpdated;
    public event EventHandler<bool>? SyncStateChanged;
    private bool _isSyncing;
    public bool IsSyncing => _isSyncing;

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
            var target = value.Date;
            if (_targetDate.Date != target)
            {
                _targetDate = target;
                var dateKeyPrefix = $"{_targetDate:yyyy-MM-dd}|";
                var matchingKey = _dataByKey.Keys.FirstOrDefault(k => k.StartsWith(dateKeyPrefix, StringComparison.OrdinalIgnoreCase));
                if (matchingKey != null && _dataByKey.TryGetValue(matchingKey, out var cachedData))
                {
                    _currentTrayData = cachedData;
                    _lastKnownEtag = _etagsByKey.TryGetValue(matchingKey, out var etag) ? etag : null;
                    _lastPublishedDate = _targetDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
                }
                else
                {
                    _lastKnownEtag = null;
                }
            }
        }
    }

    public bool HasCachedDataForDate(DateTime date)
    {
        var dateKeyPrefix = $"{date.Date:yyyy-MM-dd}|";
        return _dataByKey.Keys.Any(k => k.StartsWith(dateKeyPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRequestKey(DateTime? date, IEnumerable<string> teams, IEnumerable<string> comps)
    {
        var datePart = date.HasValue ? date.Value.ToString("yyyy-MM-dd") : DateTime.Today.ToString("yyyy-MM-dd");
        var sortedTeams = string.Join(",", teams.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        var sortedComps = string.Join(",", comps.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        return $"{datePart}|{sortedTeams}|{sortedComps}";
    }

    private static string BuildRequestKey(DateTime date, IEnumerable<string> teams, IEnumerable<string> comps) =>
        BuildRequestKey((DateTime?)date, teams, comps);

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
        TimeSpan? pollingInterval = null,
        FootballTray.Client.Services.Notifications.NotificationCoordinator? notificationCoordinator = null)
    {
        _apiClient = apiClient;
        _cacheService = cacheService;
        _notificationService = notificationService;
        _preferencesService = preferencesService;
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(15);
        _notificationCoordinator = notificationCoordinator;

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

            _lastPublishedDate = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
            _notificationCoordinator?.ProcessKickoffReminders(_currentTrayData, _preferencesService?.GetPreferences());
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
        _isSyncing = true;
        SyncStateChanged?.Invoke(this, true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lastRefreshAttempt = DateTimeOffset.UtcNow;
        try
        {
            var prefs = _preferencesService?.Current ?? _preferencesService?.GetPreferences();
            var teamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var compIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (prefs != null)
            {
                // Followed teams: canonical IDs, legacy IDs, names, numeric IDs
                foreach (var id in prefs.GetFollowedTeamIdSet())
                {
                    if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
                    {
                        teamIds.Add(id.Trim());
                    }
                }
                if (prefs.FollowedTeams != null)
                {
                    foreach (var t in prefs.FollowedTeams)
                    {
                        if (!string.IsNullOrWhiteSpace(t.Id)) teamIds.Add(t.Id.Trim());
                    }
                }

                // Followed competitions: canonical IDs, legacy IDs, codes
                foreach (var id in prefs.GetFollowedCompetitionIdSet())
                {
                    if (!string.IsNullOrWhiteSpace(id) && !id.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
                    {
                        compIds.Add(id.Trim());
                    }
                }
                if (prefs.FollowedCompetitions != null)
                {
                    foreach (var c in prefs.FollowedCompetitions)
                    {
                        if (!string.IsNullOrWhiteSpace(c.Id)) compIds.Add(c.Id.Trim());
                    }
                }
            }

            // Thick-client caching requirement: Always request primary divisions to ensure offline availability
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.PremierLeague);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.LaLiga);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.EgyptianPremierLeague);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.ChampionsLeague);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.AfricaCupOfNations);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.WorldCup);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.EuroChampionship);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.NationsLeague);
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.LegacyNationsLeague);
            compIds.Add("ft_comp_01M467Y24DRWPKFG9AX4XX88ZE");
            compIds.Add(FootballTray.Client.Services.Preferences.CanonicalEntityIds.CopaAmerica);

            DateTime queryDate = _targetDate.Date;
            var teamIdList = teamIds.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            var compIdList = compIds.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            var requestKey = BuildRequestKey(queryDate, teamIdList, compIdList);

            // Phase 1: Cache Desync Resiliency
            // If local in-memory cache is null (meaning tray.json is missing, corrupt, or deleted),
            // attempt a reload. If still null, ignore any saved ETag and pass null to the API client
            // to force the backend to return a 200 OK with the full payload to rehydrate the client.
            if (_currentTrayData == null && _dataByKey.TryGetValue(requestKey, out var memoryData))
            {
                _currentTrayData = memoryData;
            }

            if (_currentTrayData == null)
            {
                try
                {
                    var reloaded = await _cacheService.LoadCacheAsync(cancellationToken);
                    if (reloaded?.Envelope?.Data != null)
                    {
                        _currentTrayData = reloaded.Envelope.Data;
                        _lastKnownVersion = reloaded.Envelope.Version;
                        _lastKnownEtag = reloaded.ETag;
                        if (_targetDate.Date == DateTime.Today)
                        {
                            _dataByKey[requestKey] = _currentTrayData;
                            if (!string.IsNullOrWhiteSpace(_lastKnownEtag))
                            {
                                _etagsByKey[requestKey] = _lastKnownEtag;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore cache read failures; fall through to null ETag fetch
                }
            }
            else if (!_dataByKey.ContainsKey(requestKey) && _targetDate.Date == DateTime.Today)
            {
                _dataByKey[requestKey] = _currentTrayData;
                if (!string.IsNullOrWhiteSpace(_lastKnownEtag))
                {
                    _etagsByKey[requestKey] = _lastKnownEtag;
                }
            }

            string? etagToSend = null;
            if (_dataByKey.TryGetValue(requestKey, out var kData) && kData != null)
            {
                _currentTrayData = kData;
                if (_etagsByKey.TryGetValue(requestKey, out var kEtag))
                {
                    etagToSend = kEtag;
                    _lastKnownEtag = kEtag;
                }
            }
            else if (_currentTrayData != null)
            {
                etagToSend = _lastKnownEtag;
            }

            if (_currentTrayData == null)
            {
                _lastKnownEtag = null;
            }

            AppLogger.Debug("Sync", "PollStarted", $"Checking backend sync (Date: {queryDate:yyyy-MM-dd}, Teams: {teamIdList.Count}, Competitions: {compIdList.Count}, ETag: '{etagToSend ?? "none"}')...");
            var response = await _apiClient.GetTrayAsync(
                etagToSend,
                queryDate,
                teamIdList,
                compIdList,
                cancellationToken);
            sw.Stop();
            _lastRefreshDurationMs = sw.ElapsedMilliseconds;

            string normalizedDate = queryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            bool isNotModified = response.IsNotModified || (response.Envelope?.Data?.IsNotModified ?? false);

            if (isNotModified)
            {
                if (response.Envelope?.Data != null && response.Envelope.Data.Matches != null)
                {
                    _currentTrayData = response.Envelope.Data;
                }
                else if (_dataByKey.TryGetValue(requestKey, out var existingData))
                {
                    _currentTrayData = existingData;
                }

                _freshnessState = DataFreshnessState.NotModified;
                _lastValidatedAt = DateTimeOffset.UtcNow;
                _lastSuccessfulServerResponseTime = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(response.ETag))
                {
                    _lastKnownEtag = response.ETag;
                    _etagsByKey[requestKey] = response.ETag;
                }
                else if (!string.IsNullOrWhiteSpace(etagToSend))
                {
                    _lastKnownEtag = etagToSend;
                    _etagsByKey[requestKey] = etagToSend;
                }

                if (response.QuotaExhausted && _currentTrayData != null)
                {
                    _currentTrayData.QuotaExhausted = true;
                }

                if (_targetDate.Date == DateTime.Today && !string.IsNullOrWhiteSpace(_lastKnownEtag))
                {
                    try
                    {
                        await _cacheService.TouchLastValidatedAsync(_lastKnownEtag, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("Sync", "CacheTouchFailed", $"Failed to update cache validation timestamp on 304: [{ex.GetType().Name}] {ex.Message}", ex: ex);
                    }
                }

                // If user switched calendar days, publish the cached snapshot immediately
                if (!string.Equals(normalizedDate, _lastPublishedDate, StringComparison.Ordinal))
                {
                    _lastPublishedDate = normalizedDate;
                    PublishUpdate(_currentTrayData, _freshnessState, _lastKnownVersion, _lastKnownEtag, _lastValidatedAt);
                    _notificationCoordinator?.ProcessKickoffReminders(_currentTrayData, _preferencesService?.GetPreferences());
                    return _freshnessState;
                }

                AppLogger.Debug("Sync", "PollNotModified", "Tray state unchanged (304 Not Modified)");
                _notificationCoordinator?.ProcessKickoffReminders(_currentTrayData, _preferencesService?.GetPreferences());
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

                // Cache in memory by requestKey
                _dataByKey[requestKey] = _currentTrayData;
                if (!string.IsNullOrWhiteSpace(response.ETag))
                {
                    _etagsByKey[requestKey] = response.ETag;
                }

                // Atomically persist to disk if current view is today
                if (_targetDate.Date == DateTime.Today)
                {
                    await _cacheService.SaveCacheAsync(envelope, response.ETag, cancellationToken);
                }

                // Evaluate and dispatch notifications for newly observed events/transitions
                if (_notificationService != null)
                {
                    var preferences = _preferencesService?.GetPreferences();
                    string? pinnedMatchId = App.CurrentTickerCoordinator?.PinnedMatchId ?? App.CurrentMainViewModel?.ManualPinnedMatchId;
                    await _notificationService.ProcessTrayUpdateAsync(previousData, _currentTrayData, preferences, pinnedMatchId, cancellationToken);
                }

                AppLogger.Info("Sync", "PollSuccess", $"Backend payload received (v{serverVersion}, {_currentTrayData.Matches?.Count ?? 0} matches, {_lastRefreshDurationMs}ms).");
                _notificationCoordinator?.ProcessKickoffReminders(_currentTrayData, _preferencesService?.GetPreferences());
                _lastPublishedDate = normalizedDate;
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
            _isSyncing = false;
            SyncStateChanged?.Invoke(this, false);
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
        var targetData = data ?? _currentTrayData;
        var connectionState = MapToConnectionState(freshness);
        var preferences = _preferencesService?.GetPreferences();
        var formatted = TrayTickerFormatter.FormatTicker(targetData, freshness, preferences);
        var args = new TrayUpdateEventArgs
        {
            TrayData = targetData,
            FreshnessState = freshness,
            ConnectionState = connectionState,
            FormattedText = formatted,
            Version = version,
            ETag = etag,
            LastValidatedAt = lastValidated,
            QuotaExhausted = targetData?.QuotaExhausted ?? false,
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

            await _apiClient.SyncPreferencesAsync(compIds, teamIds, null, cancellationToken);
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
