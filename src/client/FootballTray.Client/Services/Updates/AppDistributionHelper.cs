using System;
using FootballTray.Core;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Detects application distribution channel and runtime package identity.
/// Differentiates between Microsoft Store (Packaged MSIX) and GitHub Releases (Unpackaged Standalone).
/// </summary>
public static class AppDistributionHelper
{
    /// <summary>
    /// Optional override for unit testing purposes.
    /// </summary>
    public static bool? ForcedIsPackagedForTesting
    {
        get => AppRuntimeEnvironment.ForcedIsPackagedForTesting;
        set => AppRuntimeEnvironment.ForcedIsPackagedForTesting = value;
    }

    /// <summary>
    /// Gets a value indicating whether the application is running inside a packaged MSIX / Windows App container.
    /// </summary>
    public static bool IsPackaged => AppRuntimeEnvironment.IsPackagedMsix;

    /// <summary>
    /// Gets a value indicating whether the application is distributed via the Microsoft Store.
    /// </summary>
    public static bool IsMicrosoftStorePackage => IsPackaged;

    /// <summary>
    /// Gets a display name for the distribution channel.
    /// </summary>
    public static string ChannelName => IsPackaged ? "Microsoft Store (MSIX)" : "GitHub Releases (Standalone)";
}
