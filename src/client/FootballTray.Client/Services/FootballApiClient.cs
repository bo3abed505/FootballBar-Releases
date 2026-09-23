using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Serialization;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services;

public class TrayApiResponse
{
    public bool IsSuccess { get; init; }
    public bool IsNotModified { get; init; }
    public string? ETag { get; init; }
    public ApiResponseEnvelope<TrayDataDto>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }

    public static TrayApiResponse Success(ApiResponseEnvelope<TrayDataDto> envelope, string? etag) =>
        new() { IsSuccess = true, IsNotModified = false, Envelope = envelope, ETag = etag };

    public static TrayApiResponse NotModified(string? etag) =>
        new() { IsSuccess = true, IsNotModified = true, ETag = etag };

    public static TrayApiResponse Error(string message) =>
        new() { IsSuccess = false, IsNotModified = false, ErrorMessage = message };
}

public class MatchDetailApiResponse
{
    public bool IsSuccess { get; init; }
    public ApiResponseEnvelope<MatchDetailDto>? Envelope { get; init; }
    public string? ErrorMessage { get; init; }

    public static MatchDetailApiResponse Success(ApiResponseEnvelope<MatchDetailDto> envelope) =>
        new() { IsSuccess = true, Envelope = envelope };

    public static MatchDetailApiResponse Error(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}

public class FootballApiClient : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:4000";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
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

    public FootballApiClient(HttpClient? httpClient = null, string? baseUrl = null)
    {
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

    /// <summary>
    /// Fetches the tray match collection from /api/v2/tray with optional ETag 304 validation.
    /// </summary>
    public async Task<TrayApiResponse> GetTrayAsync(string? ifNoneMatch = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v2/tray");

            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
            {
                var cleanEtag = ifNoneMatch.Trim();
                if (!cleanEtag.StartsWith("\"") && !cleanEtag.StartsWith("W/\""))
                {
                    cleanEtag = $"\"{cleanEtag}\"";
                }
                request.Headers.TryAddWithoutValidation("If-None-Match", cleanEtag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            string? newEtag = response.Headers.ETag?.Tag ?? response.Headers.ETag?.ToString();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return TrayApiResponse.NotModified(newEtag ?? ifNoneMatch);
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

                return TrayApiResponse.Success(envelope, newEtag);
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
            return TrayApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches full match details and timeline from /api/v2/match/:id.
    /// </summary>
    public async Task<MatchDetailApiResponse> GetMatchDetailsAsync(string matchId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return MatchDetailApiResponse.Error("Match ID cannot be empty.");
        }

        try
        {
            using var response = await _httpClient.GetAsync($"/api/v2/match/{Uri.EscapeDataString(matchId.Trim())}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync(contentStream, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto, cancellationToken);

                if (envelope == null || envelope.Data == null)
                {
                    return MatchDetailApiResponse.Error("Failed to deserialize match detail envelope.");
                }

                return MatchDetailApiResponse.Success(envelope);
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
            return MatchDetailApiResponse.Error($"Network error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
