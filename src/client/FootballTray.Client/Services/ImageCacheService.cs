using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FootballTray.Client.Services;

/// <summary>
/// Service responsible for downloading, disk-caching, and retrieving team/competition logos.
/// Features single-flight download coalescing, failure state caching to prevent retry loops,
/// and returns thread-safe file paths and local Uris without UI thread affinity.
/// </summary>
public class ImageCacheService
{
    private static ImageCacheService? _instance;
    public static ImageCacheService Instance => _instance ??= new ImageCacheService();

    public static readonly Uri DefaultFallbackShieldUri = new("ms-appx:///Assets/Square44x44Logo.targetsize-24_altform-unplated.png");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _failureCooldown;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    public ImageCacheService(
        HttpClient? httpClient = null,
        string? customCacheDirectory = null,
        TimeSpan? failureCooldown = null)
    {
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _cacheDirectory = customCacheDirectory ?? GetDefaultCacheDirectory();
        _failureCooldown = failureCooldown ?? TimeSpan.FromMinutes(10);

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch
        {
            // Directory creation will be re-attempted on first write if needed
        }
    }

    public static string GetDefaultCacheDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootballTray", "Cache", "Images");

    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// Downloads an image once and caches it to disk. On subsequent calls, returns the local disk path.
    /// Does not block the UI thread and coalesces duplicate concurrent requests for the same URL.
    /// </summary>
    public async Task<string?> GetCachedImagePathAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string trimmedUrl = url.Trim();

        // 1. Verify URL is not in a temporary failure cooldown (e.g. 404)
        if (_failedUrls.TryGetValue(trimmedUrl, out var failedAt))
        {
            if (DateTimeOffset.UtcNow - failedAt < _failureCooldown)
            {
                return null;
            }
            _failedUrls.TryRemove(trimmedUrl, out _);
        }

        // 2. Check if the image is already cached on disk
        string targetFilePath = GetLocalFilePathForUrl(trimmedUrl);
        if (File.Exists(targetFilePath))
        {
            return targetFilePath;
        }

        // 3. Coalesce in-flight downloads for identical URLs
        Task<string?> downloadTask = _inFlightDownloads.GetOrAdd(
            trimmedUrl,
            key => DownloadAndCacheCoreAsync(key, targetFilePath, cancellationToken));

        try
        {
            string? result = await downloadTask.ConfigureAwait(false);
            return result;
        }
        finally
        {
            _inFlightDownloads.TryRemove(trimmedUrl, out _);
        }
    }

    /// <summary>
    /// Returns a local file Uri for the cached image, or an optional fallback shield Uri upon failure.
    /// Does NOT instantiate any WinUI BitmapImage to preserve thread affinity requirements.
    /// </summary>
    public async Task<Uri?> GetCachedImageUriAsync(
        string? url,
        bool returnFallbackOnFailure = false,
        CancellationToken cancellationToken = default)
    {
        string? localPath = await GetCachedImagePathAsync(url, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
        {
            return new Uri(localPath, UriKind.Absolute);
        }

        return returnFallbackOnFailure ? DefaultFallbackShieldUri : null;
    }

    private async Task<string?> DownloadAndCacheCoreAsync(string url, string targetFilePath, CancellationToken cancellationToken)
    {
        try
        {
            // Double check disk cache in case written by a concurrent process
            if (File.Exists(targetFilePath))
            {
                return targetFilePath;
            }

            Directory.CreateDirectory(_cacheDirectory);

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                RecordFailure(url);
                return null;
            }

            // Write to a temporary file first then atomically move to target
            string tempFilePath = Path.Combine(_cacheDirectory, $"{Guid.NewGuid():N}.tmp");

            try
            {
                using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempFilePath, targetFilePath, overwrite: true);
                return targetFilePath;
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }
        catch (Exception)
        {
            RecordFailure(url);
            return null;
        }
    }

    private void RecordFailure(string url)
    {
        _failedUrls[url] = DateTimeOffset.UtcNow;
    }

    public bool IsUrlInFailureState(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (_failedUrls.TryGetValue(url.Trim(), out var failedAt))
        {
            return (DateTimeOffset.UtcNow - failedAt) < _failureCooldown;
        }
        return false;
    }

    private string GetLocalFilePathForUrl(string url)
    {
        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        string ext = ".png";
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
        {
            string urlExt = Path.GetExtension(parsedUri.AbsolutePath);
            if (!string.IsNullOrEmpty(urlExt) && urlExt.Length is >= 3 and <= 5)
            {
                ext = urlExt.ToLowerInvariant();
            }
        }

        return Path.Combine(_cacheDirectory, $"{hashHex}{ext}");
    }
}
