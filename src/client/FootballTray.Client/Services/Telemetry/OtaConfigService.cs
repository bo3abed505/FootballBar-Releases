using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Serialization;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Telemetry;

/// <summary>
/// Over-the-air (OTA) dynamic configuration service for sports telemetry.
/// Loads cached or compiled defaults synchronously in 0ms on startup, and
/// asynchronously polls GitHub Releases in the background (with ETag 304 validation)
/// to apply live upstream endpoint hotfixes without client redeployments.
/// </summary>
public sealed class OtaConfigService : IDisposable
{
    public const string DefaultManifestUrl = "https://github.com/bo3abed505/FootballTray-Releases/releases/latest/download/ota-telemetry-manifest.json";

    private static readonly Lazy<OtaConfigService> _lazyInstance = new(() => new OtaConfigService());
    public static OtaConfigService Instance => _lazyInstance.Value;

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly string _cacheDirectory;
    private readonly string _cacheFilePath;
    private readonly object _stateLock = new();

    private OtaTelemetryManifestDto _currentManifest;
    private string? _currentETag;
    private DateTimeOffset? _lastCheckedAt;
    private CancellationTokenSource? _syncCts;
    private Task? _backgroundSyncTask;

    /// <summary>
    /// Event raised when a new OTA manifest version is fetched and activated.
    /// </summary>
    public event Action<OtaTelemetryManifestDto>? ManifestUpdated;

    public OtaTelemetryManifestDto CurrentManifest
    {
        get
        {
            lock (_stateLock)
            {
                return _currentManifest;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _currentManifest = value;
            }
        }
    }

    public string? CurrentETag
    {
        get
        {
            lock (_stateLock)
            {
                return _currentETag;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _currentETag = value;
            }
        }
    }

    public DateTimeOffset? LastCheckedAt
    {
        get
        {
            lock (_stateLock)
            {
                return _lastCheckedAt;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _lastCheckedAt = value;
            }
        }
    }

