namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Lifecycle states for user-initiated update installation handoff.
/// </summary>
public enum UpdateInstallationState
{
    /// <summary>
    /// Update installation is not ready (package not verified or offer changed).
    /// </summary>
    NotReady,

    /// <summary>
    /// Verified package exists for current offer; ready for user to click Install.
    /// </summary>
    Ready,

    /// <summary>
    /// Extraction and pre-handoff verification in progress.
    /// </summary>
    Preparing,

    /// <summary>
    /// Preparation verified; installer process is starting.
    /// </summary>
    StartingInstaller,

    /// <summary>
    /// Installer process successfully started. Client exit pending.
    /// </summary>
    HandoffStarted,

    /// <summary>
    /// Preparation or process launch failed. Client remains running.
    /// </summary>
    Failed
}
