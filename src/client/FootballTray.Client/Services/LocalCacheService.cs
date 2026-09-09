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
        _cacheFilePath = Path.Combine(_cacheDirectory, "tray.json");
    }

    public static string GetDefaultCacheDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootballTray");

    public string CacheFilePath => _cacheFilePath;

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
}