    public OtaConfigService(HttpClient? httpClient = null, string? customCacheDirectory = null)
    {
        _cacheDirectory = customCacheDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootballTray");
        _cacheFilePath = Path.Combine(_cacheDirectory, "ota-manifest-cache.json");

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "FootballTray-Client/1.3.0");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            _disposeClient = true;
        }

        // 0ms Cold Start: load cached manifest or fall back to compiled defaults synchronously
        var defaultManifest = CreateDefaultManifest();
        if (TryLoadDiskCache(out var cachedRecord) && cachedRecord?.Manifest != null)
        {
            if (Version.TryParse(cachedRecord.Manifest.ManifestVersion, out var cachedVer) &&
                Version.TryParse(defaultManifest.ManifestVersion, out var defaultVer) &&
                cachedVer < defaultVer)
            {
                _currentManifest = defaultManifest;
                _currentETag = null;
                _lastCheckedAt = null;
                AppLogger.Info("OTA", "UpgradeOverridden", $"Cached OTA manifest v{cachedVer} is older than compiled default v{defaultVer}. Using compiled default manifest v{defaultVer}.");
            }
            else
            {
                _currentManifest = cachedRecord.Manifest;
                _currentETag = cachedRecord.ETag;
                _lastCheckedAt = cachedRecord.LastCheckedAt;
                AppLogger.Info("OTA", "CacheLoaded", $"Loaded cached OTA telemetry manifest v{_currentManifest.ManifestVersion} (ETag: {_currentETag ?? "none"}).");
            }
        }
        else
        {
            _currentManifest = defaultManifest;
            _currentETag = null;
            _lastCheckedAt = null;
            AppLogger.Info("OTA", "DefaultLoaded", "No cached OTA telemetry manifest found. Initialized with compiled default manifest v1.3.0.");
        }
    }

    /// <summary>
    /// Begins background polling for OTA manifest updates.
    /// </summary>
    public void StartBackgroundSync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_backgroundSyncTask != null && !_backgroundSyncTask.IsCompleted)
            {
                return;
            }

            _syncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _backgroundSyncTask = Task.Run(() => BackgroundSyncLoopAsync(_syncCts.Token), _syncCts.Token);
        }
    }

    private async Task BackgroundSyncLoopAsync(CancellationToken ct)
    {
        // Initial delayed check (5 seconds after startup to prioritize UI responsiveness)
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppDiagnosticsLogger.LogError("OtaConfigService", "SyncLoopError", $"Background OTA sync failed: {ex.Message}", ex);
            }

            try
            {
                // Poll every 30 minutes
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Checks GitHub Releases for a newer telemetry manifest using conditional HTTP (ETag).
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, DefaultManifestUrl);

            string? etag = CurrentETag;
            if (!string.IsNullOrWhiteSpace(etag))
            {
                var cleanEtag = etag.Trim();
                if (!cleanEtag.StartsWith("\"") && !cleanEtag.StartsWith("W/\""))
                {
                    cleanEtag = $"\"{cleanEtag}\"";
                }
                req.Headers.TryAddWithoutValidation("If-None-Match", cleanEtag);
            }

            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

            if (res.StatusCode == HttpStatusCode.NotModified)
            {
                LastCheckedAt = DateTimeOffset.UtcNow;
                SaveDiskCache(CurrentManifest, CurrentETag, LastCheckedAt.Value);
                AppLogger.Info("OTA", "NotModified", "OTA telemetry manifest is unchanged (HTTP 304).");
                return true;
            }

            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync(ct);
                var manifest = JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.OtaTelemetryManifestDto);

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.ManifestVersion) || manifest.Endpoints == null)
                {
                    AppDiagnosticsLogger.LogError("OtaConfigService", "InvalidManifest", "Received empty or malformed OTA telemetry manifest.");
                    return false;
                }

                string? newEtag = res.Headers.ETag?.Tag ?? res.Headers.ETag?.ToString();
                var now = DateTimeOffset.UtcNow;

                CurrentManifest = manifest;
                CurrentETag = newEtag;
                LastCheckedAt = now;

                SaveDiskCache(manifest, newEtag, now);
                AppLogger.Info("OTA", "ManifestUpdated", $"Successfully applied OTA telemetry manifest v{manifest.ManifestVersion} (Mode: {manifest.RoutingMode}).");

                try
                {
                    ManifestUpdated?.Invoke(manifest);
                }
                catch (Exception callbackEx)
                {
                    AppDiagnosticsLogger.LogError("OtaConfigService", "CallbackError", $"ManifestUpdated listener threw an exception: {callbackEx.Message}", callbackEx);
                }

                return true;
            }

            AppLogger.Info("OTA", "HttpNonSuccess", $"OTA manifest check returned HTTP {(int)res.StatusCode}.");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Info("OTA", "FetchError", $"Unable to fetch OTA telemetry manifest: {ex.Message}");
            return false;
        }
    }

    private bool TryLoadDiskCache(out OtaManifestCacheRecord? record)
    {
        record = null;
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return false;
            }

            using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            record = JsonSerializer.Deserialize(stream, FootballJsonSerializerContext.Default.OtaManifestCacheRecord);
            return record?.Manifest != null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OTA] Failed to read cached manifest: {ex.Message}");
            return false;
        }
    }

    private void SaveDiskCache(OtaTelemetryManifestDto manifest, string? etag, DateTimeOffset checkedAt)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

            var record = new OtaManifestCacheRecord
            {
                Manifest = manifest,
                ETag = etag,
                LastCheckedAt = checkedAt
            };

            var tempFilePath = Path.Combine(_cacheDirectory, $"ota_{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, FootballJsonSerializerContext.Default.OtaManifestCacheRecord);
                stream.Flush();
            }

            if (File.Exists(_cacheFilePath))
            {
                try
                {
                    File.Replace(tempFilePath, _cacheFilePath, null);
                }
                catch
                {
                    File.Delete(_cacheFilePath);
                    File.Move(tempFilePath, _cacheFilePath);
                }
            }
            else
            {
                File.Move(tempFilePath, _cacheFilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OTA] Failed to persist manifest cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the standard compiled-in default manifest for offline and initial startup operation.
    /// </summary>
    public static OtaTelemetryManifestDto CreateDefaultManifest()
    {
        return new OtaTelemetryManifestDto
        {
            SchemaVersion = "1.0",
            ManifestVersion = "1.3.0",
            UpdatedAt = DateTimeOffset.UtcNow,
            RoutingMode = "auto",
            CloudBaseUrl = null,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            Endpoints = new OtaTelemetryEndpointsDto
            {
                MatchesByDateUrlTemplate = "https://www.fotmob.com/api/data/matches?date={date}",
                MatchesByDateFallbackUrlTemplate = "https://www.fotmob.com/api/matches?date={date}",
                MatchDetailsUrlTemplate = "https://www.fotmob.com/api/data/matchDetails?matchId={matchId}",
                MatchDetailsFallbackUrlTemplate = "https://www.fotmob.com/api/matchDetails?matchId={matchId}",
                LeagueHubUrlTemplate = "https://www.fotmob.com/api/data/leagues?id={leagueId}",
                LeagueHubFallbackUrlTemplate = "https://www.fotmob.com/api/leagues?id={leagueId}",
                TeamHubUrlTemplate = "https://www.fotmob.com/api/data/teams?id={teamId}",
                TeamHubFallbackUrlTemplate = "https://www.fotmob.com/api/teams?id={teamId}",
                SearchSuggestUrlTemplate = "https://www.fotmob.com/api/search/suggest?term={term}",
                TeamLogoUrlTemplate = "https://images.fotmob.com/image_resources/logo/teamlogo/{teamId}.png",
                LeagueLogoUrlTemplate = "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/{leagueId}.png"
            },
            CompetitionOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ft_comp_01m33dknccnejag93cdagjsr4a"] = 47,
                ["ft_comp_01m33dknc8ckgmj9z7898ah8ha"] = 87,
                ["ft_comp_01m33dkncabrryr4k6d9dktp40"] = 519,
                ["ft_comp_01m33dknc525afcaclweasteast0"] = 525,
                ["ft_comp_01m33dknc97emtt3fnzr1ytc9e"] = 42,
                ["ft_comp_01m33dknc7hy1q85dnpb713v5y"] = 73,
                ["ft_comp_01m33dknc6q5v0q4d89z37f8w8"] = 10216,
                ["ft_comp_01m33dknc5e4n9q4p4y9a9q2g7"] = 55,
                ["ft_comp_01m33dknc4d3m8p3n3x8z8p1f6"] = 54,
                ["ft_comp_01m33dknc3c2l7n2m2w7y7o0e5"] = 53,
                ["ft_comp_01m33dknc2b1k6m1l1v6x6n9d4"] = 132,
                ["ft_comp_01m33dknc1a0j5l0k0u5w5m8c3"] = 138,
                ["ft_comp_01m33dknc09zh4kzjztzv4l7b2"] = 536,
                ["ft_comp_01m33dknbz8yg3jyiysyu3k6a1"] = 130,
                ["ft_comp_01m33dkncdv0t7deehhz20dmn7"] = 289,
                ["ft_comp_01m33dknbzrhdx2en9yqjpsv5q"] = 77,
                ["ft_comp_01m3y7e15vjygs76sm21gbr1yd"] = 50,
                ["ft_comp_01m3y7e15z5x08y7s7k0jpage6"] = 9806,
                ["ft_comp_01m3y7e1608tk80wdrtgyqhf2x"] = 44,
                ["ft_comp_01m467y24drwpkfg9ax4xx88ze"] = 9806,
                ["comp_39"] = 47,
                ["comp_140"] = 87,
                ["comp_233"] = 519,
                ["comp_191"] = 519,
                ["comp_10650"] = 519,
                ["10650"] = 519,
                ["comp_519"] = 519,
                ["519"] = 519,
                ["comp_525"] = 525,
                ["525"] = 525,
                ["afc_champions_league"] = 525,
                ["afc champions league"] = 525,
                ["comp_2"] = 42,
                ["comp_3"] = 73,
                ["comp_848"] = 10216,
                ["comp_135"] = 55,
                ["comp_78"] = 54,
                ["comp_61"] = 53,
                ["comp_45"] = 132,
                ["comp_143"] = 138,
                ["comp_307"] = 536,
                ["comp_253"] = 130,
                ["comp_6"] = 289,
                ["comp_1"] = 77,
                ["comp_4"] = 50,
                ["comp_5"] = 9806,
                ["comp_9"] = 44,
                ["premier_league"] = 47,
                ["premier league"] = 47,
                ["pl"] = 47,
                ["champions_league"] = 42,
                ["champions league"] = 42,
                ["ucl"] = 42,
                ["europa_league"] = 73,
                ["europa league"] = 73,
                ["uel"] = 73,
                ["conference_league"] = 10216,
                ["conference league"] = 10216,
                ["uecl"] = 10216,
                ["la_liga"] = 87,
                ["la liga"] = 87,
                ["laliga"] = 87,
                ["serie_a"] = 55,
                ["serie a"] = 55,
                ["bundesliga"] = 54,
                ["ligue_1"] = 53,
                ["ligue 1"] = 53,
                ["fa_cup"] = 132,
                ["fa cup"] = 132,
                ["copa_del_rey"] = 138,
                ["copa del rey"] = 138,
                ["coppa_italia"] = 141,
                ["coppa italia"] = 141,
                ["dfb_pokal"] = 209,
                ["dfb pokal"] = 209,
                ["coupe_de_france"] = 134,
                ["coupe de france"] = 134,
                ["egyptian_premier_league"] = 519,
                ["egyptian premier league"] = 519,
                ["saudi_pro_league"] = 536,
                ["saudi pro league"] = 536,
                ["spl"] = 536,
                ["mls"] = 130,
                ["major_league_soccer"] = 130,
                ["championship"] = 48,
                ["eredivisie"] = 57,
                ["primeira_liga"] = 61,
                ["primeira liga"] = 61,
                ["fifa_world_cup"] = 77,
                ["world_cup"] = 77,
                ["euro"] = 50,
                ["nations_league"] = 9806,
                ["copa_america"] = 44,
                ["africa_cup_of_nations"] = 289,
                ["afcon"] = 289
            },
            PriorityLeagueIds = new List<int>
            {
                // Tier 1: Major International & Continental Competitions (INT)
                77, 42, 50, 289, 44, 78, 74, 73, 10216, 526, 527, 525, 45, 9806,
                // Tier 2: Egypt & MENA Top Competitions
                519, 675, 9470, 536, 687, 530, 535, 516, 538, 533,
                // Tier 3: Europe's "Big 5" Leagues & Domestic Cups
                47, 87, 55, 54, 53, 132, 133, 138, 141, 209, 134,
                // Tier 4: Top Global Secondary Leagues
                61, 57, 71, 268, 112, 130, 48, 40
            }
        };
    }

    public void Dispose()
    {
        _syncCts?.Cancel();
        _syncCts?.Dispose();
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
