namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Status outcome of the update package download and verification process.
/// Completely decoupled from football synchronization data freshness and discovery health.
/// </summary>
public enum UpdateAcquisitionState
{
    /// <summary>
    /// Acquisition has not been initiated.
    /// </summary>
    NotStarted,

    /// <summary>
    /// Package is actively downloading over HTTPS.
    /// </summary>
    Downloading,

    /// <summary>
    /// Streaming download completed; byte count and SHA-256 verification are running.
    /// </summary>
    Verifying,

    /// <summary>
    /// Package byte count and SHA-256 have been fully verified and promoted to the local verified store.
    /// </summary>
    Verified,

    /// <summary>
    /// Acquisition failed due to network, HTTP, size mismatch, hash mismatch, or invalid manifest.
    /// </summary>
    Failed,

    /// <summary>
    /// Acquisition was cancelled by application shutdown or service disposal.
    /// </summary>
    Cancelled
}
