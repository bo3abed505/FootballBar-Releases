using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public class CachedTrayPayload
{
    public ApiResponseEnvelope<TrayDataDto>? Envelope { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset CachedAt { get; set; }
}

public class LocalCacheService
{
    private readonly string _cacheDirectory;
    private readonly string _cacheFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public LocalCacheService(string? customDirectory = null)
    {
        _cacheDirectory = customDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FootballTray"
        );
        _cacheFilePath = Path.Combine(_cacheDirectory, "tray.json");
    }

    public string CacheFilePath => _cacheFilePath;

    /// <summary>
    /// Loads the cached tray payload from disk. Returns null if missing or corrupt.
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
            var payload = await JsonSerializer.DeserializeAsync<CachedTrayPayload>(stream, JsonOptions, cancellationToken);
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
    /// Atomically writes the tray payload to disk using a temporary file.
    /// </summary>
    public async Task<bool> SaveCacheAsync(ApiResponseEnvelope<TrayDataDto> envelope, string? etag, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

            var payload = new CachedTrayPayload
            {
                Envelope = envelope,
                ETag = etag,
                CachedAt = DateTimeOffset.UtcNow,
            };

            var tempFilePath = Path.Combine(_cacheDirectory, $"tray_{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Atomic move/replace
            if (File.Exists(_cacheFilePath))
            {
                File.Replace(tempFilePath, _cacheFilePath, null);
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
