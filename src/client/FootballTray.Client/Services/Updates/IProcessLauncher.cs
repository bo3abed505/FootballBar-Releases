using System.Diagnostics;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Abstraction for starting external operating system processes.
/// Enables deterministic testing without spawning real PowerShell or installer processes.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Starts a process using the specified start info and returns the launched process ID, or null on failure.
    /// </summary>
    int? Start(ProcessStartInfo startInfo);
}

/// <summary>
/// Default production process launcher using standard System.Diagnostics.Process.
/// </summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public int? Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        return process?.Id;
    }
}
