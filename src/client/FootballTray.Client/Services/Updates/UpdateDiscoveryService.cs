using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core;
using FootballTray.Core.Dto;
using FootballTray.Core.Services;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Client remote release manifest discovery service.
/// Fetches release-manifest.json over HTTPS, parses and validates via source-generated JSON context,
/// and evaluates update eligibility against the currently installed application version.
/// Supports conditional ETag (If-None-Match) checks, single-flight request coalescing,
/// and complete isolation from football data synchronization health.
/// </summary>
public sealed class UpdateDiscoveryService : IUpdateDiscoveryService
{
    public const string DefaultFeedUrl = "https://github.com/bo3abed505/FootballBar-Releases/releases/latest/download/release-manifest.json";
    public const string FeedUrlEnvironmentVariable = "FOOTBALLTRAY_UPDATE_FEED_URL";
    public const int MaxAutomaticRedirections = 5;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private Task<UpdateCheckResult>? _inFlightCheck;
    private UpdateCheckResult? _cachedSuccessfulResult;

    public string FeedUrl { get; }
    public SemanticVersion CurrentVersion { get; }
    public string CurrentVersionString { get; }

    public UpdateCheckState CurrentState { get; private set; } = UpdateCheckState.NotChecked;
    public ReleaseManifest? CurrentOfferedManifest { get; private set; }
    public DateTimeOffset? LastCheckAttempt { get; private set; }
    public DateTimeOffset? LastSuccessfulCheck { get; private set; }
    public string? LastKnownETag { get; private set; }
    public string? LastFailureReason { get; private set; }

