using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services.Telemetry;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public class TrayApiResponse
{
    public bool IsSuccess { get; init; }
    public bool IsNotModified { get; init; }
    public string? ETag { get; init; }
    public ApiResponseEnvelope<TrayDataDto>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }
    public bool QuotaExhausted { get; set; }
    public bool CachedFallback { get; set; }

    public static TrayApiResponse Success(ApiResponseEnvelope<TrayDataDto> envelope, string? etag, bool quotaExhausted = false, bool cachedFallback = false) =>
        new()
        {
            IsSuccess = true,
            IsNotModified = false,
            Envelope = envelope,
            ETag = etag,
            QuotaExhausted = quotaExhausted || envelope.QuotaExhausted || (envelope.Data?.QuotaExhausted ?? false),
            CachedFallback = cachedFallback || envelope.CachedFallback || (envelope.Data?.CachedFallback ?? false)
        };

    public static TrayApiResponse NotModified(string? etag, bool quotaExhausted = false, bool cachedFallback = false) =>
        new()
        {
            IsSuccess = true,
            IsNotModified = true,
            ETag = etag,
            QuotaExhausted = quotaExhausted,
            CachedFallback = cachedFallback,
            Envelope = null
        };

    public static TrayApiResponse Error(string message) =>
        new() { IsSuccess = false, IsNotModified = false, ErrorMessage = message };
}

public class MatchDetailApiResponse
{
    public bool IsSuccess { get; init; }
    public bool IsNotModified { get; init; }
    public string? ETag { get; init; }
    public ApiResponseEnvelope<MatchDetailDto>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }

    public static MatchDetailApiResponse Success(ApiResponseEnvelope<MatchDetailDto> envelope, string? etag = null) =>
        new() { IsSuccess = true, IsNotModified = false, Envelope = envelope, ETag = etag };

    public static MatchDetailApiResponse NotModified(string? etag) =>
        new() { IsSuccess = true, IsNotModified = true, ETag = etag };

    public static MatchDetailApiResponse Error(string message) =>
        new() { IsSuccess = false, IsNotModified = false, ErrorMessage = message };
}

public class CatalogSearchApiResponse<T>
{
    public bool IsSuccess { get; init; }
    public ApiResponseEnvelope<List<T>>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }

    public static CatalogSearchApiResponse<T> Success(ApiResponseEnvelope<List<T>> envelope) =>
        new() { IsSuccess = true, Envelope = envelope };

    public static CatalogSearchApiResponse<T> Error(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}

public class CompetitionHubApiResponse
{
    public bool IsSuccess { get; init; }
    public ApiResponseEnvelope<CompetitionHubDto>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }

    public static CompetitionHubApiResponse Success(ApiResponseEnvelope<CompetitionHubDto> envelope) =>
        new() { IsSuccess = true, Envelope = envelope };

    public static CompetitionHubApiResponse Error(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}

public class TeamHubApiResponse
{
    public bool IsSuccess { get; init; }
    public ApiResponseEnvelope<TeamHubDto>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }

    public static TeamHubApiResponse Success(ApiResponseEnvelope<TeamHubDto> envelope) =>
        new() { IsSuccess = true, Envelope = envelope };

    public static TeamHubApiResponse Error(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}

