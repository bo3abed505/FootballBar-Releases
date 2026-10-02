using System;
using System.IO;
using FootballTray.Client.Diagnostics;
using Microsoft.Win32;

namespace FootballTray.Client.Services.Startup;

/// <summary>
/// Helper managing Windows Registry HKCU Run registration for startup auto-launch.
/// </summary>
public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "FootballTray";

    /// <summary>
    /// Checks whether FootballTray is configured to run on user logon in the Windows Registry.
    /// </summary>
    public static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var val = key?.GetValue(AppName);
            return val != null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Startup", "CheckFailed", $"Failed reading startup registry state: {ex.Message}", ex: ex);
            return false;
        }
    }

    /// <summary>
    /// Writes or removes FootballTray executable path to/from HKCU Run key.
    /// </summary>
    public static bool SetRunAtStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                AppLogger.Warn("Startup", "KeyNotFound", "HKCU Run registry key could not be opened for writing.");
                return false;
            }

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    exePath = Path.Combine(AppContext.BaseDirectory, "FootballTray.exe");
                }

                string command = $"\"{exePath}\"";
                key.SetValue(AppName, command);
                AppLogger.Info("Startup", "Enabled", $"Configured Run at startup: {command}");
            }
            else
            {
                key.DeleteValue(AppName, false);
                AppLogger.Info("Startup", "Disabled", "Removed FootballTray from Run at startup.");
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Startup", "RegistryUpdateFailed", $"Failed writing startup registry state: {ex.Message}", ex: ex);
            return false;
        }
    }
}
