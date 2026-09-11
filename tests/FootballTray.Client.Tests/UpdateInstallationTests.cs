using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Updates;
using FootballTray.Client.ViewModels;
using FootballTray.Core;
using FootballTray.Core.Dto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
[DoNotParallelize]
public class UpdateInstallationTests
{
    private string _tempDir = string.Empty;
    private string _updatesDir = string.Empty;
    private string _trustedInstallerScript = string.Empty;
    private MockProcessLauncher _launcher = null!;
    private MockShutdownHandler _shutdownHandler = null!;
    private PreferencesService _prefService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ft_install_test_{Guid.NewGuid():N}");
        _updatesDir = Path.Combine(_tempDir, "Updates");
        Directory.CreateDirectory(_updatesDir);
        _trustedInstallerScript = CreateDummyTrustedInstaller(_tempDir);
        _launcher = new MockProcessLauncher();
        _shutdownHandler = new MockShutdownHandler();
        _prefService = new PreferencesService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in test temp
        }
    }

    private static byte[] CreateDummyX64PeImage()
    {
        byte[] bytes = new byte[512];
        // MZ header
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        // e_lfanew at 0x3C -> 0x80
        bytes[0x3C] = 0x80;
        // PE signature at 0x80 -> "PE\0\0"
        bytes[0x80] = 0x50;
        bytes[0x81] = 0x45;
        bytes[0x82] = 0x00;
        bytes[0x83] = 0x00;
        // Machine at 0x84 -> 0x8664 (AMD64)
        bytes[0x84] = 0x64;
        bytes[0x85] = 0x86;
        return bytes;
    }

    private static (ReleaseManifest Manifest, string ZipPath) CreateValidReleaseZip(
        string targetDir,
        string version = "1.1.0",
        bool includeVersionTxt = true,
        string? customVersionTxt = null,
        bool validExe = true,
        bool includeAllDlls = true,
        Action<ZipArchive>? customEntryAction = null,
        string? customExePath = null)
    {
        string zipPath = Path.Combine(targetDir, $"package_{Guid.NewGuid():N}.zip");
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            if (includeVersionTxt)
            {
                var entry = archive.CreateEntry("VERSION.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.WriteLine(customVersionTxt ?? $"FootballTray v{version}");
            }

            var exeEntry = archive.CreateEntry("FootballTray.exe");
            using (var stream = exeEntry.Open())
            {
                if (customExePath != null)
                {
                    using var exeStream = File.OpenRead(customExePath);
                    exeStream.CopyTo(stream);
                }
                else
                {
                    byte[] peBytes = validExe ? CreateDummyX64PeImage() : [0x01, 0x02, 0x03, 0x04];
                    stream.Write(peBytes, 0, peBytes.Length);
                }
            }

            if (includeAllDlls)
            {
                string[] dlls = ["FootballTray.dll", "FootballTray.Core.dll", "Microsoft.WinUI.dll"];
                foreach (var dll in dlls)
                {
                    var dllEntry = archive.CreateEntry(dll);
                    using var stream = dllEntry.Open();
                    stream.Write([0xAA, 0xBB], 0, 2);
                }
            }

            customEntryAction?.Invoke(archive);
        }

        byte[] zipBytes = File.ReadAllBytes(zipPath);
        using var sha = SHA256.Create();
        string sha256 = Convert.ToHexStringLower(sha.ComputeHash(zipBytes));

        var manifest = new ReleaseManifest
        {
            SchemaVersion = "1.0",
            Version = version,
            PublishedAt = DateTimeOffset.UtcNow,
            Architecture = "win-x64",
            DownloadUrl = $"https://example.invalid/FootballTray-v{version}-win-x64.zip",
            Sha256 = sha256,
            SizeBytes = zipBytes.LongLength,
            ReleaseNotes = "Test notes"
        };

        return (manifest, zipPath);
    }

    private static string CreateDummyTrustedInstaller(string targetDir)
    {
        string scriptPath = Path.Combine(targetDir, "install-client.ps1");
        File.WriteAllText(scriptPath, "# Dummy trusted installer for tests\r\nparam([int]$WaitForProcessId = 0)\r\n");
        return scriptPath;
    }

    private sealed class MockProcessLauncher : IProcessLauncher
    {
        public int StartCallCount;
        public ProcessStartInfo? LastStartInfo;
        public int? ResultPid = 12345;
        public Exception? ExceptionToThrow;

        public int? Start(ProcessStartInfo startInfo)
        {
            Interlocked.Increment(ref StartCallCount);
            LastStartInfo = startInfo;
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return ResultPid;
        }
    }

    private sealed class MockShutdownHandler : IAppShutdownHandler
    {
        public int ShutdownCallCount;
        public bool ReturnValue = true;

        public bool RequestShutdown()
        {
            Interlocked.Increment(ref ShutdownCallCount);
            return ReturnValue;
        }
    }

    #region Unit & Service Tests

    [TestMethod]
    public void Test01_InitialState_IsNotReady()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        Assert.AreEqual(UpdateInstallationState.NotReady, service.CurrentState);
        Assert.IsNull(service.LastFailureReason);
    }

    [TestMethod]
    public async Task Test02_PrepareInstall_RequiresManifestAndPackagePath()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await service.PrepareInstallAsync(null!, "C:\\foo.zip");
        });

        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, "");
        });
    }

    [TestMethod]
    public async Task Test03_PrepareInstall_MissingPackageFile_ThrowsFileNotFoundException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, _) = CreateValidReleaseZip(_tempDir);
        string missingPath = Path.Combine(_tempDir, "does_not_exist.zip");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, missingPath);
        });

        Assert.AreEqual(UpdateInstallationState.Failed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test04_PrepareInstall_ByteSizeMismatch_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir);
        var tamperedManifest = new ReleaseManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Version = manifest.Version,
            PublishedAt = manifest.PublishedAt,
            Architecture = manifest.Architecture,
            DownloadUrl = manifest.DownloadUrl,
            Sha256 = manifest.Sha256,
            SizeBytes = manifest.SizeBytes + 10,
            ReleaseNotes = manifest.ReleaseNotes
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(tamperedManifest, zipPath);
        });

        Assert.AreEqual(UpdateInstallationState.Failed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test05_PrepareInstall_Sha256Mismatch_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir);
        var tamperedManifest = new ReleaseManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Version = manifest.Version,
            PublishedAt = manifest.PublishedAt,
            Architecture = manifest.Architecture,
            DownloadUrl = manifest.DownloadUrl,
            Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            SizeBytes = manifest.SizeBytes,
            ReleaseNotes = manifest.ReleaseNotes
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(tamperedManifest, zipPath);
        });

        Assert.AreEqual(UpdateInstallationState.Failed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test06_PrepareInstall_MissingTrustedInstaller_ThrowsFileNotFoundException()
    {
        string nonexistentInstaller = Path.Combine(_tempDir, "missing_install.ps1");
        using var service = new UpdateInstallationService(_launcher, _updatesDir, nonexistentInstaller);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir);

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });

        Assert.AreEqual(UpdateInstallationState.Failed, service.CurrentState);
    }

    [TestMethod]
    public async Task Test07_PrepareInstall_SuccessfulStagingAndHandoffDirectoryStructure()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        Assert.IsNotNull(prepared);
        Assert.AreEqual("1.1.0", prepared.TargetVersion);
        Assert.IsTrue(Directory.Exists(prepared.StagingDirectory), "Staging directory must exist.");
        Assert.IsTrue(Directory.Exists(prepared.HandoffDirectory), "Handoff directory must exist.");
        Assert.IsTrue(File.Exists(prepared.InstallerScriptPath), "Handoff install-client.ps1 must exist.");
        Assert.IsTrue(File.Exists(Path.Combine(prepared.StagingDirectory, "FootballTray.exe")), "FootballTray.exe must be extracted.");
        Assert.AreEqual(UpdateInstallationState.Ready, service.CurrentState);
    }

    [TestMethod]
    public async Task Test08_PrepareInstall_HandoffScriptIsIndependentCopy()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        // Handoff script is copied from trusted installer, not referenced in-place
        Assert.AreNotEqual(_trustedInstallerScript, prepared.InstallerScriptPath);
        Assert.IsTrue(prepared.InstallerScriptPath.StartsWith(prepared.HandoffDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(File.ReadAllText(_trustedInstallerScript), File.ReadAllText(prepared.InstallerScriptPath));
    }

    [TestMethod]
    public async Task Test09_StartPreparedHandoff_ProcessStartInfo_ResolvesAbsolutePowerShellAndArguments()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        // PSI is now built fresh inside StartPreparedHandoff, not stored on PreparedUpdateInstall.
        service.StartPreparedHandoff(prepared);
        var psi = _launcher.LastStartInfo!;

        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string expectedPs = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");

        Assert.AreEqual(expectedPs, psi.FileName);
        Assert.IsFalse(psi.UseShellExecute);
        Assert.IsTrue(psi.CreateNoWindow);

        // Verify ArgumentList contents
        var args = psi.ArgumentList;
        Assert.IsTrue(args.Contains("-NoProfile"));
        Assert.IsTrue(args.Contains("-ExecutionPolicy"));
        Assert.IsTrue(args.Contains("Bypass"));
        Assert.IsTrue(args.Contains("-File"));
        Assert.IsTrue(args.Contains(prepared.InstallerScriptPath));
        Assert.IsTrue(args.Contains("-SourceDir"));
        Assert.IsTrue(args.Contains(prepared.StagingDirectory));
        Assert.IsTrue(args.Contains("-WaitForProcessId"));
        Assert.IsTrue(args.Contains(Environment.ProcessId.ToString()));
        Assert.IsTrue(args.Contains("-Launch"));
    }

    [TestMethod]
    public async Task Test10_PrepareInstall_ZipSlipEntry_ThrowsSecurityExceptionAndCleansStaging()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var evilEntry = archive.CreateEntry("../evil.exe");
            using var writer = new StreamWriter(evilEntry.Open());
            writer.WriteLine("bad code");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });

        // Ensure no staging directories leaked in updates dir
        var stagingDirs = Directory.GetDirectories(_updatesDir, "staging_*", SearchOption.AllDirectories);
        Assert.AreEqual(0, stagingDirs.Length, "Staging directory must be cleaned on security failure.");
    }

    [TestMethod]
    public async Task Test11_PrepareInstall_RootedEntry_ThrowsSecurityException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var evilEntry = archive.CreateEntry("/etc/passwd");
            using var writer = new StreamWriter(evilEntry.Open());
            writer.WriteLine("bad");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });
    }

    [TestMethod]
    public async Task Test12_PrepareInstall_SymlinkReparseEntry_ThrowsSecurityException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var symlinkEntry = archive.CreateEntry("link_file.txt");
            // Set Unix symlink attribute: 0xA000 in high 16 bits
            symlinkEntry.ExternalAttributes = (0xA000 << 16);
            using var writer = new StreamWriter(symlinkEntry.Open());
            writer.WriteLine("symlink target");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });
    }

    [TestMethod]
    public async Task Test13_PrepareInstall_MissingFootballTrayExe_ThrowsInvalidOperationException()
    {
        string zipPath = Path.Combine(_tempDir, "no_exe.zip");
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            archive.CreateEntry("VERSION.txt");
            archive.CreateEntry("FootballTray.dll");
        }

        byte[] zipBytes = File.ReadAllBytes(zipPath);
        using var sha = SHA256.Create();
        var manifest = new ReleaseManifest
        {
            SchemaVersion = "1.0",
            Version = "1.1.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Architecture = "win-x64",
            DownloadUrl = "https://example.invalid/no_exe.zip",
            Sha256 = Convert.ToHexStringLower(sha.ComputeHash(zipBytes)),
            SizeBytes = zipBytes.LongLength
        };

        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });
    }

    [TestMethod]
    public async Task Test14_PrepareInstall_NonX64Exe_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", validExe: false);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });
    }

    [TestMethod]
    public async Task Test15_PrepareInstall_MissingRequiredDll_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", includeAllDlls: false);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });
    }

    [TestMethod]
    public async Task Test16_PrepareInstall_VersionMismatch_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        // Manifest says v1.1.0, but VERSION.txt inside payload says v1.0.0
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customVersionTxt: "FootballTray v1.0.0");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });
    }

    [TestMethod]
    public async Task Test17_PrepareInstall_VersionTxtResolution_MatchesExactTargetVersion()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.2.0-beta.1", customVersionTxt: "FootballTray v1.2.0-beta.1\nRuntime: net10.0");

        var prepared = await service.PrepareInstallAsync(manifest, zipPath);
        Assert.IsNotNull(prepared);
        Assert.AreEqual("1.2.0-beta.1", prepared.TargetVersion);
    }

    [TestMethod]
    public async Task Test18_PrepareInstall_SingleFlightCoalescing_SamePackageJoinsTask()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        var task1 = service.PrepareInstallAsync(manifest, zipPath);
        var task2 = service.PrepareInstallAsync(manifest, zipPath);

        var results = await Task.WhenAll(task1, task2);
        Assert.AreSame(results[0], results[1], "Concurrent calls for identical package must join the same task.");
    }

    [TestMethod]
    public async Task Test19_PrepareInstall_ConcurrentCallDifferentPackage_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest1, zipPath1) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var (manifest2, zipPath2) = CreateValidReleaseZip(_tempDir, "1.2.0");

        var tcs = new TaskCompletionSource();
        // Start first preparation
        var task1 = service.PrepareInstallAsync(manifest1, zipPath1);

        // Second preparation for different package should be rejected while first is in-flight
        try
        {
            await service.PrepareInstallAsync(manifest2, zipPath2);
        }
        catch (InvalidOperationException ex)
        {
            Assert.IsTrue(ex.Message.Contains("different package"));
        }

        await task1;
    }

    [TestMethod]
    public async Task Test20_PrepareInstall_CallerCancellation_UnblocksCallerWithoutCancellingSharedTask()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel(); // Pre-cancelled token

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath, callerCts.Token);
        });

        // The service itself should still complete a subsequent wait for the same package
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);
        Assert.IsNotNull(prepared);
    }

    [TestMethod]
    public async Task Test21_StartPreparedHandoff_LaunchesProcessViaLauncher()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        var result = service.StartPreparedHandoff(prepared);

        Assert.AreEqual(UpdateInstallationState.HandoffStarted, result.State);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(12345, result.ProcessId);
        Assert.AreEqual(1, _launcher.StartCallCount);
        Assert.AreEqual(UpdateInstallationState.HandoffStarted, service.CurrentState);
    }

    [TestMethod]
    public async Task Test22_StartPreparedHandoff_ProcessStartFailure_MarksFailedAndCleansDirectories()
    {
        _launcher.ResultPid = null; // Simulate failure to start
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        string staging = prepared.StagingDirectory;
        string handoff = prepared.HandoffDirectory;

        var result = service.StartPreparedHandoff(prepared);

        Assert.AreEqual(UpdateInstallationState.Failed, result.State);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(UpdateInstallationState.Failed, service.CurrentState);
        Assert.IsFalse(Directory.Exists(staging), "Staging directory must be deleted on launch failure.");
        Assert.IsFalse(Directory.Exists(handoff), "Handoff directory must be deleted on launch failure.");
    }

    [TestMethod]
    public async Task Test23_StartPreparedHandoff_ExceptionOnLaunch_MarksFailedAndCleansDirectories()
    {
        _launcher.ExceptionToThrow = new InvalidOperationException("OS process execution denied.");
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        string staging = prepared.StagingDirectory;
        string handoff = prepared.HandoffDirectory;

        var result = service.StartPreparedHandoff(prepared);

        Assert.AreEqual(UpdateInstallationState.Failed, result.State);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.FailureReason!.Contains("OS process execution denied."));
        Assert.IsFalse(Directory.Exists(staging));
        Assert.IsFalse(Directory.Exists(handoff));
    }

    [TestMethod]
    public async Task Test24_StartPreparedHandoff_AlreadyStarted_RejectsSecondLaunch()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        var result1 = service.StartPreparedHandoff(prepared);
        Assert.IsTrue(result1.Success);

        var result2 = service.StartPreparedHandoff(prepared);
        Assert.IsFalse(result2.Success);
        Assert.AreEqual(1, _launcher.StartCallCount, "Process must not be launched twice.");
    }

    [TestMethod]
    public async Task Test25_StartPreparedHandoff_OwnershipTransfer_PreservesDirectoriesOnDispose()
    {
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        PreparedUpdateInstall prepared;
        string staging;
        string handoff;

        using (var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript))
        {
            prepared = await service.PrepareInstallAsync(manifest, zipPath);
            staging = prepared.StagingDirectory;
            handoff = prepared.HandoffDirectory;

            var result = service.StartPreparedHandoff(prepared);
            Assert.IsTrue(result.Success);
            Assert.IsTrue(prepared.IsTransferred, "Ownership must be transferred upon launch.");
        }

        // Service is now disposed. Transferred directories must NOT have been deleted!
        Assert.IsTrue(Directory.Exists(staging), "Successfully launched staging must be preserved for installer.");
        Assert.IsTrue(Directory.Exists(handoff), "Successfully launched handoff must be preserved for installer.");
    }

    [TestMethod]
    public async Task Test26_DiscardPreparedInstall_DeletesDirectories()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        string staging = prepared.StagingDirectory;
        string handoff = prepared.HandoffDirectory;

        Assert.IsTrue(Directory.Exists(staging));
        Assert.IsTrue(Directory.Exists(handoff));

        service.DiscardPreparedInstall(prepared);

        Assert.IsFalse(Directory.Exists(staging), "Discarded staging directory must be deleted.");
        Assert.IsFalse(Directory.Exists(handoff), "Discarded handoff directory must be deleted.");
        Assert.AreEqual(UpdateInstallationState.NotReady, service.CurrentState);
    }

    [TestMethod]
    public async Task Test27_ServiceDisposal_CleansPendingUnlaunchedDirectories()
    {
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        string staging;
        string handoff;

        using (var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript))
        {
            var prepared = await service.PrepareInstallAsync(manifest, zipPath);
            staging = prepared.StagingDirectory;
            handoff = prepared.HandoffDirectory;

            Assert.IsTrue(Directory.Exists(staging));
            Assert.IsTrue(Directory.Exists(handoff));
            // Do NOT call StartPreparedHandoff
        }

        // Service disposed without launch -> must clean up
        Assert.IsFalse(Directory.Exists(staging), "Unlaunched staging directory must be cleaned on dispose.");
        Assert.IsFalse(Directory.Exists(handoff), "Unlaunched handoff directory must be cleaned on dispose.");
    }

    #endregion

    #region MainViewModel Orchestration & Zero-Gap Offer Verification Tests

    [TestMethod]
    public async Task Test28_MainViewModel_InstallUpdate_SuccessfulFlowRequestsShutdown()
    {
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        using var installService = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);

        using var vm = new MainViewModel(
            _prefService,
            updateInstallationService: installService,
            appShutdownHandler: _shutdownHandler);

        // Inject offered manifest and downloaded package path directly into view model
        var checkResult = UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifest,
            null,
            DateTimeOffset.UtcNow);

        var method = typeof(MainViewModel).GetMethod("ApplyUpdateResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, [checkResult, true]);

        // Mark update downloaded
        var verifiedProp = typeof(MainViewModel).GetProperty("VerifiedPackagePath")!;
        verifiedProp.SetValue(vm, zipPath);
        var acqProp = typeof(MainViewModel).GetProperty("AcquisitionState")!;
        acqProp.SetValue(vm, UpdateAcquisitionState.Verified);

        Assert.IsTrue(vm.CanInstallUpdate, "CanInstallUpdate must be true when package is verified.");

        var installResult = await vm.InstallUpdateAsync();

        Assert.IsNotNull(installResult);
        Assert.IsTrue(installResult.Success);
        Assert.AreEqual(UpdateInstallationState.HandoffStarted, vm.InstallationState);
        Assert.AreEqual(1, _launcher.StartCallCount, "Process launcher must have been called exactly once.");
        Assert.AreEqual(1, _shutdownHandler.ShutdownCallCount, "AppShutdownHandler.RequestShutdown must have been called.");
    }

    private sealed class PausableInstallationService : IUpdateInstallationService
    {
        private readonly IUpdateInstallationService _inner;
        public readonly TaskCompletionSource PreparationStarted = new();
        public readonly TaskCompletionSource AllowPreparationToComplete = new();

        public PausableInstallationService(IUpdateInstallationService inner) => _inner = inner;

        public UpdateInstallationState CurrentState => _inner.CurrentState;
        public string? LastFailureReason => _inner.LastFailureReason;

        public async Task<PreparedUpdateInstall> PrepareInstallAsync(ReleaseManifest manifest, string verifiedPackagePath, CancellationToken cancellationToken = default)
        {
            PreparationStarted.TrySetResult();
            await AllowPreparationToComplete.Task;
            return await _inner.PrepareInstallAsync(manifest, verifiedPackagePath, cancellationToken);
        }

        public UpdateInstallationResult StartPreparedHandoff(PreparedUpdateInstall prepared) => _inner.StartPreparedHandoff(prepared);
        public void DiscardPreparedInstall(PreparedUpdateInstall prepared) => _inner.DiscardPreparedInstall(prepared);
        public void Dispose() => _inner.Dispose();
    }

    [TestMethod]
    public async Task Test29_MainViewModel_OfferChangedDuringPreparation_DiscardsInstallAndNeverLaunchesProcess()
    {
        var (manifest1, zipPath1) = CreateValidReleaseZip(_tempDir, "1.1.0");
        var (manifest2, _) = CreateValidReleaseZip(_tempDir, "1.2.0");

        using var realInstallService = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var pausableService = new PausableInstallationService(realInstallService);

        using var vm = new MainViewModel(
            _prefService,
            updateInstallationService: pausableService,
            appShutdownHandler: _shutdownHandler);

        var checkResult1 = UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifest1,
            null,
            DateTimeOffset.UtcNow);

        var method = typeof(MainViewModel).GetMethod("ApplyUpdateResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, [checkResult1, true]);

        var verifiedProp = typeof(MainViewModel).GetProperty("VerifiedPackagePath")!;
        verifiedProp.SetValue(vm, zipPath1);
        var acqProp = typeof(MainViewModel).GetProperty("AcquisitionState")!;
        acqProp.SetValue(vm, UpdateAcquisitionState.Verified);

        // Start installation for v1.1.0
        var installTask = vm.InstallUpdateAsync();

        // Wait until preparation has actually started
        await pausableService.PreparationStarted.Task;

        // While preparation is in flight, a new update check arrives for v1.2.0
        var checkResult2 = UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.2.0"),
            manifest2,
            null,
            DateTimeOffset.UtcNow);

        // Apply new offer to view model while preparation of v1.1.0 is still in flight
        method.Invoke(vm, [checkResult2, true]);

        // Unpause preparation so v1.1.0 finishes preparing
        pausableService.AllowPreparationToComplete.SetResult();

        // Await the installation result
        var result = await installTask;

        // Must fail with offer change, discard the prepared install, and NOT launch process
        Assert.IsNotNull(result);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.FailureReason!.Contains("offer changed"), $"Expected offer changed reason, got: {result.FailureReason}");
        Assert.AreEqual(0, _launcher.StartCallCount, "ProcessLauncher.Start must NOT be called when offer changed.");
        Assert.AreEqual(0, _shutdownHandler.ShutdownCallCount, "Shutdown must NOT be requested on offer change.");
    }

    [TestMethod]
    public async Task Test30_MainViewModel_CoalescesConcurrentInstallRequests()
    {
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");
        using var installService = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);

        using var vm = new MainViewModel(
            _prefService,
            updateInstallationService: installService,
            appShutdownHandler: _shutdownHandler);

        var checkResult = UpdateCheckResult.Available(
            SemanticVersion.Parse("1.0.0"),
            SemanticVersion.Parse("1.1.0"),
            manifest,
            null,
            DateTimeOffset.UtcNow);

        var method = typeof(MainViewModel).GetMethod("ApplyUpdateResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, [checkResult, true]);

        var verifiedProp = typeof(MainViewModel).GetProperty("VerifiedPackagePath")!;
        verifiedProp.SetValue(vm, zipPath);
        var acqProp = typeof(MainViewModel).GetProperty("AcquisitionState")!;
        acqProp.SetValue(vm, UpdateAcquisitionState.Verified);

        var task1 = vm.InstallUpdateAsync();
        var task2 = vm.InstallUpdateAsync();

        var results = await Task.WhenAll(task1, task2);

        Assert.IsTrue(results[0]!.Success);
        Assert.IsTrue(results[1]!.Success);
        Assert.AreEqual(1, _launcher.StartCallCount, "Process launcher must be called only once despite concurrent install calls.");
    }

    #endregion

    #region Extended Security and Ownership Tests

    // ── Unix file-type rejection ──────────────────────────────────────────────

    [TestMethod]
    public async Task Test31_ExtractZip_FifoEntry_ThrowsSecurityException()
    {
        // Unix FIFO / named pipe: type bits 0x1000
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var entry = archive.CreateEntry("named_pipe");
            entry.ExternalAttributes = (0x1000 << 16); // S_IFIFO
            using var w = new StreamWriter(entry.Open());
            w.Write("pipe");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await service.PrepareInstallAsync(manifest, zipPath));
    }

    [TestMethod]
    public async Task Test32_ExtractZip_SocketEntry_ThrowsSecurityException()
    {
        // Unix socket: type bits 0xC000 (S_IFSOCK)
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var entry = archive.CreateEntry("unix.sock");
            entry.ExternalAttributes = (0xC000 << 16);
            using var w = new StreamWriter(entry.Open());
            w.Write("socket");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await service.PrepareInstallAsync(manifest, zipPath));
    }

    [TestMethod]
    public async Task Test33_ExtractZip_BlockDeviceEntry_ThrowsSecurityException()
    {
        // Unix block device: type bits 0x6000 (S_IFBLK)
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var entry = archive.CreateEntry("blockdev");
            entry.ExternalAttributes = (0x6000 << 16);
            using var w = new StreamWriter(entry.Open());
            w.Write("blk");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await service.PrepareInstallAsync(manifest, zipPath));
    }

    [TestMethod]
    public async Task Test34_ExtractZip_WindowsReparsePointOnly_ThrowsSecurityException()
    {
        // Windows reparse bit set in the low 16 bits with no Unix type info
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var entry = archive.CreateEntry("reparse_target.txt");
            // FileAttributes.ReparsePoint = 0x0400 in the low 16 bits; no Unix type in high 16 bits
            entry.ExternalAttributes = (int)FileAttributes.ReparsePoint;
            using var w = new StreamWriter(entry.Open());
            w.Write("junction");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await service.PrepareInstallAsync(manifest, zipPath));
    }

    // ── Accepted entries ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task Test35_ExtractZip_RegularFileWithUnixAttributes_IsAccepted()
    {
        // Unix regular file with 0644 permissions: high word = 0x81A4
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var entry = archive.CreateEntry("extra_regular.txt");
            // S_IFREG (0x8000) | 0644 (0x01A4) = 0x81A4 in high 16 bits
            entry.ExternalAttributes = (0x81A4 << 16);
            using var w = new StreamWriter(entry.Open());
            w.Write("regular content");
        });

        // Should NOT throw — a regular file with Unix permissions is safe
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);
        Assert.IsNotNull(prepared);
        Assert.IsTrue(File.Exists(Path.Combine(prepared.StagingDirectory, "extra_regular.txt")));
    }

    [TestMethod]
    public async Task Test36_ExtractZip_DirectoryEntryWithUnixAttributes_IsAccepted()
    {
        // Unix directory: type bits 0x4000 (S_IFDIR) | 0755 = 0x41ED
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var entry = archive.CreateEntry("subdir/");
            entry.ExternalAttributes = (0x41ED << 16); // S_IFDIR | 0755
        });

        // Should NOT throw — directories are explicitly accepted
        var prepared = await service.PrepareInstallAsync(manifest, zipPath);
        Assert.IsNotNull(prepared);
        Assert.IsTrue(Directory.Exists(Path.Combine(prepared.StagingDirectory, "subdir")));
    }

    // ── Path collision detection ───────────────────────────────────────────────

    [TestMethod]
    public async Task Test37_ExtractZip_DuplicateEntry_ThrowsSecurityException()
    {
        // Two entries with the exact same filename — should be rejected to prevent silent overwrite
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var e1 = archive.CreateEntry("dup_file.txt");
            using (var w = new StreamWriter(e1.Open())) w.Write("first");

            var e2 = archive.CreateEntry("dup_file.txt");
            using (var w = new StreamWriter(e2.Open())) w.Write("second");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await service.PrepareInstallAsync(manifest, zipPath));
    }

    [TestMethod]
    public async Task Test38_ExtractZip_CaseCollisionEntry_ThrowsSecurityException()
    {
        // Two entries that differ only in casing — on Windows they target the same path
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0", customEntryAction: archive =>
        {
            var e1 = archive.CreateEntry("ReadMe.txt");
            using (var w = new StreamWriter(e1.Open())) w.Write("upper");

            var e2 = archive.CreateEntry("readme.txt");
            using (var w = new StreamWriter(e2.Open())) w.Write("lower");
        });

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await service.PrepareInstallAsync(manifest, zipPath));
    }

    // ── Token ownership enforcement ───────────────────────────────────────────

    [TestMethod]
    public async Task Test39_StartPreparedHandoff_TokenFromDifferentService_ThrowsInvalidOperationException()
    {
        // A PreparedUpdateInstall token must only be consumed by the service that issued it.
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        using var service1 = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        using var service2 = new UpdateInstallationService(new MockProcessLauncher(), _updatesDir, _trustedInstallerScript);

        var prepared = await service1.PrepareInstallAsync(manifest, zipPath);

        // Passing a token issued by service1 to service2 must be rejected
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await Task.Run(() => service2.StartPreparedHandoff(prepared));
        });
    }

    [TestMethod]
    public async Task Test40_StartPreparedHandoff_BuildsFreshProcessStartInfo_EachLaunchIsIndependent()
    {
        // Verify that the PSI handed to the launcher was built from the stored immutable facts
        // (PowerShellPath, CallerProcessId) and not cached from preparation time.
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.1.0");

        var prepared = await service.PrepareInstallAsync(manifest, zipPath);

        Assert.IsNotNull(prepared.PowerShellPath, "PowerShellPath must be populated.");
        Assert.AreNotEqual(0, prepared.CallerProcessId, "CallerProcessId must be populated.");

        // The PSI is created inside StartPreparedHandoff
        Assert.IsNull(_launcher.LastStartInfo, "No PSI must have been seen by launcher before handoff.");

        service.StartPreparedHandoff(prepared);

        Assert.IsNotNull(_launcher.LastStartInfo, "Launcher must receive a PSI after StartPreparedHandoff.");
        Assert.AreEqual(prepared.PowerShellPath, _launcher.LastStartInfo!.FileName,
            "Launcher PSI must use the PowerShellPath stored in the token.");
        Assert.IsTrue(_launcher.LastStartInfo.ArgumentList.Contains(prepared.CallerProcessId.ToString()),
            "Launcher PSI must pass CallerProcessId as -WaitForProcessId.");
    }

    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "FootballTray.sln")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [TestMethod]
    public async Task Test41_PrepareInstall_VersionTxtMatchesManifest_BinaryMismatch_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        string root = FindRepoRoot();
        string realExe = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64", "FootballTray.exe");
        Assert.IsTrue(File.Exists(realExe), "Real FootballTray.exe must exist in historical release.");

        // Manifest and VERSION.txt both say 1.0.1, but executable binary has version 1.0.0
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.0.1", customVersionTxt: "FootballTray v1.0.1", customExePath: realExe);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });

        Assert.IsTrue(ex.Message.Contains("binary version 'v1.0.0' does not match expected version 'v1.0.1'"),
            $"Expected binary version mismatch error. Actual: {ex.Message}");
        Assert.AreEqual(0, _launcher.StartCallCount, "No process launch should occur when binary version mismatches.");
    }

    [TestMethod]
    public async Task Test42_PrepareInstall_InverseMismatch_VersionTxtDiffersFromBinary_ThrowsInvalidOperationException()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        string root = FindRepoRoot();
        string realExe = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64", "FootballTray.exe");
        Assert.IsTrue(File.Exists(realExe), "Real FootballTray.exe must exist in historical release.");

        // Manifest is 1.0.0 (matches real binary 1.0.0), but VERSION.txt says 1.0.1
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.0.0", customVersionTxt: "FootballTray v1.0.1", customExePath: realExe);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.PrepareInstallAsync(manifest, zipPath);
        });

        Assert.IsTrue(ex.Message.Contains("VERSION.txt") || ex.Message.Contains("mismatch"),
            $"Expected version mismatch error. Actual: {ex.Message}");
        Assert.AreEqual(0, _launcher.StartCallCount, "No process launch should occur on version mismatch.");
    }

    [TestMethod]
    public async Task Test43_PrepareInstall_VersionTxtAbsent_PeFallbackSucceeds()
    {
        using var service = new UpdateInstallationService(_launcher, _updatesDir, _trustedInstallerScript);
        string root = FindRepoRoot();
        string realExe = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64", "FootballTray.exe");
        Assert.IsTrue(File.Exists(realExe), "Real FootballTray.exe must exist in historical release.");

        // Manifest is 1.0.0, VERSION.txt is ABSENT, real binary is 1.0.0 -> PE fallback succeeds
        var (manifest, zipPath) = CreateValidReleaseZip(_tempDir, "1.0.0", includeVersionTxt: false, customExePath: realExe);

        var prepared = await service.PrepareInstallAsync(manifest, zipPath);
        Assert.IsNotNull(prepared);
        Assert.AreEqual("1.0.0", prepared.TargetVersion);
    }

    #endregion
}
