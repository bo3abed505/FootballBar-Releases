namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Abstraction for requesting graceful application shutdown after installer handoff.
/// </summary>
public interface IAppShutdownHandler
{
    /// <summary>
    /// Requests graceful application exit via normal UI window closure.
    /// Returns <see langword="true"/> when the close request was successfully enqueued or dispatched;
    /// <see langword="false"/> when the dispatcher queue was unavailable (e.g., already shutting down).
    /// </summary>
    bool RequestShutdown();
}
