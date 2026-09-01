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
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string formatted = $"[Taskbar][{timestamp}][{level.ToString().ToUpperInvariant()}] {message}";
        if (ex != null)
        {
            formatted += $" | Exception: {ex.GetType().Name}: {ex.Message}";
        }

        Trace.WriteLine(formatted);
        System.Diagnostics.Debug.WriteLine(formatted);
        Console.WriteLine(formatted);

        try
        {
            string tempLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FootballTray_taskbar.log");
            System.IO.File.AppendAllText(tempLogPath, formatted + Environment.NewLine);
        }
        catch { }

        LogMessageReceived?.Invoke(level, message, ex);
    }
}
