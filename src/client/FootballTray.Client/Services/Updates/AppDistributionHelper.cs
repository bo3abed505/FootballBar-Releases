using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Detects application distribution channel and runtime package identity.
/// Differentiates between Microsoft Store (Packaged MSIX) and GitHub Releases (Unpackaged Standalone).
/// </summary>
public static class AppDistributionHelper
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    /// <summary>
    /// Optional override for unit testing purposes.
    /// </summary>
    public static bool? ForcedIsPackagedForTesting { get; set; }

    /// <summary>
    /// Gets a value indicating whether the application is running inside a packaged MSIX / Windows App container.
    /// </summary>
    public static bool IsPackaged => ForcedIsPackagedForTesting ?? DetectIsPackaged();

    /// <summary>
    /// Gets a value indicating whether the application is distributed via the Microsoft Store.
    /// </summary>
    public static bool IsMicrosoftStorePackage => IsPackaged;

    /// <summary>
    /// Gets a display name for the distribution channel.
    /// </summary>
    public static string ChannelName => IsPackaged ? "Microsoft Store (MSIX)" : "GitHub Releases (Standalone)";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private static bool DetectIsPackaged()
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
            return result != APPMODEL_ERROR_NO_PACKAGE;
        }
        catch
        {
            return false;
        }
    }
}