    /// <summary>
    /// Resolves the effective feed URL, prioritizing developer/test environment override.
    /// </summary>
    public static string ResolveFeedUrl()
    {
        string? envUrl = Environment.GetEnvironmentVariable(FeedUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envUrl) && FootballTray.Core.Security.UrlSecurityValidator.IsSafeHttpsUrl(envUrl.Trim(), allowLoopbackHttpForTests: true))
        {
            return envUrl.Trim();
        }
        return DefaultFeedUrl;
    }

    public UpdateDiscoveryService(
        HttpClient? httpClient = null,
        string? feedUrl = null,
        string? currentVersion = null,
        TimeSpan? timeout = null)
    {
        FeedUrl = feedUrl ?? ResolveFeedUrl();
        _timeout = timeout ?? DefaultTimeout;

        CurrentVersionString = currentVersion ?? AppVersionInfo.Version;
        if (SemanticVersion.TryParse(CurrentVersionString, out var parsedVersion) && parsedVersion is not null)
        {
            CurrentVersion = parsedVersion;
        }
        else
        {
            CurrentVersion = SemanticVersion.Parse("1.0.0");
        }

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
                MaxAutomaticRedirections = MaxAutomaticRedirections
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _disposeHttpClient = true;
        }
    }

    /// <summary>
    /// Performs an asynchronous update check. Multiple overlapping callers share the in-flight check.
    /// Caller cancellation unblocks only that caller's wait without aborting the shared operation.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        Task<UpdateCheckResult> executionTask;

        await _gate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        try
        {
            if (_inFlightCheck != null && !_inFlightCheck.IsCompleted)
            {
                executionTask = _inFlightCheck;
            }
            else
            {
                executionTask = ExecuteCheckCoreAsync();
                _inFlightCheck = executionTask;
            }
        }
        finally
        {
            _gate.Release();
        }

        return await executionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UpdateCheckResult> ExecuteCheckCoreAsync()
    {
        if (FootballTray.Core.AppRuntimeEnvironment.IsPackagedMsix)
        {
            CurrentState = UpdateCheckState.UpToDate;
            LastCheckAttempt = DateTimeOffset.UtcNow;
            LastFailureReason = null;
            return UpdateCheckResult.UpToDate(
                CurrentVersion,
                CurrentVersion,
                "Updates managed by Microsoft Store",
                null,
                LastCheckAttempt.Value);
        }

        var previousState = CurrentState;
        CurrentState = UpdateCheckState.Checking;
        LastCheckAttempt = DateTimeOffset.UtcNow;
        LastFailureReason = null;

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        operationCts.CancelAfter(_timeout);
        var operationToken = operationCts.Token;

        try
        {
            // Validate Feed URL: must be absolute HTTPS
            if (!FootballTray.Core.Security.UrlSecurityValidator.IsSafeHttpsUrl(FeedUrl, allowLoopbackHttpForTests: false) ||
                !Uri.TryCreate(FeedUrl, UriKind.Absolute, out var feedUri))
            {
                return FailCheck("Update feed URL must be an absolute HTTPS URI.", LastCheckAttempt.Value);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, feedUri);

            // User-Agent: FootballTray/<version>
            request.Headers.TryAddWithoutValidation("User-Agent", $"FootballTray/{CurrentVersionString}");

            // Conditional GET via ETag
            if (!string.IsNullOrWhiteSpace(LastKnownETag))
            {
                var cleanEtag = LastKnownETag.Trim();
                if (!cleanEtag.StartsWith("\"") && !cleanEtag.StartsWith("W/\""))
                {
                    cleanEtag = $"\"{cleanEtag}\"";
                }
                request.Headers.TryAddWithoutValidation("If-None-Match", cleanEtag);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                operationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;

            // Handle HTTP 304 Not Modified
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (_cachedSuccessfulResult is null)
                {
                    return FailCheck("Received HTTP 304 Not Modified without a cached release manifest.", now);
                }

                // Update ETag if provided on 304
                string? responseEtag = response.Headers.ETag?.ToString();
                if (string.IsNullOrWhiteSpace(responseEtag) && response.Headers.TryGetValues("ETag", out var etagValues))
                {
                    responseEtag = string.Join("", etagValues);
                }
                if (!string.IsNullOrWhiteSpace(responseEtag))
                {
                    LastKnownETag = responseEtag;
                }

                LastSuccessfulCheck = now;
                CurrentOfferedManifest = _cachedSuccessfulResult.OfferedManifest;
                CurrentState = _cachedSuccessfulResult.State;

                return new UpdateCheckResult
                {
                    State = _cachedSuccessfulResult.State,
                    OfferedManifest = _cachedSuccessfulResult.OfferedManifest,
                    CurrentVersion = CurrentVersion,
                    TargetVersion = _cachedSuccessfulResult.TargetVersion,
                    Message = "Manifest not modified (HTTP 304).",
                    ETag = LastKnownETag,
                    CheckedAt = now
                };
            }

            // Handle HTTP 200 OK
            if (response.StatusCode == HttpStatusCode.OK)
            {
                string? newEtag = response.Headers.ETag?.ToString();
                if (string.IsNullOrWhiteSpace(newEtag) && response.Headers.TryGetValues("ETag", out var etagValues))
                {
                    newEtag = string.Join("", etagValues);
                }

                string manifestJson = await response.Content.ReadAsStringAsync(operationToken).ConfigureAwait(false);

                // Parse and validate structural schema
                var validationResult = ReleaseManifestParser.ParseAndValidate(manifestJson);
                if (!validationResult.IsValid || validationResult.ValidatedManifest is null)
                {
                    return FailCheck(validationResult.ErrorMessage ?? "Manifest validation failed.", now);
                }

                // Evaluate SemVer eligibility against current installed version
                var evaluation = UpdateVersionEvaluator.Evaluate(validationResult.ValidatedManifest, CurrentVersion);

                if (evaluation.Status == UpdateEvaluationStatus.UpdateAvailable && evaluation.TargetVersion is not null)
                {
                    CurrentOfferedManifest = validationResult.ValidatedManifest;
                    LastSuccessfulCheck = now;
                    LastKnownETag = newEtag;
                    CurrentState = UpdateCheckState.UpdateAvailable;

                    var result = UpdateCheckResult.Available(
                        CurrentVersion,
                        evaluation.TargetVersion,
                        validationResult.ValidatedManifest,
                        LastKnownETag,
                        now);

                    _cachedSuccessfulResult = result;
                    return result;
                }

                if (evaluation.Status == UpdateEvaluationStatus.UpToDate ||
                    evaluation.Status == UpdateEvaluationStatus.PrereleaseNotEligible)
                {
                    CurrentOfferedManifest = null;
                    LastSuccessfulCheck = now;
                    LastKnownETag = newEtag;
                    CurrentState = UpdateCheckState.UpToDate;

                    var result = UpdateCheckResult.UpToDate(
                        CurrentVersion,
                        evaluation.TargetVersion,
                        evaluation.Reason ?? "Application is up to date.",
                        LastKnownETag,
                        now);

                    _cachedSuccessfulResult = result;
                    return result;
                }

                return FailCheck(evaluation.Reason ?? "Update evaluation failed.", now);
            }

            // Handle HTTP non-200/304 (e.g. 404, 500)
            return FailCheck($"Remote server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", now);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            CurrentState = previousState;
            throw;
        }
        catch (OperationCanceledException)
        {
            // Operation-level timeout fired (operationCts.CancelAfter(_timeout))
            return FailCheck("Update check timed out.", DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException)
        {
            return FailCheck("Network error during update check.", DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return FailCheck("Unexpected error during update check.", DateTimeOffset.UtcNow);
        }
    }

    private UpdateCheckResult FailCheck(string failureReason, DateTimeOffset timestamp)
    {
        CurrentOfferedManifest = null;
        LastFailureReason = failureReason;
        CurrentState = UpdateCheckState.CheckFailed;
        return UpdateCheckResult.Failed(failureReason, timestamp, CurrentVersion);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _lifetimeCts.Cancel();
        }
        catch { }

        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        _lifetimeCts.Dispose();
        _gate.Dispose();
    }
}
