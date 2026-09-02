namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Represents the operational health state of the FootballTray client application.
/// Distinct from DataFreshnessState (which tracks match data staleness).
/// </summary>
public enum ApplicationHealthState
{
    /// <summary>
    /// All subsystems operational and responding normally.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// Core client functioning with fallback, stale cache, or non-fatal degraded subsystem.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// Subsystem currently attempting recovery (e.g. TaskbarHost recreating after Explorer restart).
    /// </summary>
    Recovering = 2,

    /// <summary>
    /// Critical unrecoverable failure encountered.
    /// </summary>
    Failed = 3
}
