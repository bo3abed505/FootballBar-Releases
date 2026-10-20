using System;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Safe diagnostic helper for logging MatchCenter and UI window lifecycle events
/// into the unified AppLogger file and memory sinks.
/// </summary>
public static class AppDiagnosticsLogger
{
    public static void Log(string message)
    {
        AppLogger.Error("MatchCenter", "OpenFailed", message);
    }

    public static void LogError(string component, string eventName, string message, Exception? ex = null)
    {
        AppLogger.Error(component, eventName, message, ex: ex);
    }
}
