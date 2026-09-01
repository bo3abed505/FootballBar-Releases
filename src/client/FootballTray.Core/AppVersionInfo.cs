using System;
using System.Reflection;

namespace FootballTray.Core;

/// <summary>
/// Authoritative version provider for FootballTray runtime and diagnostics.
/// </summary>
public static class AppVersionInfo
{
    public const string FallbackVersion = "1.0.0";

    /// <summary>
    /// SemVer application version (e.g. "1.0.0").
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        try
        {
            var assembly = typeof(AppVersionInfo).Assembly;
            var infoAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(infoAttr?.InformationalVersion))
            {
                // Strip git commit hash suffix if present (e.g. "1.0.0+abc1234" -> "1.0.0")
                string fullVersion = infoAttr.InformationalVersion;
                int plusIdx = fullVersion.IndexOf('+');
                return plusIdx > 0 ? fullVersion[..plusIdx] : fullVersion;
            }

            var ver = assembly.GetName().Version;
            if (ver != null)
            {
                return $"{ver.Major}.{ver.Minor}.{ver.Build}";
            }
        }
        catch
        {
            // Fallback for isolated test environments
        }

        return FallbackVersion;
    }
}
