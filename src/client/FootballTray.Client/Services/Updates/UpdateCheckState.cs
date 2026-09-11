namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Status outcome of the client-side remote release manifest discovery operation.
/// Completely decoupled from football synchronization data freshness and ticker health.
/// </summary>
public enum UpdateCheckState
{
    /// <summary>
    /// Discovery has not been executed since application startup.
    /// </summary>
    NotChecked,

    /// <summary>
    /// An update discovery check is currently in flight.
    /// </summary>
    Checking,

    /// <summary>
    /// The installed application is at or ahead of the latest eligible stable release.
    /// </summary>
    UpToDate,

    /// <summary>
    /// A newer valid eligible stable release was discovered.
    /// </summary>
    UpdateAvailable,

    /// <summary>
    /// An error occurred during manifest discovery (network, HTTP, schema, hash, or validation failure).
    /// </summary>
    CheckFailed
}
