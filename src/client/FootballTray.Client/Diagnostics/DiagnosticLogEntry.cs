using System;
using System.Text.Json.Serialization;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Structured diagnostic log entry.
/// </summary>
public record DiagnosticLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Component { get; init; } = "General";
    public string EventName { get; init; } = "Message";
    public DiagnosticLogLevel Level { get; init; } = DiagnosticLogLevel.Info;
    public string Message { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }

    public override string ToString()
    {
        string ts = Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string levelStr = Level.ToString().ToUpperInvariant();
        string corr = string.IsNullOrWhiteSpace(CorrelationId) ? string.Empty : $"[{CorrelationId}] ";
        string ex = string.IsNullOrWhiteSpace(ExceptionMessage) ? string.Empty : $" | Exception: {ExceptionType}: {ExceptionMessage}";
        return $"[{ts}][{levelStr}][{Component}][{EventName}] {corr}{Message}{ex}";
    }
}
