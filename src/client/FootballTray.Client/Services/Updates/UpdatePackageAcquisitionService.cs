using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;
using FootballTray.Core.Dto;
using FootballTray.Core.Services;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Service responsible for downloading, streaming, and cryptographically verifying update packages.
/// Ensures atomicity via temporary files (.part), verifies exact byte counts and SHA-256 digests,
/// coalesces identical concurrent download requests (single-flight), and cleanly isolates caller cancellation.
/// </summary>
public sealed class UpdatePackageAcquisitionService : IUpdatePackageAcquisitionService
{
    public const int DefaultMaxAutomaticRedirections = 5;
    public static readonly TimeSpan DefaultHeaderTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);
    private const int StreamBufferSize = 81920; // 80 KB bounded buffer
    private const long ProgressByteInterval = 262144; // 256 KB

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _baseDirectory;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _stallTimeout;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _stateLock = new();
    private bool _disposed;

    private PackageIdentity? _inFlightIdentity;
    private Task<UpdateAcquisitionResult>? _inFlightTask;

    public UpdateAcquisitionState CurrentState { get; private set; } = UpdateAcquisitionState.NotStarted;
    public string? CurrentVerifiedPackagePath { get; private set; }
    public string? LastFailureReason { get; private set; }

    public static string GetDefaultUpdateDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootballTray", "Updates");

    public UpdatePackageAcquisitionService(
        HttpClient? httpClient = null,
        string? baseDirectory = null,
        TimeSpan? headerTimeout = null,
        TimeSpan? stallTimeout = null)
    {
        _baseDirectory = baseDirectory ?? GetDefaultUpdateDirectory();
        _headerTimeout = headerTimeout ?? DefaultHeaderTimeout;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = DefaultMaxAutomaticRedirections,
                AutomaticDecompression = DecompressionMethods.None
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _disposeHttpClient = true;
        }
    }

    /// <summary>
    /// Acquires and cryptographically verifies the update package described by the release manifest.
    /// </summary>
    public async Task<UpdateAcquisitionResult> AcquirePackageAsync(
        ReleaseManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Defensively validate manifest at the service entry boundary
        var validationResult = ReleaseManifestValidator.Validate(manifest);
        if (!validationResult.IsValid || validationResult.ValidatedManifest is null)
        {
            string error = "Invalid release manifest: " + (validationResult.ErrorMessage ?? "Validation failed.");
            return Fail(error, manifest?.Version);
        }

        var validatedManifest = validationResult.ValidatedManifest;
        string canonicalVersion = validationResult.ParsedVersion?.ToString() ?? validatedManifest.Version;

        // 2. Derive package identity
        var targetIdentity = new PackageIdentity(
            canonicalVersion,
            validatedManifest.Architecture,
            validatedManifest.DownloadUrl,
            validatedManifest.Sha256,
            validatedManifest.SizeBytes);

        // 3. Resolve and validate deterministic local file paths
        string versionDir;
        string finalPath;
        string partPath;
        try
        {
            // Verify canonical version does not contain path traversal characters
            if (canonicalVersion.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                canonicalVersion.Contains('/') ||
                canonicalVersion.Contains('\\') ||
                canonicalVersion.Contains(".."))
            {
                return Fail("Invalid canonical version format for storage path.", canonicalVersion);
            }

            string fullBase = Path.GetFullPath(_baseDirectory);
            string rootWithSeparator = fullBase.EndsWith(Path.DirectorySeparatorChar) || fullBase.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            versionDir = Path.GetFullPath(Path.Combine(fullBase, canonicalVersion));
            string finalFilename = $"FootballTray-v{canonicalVersion}-win-x64.zip";
            string partFilename = $"{finalFilename}.part";

            finalPath = Path.GetFullPath(Path.Combine(versionDir, finalFilename));
            partPath = Path.GetFullPath(Path.Combine(versionDir, partFilename));

            // Verify all target paths remain strictly inside the staging directory boundary
            if (!versionDir.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                !finalPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                !partPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Target storage path traverses outside the configured updates directory.", canonicalVersion);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Updates", "AcquisitionPathError", $"Failed to compute storage paths: {ex.Message}", ex: ex);
            return Fail("Unable to initialize update storage path.", canonicalVersion);
        }

        // 4. Revalidate existing final file if present (no network needed)
        if (File.Exists(finalPath))
        {
            var revalidation = await TryRevalidateExistingFileAsync(finalPath, validatedManifest, canonicalVersion).ConfigureAwait(false);
            if (revalidation != null)
            {
                progress?.Report(UpdateDownloadProgress.Create(validatedManifest.SizeBytes, validatedManifest.SizeBytes));
                CurrentState = UpdateAcquisitionState.Verified;
                CurrentVerifiedPackagePath = finalPath;
                LastFailureReason = null;
                return revalidation;
            }
        }

        // 5. Coalesce single-flight requests
        Task<UpdateAcquisitionResult> executionTask;
        lock (_stateLock)
        {
            if (_inFlightTask != null && !_inFlightTask.IsCompleted)
            {
                if (_inFlightIdentity == targetIdentity)
                {
                    // Same package already downloading: join in-flight task
                    executionTask = _inFlightTask;
                }
                else
                {
                    // Competing download for a different package: return safe busy result without mutating active state
                    return UpdateAcquisitionResult.Failed(
                        "Another update package acquisition is already in progress.",
                        DateTimeOffset.UtcNow,
                        canonicalVersion);
                }
            }
            else
            {
                _inFlightIdentity = targetIdentity;
                executionTask = ExecuteAcquisitionCoreAsync(validatedManifest, canonicalVersion, versionDir, finalPath, partPath, progress);
                _inFlightTask = executionTask;
            }
        }

        // 6. Caller waits independently for the execution task
        return await executionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateAcquisitionResult?> TryRevalidateExistingFileAsync(
        string finalPath,
        ReleaseManifest manifest,
        string canonicalVersion)
    {
        try
        {
            var fileInfo = new FileInfo(finalPath);
            if (fileInfo.Length != manifest.SizeBytes)
            {
                return null;
            }

            using var fs = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[StreamBufferSize];
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), _lifetimeCts.Token).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }

            string computedHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (string.Equals(computedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info("Updates", "PackageRevalidated", $"Existing package for v{canonicalVersion} revalidated successfully without network download.");
                return UpdateAcquisitionResult.Verified(canonicalVersion, finalPath, manifest.SizeBytes, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Updates", "ExistingPackageCheckFailed", $"Failed revalidating existing file '{finalPath}': {ex.Message}", ex: ex);
        }

        return null;
    }

    private async Task<UpdateAcquisitionResult> ExecuteAcquisitionCoreAsync(
        ReleaseManifest manifest,
        string canonicalVersion,
        string versionDir,
        string finalPath,
        string partPath,
        IProgress<UpdateDownloadProgress>? progress)
    {
        CurrentState = UpdateAcquisitionState.Downloading;
        LastFailureReason = null;
        CurrentVerifiedPackagePath = null;
        progress?.Report(UpdateDownloadProgress.Create(0, manifest.SizeBytes));

        long bytesWritten = 0;
        try
        {
            Directory.CreateDirectory(versionDir);

            // Clean up any stale partial download
            TryDeleteFile(partPath);

            // 1. Send HTTP request with header timeout
            using var request = new HttpRequestMessage(HttpMethod.Get, manifest.DownloadUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "FootballTray-Client/1.0");

            HttpResponseMessage response;
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token))
            {
                headerCts.CancelAfter(_headerTimeout);
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        headerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (headerCts.IsCancellationRequested && !_lifetimeCts.IsCancellationRequested)
                {
                    return Fail("Update download timed out.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                }
                catch (OperationCanceledException)
                {
                    return Cancel(canonicalVersion, bytesWritten, manifest.SizeBytes);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Updates", "AcquisitionConnectError", $"Failed to connect to '{manifest.DownloadUrl}': {ex.Message}", ex: ex);
                    return Fail("Unable to download update.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                }
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Fail("Update package not found on download server (HTTP 404).", canonicalVersion, bytesWritten, manifest.SizeBytes);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Fail($"Update download server error (HTTP {(int)response.StatusCode}).", canonicalVersion, bytesWritten, manifest.SizeBytes);
                }

                // Check Content-Length if provided
                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value != manifest.SizeBytes)
                {
                    return Fail("Downloaded update size did not match the release manifest.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                }

                using var responseStream = await response.Content.ReadAsStreamAsync(_lifetimeCts.Token).ConfigureAwait(false);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                FileStream? fs = null;

                try
                {
                    fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true);

                    byte[] buffer = new byte[StreamBufferSize];
                    int lastReportedPercent = 0;
                    long lastReportedBytes = 0;

                    while (true)
                    {
                        int read;
                        using (var stallCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token))
                        {
                            stallCts.CancelAfter(_stallTimeout);
                            try
                            {
                                read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (stallCts.IsCancellationRequested && !_lifetimeCts.IsCancellationRequested)
                            {
                                DisposeAndTryDelete(ref fs, partPath);
                                return Fail("Update download timed out.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                            }
                            catch (OperationCanceledException)
                            {
                                DisposeAndTryDelete(ref fs, partPath);
                                return Cancel(canonicalVersion, bytesWritten, manifest.SizeBytes);
                            }
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        // Strict size ceiling enforcement: do not write beyond expected size
                        long remaining = manifest.SizeBytes - bytesWritten;
                        if (read > remaining)
                        {
                            // Exceeded expected size: abort immediately without writing extra bytes
                            DisposeAndTryDelete(ref fs, partPath);
                            return Fail("Downloaded update size did not match the release manifest.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                        }

                        await fs.WriteAsync(buffer.AsMemory(0, read), _lifetimeCts.Token).ConfigureAwait(false);
                        sha.AppendData(buffer, 0, read);
                        bytesWritten += read;

                        // Progress reporting
                        var progressInfo = UpdateDownloadProgress.Create(bytesWritten, manifest.SizeBytes);
                        if (progressInfo.Percent != lastReportedPercent ||
                            (bytesWritten - lastReportedBytes >= ProgressByteInterval) ||
                            bytesWritten == manifest.SizeBytes)
                        {
                            progress?.Report(progressInfo);
                            lastReportedPercent = progressInfo.Percent;
                            lastReportedBytes = bytesWritten;
                        }
                    }

                    // 2. Stream finished, transition to Verifying
                    CurrentState = UpdateAcquisitionState.Verifying;

                    // Flush and close file before hashing verification
                    await fs.FlushAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    fs.Dispose();
                    fs = null;

                    if (bytesWritten != manifest.SizeBytes)
                    {
                        DisposeAndTryDelete(ref fs, partPath);
                        return Fail("Downloaded update size did not match the release manifest.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                    }

                    string computedHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                    if (!string.Equals(computedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Warn("Updates", "ShaMismatch", $"SHA-256 verification failed for v{canonicalVersion}. Computed: '{computedHash}', Expected: '{manifest.Sha256}'");
                        DisposeAndTryDelete(ref fs, partPath);
                        return Fail("Downloaded update failed integrity verification.", canonicalVersion, bytesWritten, manifest.SizeBytes);
                    }

                    // 3. Atomically promote .part to final ZIP
                    File.Move(partPath, finalPath, overwrite: true);

                    CurrentState = UpdateAcquisitionState.Verified;
                    CurrentVerifiedPackagePath = finalPath;
                    LastFailureReason = null;
                    progress?.Report(UpdateDownloadProgress.Create(manifest.SizeBytes, manifest.SizeBytes));

                    AppLogger.Info("Updates", "PackageAcquired", $"Update package v{canonicalVersion} successfully acquired and verified at '{finalPath}'.");
                    return UpdateAcquisitionResult.Verified(canonicalVersion, finalPath, manifest.SizeBytes, DateTimeOffset.UtcNow);
                }
                finally
                {
                    if (fs != null)
                    {
                        try { fs.Dispose(); } catch { }
                        fs = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            return Cancel(canonicalVersion, bytesWritten, manifest.SizeBytes);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Updates", "AcquisitionExecutionError", $"Unexpected error acquiring package: {ex.Message}", ex: ex);
            TryDeleteFile(partPath);
            return Fail("Unable to download update.", canonicalVersion, bytesWritten, manifest.SizeBytes);
        }
        finally
        {
            lock (_stateLock)
            {
                _inFlightTask = null;
                _inFlightIdentity = null;
            }
        }
    }

    private static void DisposeAndTryDelete(ref FileStream? fs, string path)
    {
        if (fs != null)
        {
            try { fs.Dispose(); } catch { }
            fs = null;
        }
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private UpdateAcquisitionResult Fail(
        string reason,
        string? version,
        long bytesReceived = 0,
        long totalExpected = 0)
    {
        CurrentState = UpdateAcquisitionState.Failed;
        LastFailureReason = reason;
        CurrentVerifiedPackagePath = null;
        return UpdateAcquisitionResult.Failed(reason, DateTimeOffset.UtcNow, version, bytesReceived, totalExpected);
    }

    private UpdateAcquisitionResult Cancel(
        string? version,
        long bytesReceived = 0,
        long totalExpected = 0)
    {
        CurrentState = UpdateAcquisitionState.Cancelled;
        LastFailureReason = "Update download was cancelled.";
        CurrentVerifiedPackagePath = null;
        return UpdateAcquisitionResult.Cancelled(DateTimeOffset.UtcNow, version, bytesReceived, totalExpected);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Best effort
        }

        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
