using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Production service for preparing verified update packages, creating isolated installer handoff directories,
/// and launching the trusted installer process with graceful application shutdown orchestration.
/// </summary>
public sealed class UpdateInstallationService : IUpdateInstallationService
{
    private readonly object _stateLock = new();
    private readonly IProcessLauncher _processLauncher;
    private readonly string _baseUpdatesDirectory;
    private readonly string? _trustedInstallerOverridePath;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly List<PreparedUpdateInstall> _activePreparedInstalls = new();

    private bool _disposed;
    private UpdateInstallationState _currentState = UpdateInstallationState.NotReady;
    private string? _lastFailureReason;
    private Task<PreparedUpdateInstall>? _inFlightPreparationTask;
    private PackageIdentity? _inFlightPreparationIdentity;

    public UpdateInstallationState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    public string? LastFailureReason
    {
        get
        {
            lock (_stateLock)
            {
                return _lastFailureReason;
            }
        }
    }

    public UpdateInstallationService(
        IProcessLauncher? processLauncher = null,
        string? baseUpdatesDirectory = null,
        string? trustedInstallerOverridePath = null)
    {
        _processLauncher = processLauncher ?? new SystemProcessLauncher();
        _baseUpdatesDirectory = baseUpdatesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FootballTray",
            "Updates");
        _trustedInstallerOverridePath = trustedInstallerOverridePath;
    }

    /// <inheritdoc />
    public async Task<PreparedUpdateInstall> PrepareInstallAsync(
        ReleaseManifest manifest,
        string verifiedPackagePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedPackagePath);

        var targetIdentity = PackageIdentity.FromManifest(manifest);
        Task<PreparedUpdateInstall> prepTask;

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_inFlightPreparationTask != null && !_inFlightPreparationTask.IsCompleted)
            {
                if (_inFlightPreparationIdentity == targetIdentity)
                {
                    prepTask = _inFlightPreparationTask;
                }
                else
                {
                    throw new InvalidOperationException("Another update preparation is already in progress for a different package.");
                }
            }
            else
            {
                _currentState = UpdateInstallationState.Preparing;
                _lastFailureReason = null;
                _inFlightPreparationIdentity = targetIdentity;
                _inFlightPreparationTask = RunPreparationCoreAsync(manifest, verifiedPackagePath, targetIdentity, _lifetimeCts.Token);
                prepTask = _inFlightPreparationTask;
            }
        }

        // Caller cancellation unblocks only this caller's wait without cancelling shared work
        return await prepTask.WaitAsync(cancellationToken);
    }

    private async Task<PreparedUpdateInstall> RunPreparationCoreAsync(
        ReleaseManifest manifest,
        string verifiedPackagePath,
        PackageIdentity targetIdentity,
        CancellationToken cancellationToken)
    {
        string uniqueId = Guid.NewGuid().ToString("N");
        string versionDir = Path.Combine(_baseUpdatesDirectory, manifest.Version);
        string stagingDir = Path.Combine(versionDir, $"staging_{uniqueId}");
        string handoffDir = Path.Combine(versionDir, $"handoff_{uniqueId}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Re-verify verified ZIP file exists
            if (!File.Exists(verifiedPackagePath))
            {
                throw new FileNotFoundException($"Verified update package was not found at '{verifiedPackagePath}'.");
            }

            // 2. Re-verify exact byte size
            var fileInfo = new FileInfo(verifiedPackagePath);
            if (fileInfo.Length != manifest.SizeBytes)
            {
                throw new InvalidOperationException($"Package byte size mismatch. Actual: {fileInfo.Length}, Expected: {manifest.SizeBytes}.");
            }

            // 3. Re-verify SHA-256 hash
            await using (var fs = new FileStream(verifiedPackagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                using var sha = SHA256.Create();
                byte[] hashBytes = await sha.ComputeHashAsync(fs, cancellationToken);
                string actualHash = Convert.ToHexStringLower(hashBytes);
                if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Package SHA-256 hash mismatch. Actual: '{actualHash}', Expected: '{manifest.Sha256}'.");
                }
            }

            // 4. Resolve trusted install-client.ps1 (production: AppContext.BaseDirectory only, test: override path)
            string trustedInstallerSource = _trustedInstallerOverridePath ?? Path.Combine(AppContext.BaseDirectory, "install-client.ps1");
            if (!File.Exists(trustedInstallerSource))
            {
                throw new FileNotFoundException($"Trusted installer script was not found at '{trustedInstallerSource}'.");
            }

            // 5. Create fresh version staging and handoff directories
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(handoffDir);

            // 6. Secure ZIP extraction with Zip Slip and reparse/symlink rejection
            ExtractZipSecurely(verifiedPackagePath, stagingDir);

            // 7. Validate extracted payload integrity and version
            ValidateExtractedPayload(stagingDir, manifest.Version);

            // 8. Copy trusted installer script into isolated handoff folder
            string handoffScriptPath = Path.Combine(handoffDir, "install-client.ps1");
            File.Copy(trustedInstallerSource, handoffScriptPath, overwrite: true);

            // 9. Resolve absolute Windows PowerShell executable
            string psExe = ResolvePowerShellExecutable();

            // 10. Capture immutable facts; ProcessStartInfo is built fresh inside StartPreparedHandoff
            var prepared = new PreparedUpdateInstall(
                targetIdentity,
                stagingDir,
                handoffDir,
                handoffScriptPath,
                psExe,
                Environment.ProcessId);

            lock (_stateLock)
            {
                _activePreparedInstalls.Add(prepared);
                _currentState = UpdateInstallationState.Ready;
            }

            return prepared;
        }
        catch (Exception ex)
        {
            // Clean up staging on preparation failure
            TryDeleteDirectory(stagingDir);
            TryDeleteDirectory(handoffDir);

            lock (_stateLock)
            {
                _currentState = UpdateInstallationState.Failed;
                _lastFailureReason = ex.Message;
            }
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _inFlightPreparationTask = null;
                _inFlightPreparationIdentity = null;
            }
        }
    }

    /// <inheritdoc />
    public UpdateInstallationResult StartPreparedHandoff(PreparedUpdateInstall prepared)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prepared);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Ownership verification: only tokens issued by this service instance are accepted
            if (!_activePreparedInstalls.Contains(prepared))
            {
                throw new InvalidOperationException("Not a token issued by this service instance.");
            }

            if (_currentState == UpdateInstallationState.HandoffStarted)
            {
                return UpdateInstallationResult.Failed(prepared.TargetVersion, "Installer handoff has already been started.");
            }

            _currentState = UpdateInstallationState.StartingInstaller;
        }

        try
        {
            // Build ProcessStartInfo fresh at launch time — never mutated by external callers
            var startInfo = new ProcessStartInfo(prepared.PowerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(prepared.InstallerScriptPath);
            startInfo.ArgumentList.Add("-SourceDir");
            startInfo.ArgumentList.Add(prepared.StagingDirectory);
            startInfo.ArgumentList.Add("-WaitForProcessId");
            startInfo.ArgumentList.Add(prepared.CallerProcessId.ToString());
            startInfo.ArgumentList.Add("-Launch");

            int? pid = _processLauncher.Start(startInfo);
            if (pid == null)
            {
                lock (_stateLock)
                {
                    _activePreparedInstalls.Remove(prepared);
                    _currentState = UpdateInstallationState.Failed;
                    _lastFailureReason = "Failed to launch installer process.";
                }
                TryDeleteDirectory(prepared.StagingDirectory);
                TryDeleteDirectory(prepared.HandoffDirectory);
                return UpdateInstallationResult.Failed(prepared.TargetVersion, "Failed to launch installer process.");
            }

            lock (_stateLock)
            {
                // Ownership successfully transferred to installer process
                prepared.IsTransferred = true;
                _currentState = UpdateInstallationState.HandoffStarted;
            }

            return UpdateInstallationResult.HandoffStarted(prepared.TargetVersion, pid);
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _activePreparedInstalls.Remove(prepared);
                _currentState = UpdateInstallationState.Failed;
                _lastFailureReason = ex.Message;
            }
            TryDeleteDirectory(prepared.StagingDirectory);
            TryDeleteDirectory(prepared.HandoffDirectory);
            return UpdateInstallationResult.Failed(prepared.TargetVersion, $"Installer launch exception: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void DiscardPreparedInstall(PreparedUpdateInstall prepared)
    {
        if (prepared == null) return;

        lock (_stateLock)
        {
            if (prepared.IsTransferred)
            {
                return; // Do not touch transferred directories
            }

            _activePreparedInstalls.Remove(prepared);
            if (_currentState == UpdateInstallationState.Ready)
            {
                _currentState = UpdateInstallationState.NotReady;
            }
        }

        TryDeleteDirectory(prepared.StagingDirectory);
        TryDeleteDirectory(prepared.HandoffDirectory);
    }

    private static void ExtractZipSecurely(string zipPath, string destinationDirectory)
    {
        string fullDest = Path.GetFullPath(destinationDirectory);
        string rootWithSeparator = Path.EndsInDirectorySeparator(fullDest)
            ? fullDest
            : fullDest + Path.DirectorySeparatorChar;

        // Track all file paths extracted so far (case-insensitive on Windows) to detect collisions.
        var seenFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            // Defensively reject rooted entry names
            if (Path.IsPathRooted(entry.FullName) || entry.FullName.StartsWith('/') || entry.FullName.StartsWith('\\'))
            {
                throw new SecurityException($"Zip entry '{entry.FullName}' is rooted and rejected.");
            }

            // Reject any special Unix file type. The Unix file type occupies the top 4 bits of the
            // high 16-bit mode word in ExternalAttributes. Accepted types are:
            //   0x0000 — no Unix metadata (Windows-only ZIP)
            //   0x8000 — regular file (S_IFREG)
            //   0x4000 — directory (S_IFDIR)
            // Everything else (symlink 0xA000, socket 0xC000, FIFO 0x1000, block 0x6000,
            // char device 0x2000) is rejected.
            ushort unixFileType = (ushort)((entry.ExternalAttributes >> 16) & 0xF000);
            bool isSpecialUnixType = unixFileType != 0x0000 && unixFileType != 0x8000 && unixFileType != 0x4000;

            // Reject Windows reparse-point entries regardless of Unix type.
            bool isWindowsReparse = (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;

            if (isSpecialUnixType || isWindowsReparse)
            {
                throw new SecurityException($"Zip entry '{entry.FullName}' is a special file type (Unix type 0x{unixFileType:X4}) and is rejected.");
            }

            string targetPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));

            // Zip Slip protection: must remain beneath destination directory with directory boundary separator
            if (!targetPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException($"Zip entry '{entry.FullName}' escapes staging root directory.");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
            }
            else
            {
                // Fail closed on duplicate or case-collision: two entries mapping to the same path
                // on a case-insensitive filesystem would silently overwrite each other.
                if (!seenFilePaths.Add(targetPath))
                {
                    throw new SecurityException($"Zip entry '{entry.FullName}' collides (case-insensitively) with an already-extracted path.");
                }

                string? parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }
                entry.ExtractToFile(targetPath, overwrite: false);
            }
        }
    }

    private static void ValidateExtractedPayload(string stagingDirectory, string expectedVersion)
    {
        string exePath = Path.Combine(stagingDirectory, "FootballTray.exe");
        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException("Extracted payload is missing 'FootballTray.exe'.");
        }

        // Validate x64 PE image architecture
        if (!IsValidX64Executable(exePath))
        {
            throw new InvalidOperationException("Extracted 'FootballTray.exe' is not a valid 64-bit (x64) PE image.");
        }

        // Validate required runtime components
        string[] requiredComponents =
        [
            "FootballTray.dll",
            "FootballTray.Core.dll",
            "Microsoft.WinUI.dll"
        ];

        foreach (var req in requiredComponents)
        {
            string reqPath = Path.Combine(stagingDirectory, req);
            if (!File.Exists(reqPath))
            {
                throw new InvalidOperationException($"Extracted payload is missing required component '{req}'.");
            }
        }

        // Version verification: VERSION.txt takes priority, with PE ProductVersion/FileVersion fallback
        string? payloadVersion = null;
        string versionTxtPath = Path.Combine(stagingDirectory, "VERSION.txt");
        if (File.Exists(versionTxtPath))
        {
            string content = File.ReadAllText(versionTxtPath);
            foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line, @"(?:FootballTray\s+)?v?([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)");
                if (match.Success && SemanticVersion.TryParse(match.Groups[1].Value, out var parsed) && parsed != null)
                {
                    payloadVersion = parsed.ToString();
                    break;
                }
            }
        }

        if (payloadVersion == null)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(vi.ProductVersion) &&
                    SemanticVersion.TryParse(vi.ProductVersion, out var parsedProd) && parsedProd != null)
                {
                    payloadVersion = parsedProd.ToString();
                }
                else if (!string.IsNullOrWhiteSpace(vi.FileVersion) &&
                         SemanticVersion.TryParse(vi.FileVersion, out var parsedFile) && parsedFile != null)
                {
                    payloadVersion = parsedFile.ToString();
                }
            }
            catch { }
        }

        if (payloadVersion == null)
        {
            throw new InvalidOperationException("Unable to determine valid semantic version from extracted payload.");
        }

        if (!string.Equals(payloadVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Extracted payload version 'v{payloadVersion}' does not match expected version 'v{expectedVersion}'.");
        }
    }

    private static bool IsValidX64Executable(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false; // "MZ"
            stream.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = reader.ReadInt32();
            stream.Seek(peOffset, SeekOrigin.Begin);
            uint peSig = reader.ReadUInt32();
            if (peSig != 0x00004550) return false; // "PE\0\0"
            ushort machine = reader.ReadUInt16();
            return machine == 0x8664; // IMAGE_FILE_MACHINE_AMD64
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePowerShellExecutable()
    {
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string psPath = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(psPath))
        {
            return psPath;
        }

        throw new FileNotFoundException($"Authoritative Windows PowerShell host was not found at '{psPath}'.");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        lock (_stateLock)
        {
            foreach (var prepared in _activePreparedInstalls)
            {
                // Clean up only if ownership was NOT transferred to installer process
                if (!prepared.IsTransferred)
                {
                    TryDeleteDirectory(prepared.StagingDirectory);
                    TryDeleteDirectory(prepared.HandoffDirectory);
                }
            }
            _activePreparedInstalls.Clear();
        }

        _lifetimeCts.Dispose();
    }
}