public class FootballApiClient : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:4000";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly bool _isCustomHttpClient;
    private readonly ConcurrentDictionary<string, string> _etagsByUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ApiResponseEnvelope<TrayDataDto>> _payloadsByUrl = new(StringComparer.OrdinalIgnoreCase);

    // In-process Tier-2 Native Telemetry Provider & fast circuit breaker state
    private readonly NativeTelemetryProvider _nativeProvider = new();
    private readonly LocalCacheService _cacheService;
    private volatile bool _useStandaloneNativeEngine = false;
    private bool _enableNativeFallback;
    private DateTimeOffset _lastProbeTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);
    private int _probingLock = 0;

    public bool IsStandaloneNativeEngineActive => _useStandaloneNativeEngine;

    internal void SetStandaloneNativeEngineActiveForTesting(bool active) => _useStandaloneNativeEngine = active;

    public bool EnableNativeFallback
    {
        get => _enableNativeFallback;
        set => _enableNativeFallback = value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = FootballJsonSerializerContext.Default,
    };

    public static string GetDefaultBaseUrl()
    {
        string? envUrl = Environment.GetEnvironmentVariable("FOOTBALLTRAY_BACKEND_URL");
        if (!string.IsNullOrWhiteSpace(envUrl) && Uri.TryCreate(envUrl, UriKind.Absolute, out _))
        {
            return envUrl.TrimEnd('/');
        }
        return DefaultBaseUrl;
    }

    public void InvalidateETag(string? uri = null)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            _etagsByUrl.Clear();
            _payloadsByUrl.Clear();
        }
        else
        {
            _etagsByUrl.TryRemove(uri, out _);
            _payloadsByUrl.TryRemove(uri, out _);
        }
    }

    public string? GetCachedETag(string uri) =>
        _etagsByUrl.TryGetValue(uri, out var etag) ? etag : null;

    public FootballApiClient(
        HttpClient? httpClient = null,
        string? baseUrl = null,
        LocalCacheService? cacheService = null,
        bool? enableNativeFallback = null)
    {
        _cacheService = cacheService ?? new LocalCacheService();
        _enableNativeFallback = enableNativeFallback ?? (httpClient == null && baseUrl == null);
        _isCustomHttpClient = (httpClient != null);

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUrl ?? GetDefaultBaseUrl()),
                Timeout = TimeSpan.FromSeconds(5),
            };
            _disposeClient = true;
        }

        if (_httpClient.BaseAddress == null && baseUrl != null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    #region Circuit Breaker & Probing Engine

    private async Task EnsureEngineStateAsync(CancellationToken ct = default)
    {
        if (!_enableNativeFallback)
        {
            return;
        }

        var manifest = OtaConfigService.Instance.CurrentManifest;

        // If OTA manifest designates native-only mode, bypass network probe completely
        if (string.Equals(manifest?.RoutingMode, "native-only", StringComparison.OrdinalIgnoreCase))
        {
            _useStandaloneNativeEngine = true;
            return;
        }

        // If OTA manifest designates cloud-primary mode, configure base address to cloud server
        if (string.Equals(manifest?.RoutingMode, "cloud-primary", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(manifest?.CloudBaseUrl) &&
            Uri.TryCreate(manifest.CloudBaseUrl, UriKind.Absolute, out var cloudUri))
        {
            if (_httpClient.BaseAddress != cloudUri)
            {
                _httpClient.BaseAddress = cloudUri;
                _lastProbeTime = DateTimeOffset.MinValue;
            }
        }

        // Production Standalone Optimization:
        // When FOOTBALLTRAY_ENABLE_LOCAL_DAEMON is not "1" and no custom mock HttpClient was injected (production runtime),
        // go straight to NativeTelemetryProvider to prevent hijacking by stale 127.0.0.1:4000 processes.
        bool isLocalDaemonExplicitlyEnabled = string.Equals(Environment.GetEnvironmentVariable("FOOTBALLTRAY_ENABLE_LOCAL_DAEMON"), "1", StringComparison.OrdinalIgnoreCase);
        if (!isLocalDaemonExplicitlyEnabled && !_isCustomHttpClient)
        {
            _useStandaloneNativeEngine = true;
            return;
        }

        if (_lastProbeTime == DateTimeOffset.MinValue)
        {
            // Initial probe with fast 1.5s probe to check if Tier 1 server responds
            bool isHealthy = await CheckBackendHealthFastAsync(ct);
            _lastProbeTime = DateTimeOffset.UtcNow;
            if (!isHealthy)
            {
                _useStandaloneNativeEngine = true;
                AppLogger.Info("ApiClient", "StandaloneModeActivated", "Tier 1 server is not reachable. Switched to In-Process Native C# Direct Telemetry Engine.");
            }
            else
            {
                _useStandaloneNativeEngine = false;
            }
            return;
        }

        if (_useStandaloneNativeEngine && DateTimeOffset.UtcNow - _lastProbeTime > ProbeInterval)
        {
            _lastProbeTime = DateTimeOffset.UtcNow;
            if (Interlocked.CompareExchange(ref _probingLock, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        bool isHealthy = await CheckBackendHealthFastAsync(CancellationToken.None);
                        if (isHealthy)
                        {
                            _useStandaloneNativeEngine = false;
                            AppLogger.Info("ApiClient", "DaemonReconnected", "Tier 1 server is online. Switched to Tier 1 daemon.");
                        }
                    }
                    catch
                    {
                        // Ignore background probe failure
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _probingLock, 0);
                    }
                });
            }
        }
    }

    private void ActivateStandaloneMode(string reason)
    {
        if (!_enableNativeFallback)
        {
            return;
        }

        if (!_useStandaloneNativeEngine)
        {
            _useStandaloneNativeEngine = true;
            _lastProbeTime = DateTimeOffset.UtcNow;
            AppLogger.Info("ApiClient", "FailoverToNative", $"Local daemon unreachable ({reason}). Switched to in-process Native C# Telemetry Engine.");
        }
    }

    private async Task<bool> CheckBackendHealthFastAsync(CancellationToken ct = default)
    {
        if (_httpClient.BaseAddress == null) return false;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
            using var response = await _httpClient.GetAsync("/health", HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    /// <summary>
    /// Fetches the tray match collection from /api/v2/tray with optional date, followed teams, and ETag 304 validation.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public Task<TrayApiResponse> GetTrayAsync(string? ifNoneMatch, CancellationToken cancellationToken) =>
        GetTrayAsync(ifNoneMatch, null, null, null, null, cancellationToken);

    public Task<TrayApiResponse> GetTrayAsync(
        string? ifNoneMatch,
        DateTime? date,
        IEnumerable<string>? teams,
        CancellationToken cancellationToken) =>
        GetTrayAsync(ifNoneMatch, date, teams, null, null, cancellationToken);

    public async Task<TrayApiResponse> GetTrayAsync(
        string? ifNoneMatch = null,
        DateTime? date = null,
        IEnumerable<string>? teams = null,
        IEnumerable<string>? competitions = null,
        string? pinnedMatchId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var effectiveDate = date ?? DateTime.Today;
            var nativeRes = await _nativeProvider.GetTrayForDateAsync(
                effectiveDate.ToString("yyyy-MM-dd"),
                ifNoneMatch,
                teams,
                competitions,
                pinnedMatchId,
                cancellationToken);

            if (nativeRes.IsSuccess && nativeRes.Envelope != null)
            {
                _ = _cacheService.SaveTrayDateCacheAsync(effectiveDate.ToString("yyyy-MM-dd"), nativeRes.Envelope, CancellationToken.None);
                return nativeRes;
            }

            // Tier 3 Offline Disk Fallback
            var offlineCached = await _cacheService.LoadTrayDateCacheAsync(effectiveDate.ToString("yyyy-MM-dd"), cancellationToken);
            if (offlineCached != null)
            {
                offlineCached.Stale = true;
                if (offlineCached.Data != null)
                {
                    offlineCached.Data.CachedFallback = true;
                }
                return TrayApiResponse.Success(offlineCached, offlineCached.ETag ?? offlineCached.Data?.ETag, quotaExhausted: false, cachedFallback: true);
            }

            return nativeRes;
        }

        try
        {
            var uriBuilder = new System.Text.StringBuilder("/api/v2/tray?");
            var effectiveDate = date ?? DateTime.Today;
            uriBuilder.Append($"date={effectiveDate:yyyy-MM-dd}&");
            if (teams != null)
            {
                var teamList = teams.Where(t => !string.IsNullOrWhiteSpace(t))
                                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                if (teamList.Count > 0)
                {
                    uriBuilder.Append($"teams={Uri.EscapeDataString(string.Join(",", teamList))}&");
                }
            }
            if (competitions != null)
            {
                var compList = competitions.Where(c => !string.IsNullOrWhiteSpace(c))
                                           .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                           .ToList();
                if (compList.Count > 0)
                {
                    uriBuilder.Append($"competitions={Uri.EscapeDataString(string.Join(",", compList))}&");
                }
            }
            if (!string.IsNullOrWhiteSpace(pinnedMatchId))
            {
                uriBuilder.Append($"pinnedMatchId={Uri.EscapeDataString(pinnedMatchId.Trim())}&");
            }
            string uri = uriBuilder.ToString().TrimEnd('?', '&');
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            string? effectiveEtag = ifNoneMatch;
            if (string.IsNullOrWhiteSpace(effectiveEtag) && _etagsByUrl.TryGetValue(uri, out var cachedEtag))
            {
                effectiveEtag = cachedEtag;
            }

            if (!string.IsNullOrWhiteSpace(effectiveEtag))
            {
                var cleanEtag = effectiveEtag.Trim();
                if (!cleanEtag.StartsWith("\"") && !cleanEtag.StartsWith("W/\""))
                {
                    cleanEtag = $"\"{cleanEtag}\"";
                }
                request.Headers.TryAddWithoutValidation("If-None-Match", cleanEtag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            bool quotaExhaustedHeader = false;
            if (response.Headers.Contains("X-Quota-Exhausted"))
            {
                var val = response.Headers.GetValues("X-Quota-Exhausted").FirstOrDefault();
                quotaExhaustedHeader = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
            }

            string? newEtag = response.Headers.ETag?.Tag ?? response.Headers.ETag?.ToString();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                string? matchedEtag = newEtag ?? effectiveEtag;
                if (!string.IsNullOrWhiteSpace(matchedEtag))
                {
                    _etagsByUrl[uri] = matchedEtag;
                }

                if (_payloadsByUrl.TryGetValue(uri, out var cachedEnvelope) && cachedEnvelope?.Data != null)
                {
                    cachedEnvelope.Data.IsNotModified = true;
                    return new TrayApiResponse
                    {
                        IsSuccess = true,
                        IsNotModified = true,
                        ETag = matchedEtag,
                        QuotaExhausted = quotaExhaustedHeader || cachedEnvelope.QuotaExhausted || (cachedEnvelope.Data?.QuotaExhausted ?? false),
                        CachedFallback = cachedEnvelope.CachedFallback || (cachedEnvelope.Data?.CachedFallback ?? false),
                        Envelope = cachedEnvelope
                    };
                }

                return TrayApiResponse.NotModified(matchedEtag, quotaExhaustedHeader);
            }

            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync(contentStream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto, cancellationToken);

                if (envelope == null)
                {
                    return TrayApiResponse.Error("Failed to deserialize tray response envelope.");
                }

                if (!TrayPayloadValidator.IsValid(envelope, out var validationError))
                {
                    return TrayApiResponse.Error($"Malformed tray response payload: {validationError}");
                }

                if (quotaExhaustedHeader)
                {
                    envelope.QuotaExhausted = true;
                    if (envelope.Data != null)
                    {
                        envelope.Data.QuotaExhausted = true;
                    }
                }

                if (!string.IsNullOrWhiteSpace(newEtag))
                {
                    _etagsByUrl[uri] = newEtag;
                }
                _payloadsByUrl[uri] = envelope;
                _ = _cacheService.SaveTrayDateCacheAsync(effectiveDate.ToString("yyyy-MM-dd"), envelope, CancellationToken.None);

                return TrayApiResponse.Success(envelope, newEtag, quotaExhaustedHeader);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return TrayApiResponse.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var effectiveDate = date ?? DateTime.Today;
                var nativeRes = await _nativeProvider.GetTrayForDateAsync(
                    effectiveDate.ToString("yyyy-MM-dd"),
                    ifNoneMatch,
                    teams,
                    competitions,
                    pinnedMatchId,
                    cancellationToken);

                if (nativeRes.IsSuccess && nativeRes.Envelope != null)
                {
                    _ = _cacheService.SaveTrayDateCacheAsync(effectiveDate.ToString("yyyy-MM-dd"), nativeRes.Envelope, CancellationToken.None);
                    return nativeRes;
                }

                // Tier 3 Offline Disk Fallback
                var offlineCached = await _cacheService.LoadTrayDateCacheAsync(effectiveDate.ToString("yyyy-MM-dd"), cancellationToken);
                if (offlineCached != null)
                {
                    offlineCached.Stale = true;
                    if (offlineCached.Data != null)
                    {
                        offlineCached.Data.CachedFallback = true;
                    }
                    return TrayApiResponse.Success(offlineCached, offlineCached.ETag ?? offlineCached.Data?.ETag, quotaExhausted: false, cachedFallback: true);
                }

                return nativeRes;
            }

            return TrayApiResponse.Error($"Failed to fetch tray data from daemon: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches lightweight live tray matches from /api/v2/tray/live with ETag 304 support.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public async Task<TrayApiResponse> GetLiveTrayAsync(
        string? ifNoneMatch = null,
        IEnumerable<string>? teams = null,
        IEnumerable<string>? competitions = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var nativeRes = await _nativeProvider.GetTrayForDateAsync(
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                ifNoneMatch,
                teams,
                competitions,
                null,
                cancellationToken);

            if (nativeRes.IsSuccess && nativeRes.Envelope != null)
            {
                _ = _cacheService.SaveTrayDateCacheAsync(DateTime.UtcNow.ToString("yyyy-MM-dd"), nativeRes.Envelope, CancellationToken.None);
                return nativeRes;
            }

            var offlineCached = await _cacheService.LoadTrayDateCacheAsync(DateTime.UtcNow.ToString("yyyy-MM-dd"), cancellationToken);
            if (offlineCached != null)
            {
                offlineCached.Stale = true;
                if (offlineCached.Data != null)
                {
                    offlineCached.Data.CachedFallback = true;
                }
                return TrayApiResponse.Success(offlineCached, offlineCached.ETag ?? offlineCached.Data?.ETag, quotaExhausted: false, cachedFallback: true);
            }

            return nativeRes;
        }

        try
        {
            var uriBuilder = new System.Text.StringBuilder("/api/v2/tray/live?");
            if (teams != null)
            {
                var teamList = teams.Where(t => !string.IsNullOrWhiteSpace(t))
                                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                if (teamList.Count > 0)
                {
                    uriBuilder.Append($"teams={Uri.EscapeDataString(string.Join(",", teamList))}&");
                }
            }
            if (competitions != null)
            {
                var compList = competitions.Where(c => !string.IsNullOrWhiteSpace(c))
                                           .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                           .ToList();
                if (compList.Count > 0)
                {
                    uriBuilder.Append($"competitions={Uri.EscapeDataString(string.Join(",", compList))}&");
                }
            }
            string uri = uriBuilder.ToString().TrimEnd('?', '&');
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            string? effectiveEtag = ifNoneMatch;
            if (string.IsNullOrWhiteSpace(effectiveEtag) && _etagsByUrl.TryGetValue(uri, out var cachedEtag))
            {
                effectiveEtag = cachedEtag;
            }

            if (!string.IsNullOrWhiteSpace(effectiveEtag))
            {
                var cleanEtag = effectiveEtag.Trim();
                if (!cleanEtag.StartsWith("\"") && !cleanEtag.StartsWith("W/\""))
                {
                    cleanEtag = $"\"{cleanEtag}\"";
                }
                request.Headers.TryAddWithoutValidation("If-None-Match", cleanEtag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            bool quotaExhaustedHeader = false;
            if (response.Headers.Contains("X-Quota-Exhausted"))
            {
                var val = response.Headers.GetValues("X-Quota-Exhausted").FirstOrDefault();
                quotaExhaustedHeader = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
            }

            bool cachedFallbackHeader = false;
            if (response.Headers.Contains("X-Cached-Fallback"))
            {
                var val = response.Headers.GetValues("X-Cached-Fallback").FirstOrDefault();
                cachedFallbackHeader = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
            }

            string? newEtag = response.Headers.ETag?.ToString();

            // HTTP 304 Not Modified: Zero deserialization overhead
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                string? matchedEtag = newEtag ?? effectiveEtag;
                if (!string.IsNullOrWhiteSpace(matchedEtag))
                {
                    _etagsByUrl[uri] = matchedEtag;
                }
                return TrayApiResponse.NotModified(matchedEtag, quotaExhaustedHeader, cachedFallbackHeader);
            }

            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync(
                    contentStream,
                    FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto,
                    cancellationToken);

                if (envelope == null)
                {
                    return TrayApiResponse.Error("Failed to deserialize live tray envelope.");
                }

                if (!TrayPayloadValidator.IsValid(envelope, out var validationError))
                {
                    return TrayApiResponse.Error($"Malformed live tray response payload: {validationError}");
                }

                if (quotaExhaustedHeader)
                {
                    envelope.QuotaExhausted = true;
                    if (envelope.Data != null) envelope.Data.QuotaExhausted = true;
                }
                if (cachedFallbackHeader)
                {
                    envelope.CachedFallback = true;
                    if (envelope.Data != null) envelope.Data.CachedFallback = true;
                }

                if (!string.IsNullOrWhiteSpace(newEtag))
                {
                    _etagsByUrl[uri] = newEtag;
                }

                _ = _cacheService.SaveTrayDateCacheAsync(DateTime.UtcNow.ToString("yyyy-MM-dd"), envelope, CancellationToken.None);

                return TrayApiResponse.Success(envelope, newEtag, quotaExhaustedHeader, cachedFallbackHeader);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return TrayApiResponse.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var nativeRes = await _nativeProvider.GetTrayForDateAsync(
                    DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    ifNoneMatch,
                    teams,
                    competitions,
                    null,
                    cancellationToken);

                if (nativeRes.IsSuccess && nativeRes.Envelope != null)
                {
                    _ = _cacheService.SaveTrayDateCacheAsync(DateTime.UtcNow.ToString("yyyy-MM-dd"), nativeRes.Envelope, CancellationToken.None);
                    return nativeRes;
                }

                var offlineCached = await _cacheService.LoadTrayDateCacheAsync(DateTime.UtcNow.ToString("yyyy-MM-dd"), cancellationToken);
                if (offlineCached != null)
                {
                    offlineCached.Stale = true;
                    if (offlineCached.Data != null)
                    {
                        offlineCached.Data.CachedFallback = true;
                    }
                    return TrayApiResponse.Success(offlineCached, offlineCached.ETag ?? offlineCached.Data?.ETag, quotaExhausted: false, cachedFallback: true);
                }

                return nativeRes;
            }

            return TrayApiResponse.Error($"Failed to fetch live tray data from daemon: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches full match details and timeline from /api/v2/matches/:id with ETag 304 support.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public Task<MatchDetailApiResponse> GetMatchDetailsAsync(string matchId, CancellationToken cancellationToken = default) =>
        GetMatchDetailsAsync(matchId, null, cancellationToken);

    public async Task<MatchDetailApiResponse> GetMatchDetailsAsync(
        string matchId,
        string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return MatchDetailApiResponse.Error("Match ID cannot be empty.");
        }

        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var detail = await _nativeProvider.GetMatchDetailsAsync(matchId, cancellationToken);
            if (detail != null)
            {
                var env = new ApiResponseEnvelope<MatchDetailDto>
                {
                    SchemaVersion = "2.0",
                    RequestId = Guid.NewGuid().ToString("N"),
                    Version = 2,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Stale = false,
                    Data = detail
                };
                _ = _cacheService.SaveMatchDetailCacheAsync(matchId, env, CancellationToken.None);
                return MatchDetailApiResponse.Success(env);
            }

            // Tier 3 Offline Disk Fallback
            var offlineCached = await _cacheService.LoadMatchDetailCacheAsync(matchId, cancellationToken);
            if (offlineCached != null)
            {
                offlineCached.Stale = true;
                return MatchDetailApiResponse.Success(offlineCached);
            }

            return MatchDetailApiResponse.Error($"Match center details not found for '{matchId}'.");
        }

        try
        {
            string uri = $"/api/v2/match/{Uri.EscapeDataString(matchId.Trim())}";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            string? effectiveEtag = ifNoneMatch;
            if (string.IsNullOrWhiteSpace(effectiveEtag) && _etagsByUrl.TryGetValue(uri, out var cachedEtag))
            {
                effectiveEtag = cachedEtag;
            }

            if (!string.IsNullOrWhiteSpace(effectiveEtag))
            {
                var cleanEtag = effectiveEtag.Trim();
                if (!cleanEtag.StartsWith("\"") && !cleanEtag.StartsWith("W/\""))
                {
                    cleanEtag = $"\"{cleanEtag}\"";
                }
                request.Headers.TryAddWithoutValidation("If-None-Match", cleanEtag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            string? newEtag = response.Headers.ETag?.ToString();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                string? matchedEtag = newEtag ?? effectiveEtag;
                if (!string.IsNullOrWhiteSpace(matchedEtag))
                {
                    _etagsByUrl[uri] = matchedEtag;
                }
                return MatchDetailApiResponse.NotModified(matchedEtag);
            }

            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync(
                    contentStream,
                    FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto,
                    cancellationToken);

                if (envelope == null || envelope.Data == null)
                {
                    return MatchDetailApiResponse.Error("Failed to deserialize match detail envelope.");
                }

                if (!string.IsNullOrWhiteSpace(newEtag))
                {
                    _etagsByUrl[uri] = newEtag;
                }

                _ = _cacheService.SaveMatchDetailCacheAsync(matchId, envelope, CancellationToken.None);

                return MatchDetailApiResponse.Success(envelope, newEtag);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return MatchDetailApiResponse.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var detail = await _nativeProvider.GetMatchDetailsAsync(matchId, cancellationToken);
                if (detail != null)
                {
                    var env = new ApiResponseEnvelope<MatchDetailDto>
                    {
                        SchemaVersion = "2.0",
                        RequestId = Guid.NewGuid().ToString("N"),
                        Version = 2,
                        GeneratedAt = DateTimeOffset.UtcNow,
                        ServerTime = DateTimeOffset.UtcNow,
                        Stale = false,
                        Data = detail
                    };
                    _ = _cacheService.SaveMatchDetailCacheAsync(matchId, env, CancellationToken.None);
                    return MatchDetailApiResponse.Success(env);
                }

                // Tier 3 Offline Disk Fallback
                var offlineCached = await _cacheService.LoadMatchDetailCacheAsync(matchId, cancellationToken);
                if (offlineCached != null)
                {
                    offlineCached.Stale = true;
                    return MatchDetailApiResponse.Success(offlineCached);
                }
            }

            return MatchDetailApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches clubs/teams in the catalog via /api/v2/catalog/teams?q={query}.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public async Task<CatalogSearchApiResponse<V2CatalogTeamDto>> SearchTeamsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var emptyEnvelope = new ApiResponseEnvelope<List<V2CatalogTeamDto>>
            {
                Data = new List<V2CatalogTeamDto>(),
                SchemaVersion = "2.0",
                Version = 2
            };
            return CatalogSearchApiResponse<V2CatalogTeamDto>.Success(emptyEnvelope);
        }

        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var teams = await _nativeProvider.SearchTeamsAsync(query, limit, cancellationToken);
            var env = new ApiResponseEnvelope<List<V2CatalogTeamDto>>
            {
                SchemaVersion = "2.0",
                RequestId = Guid.NewGuid().ToString("N"),
                Version = 2,
                GeneratedAt = DateTimeOffset.UtcNow,
                ServerTime = DateTimeOffset.UtcNow,
                Stale = false,
                Data = teams
            };
            return CatalogSearchApiResponse<V2CatalogTeamDto>.Success(env);
        }

        try
        {
            string url = $"/api/v2/catalog/teams?q={Uri.EscapeDataString(query.Trim())}&limit={Math.Clamp(limit, 1, 50)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync(contentStream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeListV2CatalogTeamDto, cancellationToken);

                if (envelope == null || envelope.Data == null)
                {
                    return CatalogSearchApiResponse<V2CatalogTeamDto>.Error("Failed to deserialize catalog teams envelope.");
                }

                return CatalogSearchApiResponse<V2CatalogTeamDto>.Success(envelope);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return CatalogSearchApiResponse<V2CatalogTeamDto>.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var teams = await _nativeProvider.SearchTeamsAsync(query, limit, cancellationToken);
                var env = new ApiResponseEnvelope<List<V2CatalogTeamDto>>
                {
                    SchemaVersion = "2.0",
                    RequestId = Guid.NewGuid().ToString("N"),
                    Version = 2,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Stale = false,
                    Data = teams
                };
                return CatalogSearchApiResponse<V2CatalogTeamDto>.Success(env);
            }

            return CatalogSearchApiResponse<V2CatalogTeamDto>.Error($"Search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches leagues/competitions in the catalog via /api/v2/catalog/competitions?q={query}.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public async Task<CatalogSearchApiResponse<V2CatalogCompetitionDto>> SearchCompetitionsAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var emptyEnvelope = new ApiResponseEnvelope<List<V2CatalogCompetitionDto>>
            {
                Data = new List<V2CatalogCompetitionDto>(),
                SchemaVersion = "2.0",
                Version = 2
            };
            return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Success(emptyEnvelope);
        }

        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var comps = await _nativeProvider.SearchCompetitionsAsync(query, limit, cancellationToken);
            var env = new ApiResponseEnvelope<List<V2CatalogCompetitionDto>>
            {
                SchemaVersion = "2.0",
                RequestId = Guid.NewGuid().ToString("N"),
                Version = 2,
                GeneratedAt = DateTimeOffset.UtcNow,
                ServerTime = DateTimeOffset.UtcNow,
                Stale = false,
                Data = comps
            };
            return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Success(env);
        }

        try
        {
            string url = $"/api/v2/catalog/competitions?q={Uri.EscapeDataString(query.Trim())}&limit={Math.Clamp(limit, 1, 50)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync(contentStream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeListV2CatalogCompetitionDto, cancellationToken);

                if (envelope == null || envelope.Data == null)
                {
                    return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Error("Failed to deserialize catalog competitions envelope.");
                }

                return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Success(envelope);
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var comps = await _nativeProvider.SearchCompetitionsAsync(query, limit, cancellationToken);
                var env = new ApiResponseEnvelope<List<V2CatalogCompetitionDto>>
                {
                    SchemaVersion = "2.0",
                    RequestId = Guid.NewGuid().ToString("N"),
                    Version = 2,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Stale = false,
                    Data = comps
                };
                return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Success(env);
            }

            return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Error($"Search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pushes the user's followed competitions and teams to the daemon via POST /api/v2/preferences.
    /// In standalone mode, preferences are saved locally and returns true immediately.
    /// </summary>
    public async Task<bool> SyncPreferencesAsync(
        IEnumerable<string> competitionIds,
        IEnumerable<string> teamIds,
        string? activeProvider = null,
        CancellationToken cancellationToken = default)
    {
        if (_useStandaloneNativeEngine)
        {
            return true;
        }

        try
        {
            var payload = new SyncPreferencesRequestDto
            {
                CompetitionIds = competitionIds.ToList(),
                TeamIds = teamIds.ToList(),
                ActiveProvider = activeProvider
            };

            var json = JsonSerializer.Serialize(payload, FootballJsonSerializerContext.Default.SyncPreferencesRequestDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/v2/preferences", content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FootballApiClient] Failed to sync preferences: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Updates the pinned match ID on the daemon via POST /api/v2/preferences.
    /// In standalone mode, preferences are saved locally and returns true immediately.
    /// </summary>
    public async Task<bool> UpdatePinnedMatchAsync(string? pinnedMatchId, CancellationToken cancellationToken = default)
    {
        if (_useStandaloneNativeEngine)
        {
            return true;
        }

        try
        {
            var payload = new
            {
                pinnedMatchId = pinnedMatchId,
                pinnedMatchIds = string.IsNullOrEmpty(pinnedMatchId) ? Array.Empty<string>() : new[] { pinnedMatchId }
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/api/v2/preferences", content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FootballApiClient] Failed to update pinned match: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fetches full competition standings, top scorers, and top assists via GET /api/v2/competitions/{id}/hub.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public async Task<CompetitionHubApiResponse> GetCompetitionHubAsync(
        string competitionId,
        string? name = null,
        string? logoUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(competitionId))
        {
            return CompetitionHubApiResponse.Error("Competition ID cannot be empty.");
        }

        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var hub = await _nativeProvider.GetCompetitionHubAsync(competitionId, name, logoUrl, cancellationToken);
            if (hub != null)
            {
                var env = new ApiResponseEnvelope<CompetitionHubDto>
                {
                    SchemaVersion = "2.0",
                    RequestId = Guid.NewGuid().ToString("N"),
                    Version = 2,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Stale = false,
                    Data = hub
                };
                _ = _cacheService.SaveCompetitionHubCacheAsync(competitionId, env, CancellationToken.None);
                return CompetitionHubApiResponse.Success(env);
            }

            // Tier 3 Offline Disk Fallback
            var offlineCached = await _cacheService.LoadCompetitionHubCacheAsync(competitionId, cancellationToken);
            if (offlineCached != null)
            {
                offlineCached.Stale = true;
                return CompetitionHubApiResponse.Success(offlineCached);
            }

            return CompetitionHubApiResponse.Error($"Competition hub data not found for '{competitionId}'.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var sb = new System.Text.StringBuilder($"/api/v2/competitions/{Uri.EscapeDataString(competitionId.Trim())}/hub");
        var hasQuery = false;
        if (!string.IsNullOrWhiteSpace(name))
        {
            sb.Append(hasQuery ? "&" : "?");
            sb.Append("name=").Append(Uri.EscapeDataString(name.Trim()));
            hasQuery = true;
        }
        if (!string.IsNullOrWhiteSpace(logoUrl))
        {
            sb.Append(hasQuery ? "&" : "?");
            sb.Append("logoUrl=").Append(Uri.EscapeDataString(logoUrl.Trim()));
            hasQuery = true;
        }
        string url = sb.ToString();
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
                try
                {
                    var envelope = JsonSerializer.Deserialize(
                        json,
                        FootballJsonSerializerContext.Default.ApiResponseEnvelopeCompetitionHubDto
                    );

                    if (envelope?.Data == null)
                    {
                        var snippet = json.Length > 200 ? json.Substring(0, 200) + "..." : json;
                        Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "CompetitionHubNullData", $"Competition hub envelope Data was null for '{competitionId}'. Payload: {snippet}");
                        return CompetitionHubApiResponse.Error("Failed to deserialize competition hub response.");
                    }

                    _ = _cacheService.SaveCompetitionHubCacheAsync(competitionId, envelope, CancellationToken.None);
                    return CompetitionHubApiResponse.Success(envelope);
                }
                catch (JsonException jEx)
                {
                    var snippet = json.Length > 300 ? json.Substring(0, 300) + "..." : json;
                    Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "CompetitionHubDeserializationFailed", $"Failed to parse JSON for '{competitionId}': {jEx.Message}. Payload: {snippet}", jEx);
                    return CompetitionHubApiResponse.Error($"JSON deserialization error: {jEx.Message}");
                }
            }

            var errorBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
            Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "CompetitionHubHttpError", $"HTTP {(int)response.StatusCode} for '{url}': {errorBody}");
            return CompetitionHubApiResponse.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var hub = await _nativeProvider.GetCompetitionHubAsync(competitionId, name, logoUrl, linkedCts.Token);
                if (hub != null)
                {
                    var env = new ApiResponseEnvelope<CompetitionHubDto>
                    {
                        SchemaVersion = "2.0",
                        RequestId = Guid.NewGuid().ToString("N"),
                        Version = 2,
                        GeneratedAt = DateTimeOffset.UtcNow,
                        ServerTime = DateTimeOffset.UtcNow,
                        Stale = false,
                        Data = hub
                    };
                    _ = _cacheService.SaveCompetitionHubCacheAsync(competitionId, env, CancellationToken.None);
                    return CompetitionHubApiResponse.Success(env);
                }

                // Tier 3 Offline Disk Fallback
                var offlineCached = await _cacheService.LoadCompetitionHubCacheAsync(competitionId, cancellationToken);
                if (offlineCached != null)
                {
                    offlineCached.Stale = true;
                    return CompetitionHubApiResponse.Success(offlineCached);
                }
            }

            Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "CompetitionHubNetworkError", $"Network error for '{url}': [{ex.GetType().Name}] {ex.Message}", ex);
            return CompetitionHubApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches team hub details, fixtures, form, squad, and standings.
    /// Falls back seamlessly to In-Process Native Telemetry when local daemon is absent.
    /// </summary>
    public async Task<TeamHubApiResponse> GetTeamHubAsync(
        string teamId,
        string? name = null,
        string? logoUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            return TeamHubApiResponse.Error("Team ID cannot be empty.");
        }

        await EnsureEngineStateAsync(cancellationToken);

        if (_useStandaloneNativeEngine)
        {
            var hub = await _nativeProvider.GetTeamHubAsync(teamId, name, logoUrl, cancellationToken);
            if (hub != null)
            {
                var env = new ApiResponseEnvelope<TeamHubDto>
                {
                    SchemaVersion = "2.0",
                    RequestId = Guid.NewGuid().ToString("N"),
                    Version = 2,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Stale = false,
                    Data = hub
                };
                _ = _cacheService.SaveTeamHubCacheAsync(teamId, env, CancellationToken.None);
                return TeamHubApiResponse.Success(env);
            }

            // Tier 3 Offline Disk Fallback
            var offlineCached = await _cacheService.LoadTeamHubCacheAsync(teamId, cancellationToken);
            if (offlineCached != null)
            {
                offlineCached.Stale = true;
                return TeamHubApiResponse.Success(offlineCached);
            }

            return TeamHubApiResponse.Error($"Team hub data not found for '{teamId}'.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

        string url = $"{_httpClient.BaseAddress}api/v2/teams/{Uri.EscapeDataString(teamId)}/hub";
        try
        {
            using var response = await _httpClient.GetAsync(url, linkedCts.Token);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(linkedCts.Token);
                string snippet = json.Length > 200 ? json[..200] : json;

                try
                {
                    var envelope = JsonSerializer.Deserialize(
                        json,
                        FootballJsonSerializerContext.Default.ApiResponseEnvelopeTeamHubDto
                    );

                    if (envelope?.Data == null)
                    {
                        Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "TeamHubNullData", $"Team hub envelope Data was null for '{teamId}'. Payload: {snippet}");
                        return TeamHubApiResponse.Error("Failed to deserialize team hub response.");
                    }

                    _ = _cacheService.SaveTeamHubCacheAsync(teamId, envelope, CancellationToken.None);
                    return TeamHubApiResponse.Success(envelope);
                }
                catch (JsonException jEx)
                {
                    Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "TeamHubDeserializationFailed", $"Failed to parse JSON for '{teamId}': {jEx.Message}. Payload: {snippet}", jEx);
                    return TeamHubApiResponse.Error($"JSON deserialization error: {jEx.Message}");
                }
            }

            string errorBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
            Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "TeamHubHttpError", $"HTTP {(int)response.StatusCode} for '{url}': {errorBody}");
            return TeamHubApiResponse.Error($"Server returned HTTP {(int)response.StatusCode}: {errorBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_enableNativeFallback)
            {
                ActivateStandaloneMode(ex.Message);
                var hub = await _nativeProvider.GetTeamHubAsync(teamId, name, logoUrl, linkedCts.Token);
                if (hub != null)
                {
                    var env = new ApiResponseEnvelope<TeamHubDto>
                    {
                        SchemaVersion = "2.0",
                        RequestId = Guid.NewGuid().ToString("N"),
                        Version = 2,
                        GeneratedAt = DateTimeOffset.UtcNow,
                        ServerTime = DateTimeOffset.UtcNow,
                        Stale = false,
                        Data = hub
                    };
                    _ = _cacheService.SaveTeamHubCacheAsync(teamId, env, CancellationToken.None);
                    return TeamHubApiResponse.Success(env);
                }

                // Tier 3 Offline Disk Fallback
                var offlineCached = await _cacheService.LoadTeamHubCacheAsync(teamId, cancellationToken);
                if (offlineCached != null)
                {
                    offlineCached.Stale = true;
                    return TeamHubApiResponse.Success(offlineCached);
                }
            }

            Diagnostics.AppDiagnosticsLogger.LogError("FootballApiClient", "TeamHubNetworkError", $"Network error for '{url}': [{ex.GetType().Name}] {ex.Message}", ex);
            return TeamHubApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _nativeProvider.Dispose();
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
