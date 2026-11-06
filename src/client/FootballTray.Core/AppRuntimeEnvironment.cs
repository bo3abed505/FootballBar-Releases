using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FootballTray.Core;

/// <summary>
/// Authoritative environment detector identifying application packaging container (MSIX vs Standalone/Portable).
/// Enforces Microsoft Store Policy 10.2.4 compliance.
/// </summary>
public static class AppRuntimeEnvironment
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>
    /// Testing hook to simulate packaged MSIX or standalone unpackaged environment.
    /// </summary>
    public static bool? ForcedIsPackagedForTesting { get; set; }

    /// <summary>
    /// Gets a value indicating whether the application is running inside a packaged MSIX / Windows App container.
    /// </summary>
    public static bool IsPackagedMsix => ForcedIsPackagedForTesting ?? DetectIsPackagedMsix();

    private static bool DetectIsPackagedMsix()
    {
        // 1. Environment variable override for testability and container testing
        string? env = Environment.GetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (string.Equals(env, "store", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "msix", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "packaged", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(env, "github", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "standalone", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "unpackaged", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 2. Win32 GetCurrentPackageFullName probe
        try
        {
            int length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            return result != APPMODEL_ERROR_NO_PACKAGE && length > 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}

