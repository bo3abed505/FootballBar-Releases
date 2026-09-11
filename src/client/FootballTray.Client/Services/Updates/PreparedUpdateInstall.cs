namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Immutable token representing an extracted and verified update payload ready for installer handoff.
/// The <see cref="UpdateInstallationService"/> that issued this token is the only authority that may
/// consume it via <see cref="UpdateInstallationService.StartPreparedHandoff"/>. Passing a token to a
/// different service instance will be rejected.
/// </summary>
public sealed class PreparedUpdateInstall
{
    public PackageIdentity TargetIdentity { get; }
    public string TargetVersion => TargetIdentity.Version;
    public string StagingDirectory { get; }
    public string HandoffDirectory { get; }
    public string InstallerScriptPath { get; }

    /// <summary>Absolute path to the Windows PowerShell executable resolved at preparation time.</summary>
    public string PowerShellPath { get; }

    /// <summary>Process ID of the calling application at preparation time, passed to the installer.</summary>
    public int CallerProcessId { get; }

    public bool IsTransferred { get; internal set; }

    public PreparedUpdateInstall(
        PackageIdentity targetIdentity,
        string stagingDirectory,
        string handoffDirectory,
        string installerScriptPath,
        string powerShellPath,
        int callerProcessId)
    {
        TargetIdentity = targetIdentity;
        StagingDirectory = stagingDirectory;
        HandoffDirectory = handoffDirectory;
        InstallerScriptPath = installerScriptPath;
        PowerShellPath = powerShellPath;
        CallerProcessId = callerProcessId;
    }
}
