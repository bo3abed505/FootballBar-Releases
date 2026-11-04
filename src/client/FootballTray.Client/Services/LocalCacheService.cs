using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Serialization;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public class CachedTrayPayload
{
    public ApiResponseEnvelope<TrayDataDto>? Envelope { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset CachedAt { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; }
}

public class LocalCacheService
{
    private readonly string _cacheDirectory;
    private readonly string _entitiesCacheDirectory;
    private readonly string _cacheFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = FootballJsonSerializerContext.Default,
    };

    public LocalCacheService(string? customDirectory = null)
    {
        _cacheDirectory = customDirectory ?? GetDefaultCacheDirectory();
        _entitiesCacheDirectory = Path.Combine(_cacheDirectory, "Cache");
        _cacheFilePath = Path.Combine(_cacheDirectory, "tray.json");

        // Prune expired cache files older than 14 days asynchronously on startup
        _ = Task.Run(() => PruneExpiredCaches());
    }

    public static string GetDefaultCacheDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootballTray");

    public string CacheFilePath => _cacheFilePath;
    public string EntitiesCacheDirectory => _entitiesCacheDirectory;

    /// <summary>
    /// Loads the cached tray payload from disk. Returns null if missing, corrupt, or structurally invalid.
    /// </summary>
    public async Task<CachedTrayPayload?> LoadCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return null;
            }

            using var stream = new FileStream(_cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var payload = await JsonSerializer.DeserializeAsync(stream, FootballJsonSerializerContext.Default.CachedTrayPayload, cancellationToken);

            if (payload?.Envelope == null)
            {
                return null;
            }

            if (!TrayPayloadValidator.IsValid(payload.Envelope, out var error))
            {
                System.Diagnostics.Debug.WriteLine($"[CACHE] Invalid or corrupt cache rejected: {error}");
                return null;
            }

            NormalizeConcludedMatches(payload.Envelope.Data?.Matches);

            return payload;
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable cache is safely ignored
            System.Diagnostics.Debug.WriteLine($"[CACHE] Failed to load cache: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Atomically writes the validated tray payload to disk using a temporary file.
    /// </summary>
    public async Task<bool> SaveCacheAsync(ApiResponseEnvelope<TrayDataDto> envelope, string? etag, CancellationToken cancellationToken = default)
    {
        // 1. Structural Validation before disk write
        if (!TrayPayloadValidator.IsValid(envelope, out var error))
        {
            System.Diagnostics.Debug.WriteLine($"[CACHE] Refused to save structurally invalid envelope: {error}");
            return false;
        }

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

            var now = DateTimeOffset.UtcNow;
            var payload = new CachedTrayPayload
            {
                Envelope = envelope,
                ETag = etag,
                CachedAt = now,
                LastValidatedAt = now,
            };

            var tempFilePath = Path.Combine(_cacheDirectory, $"tray_{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, payload, FootballJsonSerializerContext.Default.CachedTrayPayload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Atomic move/replace
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

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CACHE] Failed to save cache: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Updates the LastValidatedAt timestamp for the current cached payload upon receiving HTTP 304.
    /// </summary>
    public virtual async Task<bool> TouchLastValidatedAsync(string? etag, CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = await LoadCacheAsync(cancellationToken);
            if (cached?.Envelope != null)
            {
                cached.LastValidatedAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(etag))
                {
                    cached.ETag = etag;
                }
                return await SaveCacheAsync(cached.Envelope, cached.ETag, cancellationToken);
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CACHE] Failed to touch validation timestamp: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Clears the cached file if present.
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                File.Delete(_cacheFilePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CACHE] Failed to clear cache: {ex.Message}");
        }
    }

    #region Multi-Entity Offline Disk Cache (Tier 3)

    private static void NormalizeConcludedMatches(IEnumerable<TrayMatchDto>? matches)
    {
        if (matches == null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var m in matches)
        {
            if (m == null) continue;
            var state = (m.State ?? string.Empty).Trim().ToUpperInvariant();
            if (state is "FINISHED" or "FT" or "AET" or "PEN" or "FINISHED_AET" or "FINISHED_PEN" or "POSTPONED" or "CANCELLED")
            {
                m.ElapsedMinute = 0;
                m.ExtraMinute = 0;
                continue;
            }

            bool hasShootout = m.Score?.Penalty != null;
            bool isPastDate = m.KickoffTime != default && (now - m.KickoffTime).TotalHours > 12;
            bool isOvertimeThreshold = m.KickoffTime != default && (now - m.KickoffTime).TotalMinutes > 150;

            if (hasShootout)
            {
                m.State = "FINISHED_PEN";
                m.ElapsedMinute = 0;
                m.ExtraMinute = 0;
            }
            else if (isPastDate || isOvertimeThreshold)
            {
                m.State = "FINISHED";
                m.ElapsedMinute = 0;
                m.ExtraMinute = 0;
            }
        }
    }

    private static string SanitizeFileNameKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = rawKey.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == ' ')
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// Persists daily match schedules (tray_{date}.json) to disk for resilient offline browsing.
    /// </summary>
    public async Task<bool> SaveTrayDateCacheAsync(string dateKey, ApiResponseEnvelope<TrayDataDto> envelope, CancellationToken cancellationToken = default)
    {
        if (!TrayPayloadValidator.IsValid(envelope, out _)) return false;
        string safeKey = SanitizeFileNameKey(dateKey);
        string path = Path.Combine(_entitiesCacheDirectory, $"tray_{safeKey}.json");
        return await WriteAtomicEntityFileAsync(path, async stream =>
        {
            await JsonSerializer.SerializeAsync(stream, envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Loads daily match schedule for a specific date from disk if present.
    /// </summary>
    public async Task<ApiResponseEnvelope<TrayDataDto>?> LoadTrayDateCacheAsync(string dateKey, CancellationToken cancellationToken = default)
    {
        try
        {
            string safeKey = SanitizeFileNameKey(dateKey);
            string path = Path.Combine(_entitiesCacheDirectory, $"tray_{safeKey}.json");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var envelope = await JsonSerializer.DeserializeAsync(stream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto, cancellationToken);
            if (envelope == null || !TrayPayloadValidator.IsValid(envelope, out _)) return null;
            NormalizeConcludedMatches(envelope.Data?.Matches);
            return envelope;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists match details, 5-anchor lineups, stats, commentary (match_{matchId}.json) for offline Match Center viewing.
    /// </summary>
    public async Task<bool> SaveMatchDetailCacheAsync(string matchId, ApiResponseEnvelope<MatchDetailDto> envelope, CancellationToken cancellationToken = default)
    {
        if (envelope?.Data == null) return false;
        string safeKey = SanitizeFileNameKey(matchId);
        string path = Path.Combine(_entitiesCacheDirectory, $"match_{safeKey}.json");
        return await WriteAtomicEntityFileAsync(path, async stream =>
        {
            await JsonSerializer.SerializeAsync(stream, envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Loads match details for a specific match ID from disk if present.
    /// </summary>
    public async Task<ApiResponseEnvelope<MatchDetailDto>?> LoadMatchDetailCacheAsync(string matchId, CancellationToken cancellationToken = default)
    {
        try
        {
            string safeKey = SanitizeFileNameKey(matchId);
            string path = Path.Combine(_entitiesCacheDirectory, $"match_{safeKey}.json");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var envelope = await JsonSerializer.DeserializeAsync(stream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto, cancellationToken);
            if (envelope?.Data == null) return null;
            return envelope;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists competition standings and leaders (hub_{competitionKey}.json) for offline viewing.
    /// </summary>
    public async Task<bool> SaveCompetitionHubCacheAsync(string competitionKey, ApiResponseEnvelope<CompetitionHubDto> envelope, CancellationToken cancellationToken = default)
    {
        if (envelope?.Data == null) return false;
        string safeKey = SanitizeFileNameKey(competitionKey);
        string path = Path.Combine(_entitiesCacheDirectory, $"hub_{safeKey}.json");
        return await WriteAtomicEntityFileAsync(path, async stream =>
        {
            await JsonSerializer.SerializeAsync(stream, envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeCompetitionHubDto, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Loads competition standings and leaders from disk if present.
    /// </summary>
    public async Task<ApiResponseEnvelope<CompetitionHubDto>?> LoadCompetitionHubCacheAsync(string competitionKey, CancellationToken cancellationToken = default)
    {
        try
        {
            string safeKey = SanitizeFileNameKey(competitionKey);
            string path = Path.Combine(_entitiesCacheDirectory, $"hub_{safeKey}.json");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var envelope = await JsonSerializer.DeserializeAsync(stream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeCompetitionHubDto, cancellationToken);
            if (envelope?.Data == null) return null;
            return envelope;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Persists team hub information (team_{teamKey}.json) for offline viewing.
    /// </summary>
    public async Task<bool> SaveTeamHubCacheAsync(string teamKey, ApiResponseEnvelope<TeamHubDto> envelope, CancellationToken cancellationToken = default)
    {
        if (envelope?.Data == null) return false;
        string safeKey = SanitizeFileNameKey(teamKey);
        string path = Path.Combine(_entitiesCacheDirectory, $"team_{safeKey}.json");
        return await WriteAtomicEntityFileAsync(path, async stream =>
        {
            await JsonSerializer.SerializeAsync(stream, envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTeamHubDto, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Loads team hub information from disk if present.
    /// </summary>
    public async Task<ApiResponseEnvelope<TeamHubDto>?> LoadTeamHubCacheAsync(string teamKey, CancellationToken cancellationToken = default)
    {
        try
        {
            string safeKey = SanitizeFileNameKey(teamKey);
            string path = Path.Combine(_entitiesCacheDirectory, $"team_{safeKey}.json");
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var envelope = await JsonSerializer.DeserializeAsync(stream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTeamHubDto, cancellationToken);
            if (envelope?.Data == null) return null;
            return envelope;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> WriteAtomicEntityFileAsync(string targetPath, Func<Stream, Task> serializeAction, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_entitiesCacheDirectory))
            {
                Directory.CreateDirectory(_entitiesCacheDirectory);
            }

            string tempPath = Path.Combine(_entitiesCacheDirectory, $"{Path.GetFileNameWithoutExtension(targetPath)}_{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await serializeAction(stream);
                await stream.FlushAsync(ct);
            }

            if (File.Exists(targetPath))
            {
                try
                {
                    File.Replace(tempPath, targetPath, null);
                }
                catch
                {
                    File.Delete(targetPath);
                    File.Move(tempPath, targetPath);
                }
            }
            else
            {
                File.Move(tempPath, targetPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CACHE] WriteAtomicEntityFileAsync failed for '{targetPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Evicts cached entity files older than the specified age (default 14 days) to keep disk usage negligible.
    /// </summary>
    public void PruneExpiredCaches(TimeSpan? maxAge = null)
    {
        try
        {
            if (!Directory.Exists(_entitiesCacheDirectory)) return;

            var limit = DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(14));
            var dir = new DirectoryInfo(_entitiesCacheDirectory);

            foreach (var file in dir.EnumerateFiles("*.json"))
            {
                try
                {
                    if (file.LastWriteTimeUtc < limit)
                    {
                        file.Delete();
                    }
                }
                catch
                {
                    // Ignore transient file lock
                }
            }

            // Also prune any leftover temporary files (.tmp) older than 1 hour
            var tmpLimit = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in dir.EnumerateFiles("*.tmp"))
            {
                try
                {
                    if (file.LastWriteTimeUtc < tmpLimit)
                    {
                        file.Delete();
                    }
                }
                catch
                {
                    // Ignore transient lock
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CACHE] PruneExpiredCaches failed: {ex.Message}");
        }
    }

    #endregion
}

