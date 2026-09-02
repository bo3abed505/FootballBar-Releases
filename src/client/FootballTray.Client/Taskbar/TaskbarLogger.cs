using System;
using System.Diagnostics;

namespace FootballTray.Client.Taskbar;

public enum TaskbarLogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// Lightweight structured logger for taskbar subsystem events and diagnostics.
/// </summary>
public static class TaskbarLogger
{
    public static event Action<TaskbarLogLevel, string, Exception?>? LogMessageReceived;

    public static void Debug(string message) => Log(TaskbarLogLevel.Debug, message);
    public static void Info(string message) => Log(TaskbarLogLevel.Info, message);
    public static void Warn(string message) => Log(TaskbarLogLevel.Warn, message);
    public static void Error(string message, Exception? ex = null) => Log(TaskbarLogLevel.Error, message, ex);

    private static void Log(TaskbarLogLevel level, string message, Exception? ex = null)
    {
        var diagLevel = level switch
        {
            TaskbarLogLevel.Debug => Diagnostics.DiagnosticLogLevel.Debug,
            TaskbarLogLevel.Info => Diagnostics.DiagnosticLogLevel.Info,
            TaskbarLogLevel.Warn => Diagnostics.DiagnosticLogLevel.Warning,
            TaskbarLogLevel.Error => Diagnostics.DiagnosticLogLevel.Error,
            _ => Diagnostics.DiagnosticLogLevel.Info
        };

        Diagnostics.AppLogger.Log(diagLevel, "Taskbar", "TaskbarEvent", message, null, ex);
        LogMessageReceived?.Invoke(level, message, ex);
    }
}
