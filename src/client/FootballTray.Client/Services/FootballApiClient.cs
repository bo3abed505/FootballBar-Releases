using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
    /// Fetches the tray match collection from /api/v2/tray with optional date, followed teams, and ETag 304 validation.
    /// </summary>
    public Task<TrayApiResponse> GetTrayAsync(string? ifNoneMatch, CancellationToken cancellationToken) =>
        GetTrayAsync(ifNoneMatch, null, null, null, cancellationToken);

    public Task<TrayApiResponse> GetTrayAsync(
        string? ifNoneMatch,
        DateTime? date,
        IEnumerable<string>? teams,
        CancellationToken cancellationToken) =>
        GetTrayAsync(ifNoneMatch, date, teams, null, cancellationToken);

    public async Task<TrayApiResponse> GetTrayAsync(
        string? ifNoneMatch = null,
        DateTime? date = null,
        IEnumerable<string>? teams = null,
        IEnumerable<string>? competitions = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var uriBuilder = new System.Text.StringBuilder("/api/v2/tray?");
            if (date.HasValue)
            {
                uriBuilder.Append($"date={date.Value:yyyy-MM-dd}&");
            }
            if (teams != null)
            {
                var teamList = teams.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (teamList.Count > 0)
                {
                    uriBuilder.Append($"teams={Uri.EscapeDataString(string.Join(",", teamList))}&");
                }
            }
            if (competitions != null)
            {
                var compList = competitions.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                if (compList.Count > 0)
                {
                    uriBuilder.Append($"competitions={Uri.EscapeDataString(string.Join(",", compList))}&");
                }
            }
            string uri = uriBuilder.ToString().TrimEnd('?', '&');
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

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

    /// <summary>
    /// Searches clubs/teams in the backend catalog via /api/v2/catalog/teams?q={query}.
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
            return CatalogSearchApiResponse<V2CatalogTeamDto>.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches leagues/competitions in the backend catalog via /api/v2/catalog/competitions?q={query}.
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
            return CatalogSearchApiResponse<V2CatalogCompetitionDto>.Error($"Network error: {ex.Message}");
        }
    }

    /// <summary>
    /// Pushes the user's followed competitions and teams to the daemon via POST /api/v2/preferences.
    /// </summary>
    public async Task<bool> SyncPreferencesAsync(
        IEnumerable<string> competitionIds,
        IEnumerable<string> teamIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new SyncPreferencesRequestDto
            {
                CompetitionIds = competitionIds.ToList(),
                TeamIds = teamIds.ToList(),
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

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
