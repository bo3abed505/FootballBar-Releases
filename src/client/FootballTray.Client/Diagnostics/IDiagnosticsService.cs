using System;
using System.Collections.Generic;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Contract for client diagnostics, health snapshotting, and troubleshooting reporting.
/// </summary>
public interface IDiagnosticsService
{
    DateTimeOffset StartupTime { get; }

    ClientHealthSnapshot GetCurrentSnapshot();

    string GetSanitizedReportText();

    IReadOnlyList<DiagnosticLogEntry> GetRecentLogEntries();

    void RecordError(string component, string errorSummary, Exception? ex = null);

    void RecordTaskbarStatus(string status);

    void RecordNotificationRegistrationStatus(string status);
}
