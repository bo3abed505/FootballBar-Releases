using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Central structured diagnostic logger for the FootballTray client application.
/// Dispatches safe, sanitized log entries to a local bounded file sink and in-memory ring buffer.
/// </summary>
public static class AppLogger
{
    private static FileLogSink _fileSink = new();
    private static readonly ConcurrentQueue<DiagnosticLogEntry> _recentEntries = new();
    private const int MaxRecentEntries = 100;
    private static readonly object _initLock = new();

    public static event Action<DiagnosticLogEntry>? EntryLogged;

    public static FileLogSink FileSink => _fileSink;
    public static string LogDirectory => _fileSink.LogDirectory;

    public static volatile bool IsLoggingEnabled = true;
    public static volatile bool IsDebugLoggingEnabled = false;

    /// <summary>
    /// Configures custom log sink (primarily for tests).
    /// </summary>
    public static void ConfigureSink(FileLogSink sink)
    {
        lock (_initLock)
        {
            _fileSink = sink ?? new FileLogSink();
        }
    }

    public static void Debug(string component, string eventName, string message, string? correlationId = null) =>
        Log(DiagnosticLogLevel.Debug, component, eventName, message, correlationId);

    public static void Info(string component, string eventName, string message, string? correlationId = null) =>
        Log(DiagnosticLogLevel.Info, component, eventName, message, correlationId);

    public static void Warn(string component, string eventName, string message, string? correlationId = null, Exception? ex = null) =>
        Log(DiagnosticLogLevel.Warning, component, eventName, message, correlationId, ex);

    public static void Warning(string component, string eventName, string message, string? correlationId = null, Exception? ex = null) =>
        Warn(component, eventName, message, correlationId, ex);

    public static void Error(string component, string eventName, string message, string? correlationId = null, Exception? ex = null) =>
        Log(DiagnosticLogLevel.Error, component, eventName, message, correlationId, ex);

    public static void Critical(string component, string eventName, string message, string? correlationId = null, Exception? ex = null) =>
        Log(DiagnosticLogLevel.Critical, component, eventName, message, correlationId, ex);

    public static void Log(
        DiagnosticLogLevel level,
        string component,
        string eventName,
        string message,
        string? correlationId = null,
        Exception? ex = null)
    {
        try
        {
            if (!IsLoggingEnabled) return;
            if (level == DiagnosticLogLevel.Debug && !IsDebugLoggingEnabled) return;

            string sanitizedMsg = Sanitize(message);
            string? exType = ex?.GetType().Name;
            string? exMsg = null;
            if (ex != null)
            {
                string hresultHex = $"0x{ex.HResult:X8}";
                string rawMsg = !string.IsNullOrWhiteSpace(ex.Message)
                    ? $"{ex.Message} (HResult: {hresultHex})"
                    : $"HResult: {hresultHex}";
                exMsg = Sanitize(rawMsg);
            }

            var entry = new DiagnosticLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Component = component ?? "App",
                EventName = eventName ?? "Event",
                Level = level,
                Message = sanitizedMsg,
                CorrelationId = correlationId,
                ExceptionType = exType,
                ExceptionMessage = exMsg
            };

            // 1. Write to file sink
            _fileSink.Write(entry);

            // 2. Buffer in-memory for UI inspection
            _recentEntries.Enqueue(entry);
            while (_recentEntries.Count > MaxRecentEntries && _recentEntries.TryDequeue(out _)) { }

            // 3. Output to debug/trace
            string text = entry.ToString();
            Trace.WriteLine(text);
            System.Diagnostics.Debug.WriteLine(text);
            Console.WriteLine(text);

            EntryLogged?.Invoke(entry);
        }
        catch
        {
            // Defensive: logging must never throw
        }
    }

    /// <summary>
    /// Returns a snapshot copy of recent log entries.
    /// </summary>
    public static IReadOnlyList<DiagnosticLogEntry> GetRecentEntries()
    {
        return _recentEntries.ToArray();
    }

    /// <summary>
    /// Sanitizes message strings to remove potential secrets, tokens, or credentials.
    /// </summary>
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Apply path, username PII, and token redaction
        string sanitized = FootballTray.Core.Diagnostics.DiagnosticLogSanitizer.RedactSensitivePaths(input);

        // Redact API keys, tokens, postgres connection strings
        sanitized = Regex.Replace(sanitized, @"key=[a-zA-Z0-9_-]+", "key=[REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(x-apisports" + @"-key):\s*[a-zA-Z0-9_-]+", "$1: [REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"(postgres" + @"ql://)[^@]+@", "$1[REDACTED]@", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"password=[^;\s]+", "password=[REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"Bearer\s+[a-zA-Z0-9_\-\.]+", "Bearer [REDACTED]", RegexOptions.IgnoreCase);

        return sanitized;
    }
}
