namespace FootballTray.Client.Services;

/// <summary>
/// Represents the client-side freshness status of the local football data.
/// </summary>
public enum DataFreshnessState
{
    /// <summary>
    /// Successful 200 response with newly received and verified structural payload.
    /// </summary>
    Fresh,

    /// <summary>
    /// Successful 304 response confirming that the current cached payload is still up to date on the server.
    /// </summary>
    NotModified,

    /// <summary>
    /// Network/server error or timeout; operating on valid cached state whose freshness cannot currently be verified.
    /// </summary>
    Stale,

    /// <summary>
    /// No valid server response and no valid local cache available.
    /// </summary>
    Empty,
}
