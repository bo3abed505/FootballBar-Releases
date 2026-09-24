using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Core;
using FootballTray.Core.Services;

namespace FootballTray.Client.Tests;

[TestClass]
[DoNotParallelize]
public class ReleaseEngineeringTests
{
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
    public void Test01_VersionSource_IsValidSemVer()
    {
        string root = FindRepoRoot();
        string versionFile = Path.Combine(root, "VERSION");
        Assert.IsTrue(File.Exists(versionFile), "VERSION file must exist in repository root.");

        string version = File.ReadAllText(versionFile).Trim();
        Assert.IsTrue(Regex.IsMatch(version, @"^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$"),
            $"VERSION '{version}' is not valid SemVer format.");
    }

    [TestMethod]
    public void Test02_AppVersionInfo_MatchesConfiguredVersion()
    {
        string root = FindRepoRoot();
        string versionFile = Path.Combine(root, "VERSION");
        string expectedVersion = File.ReadAllText(versionFile).Trim();

        Assert.AreEqual(expectedVersion, AppVersionInfo.Version);
    }

    [TestMethod]
    public void Test03_PublishProfile_SpecifiesWinX64AndSelfContained()
    {
        string root = FindRepoRoot();
        string pubxmlPath = Path.Combine(root, "src", "client", "FootballTray.Client", "Properties", "PublishProfiles", "win-x64.pubxml");
        Assert.IsTrue(File.Exists(pubxmlPath), "win-x64.pubxml must exist.");

        string content = File.ReadAllText(pubxmlPath);
        Assert.IsTrue(content.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>"), "Must target win-x64");
        Assert.IsTrue(content.Contains("<SelfContained>true</SelfContained>"), "Must be self-contained");
        Assert.IsTrue(content.Contains("<PublishSingleFile>False</PublishSingleFile>") || content.Contains("<PublishSingleFile>false</PublishSingleFile>"), "Must not force single-file");
    }

    [TestMethod]
    public void Test04_FootballApiClient_GetDefaultBaseUrl_AllowsEnvironmentOverride()
    {
        string? original = Environment.GetEnvironmentVariable("FOOTBALLTRAY_BACKEND_URL");
        try
        {
            Environment.SetEnvironmentVariable("FOOTBALLTRAY_BACKEND_URL", null);
            Assert.AreEqual("http://127.0.0.1:4000", FootballApiClient.GetDefaultBaseUrl());

            Environment.SetEnvironmentVariable("FOOTBALLTRAY_BACKEND_URL", "http://192.168.1.100:5000/");
            Assert.AreEqual("http://192.168.1.100:5000", FootballApiClient.GetDefaultBaseUrl());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FOOTBALLTRAY_BACKEND_URL", original);
        }
    }

    [TestMethod]
    public void Test05_ClientConstants_ContainNoSecretsOrDatabaseCredentials()
    {
        string root = FindRepoRoot();
        string clientDir = Path.Combine(root, "src", "client");
        var csFiles = Directory.GetFiles(clientDir, "*.cs", SearchOption.AllDirectories);

        string[] forbiddenKeywords =
        [
            "API_FOOTBALL_KEY",
            "x-apisports-key",
            "postgresql://",
            "postgres:your_password",
            "DATABASE_URL"
        ];

        foreach (var file in csFiles)
        {
            string content = File.ReadAllText(file);
            foreach (var kw in forbiddenKeywords)
            {
                Assert.IsFalse(content.Contains(kw), $"Forbidden secret/database keyword '{kw}' found in client source '{Path.GetFileName(file)}'.");
            }
        }
    }

    [TestMethod]
    public void Test06_DeterministicSha256Hashing_ProducesValidHex()
    {
        byte[] testData = "FootballTray Release Verification"u8.ToArray();
        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(testData);
        string hex = Convert.ToHexStringLower(hashBytes);

        Assert.AreEqual(64, hex.Length);
        Assert.IsTrue(Regex.IsMatch(hex, "^[a-f0-9]{64}$"));
    }

    [TestMethod]
    public void Test07_PublishScripts_ExistAndAreExecutable()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");

        Assert.IsTrue(File.Exists(publishScript), "publish-release.ps1 must exist.");
        Assert.IsTrue(File.Exists(validateScript), "validate-release.ps1 must exist.");

        string publishContent = File.ReadAllText(publishScript);
        Assert.IsTrue(publishContent.Contains("FootballTray.exe"), "publish script must check FootballTray.exe");
        Assert.IsTrue(publishContent.Contains("SHA256SUMS.txt"), "publish script must generate checksums");

        string validateContent = File.ReadAllText(validateScript);
        Assert.IsTrue(validateContent.Contains("IMAGE_FILE_MACHINE_AMD64") || validateContent.Contains("0x8664"), "validate script must verify x64 PE header");
    }

    [TestMethod]
    public void Test08_BackendProductionTemplate_ContainsOnlySafePlaceholders()
    {
        string root = FindRepoRoot();
        string templatePath = Path.Combine(root, "backend", ".env.production.example");
        Assert.IsTrue(File.Exists(templatePath), ".env.production.example must exist.");

        string content = File.ReadAllText(templatePath);
        Assert.IsTrue(content.Contains("ALLOW_LIVE_PROVIDER_REQUESTS=false"), "Template must default to safe offline mode");
        Assert.IsTrue(content.Contains("YOUR_PRODUCTION_API_FOOTBALL_KEY_HERE"), "Template must have API key placeholder");
        Assert.IsTrue(content.Contains("SECURE_DB_PASSWORD"), "Template must have DB password placeholder");
    }

    #region Release Manifest Pipeline Tests

    private sealed class IsolatedReleaseFixture : IDisposable
    {
        public string FixturePath { get; }
        public string ManifestPath { get; }
        public string ArchivePath { get; }
        public string ChecksumPath { get; }

        public IsolatedReleaseFixture(string root, string version = "1.0.0", bool makeA2Compliant = false)
        {
            string realRelease = Path.Combine(root, "artifacts", "releases", $"v{version}");
            FixturePath = Path.Combine(root, "artifacts", $".tmp_fixture_{Guid.NewGuid():N}");
            Directory.CreateDirectory(FixturePath);

            // Copy win-x64 directory independently (zero hardlinks) to ensure complete test isolation.
            // Using independent copies guarantees that mutating fixture files (such as deps.json
            // in MakeA2Compliant, or release notes in tests) never writes through or modifies the immutable historical release.
            string realPublish = Path.Combine(realRelease, "win-x64");
            string fixturePublish = Path.Combine(FixturePath, "win-x64");
            CopyDirectory(realPublish, fixturePublish);

            // Copy ZIP independently to ensure complete isolation
            string zipName = $"FootballTray-v{version}-win-x64.zip";
            string realZip = Path.Combine(realRelease, zipName);
            ArchivePath = Path.Combine(FixturePath, zipName);
            File.Copy(realZip, ArchivePath, overwrite: true);

            // Copy small metadata files
            File.Copy(Path.Combine(realRelease, "SHA256SUMS.txt"), Path.Combine(FixturePath, "SHA256SUMS.txt"));
            File.Copy(Path.Combine(realRelease, "VERSION.txt"), Path.Combine(FixturePath, "VERSION.txt"));
            if (File.Exists(Path.Combine(realRelease, "RELEASE_NOTES.txt")))
            {
                File.Copy(Path.Combine(realRelease, "RELEASE_NOTES.txt"), Path.Combine(FixturePath, "RELEASE_NOTES.txt"));
            }

            ManifestPath = Path.Combine(FixturePath, "release-manifest.json");
            ChecksumPath = Path.Combine(FixturePath, "SHA256SUMS.txt");

            if (makeA2Compliant)
            {
                MakeA2Compliant();
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir);
            }
        }

        public void MakeA2Compliant()
        {
            string fixturePublish = Path.Combine(FixturePath, "win-x64");
            string[] forbiddenAiMlFiles =
            [
                "DirectML.dll",
                "Microsoft.Graphics.Imaging.Projection.dll",
                "Microsoft.ML.OnnxRuntime.dll",
                "Microsoft.Windows.AI.ContentSafety.Projection.dll",
                "Microsoft.Windows.AI.Foundation.Projection.dll",
                "Microsoft.Windows.AI.Imaging.Projection.dll",
                "Microsoft.Windows.AI.MachineLearning.dll",
                "Microsoft.Windows.AI.MachineLearning.Projection.dll",
                "Microsoft.Windows.AI.Projection.dll",
                "Microsoft.Windows.AI.Text.Projection.dll",
                "Microsoft.Windows.AI.Video.Projection.dll",
                "onnxruntime.dll",
                "System.Numerics.Tensors.dll"
            ];
            foreach (var forbidden in forbiddenAiMlFiles)
            {
                string target = Path.Combine(fixturePublish, forbidden);
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }

            string fixtureDeps = Path.Combine(fixturePublish, "FootballTray.deps.json");
            if (File.Exists(fixtureDeps))
            {
                var depsNode = JsonNode.Parse(File.ReadAllText(fixtureDeps));
                if (depsNode != null)
                {
                    string[] forbiddenPrefixes =
                    [
                        "Microsoft.WindowsAppSDK.ML",
                        "Microsoft.WindowsAppSDK.AI",
                        "Microsoft.Windows.AI.MachineLearning",
                        "Microsoft.ML.OnnxRuntime",
                        "System.Numerics.Tensors"
                    ];

                    var targetsNode = depsNode["targets"]?.AsObject();
                    if (targetsNode != null)
                    {
                        foreach (var targetKvp in targetsNode)
                        {
                            var targetObj = targetKvp.Value?.AsObject();
                            if (targetObj != null)
                            {
                                var toRemove = targetObj
                                    .Where(p => forbiddenPrefixes.Any(fp => p.Key == fp || p.Key.StartsWith(fp + "/")))
                                    .Select(p => p.Key)
                                    .ToList();
                                foreach (var key in toRemove) targetObj.Remove(key);
                            }
                        }
                    }

                    var libsNode = depsNode["libraries"]?.AsObject();
                    if (libsNode != null)
                    {
                        var toRemove = libsNode
                            .Where(p => forbiddenPrefixes.Any(fp => p.Key == fp || p.Key.StartsWith(fp + "/")))
                            .Select(p => p.Key)
                            .ToList();
                        foreach (var key in toRemove) libsNode.Remove(key);
                    }

                    File.WriteAllText(fixtureDeps, depsNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(FixturePath))
            {
                try
                {
                    RunPowerShell($"Remove-Item -LiteralPath '{FixturePath}' -Recurse -Force", Path.GetDirectoryName(FixturePath)!);
                }
                catch { }
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPowerShell(
        string scriptContent,
        string workingDirectory,
        int timeoutMs = 60000)
    {
        string tempScript = Path.Combine(Path.GetTempPath(), $"pwsh_test_{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(tempScript, scriptContent);
            return RunScriptDirect(tempScript, string.Empty, workingDirectory, timeoutMs);
        }
        finally
        {
            if (File.Exists(tempScript))
            {
                try { File.Delete(tempScript); } catch { }
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunScriptDirect(
        string scriptPath,
        string arguments,
        string workingDirectory,
        int timeoutMs = 60000)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = string.IsNullOrWhiteSpace(arguments)
                ? $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
                : $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (stdoutBuilder) { stdoutBuilder.AppendLine(e.Data); }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (stderrBuilder) { stderrBuilder.AppendLine(e.Data); }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"PowerShell process timed out after {timeoutMs}ms. Target: {scriptPath} {arguments}");
        }

        process.WaitForExit();

        string stdout;
        string stderr;
        lock (stdoutBuilder) { stdout = stdoutBuilder.ToString(); }
        lock (stderrBuilder) { stderr = stderrBuilder.ToString(); }

        return (process.ExitCode, stdout, stderr);
    }

    [TestMethod]
    public void Test09_PublishScript_WithoutUpdateDownloadUrl_RetainsCompatibilityAndOmitsManifest()
    {
        string root = FindRepoRoot();
        string testVersion = $"0.0.0-test.{Guid.NewGuid():N}";
        string releaseDir = Path.Combine(root, "artifacts", "releases", $"v{testVersion}");
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        string manifestFile = Path.Combine(releaseDir, "release-manifest.json");

        try
        {
            var result = RunScriptDirect(publishScript, $"-Version \"{testVersion}\"", root, timeoutMs: 120000);
            Assert.AreEqual(0, result.ExitCode, $"publish-release failed: {result.Stdout} {result.Stderr}");
            Assert.IsTrue(File.Exists(Path.Combine(releaseDir, "win-x64", "FootballTray.exe")), "Executable must exist.");
            Assert.IsTrue(File.Exists(Path.Combine(releaseDir, $"FootballTray-v{testVersion}-win-x64.zip")), "Archive must exist.");
            Assert.IsTrue(File.Exists(Path.Combine(releaseDir, "SHA256SUMS.txt")), "Checksums must exist.");
            Assert.IsTrue(File.Exists(Path.Combine(releaseDir, "VERSION.txt")), "VERSION.txt must exist.");
            Assert.IsFalse(File.Exists(manifestFile), "release-manifest.json must NOT be generated when -UpdateDownloadUrl is omitted.");
            Assert.IsTrue(result.Stdout.Contains("Skipping release manifest (-UpdateDownloadUrl was not specified)."),
                "Expected skip logging in output.");
        }
        finally
        {
            if (Directory.Exists(releaseDir))
            {
                try { Directory.Delete(releaseDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test10_PublishScript_FailsFastOnInvalidUpdateUrls()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");

        // 1. Insecure HTTP
        var rHttp = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"http://releases.example.invalid/v1.0.0/FootballTray-v1.0.0-win-x64.zip\"", root);
        Assert.AreNotEqual(0, rHttp.ExitCode);
        Assert.IsTrue(rHttp.Stdout.Contains("must use HTTPS scheme") || rHttp.Stderr.Contains("must use HTTPS scheme"),
            $"Expected HTTPS scheme failure message. Output: {rHttp.Stdout} {rHttp.Stderr}");

        // 2. Local file:// scheme
        var rFile = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"file:///C:/FootballTray-v1.0.0-win-x64.zip\"", root);
        Assert.AreNotEqual(0, rFile.ExitCode);
        Assert.IsTrue(rFile.Stdout.Contains("must use HTTPS scheme") || rFile.Stderr.Contains("must use HTTPS scheme"),
            $"Expected file scheme failure message. Output: {rFile.Stdout} {rFile.Stderr}");

        // 3. Relative URL
        var rRel = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"/downloads/FootballTray-v1.0.0-win-x64.zip\"", root);
        Assert.AreNotEqual(0, rRel.ExitCode);
        Assert.IsTrue(rRel.Stdout.Contains("not a valid absolute URI") || rRel.Stderr.Contains("not a valid absolute URI"),
            $"Expected absolute URI failure message. Output: {rRel.Stdout} {rRel.Stderr}");

        // 4. Empty string
        var rEmpty = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"\"", root);
        Assert.AreNotEqual(0, rEmpty.ExitCode);
        Assert.IsTrue(rEmpty.Stdout.Contains("empty or whitespace") || rEmpty.Stderr.Contains("empty or whitespace"),
            $"Expected empty URL failure message. Output: {rEmpty.Stdout} {rEmpty.Stderr}");

        // 5. Whitespace string
        var rWs = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"   \"", root);
        Assert.AreNotEqual(0, rWs.ExitCode);
        Assert.IsTrue(rWs.Stdout.Contains("empty or whitespace") || rWs.Stderr.Contains("empty or whitespace"),
            $"Expected whitespace URL failure message. Output: {rWs.Stdout} {rWs.Stderr}");
    }

    [TestMethod]
    public void Test11_PublishScript_FailsFastOnUrlFilenameMismatch()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");

        var result = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"https://example.invalid/releases/v1.0.0/Foo.zip\"", root);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsTrue(result.Stdout.Contains("does not match the generated release archive filename") ||
                      result.Stderr.Contains("does not match the generated release archive filename"),
            $"Expected filename mismatch failure. Output: {result.Stdout} {result.Stderr}");
    }

    [TestMethod]
    public void Test12_PublishScript_FailsFastWhenSkipArchiveSpecifiedWithUpdateUrl()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");

        var result = RunScriptDirect(publishScript, "-UpdateDownloadUrl \"https://example.invalid/v1.0.0/FootballTray-v1.0.0-win-x64.zip\" -SkipArchive", root);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsTrue(result.Stdout.Contains("Cannot generate release manifest when -SkipArchive is specified") ||
                      result.Stderr.Contains("Cannot generate release manifest when -SkipArchive is specified"),
            $"Expected -SkipArchive conflict failure. Output: {result.Stdout} {result.Stderr}");
    }

    [TestMethod]
    public void Test13_PublishScript_GeneratesValidManifestAcceptedByDomainParser()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root);

        const string downloadUrl = "https://example.invalid/releases/v1.0.0/FootballTray-v1.0.0-win-x64.zip";
        string notesFile = Path.Combine(fixture.FixturePath, "RELEASE_NOTES.txt");

        var result = RunPowerShell(
            $". '{publishScript}'; New-ReleaseManifest -Version '1.0.0' -DownloadUrl '{downloadUrl}' -ArchiveFile '{fixture.ArchivePath}' -ReleaseNotesFile '{notesFile}' -ManifestFile '{fixture.ManifestPath}'",
            root);

        Assert.AreEqual(0, result.ExitCode, $"New-ReleaseManifest failed: {result.Stdout} {result.Stderr}");
        Assert.IsTrue(File.Exists(fixture.ManifestPath), "Manifest file must be generated.");

        string json = File.ReadAllText(fixture.ManifestPath);

        // Verify with actual C# domain parser
        var parseResult = ReleaseManifestParser.ParseAndValidate(json);
        Assert.IsTrue(parseResult.IsValid, $"Manifest validation failed: {parseResult.ErrorMessage}");

        var manifest = parseResult.ValidatedManifest!;
        Assert.AreEqual("1.0", manifest.SchemaVersion);
        Assert.AreEqual("1.0.0", manifest.Version);
        Assert.AreEqual("win-x64", manifest.Architecture);
        Assert.AreEqual(downloadUrl, manifest.DownloadUrl);

        // Compute actual ZIP hash and length to verify exact match
        byte[] archiveBytes = File.ReadAllBytes(fixture.ArchivePath);
        using var sha = SHA256.Create();
        string expectedZipHash = Convert.ToHexStringLower(sha.ComputeHash(archiveBytes));
        Assert.AreEqual(expectedZipHash, manifest.Sha256);
        Assert.AreEqual(archiveBytes.LongLength, manifest.SizeBytes);

        // Verify timestamp has explicit UTC Z
        Assert.AreEqual(TimeSpan.Zero, manifest.PublishedAt.Offset);
        Assert.IsTrue(json.Contains("\"publishedAt\":"));
        Assert.IsTrue(Regex.IsMatch(json, @"""publishedAt"":\s*""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z"""));

        // Verify release notes present
        Assert.IsNotNull(manifest.ReleaseNotes);
        Assert.IsTrue(manifest.ReleaseNotes.Contains("FootballTray v1.0.0"));
    }

    [TestMethod]
    public void Test14_PublishScript_JsonSensitiveReleaseNotes_RemainValid()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root);

        string sensitiveNotesFile = Path.Combine(fixture.FixturePath, "SENSITIVE_NOTES.txt");
        string sensitiveNotes = "Version 1.0.0 \"Special\"\r\nPath: C:\\Program Files\\FootballTray\\bin\\\nFeatures:\n\t• Improved \"Taskbar\" integration\n\t• Fixed \\ escaped symbols & quotes 'test'";
        File.WriteAllText(sensitiveNotesFile, sensitiveNotes);

        var result = RunPowerShell(
            $". '{publishScript}'; New-ReleaseManifest -Version '1.0.0' -DownloadUrl 'https://example.invalid/releases/v1.0.0/FootballTray-v1.0.0-win-x64.zip' -ArchiveFile '{fixture.ArchivePath}' -ReleaseNotesFile '{sensitiveNotesFile}' -ManifestFile '{fixture.ManifestPath}'",
            root);

        Assert.AreEqual(0, result.ExitCode, $"Failed: {result.Stdout} {result.Stderr}");

        string json = File.ReadAllText(fixture.ManifestPath);
        var parseResult = ReleaseManifestParser.ParseAndValidate(json);
        Assert.IsTrue(parseResult.IsValid, $"Validation failed: {parseResult.ErrorMessage}");

        // Verify content faithfully preserved
        Assert.AreEqual(sensitiveNotes, parseResult.ValidatedManifest!.ReleaseNotes);
    }

    [TestMethod]
    public void Test15_PublishScript_ReleaseNotesExceeding10000Chars_FailsGeneration()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root);

        string hugeNotesFile = Path.Combine(fixture.FixturePath, "HUGE_NOTES.txt");
        string hugeNotes = new string('A', 10001);
        File.WriteAllText(hugeNotesFile, hugeNotes);

        var result = RunPowerShell(
            $". '{publishScript}'; New-ReleaseManifest -Version '1.0.0' -DownloadUrl 'https://example.invalid/releases/v1.0.0/FootballTray-v1.0.0-win-x64.zip' -ArchiveFile '{fixture.ArchivePath}' -ReleaseNotesFile '{hugeNotesFile}' -ManifestFile '{fixture.ManifestPath}'",
            root);

        Assert.AreNotEqual(0, result.ExitCode, "Manifest generation must fail when release notes exceed 10000 characters.");
        Assert.IsTrue(result.Stdout.Contains("exceeds maximum allowed length of 10000 characters") ||
                      result.Stderr.Contains("exceeds maximum allowed length of 10000 characters"),
            $"Expected length error message. Output: {result.Stdout} {result.Stderr}");
        Assert.IsFalse(File.Exists(fixture.ManifestPath), "Manifest file must not be created on failure.");
    }

    [TestMethod]
    public void Test16_ValidateReleaseScript_ManifestScenarios()
    {
        string root = FindRepoRoot();
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root, makeA2Compliant: true);

        // Case A: Manifest does NOT exist -> skipped and allowed
        var rWithout = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(0, rWithout.ExitCode, $"validate-release failed without manifest: {rWithout.Stdout} {rWithout.Stderr}");
        Assert.IsTrue(rWithout.Stdout.Contains("No release-manifest.json found (optional artifact; skipped)"));

        // Case B: Valid manifest exists -> passes
        byte[] archiveBytes = File.ReadAllBytes(fixture.ArchivePath);
        using var sha = SHA256.Create();
        string zipHash = Convert.ToHexStringLower(sha.ComputeHash(archiveBytes));

        string validManifestJson = "{\n" +
            "  \"schemaVersion\": \"1.0\",\n" +
            "  \"version\": \"1.0.0\",\n" +
            "  \"publishedAt\": \"2026-09-10T20:00:00Z\",\n" +
            "  \"architecture\": \"win-x64\",\n" +
            "  \"downloadUrl\": \"https://example.invalid/releases/v1.0.0/FootballTray-v1.0.0-win-x64.zip\",\n" +
            $"  \"sha256\": \"{zipHash}\",\n" +
            $"  \"sizeBytes\": {archiveBytes.LongLength},\n" +
            "  \"releaseNotes\": \"FootballTray v1.0.0 release notes\"\n" +
            "}";

        File.WriteAllText(fixture.ManifestPath, validManifestJson);

        var rWith = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(0, rWith.ExitCode, $"validate-release failed with valid manifest: {rWith.Stdout} {rWith.Stderr}");
        Assert.IsTrue(rWith.Stdout.Contains("Manifest SHA-256 matches release archive"));
        Assert.IsTrue(rWith.Stdout.Contains("Manifest sizeBytes matches release archive"));
        Assert.IsTrue(rWith.Stdout.Contains("Manifest downloadUrl matches archive filename"));
        Assert.IsTrue(rWith.Stdout.Contains("VALIDATION PASSED"));

        // Case C: Corrupted manifest (invalid sha256) -> fails
        string corruptManifestJson = validManifestJson.Replace(zipHash, "0000000000000000000000000000000000000000000000000000000000000000");
        File.WriteAllText(fixture.ManifestPath, corruptManifestJson);

        var rCorrupt = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, rCorrupt.ExitCode, "validate-release must exit with code 1 on sha256 mismatch");
        Assert.IsTrue(rCorrupt.Stdout.Contains("sha256 mismatch") || rCorrupt.Stderr.Contains("sha256 mismatch"));

        // Case D: DownloadUrl filename mismatch -> fails
        string mismatchedUrlManifestJson = validManifestJson.Replace("FootballTray-v1.0.0-win-x64.zip", "WrongArchiveName.zip");
        File.WriteAllText(fixture.ManifestPath, mismatchedUrlManifestJson);

        var rUrlMismatch = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, rUrlMismatch.ExitCode, "validate-release must exit with code 1 on downloadUrl filename mismatch");
        Assert.IsTrue(rUrlMismatch.Stdout.Contains("downloadUrl filename") && rUrlMismatch.Stdout.Contains("does not match archive filename"));

        // Case E: Offset-less timestamp fails
        string offsetLessManifestJson = validManifestJson.Replace("2026-09-10T20:00:00Z", "2026-09-10T20:00:00");
        File.WriteAllText(fixture.ManifestPath, offsetLessManifestJson);

        var rOffsetLess = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, rOffsetLess.ExitCode, "validate-release must exit with code 1 on offset-less timestamp");
        Assert.IsTrue(rOffsetLess.Stdout.Contains("must include an explicit UTC 'Z' or timezone offset"));

        // Case F: Malformed JSON syntax -> fails safely
        string malformedJson = "{ \"schemaVersion\": \"1.0\", unquoted_bad_json: ";
        File.WriteAllText(fixture.ManifestPath, malformedJson);
        var rMalformed = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, rMalformed.ExitCode, "validate-release must exit with code 1 on malformed JSON");
        Assert.IsTrue(rMalformed.Stdout.Contains("Failed to parse or inspect manifest") || rMalformed.Stderr.Contains("Failed to parse or inspect manifest"));

        // Case G: Required field with wrong JSON type (sizeBytes as string) -> fails
        string wrongTypeJson = validManifestJson.Replace($"\"sizeBytes\": {archiveBytes.LongLength}", "\"sizeBytes\": \"not-a-number\"");
        File.WriteAllText(fixture.ManifestPath, wrongTypeJson);
        var rWrongType = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, rWrongType.ExitCode, "validate-release must exit with code 1 on wrong JSON type for sizeBytes");
        Assert.IsTrue(rWrongType.Stdout.Contains("sizeBytes is missing or not a number") || rWrongType.Stderr.Contains("sizeBytes is missing or not a number"));

        // Case H: Incompatible JSON type for releaseNotes (number instead of string/null) -> fails
        string wrongNotesTypeJson = validManifestJson.Replace("\"releaseNotes\": \"FootballTray v1.0.0 release notes\"", "\"releaseNotes\": 123");
        File.WriteAllText(fixture.ManifestPath, wrongNotesTypeJson);
        var rWrongNotes = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, rWrongNotes.ExitCode, "validate-release must exit with code 1 on incompatible releaseNotes type");
        Assert.IsTrue(rWrongNotes.Stdout.Contains("releaseNotes must be a string or null") || rWrongNotes.Stderr.Contains("releaseNotes must be a string or null"));

        // Case I: Required field with different casing (SchemaVersion) -> matches Core case-insensitive contract and succeeds
        string casedJson = validManifestJson.Replace("\"schemaVersion\": \"1.0\"", "\"SchemaVersion\": \"1.0\"");
        var coreResult = ReleaseManifestParser.ParseAndValidate(casedJson);
        Assert.IsTrue(coreResult.IsValid, $"Core parser must accept SchemaVersion casing: {coreResult.ErrorMessage}");

        File.WriteAllText(fixture.ManifestPath, casedJson);
        var rCasing = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(0, rCasing.ExitCode, $"validate-release must exit with code 0 on SchemaVersion casing parity: {rCasing.Stdout} {rCasing.Stderr}");
        Assert.IsTrue(rCasing.Stdout.Contains("VALIDATION PASSED"), "validate-release should report VALIDATION PASSED");
    }

    [TestMethod]
    public void Test17_PublishScript_WithUpdateDownloadUrl_GeneratesValidManifestEndToEnd()
    {
        string root = FindRepoRoot();
        string testVersion = $"0.0.0-test.{Guid.NewGuid():N}";
        string releaseDir = Path.Combine(root, "artifacts", "releases", $"v{testVersion}");
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        string manifestFile = Path.Combine(releaseDir, "release-manifest.json");
        string zipFile = Path.Combine(releaseDir, $"FootballTray-v{testVersion}-win-x64.zip");
        string checksumFile = Path.Combine(releaseDir, "SHA256SUMS.txt");
        string downloadUrl = $"https://example.invalid/releases/v{testVersion}/FootballTray-v{testVersion}-win-x64.zip";

        try
        {
            var result = RunScriptDirect(
                publishScript,
                $"-Version \"{testVersion}\" -UpdateDownloadUrl \"{downloadUrl}\"",
                root,
                timeoutMs: 180000);

            Assert.AreEqual(0, result.ExitCode, $"publish-release failed: {result.Stdout} {result.Stderr}");
            Assert.IsTrue(Directory.Exists(releaseDir), "Release directory must exist.");
            Assert.IsTrue(File.Exists(zipFile), "ZIP archive must exist.");
            Assert.IsTrue(File.Exists(manifestFile), "release-manifest.json must exist.");

            // Verify with actual domain parser
            string json = File.ReadAllText(manifestFile);
            var parseResult = ReleaseManifestParser.ParseAndValidate(json);
            Assert.IsTrue(parseResult.IsValid, $"Manifest domain validation failed: {parseResult.ErrorMessage}");

            var manifest = parseResult.ValidatedManifest!;
            Assert.AreEqual(testVersion, manifest.Version);
            Assert.AreEqual(downloadUrl, manifest.DownloadUrl);
            Assert.AreEqual(TimeSpan.Zero, manifest.PublishedAt.Offset);

            // Verify SHA-256 and byte size against actual generated ZIP
            byte[] zipBytes = File.ReadAllBytes(zipFile);
            using var sha = SHA256.Create();
            string expectedZipHash = Convert.ToHexStringLower(sha.ComputeHash(zipBytes));
            long expectedSizeBytes = zipBytes.LongLength;

            Assert.AreEqual(expectedZipHash, manifest.Sha256);
            Assert.AreEqual(expectedSizeBytes, manifest.SizeBytes);

            // Verify SHA256SUMS.txt entry matches the same ZIP
            Assert.IsTrue(File.Exists(checksumFile), "SHA256SUMS.txt must exist.");
            string checksumContent = File.ReadAllText(checksumFile);
            Assert.IsTrue(checksumContent.Contains(expectedZipHash), "SHA256SUMS.txt must contain the ZIP hash.");

            // Verify end-to-end generated release notes use generic fallback without Milestone 8
            string releaseNotesPath = Path.Combine(releaseDir, "RELEASE_NOTES.txt");
            Assert.IsTrue(File.Exists(releaseNotesPath), "RELEASE_NOTES.txt must exist in release directory.");
            string releaseNotes = File.ReadAllText(releaseNotesPath);
            Assert.IsTrue(releaseNotes.Contains($"FootballTray v{testVersion}"), "Fallback release notes must contain test version.");
            Assert.IsTrue(releaseNotes.Contains("Release — Native Windows Taskbar Football Tracker"), "Fallback release notes must contain generic header.");
            Assert.IsFalse(releaseNotes.Contains("Milestone 8"), "Fallback release notes must NOT contain Milestone 8.");
            Assert.IsFalse(releaseNotes.Contains("Changes since v1.0.0"), "Fallback release notes must NOT contain Changes since v1.0.0.");
            Assert.AreEqual(releaseNotes.Trim(), manifest.ReleaseNotes?.Trim(), "manifest.releaseNotes must match RELEASE_NOTES.txt.");
        }
        finally
        {
            if (Directory.Exists(releaseDir))
            {
                try { Directory.Delete(releaseDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test18_PublishedClientPayload_ContainsInstallClientScript()
    {
        string root = FindRepoRoot();
        string csprojPath = Path.Combine(root, "src", "client", "FootballTray.Client", "FootballTray.Client.csproj");
        Assert.IsTrue(File.Exists(csprojPath), "FootballTray.Client.csproj must exist.");

        string content = File.ReadAllText(csprojPath);
        Assert.IsTrue(content.Contains("scripts\\install-client.ps1") || content.Contains("scripts/install-client.ps1"),
            "csproj must include scripts/install-client.ps1");
        Assert.IsTrue(content.Contains("scripts\\uninstall-client.ps1") || content.Contains("scripts/uninstall-client.ps1"),
            "csproj must include scripts/uninstall-client.ps1");
        Assert.IsTrue(content.Contains("<CopyToPublishDirectory>Always</CopyToPublishDirectory>") ||
                      content.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>"),
            "install-client.ps1 must be configured to copy to publish directory.");

        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        string validateContent = File.ReadAllText(validateScript);
        Assert.IsTrue(validateContent.Contains("install-client.ps1"),
            "validate-release.ps1 must require install-client.ps1 in publish directory.");
        Assert.IsTrue(validateContent.Contains("uninstall-client.ps1"),
            "validate-release.ps1 must require uninstall-client.ps1 in publish directory.");
    }

    [TestMethod]
    public void Test19_InstallerScript_WaitForProcessId_ExitedPidAllowsContinuation()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");

        // Spawn a process that exits immediately
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var helperProcess = System.Diagnostics.Process.Start(psi)!;
        helperProcess.WaitForExit();
        int exitedPid = helperProcess.Id;

        string sourceDir = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64");
        string sourceArg = Directory.Exists(sourceDir) ? $"-SourceDir \"{sourceDir}\"" : "";

        // Passing an exited PID with -WhatIf should wait 0s, detect process exited, and proceed to WhatIf
        var result = RunScriptDirect(installerScript, $"-WaitForProcessId {exitedPid} -WhatIf -AllowDowngrade {sourceArg}", root);
        Assert.AreEqual(0, result.ExitCode, $"install-client with exited PID failed: {result.Stdout} {result.Stderr}");
        Assert.IsTrue(result.Stdout.Contains("Calling process exited gracefully.") || result.Stdout.Contains("WHATIF:"),
            $"Expected successful wait output. Stdout: {result.Stdout}");
    }

    [TestMethod]
    public void Test20_InstallerScript_WaitForProcessId_TimeoutFailsClosedWithoutForcedKill()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");

        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Start-Sleep -Seconds 30\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var livingProcess = System.Diagnostics.Process.Start(psi)!;
        try
        {
            int livingPid = livingProcess.Id;

            string sourceDir = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64");
            string sourceArg = Directory.Exists(sourceDir) ? $"-SourceDir \"{sourceDir}\"" : "";

            var result = RunScriptDirect(installerScript, $"-WaitForProcessId {livingPid} -WhatIf -AllowDowngrade {sourceArg}", root, timeoutMs: 35000);
            Assert.AreNotEqual(0, result.ExitCode, "Installer must fail closed when caller does not exit.");
            Assert.IsTrue(result.Stdout.Contains("did not exit within 15 seconds") || result.Stderr.Contains("did not exit within 15 seconds"),
                $"Expected 15s timeout error message. Stdout: {result.Stdout} Stderr: {result.Stderr}");

            // Crucial assertion: Caller process must NOT have been force-killed!
            Assert.IsFalse(livingProcess.HasExited, "Installer must NOT force-kill the calling process upon timeout.");
        }
        finally
        {
            try { livingProcess.Kill(entireProcessTree: true); } catch { }
        }
    }

    [TestMethod]
    public void Test21_InstallerScript_WaitForProcessId_PreservesRemainingProcessProtection()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        string content = File.ReadAllText(installerScript);

        // Verify WaitForProcessId block exists
        Assert.IsTrue(content.Contains("$WaitForProcessId -gt 0"), "Script must handle $WaitForProcessId > 0.");

        // Verify Stop-RunningClient is called after wait loop inside WaitForProcessId block
        int waitIdx = content.IndexOf("$WaitForProcessId -gt 0");
        int stopRunningClientIdx = content.IndexOf("Stop-RunningClient", waitIdx);
        Assert.IsTrue(stopRunningClientIdx > waitIdx, "Stop-RunningClient must be called after waiting for caller PID.");

        // Verify Stop-RunningClient occurs before destructive staging and backup directory cleanup
        int stagingIdx = content.IndexOf("$StagingDir", stopRunningClientIdx);
        Assert.IsTrue(stagingIdx > stopRunningClientIdx, "Destructive staging operations must occur after Stop-RunningClient.");
    }

    private static string NormalizeProductVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        int plusIdx = raw.IndexOf('+');
        return plusIdx > 0 ? raw[..plusIdx] : raw;
    }

    [TestMethod]
    public void Test22_PublishScript_PropagatesVersionToCompiledBinaries_EndToEnd()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        const string testVersion = "9.8.7";
        string releaseDir = Path.Combine(root, "artifacts", "releases", $"v{testVersion}");

        // Record source file contents before publish to verify source immutability
        string dirBuildPropsPath = Path.Combine(root, "Directory.Build.props");
        string rootVersionPath = Path.Combine(root, "VERSION");
        byte[] originalPropsBytes = File.ReadAllBytes(dirBuildPropsPath);
        byte[] originalVersionBytes = File.ReadAllBytes(rootVersionPath);

        try
        {
            var publishResult = RunScriptDirect(publishScript, $"-Version \"{testVersion}\" -SkipArchive", root, timeoutMs: 180000);
            Assert.AreEqual(0, publishResult.ExitCode, $"publish-release failed: {publishResult.Stdout} {publishResult.Stderr}");

            string publishDir = Path.Combine(releaseDir, "win-x64");
            string versionTxtPath = Path.Combine(publishDir, "VERSION.txt");
            string coreDllPath = Path.Combine(publishDir, "FootballTray.Core.dll");
            string exePath = Path.Combine(publishDir, "FootballTray.exe");

            Assert.IsTrue(File.Exists(versionTxtPath), "VERSION.txt must exist.");
            Assert.IsTrue(File.Exists(coreDllPath), "FootballTray.Core.dll must exist.");
            Assert.IsTrue(File.Exists(exePath), "FootballTray.exe must exist.");

            // 1. VERSION.txt target
            string versionTxt = File.ReadAllText(versionTxtPath);
            Assert.IsTrue(versionTxt.Contains($"FootballTray v{testVersion}"), $"VERSION.txt content unexpected: {versionTxt}");

            // 2. FootballTray.Core.dll metadata
            var coreVi = System.Diagnostics.FileVersionInfo.GetVersionInfo(coreDllPath);
            Assert.AreEqual(testVersion, NormalizeProductVersion(coreVi.ProductVersion), "FootballTray.Core.dll ProductVersion must match test version.");
            Assert.AreEqual($"{testVersion}.0", coreVi.FileVersion, "FootballTray.Core.dll FileVersion must be 4-part numeric.");

            var coreAssemblyName = System.Reflection.AssemblyName.GetAssemblyName(coreDllPath);
            Assert.AreEqual($"{testVersion}.0", coreAssemblyName.Version?.ToString(), "FootballTray.Core.dll AssemblyVersion must match numeric 4-part.");

            // 3. FootballTray.exe metadata
            var exeVi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            Assert.AreEqual(testVersion, NormalizeProductVersion(exeVi.ProductVersion), "FootballTray.exe ProductVersion must match test version.");
            Assert.AreEqual($"{testVersion}.0", exeVi.FileVersion, "FootballTray.exe FileVersion must be 4-part numeric.");

            // 4. Strict validate-release accepts the internally consistent release
            var validateResult = RunScriptDirect(validateScript, $"-Version \"{testVersion}\" -ReleaseDir \"{releaseDir}\" -RequireAutoUpdateInstaller", root);
            Assert.AreEqual(0, validateResult.ExitCode, $"validate-release failed on consistent release: {validateResult.Stdout} {validateResult.Stderr}");
            Assert.IsTrue(validateResult.Stdout.Contains("VALIDATION PASSED"));

            // 5. Source files remain 100% byte-for-byte unchanged
            CollectionAssert.AreEqual(originalPropsBytes, File.ReadAllBytes(dirBuildPropsPath), "Directory.Build.props must not be modified.");
            CollectionAssert.AreEqual(originalVersionBytes, File.ReadAllBytes(rootVersionPath), "Root VERSION file must not be modified.");
        }
        finally
        {
            if (Directory.Exists(releaseDir))
            {
                try { Directory.Delete(releaseDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test23_ValidateRelease_MismatchedBinaryVersion_FailsClosed()
    {
        string root = FindRepoRoot();
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"validate_mismatch_{Guid.NewGuid():N}");
        string winX64 = Path.Combine(tempDir, "win-x64");
        Directory.CreateDirectory(winX64);

        try
        {
            string histDir = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64");
            File.Copy(Path.Combine(histDir, "FootballTray.exe"), Path.Combine(winX64, "FootballTray.exe"));
            foreach (var req in new[] { "FootballTray.dll", "FootballTray.Core.dll", "Microsoft.WinUI.dll", "Microsoft.WindowsAppRuntime.Bootstrap.dll", "Microsoft.Windows.AppNotifications.Projection.dll" })
            {
                string src = Path.Combine(histDir, req);
                if (File.Exists(src)) { File.Copy(src, Path.Combine(winX64, req)); }
            }

            File.WriteAllText(Path.Combine(tempDir, "VERSION.txt"), "FootballTray v9.8.7\nRuntime: net10.0");
            File.WriteAllText(Path.Combine(winX64, "VERSION.txt"), "FootballTray v9.8.7\nRuntime: net10.0");

            var result = RunScriptDirect(validateScript, $"-Version \"9.8.7\" -ReleaseDir \"{tempDir}\"", root);
            Assert.AreNotEqual(0, result.ExitCode, "validate-release must fail closed when binary metadata does not match requested version.");
            Assert.IsTrue(result.Stdout.Contains("FootballTray.exe ProductVersion") || result.Stderr.Contains("FootballTray.exe ProductVersion"),
                $"Expected ProductVersion mismatch error. Stdout: {result.Stdout} Stderr: {result.Stderr}");
            Assert.IsTrue(result.Stdout.Contains("FootballTray.Core.dll ProductVersion") || result.Stderr.Contains("FootballTray.Core.dll ProductVersion"),
                $"Expected Core ProductVersion mismatch error. Stdout: {result.Stdout} Stderr: {result.Stderr}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test24_InstallerScript_MismatchedVersionTxtAndBinary_FailsClosed()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        string tempDir = Path.Combine(Path.GetTempPath(), $"installer_mismatch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Copy real binary (1.0.0) and required dependencies from historical release
            string histDir = Path.Combine(root, "artifacts", "releases", "v1.0.0", "win-x64");
            string realExe = Path.Combine(histDir, "FootballTray.exe");
            Assert.IsTrue(File.Exists(realExe), "Real FootballTray.exe must exist in historical release.");
            File.Copy(realExe, Path.Combine(tempDir, "FootballTray.exe"));

            foreach (var req in new[] { "FootballTray.dll", "FootballTray.Core.dll", "Microsoft.WinUI.dll" })
            {
                string src = Path.Combine(histDir, req);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(tempDir, req));
                }
            }

            // Write mismatched VERSION.txt (9.8.7)
            File.WriteAllText(Path.Combine(tempDir, "VERSION.txt"), "FootballTray v9.8.7\nRuntime: net10.0");

            var result = RunScriptDirect(installerScript, $"-SourceDir \"{tempDir}\" -WhatIf", root);
            Assert.AreNotEqual(0, result.ExitCode, "install-client must fail when VERSION.txt and binary version disagree.");
            Assert.IsTrue(result.Stdout.Contains("Payload version mismatch") || result.Stderr.Contains("Payload version mismatch"),
                $"Expected mismatch error message. Stdout: {result.Stdout} Stderr: {result.Stderr}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test25_PublishRelease_BuildMetadata_FailsClosedBeforePublish()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        const string testVersion = "1.2.3+build.4";
        string releaseDir = Path.Combine(root, "artifacts", "releases", $"v{testVersion}");

        try
        {
            var result = RunScriptDirect(publishScript, $"-Version \"{testVersion}\" -SkipArchive", root);
            Assert.AreNotEqual(0, result.ExitCode, "publish-release must fail closed when build metadata is provided.");
            Assert.IsTrue(
                result.Stdout.Contains("Release build metadata ('+...') is not supported by the FootballTray publisher.") ||
                result.Stderr.Contains("Release build metadata ('+...') is not supported by the FootballTray publisher."),
                $"Expected build metadata rejection error. Stdout: {result.Stdout} Stderr: {result.Stderr}");

            // Verify dotnet publish was not completed for that release
            Assert.IsFalse(Directory.Exists(releaseDir), "Release directory should not exist when build metadata is rejected.");

            // Verify canonical v1.0.0 artifact was not touched
            string v1Dir = Path.Combine(root, "artifacts", "releases", "v1.0.0");
            Assert.IsTrue(Directory.Exists(v1Dir), "Historical v1.0.0 directory must remain intact.");
            string v1Zip = Path.Combine(v1Dir, "FootballTray-v1.0.0-win-x64.zip");
            Assert.IsTrue(File.Exists(v1Zip), "Historical v1.0.0 ZIP must remain intact.");
        }
        finally
        {
            if (Directory.Exists(releaseDir))
            {
                try { Directory.Delete(releaseDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test26_PublishScript_ResolvesVersionSpecificReleaseNotes_WithManifestParity()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        string notesFileV101 = Path.Combine(root, "release-notes", "v1.0.1.txt");

        // 1. Source existence and content verification
        Assert.IsTrue(File.Exists(notesFileV101), "release-notes/v1.0.1.txt must exist in repository source.");
        string sourceNotes = File.ReadAllText(notesFileV101);
        Assert.IsTrue(sourceNotes.Contains("Maintenance Release — Automatic Update Foundation"),
            "Source notes must identify maintenance release.");
        Assert.IsTrue(sourceNotes.Contains("Changes since v1.0.0:"),
            "Source notes must specify changes section.");
        Assert.IsFalse(sourceNotes.Contains("Milestone 8"),
            "Source notes must NOT contain Milestone 8.");

        // 2. Publisher script contract verification
        string publishScriptContent = File.ReadAllText(publishScript);
        Assert.IsTrue(publishScriptContent.Contains("release-notes"),
            "publish-release.ps1 must reference release-notes directory.");
        Assert.IsTrue(publishScriptContent.Contains("v$Version.txt"),
            "publish-release.ps1 must look up v$Version.txt convention.");
        Assert.IsFalse(publishScriptContent.Contains("Milestone 8"),
            "publish-release.ps1 must NOT contain Milestone 8.");

        // 3. Isolated release publish simulation for v1.0.1
        using var fixture = new IsolatedReleaseFixture(root);
        const string version = "1.0.1";
        const string downloadUrl = "https://example.invalid/releases/v1.0.1/FootballTray-v1.0.1-win-x64.zip";

        var result = RunPowerShell($@"
            $RootDir = '{root.Replace("'", "''")}'
            $Version = '{version}'
            $ReleaseBaseDir = '{fixture.FixturePath.Replace("'", "''")}'
            $PublishDir = Join-Path $ReleaseBaseDir 'win-x64'
            $versionTxtPath = Join-Path $PublishDir 'VERSION.txt'
            $releaseNotesPath = Join-Path $PublishDir 'RELEASE_NOTES.txt'

            $versionNotesFile = Join-Path $RootDir 'release-notes' ""v$Version.txt""
            if (Test-Path -LiteralPath $versionNotesFile) {{
                $releaseNotesContent = (Get-Content -LiteralPath $versionNotesFile -Raw).TrimEnd()
                Write-Host ""  Loaded version-specific release notes from: release-notes/v$Version.txt""
            }} else {{
                Write-Host ""  No version-specific release notes found at: release-notes/v$Version.txt — using generic template.""
                $releaseNotesContent = @""
FootballTray v$Version
============================================================
Release — Native Windows Taskbar Football Tracker

Requirements:
- Windows 11 64-bit (or Windows 10 build 17763+)
- x64 processor
- FootballTray Backend Service (Local or Network-hosted)
""@
            }}

            Set-Content -Path $releaseNotesPath -Value $releaseNotesContent
            Copy-Item -Path $releaseNotesPath -Destination (Join-Path $ReleaseBaseDir 'RELEASE_NOTES.txt') -Force

            . '{publishScript.Replace("'", "''")}'; New-ReleaseManifest `
                -Version $Version `
                -DownloadUrl '{downloadUrl}' `
                -ArchiveFile '{fixture.ArchivePath.Replace("'", "''")}' `
                -ReleaseNotesFile $releaseNotesPath `
                -ManifestFile '{fixture.ManifestPath.Replace("'", "''")}' `
                -Architecture 'win-x64'
        ", root);

        Assert.AreEqual(0, result.ExitCode, $"Isolated publish failed: {result.Stdout} {result.Stderr}");

        // A. Verify generated RELEASE_NOTES.txt
        string generatedNotesPath = Path.Combine(fixture.FixturePath, "RELEASE_NOTES.txt");
        Assert.IsTrue(File.Exists(generatedNotesPath), "Generated RELEASE_NOTES.txt must exist in isolated release.");
        string generatedNotes = File.ReadAllText(generatedNotesPath);

        Assert.IsTrue(generatedNotes.Contains("Maintenance Release — Automatic Update Foundation"),
            "Generated RELEASE_NOTES.txt must contain maintenance release title.");
        Assert.IsTrue(generatedNotes.Contains("Changes since v1.0.0:"),
            "Generated RELEASE_NOTES.txt must contain changes section.");
        Assert.IsFalse(generatedNotes.Contains("Milestone 8 Release"),
            "Generated RELEASE_NOTES.txt must NOT contain 'Milestone 8 Release'.");
        Assert.IsFalse(generatedNotes.Contains("Milestone 8"),
            "Generated RELEASE_NOTES.txt must NOT contain 'Milestone 8'.");

        // B. Verify manifest consistency
        Assert.IsTrue(File.Exists(fixture.ManifestPath), "release-manifest.json must exist in isolated release.");
        string manifestJson = File.ReadAllText(fixture.ManifestPath);
        var parseResult = ReleaseManifestParser.ParseAndValidate(manifestJson);
        Assert.IsTrue(parseResult.IsValid, $"Manifest domain parsing failed: {parseResult.ErrorMessage}");

        var manifest = parseResult.ValidatedManifest!;
        Assert.IsNotNull(manifest.ReleaseNotes, "manifest.releaseNotes must not be null.");
        Assert.AreEqual(generatedNotes.Trim(), manifest.ReleaseNotes.Trim(),
            "manifest.releaseNotes must match generated RELEASE_NOTES.txt content identically.");

        // D. Historical safety: canonical v1.0.0 artifacts must remain untouched
        string hist100Zip = Path.Combine(root, "artifacts", "releases", "v1.0.0", "FootballTray-v1.0.0-win-x64.zip");
        Assert.IsTrue(File.Exists(hist100Zip), "Historical v1.0.0 ZIP must exist.");
    }

    [TestMethod]
    public void Test27_PublishScript_GenericFallbackReleaseNotes_WhenDedicatedNotesAbsent()
    {
        string root = FindRepoRoot();
        string publishScript = Path.Combine(root, "scripts", "publish-release.ps1");
        string fallbackVersion = $"0.0.0-fallback.{Guid.NewGuid():N}";
        string dedicatedNotesFile = Path.Combine(root, "release-notes", $"v{fallbackVersion}.txt");

        // Verify no dedicated notes file exists for this safe test version
        Assert.IsFalse(File.Exists(dedicatedNotesFile), "No dedicated notes file must exist for fallback test version.");

        using var fixture = new IsolatedReleaseFixture(root);
        string downloadUrl = $"https://example.invalid/releases/v{fallbackVersion}/FootballTray-v{fallbackVersion}-win-x64.zip";

        var result = RunPowerShell($@"
            $RootDir = '{root.Replace("'", "''")}'
            $Version = '{fallbackVersion}'
            $ReleaseBaseDir = '{fixture.FixturePath.Replace("'", "''")}'
            $PublishDir = Join-Path $ReleaseBaseDir 'win-x64'
            $versionTxtPath = Join-Path $PublishDir 'VERSION.txt'
            $releaseNotesPath = Join-Path $PublishDir 'RELEASE_NOTES.txt'

            $versionNotesFile = Join-Path $RootDir 'release-notes' ""v$Version.txt""
            if (Test-Path -LiteralPath $versionNotesFile) {{
                $releaseNotesContent = (Get-Content -LiteralPath $versionNotesFile -Raw).TrimEnd()
                Write-Host ""  Loaded version-specific release notes from: release-notes/v$Version.txt""
            }} else {{
                Write-Host ""  No version-specific release notes found at: release-notes/v$Version.txt — using generic template.""
                $releaseNotesContent = @""
FootballTray v$Version
============================================================
Release — Native Windows Taskbar Football Tracker

Requirements:
- Windows 11 64-bit (or Windows 10 build 17763+)
- x64 processor
- FootballTray Backend Service (Local or Network-hosted)
""@
            }}

            Set-Content -Path $releaseNotesPath -Value $releaseNotesContent
            Copy-Item -Path $releaseNotesPath -Destination (Join-Path $ReleaseBaseDir 'RELEASE_NOTES.txt') -Force

            . '{publishScript.Replace("'", "''")}'; New-ReleaseManifest `
                -Version $Version `
                -DownloadUrl '{downloadUrl}' `
                -ArchiveFile '{fixture.ArchivePath.Replace("'", "''")}' `
                -ReleaseNotesFile $releaseNotesPath `
                -ManifestFile '{fixture.ManifestPath.Replace("'", "''")}' `
                -Architecture 'win-x64'
        ", root);

        Assert.AreEqual(0, result.ExitCode, $"Isolated fallback publish failed: {result.Stdout} {result.Stderr}");

        // C. Verify generic fallback content
        string generatedNotesPath = Path.Combine(fixture.FixturePath, "RELEASE_NOTES.txt");
        Assert.IsTrue(File.Exists(generatedNotesPath), "Generated RELEASE_NOTES.txt must exist in fallback release.");
        string generatedNotes = File.ReadAllText(generatedNotesPath);

        Assert.IsTrue(generatedNotes.Contains($"FootballTray v{fallbackVersion}"),
            "Fallback notes must contain the dynamic target version.");
        Assert.IsTrue(generatedNotes.Contains("Release — Native Windows Taskbar Football Tracker"),
            "Fallback notes must contain generic release header.");
        Assert.IsFalse(generatedNotes.Contains("Milestone 8"),
            "Fallback notes must NOT contain 'Milestone 8'.");
        Assert.IsFalse(generatedNotes.Contains("Changes since v1.0.0"),
            "Fallback notes must NOT contain 'Changes since v1.0.0'.");

        // Manifest consistency for fallback
        string manifestJson = File.ReadAllText(fixture.ManifestPath);
        var parseResult = ReleaseManifestParser.ParseAndValidate(manifestJson);
        Assert.IsTrue(parseResult.IsValid, $"Fallback manifest domain parsing failed: {parseResult.ErrorMessage}");
        Assert.AreEqual(generatedNotes.Trim(), parseResult.ValidatedManifest!.ReleaseNotes?.Trim(),
            "manifest.releaseNotes must match fallback RELEASE_NOTES.txt.");

        // Verify no permanent source release-notes file was created
        Assert.IsFalse(File.Exists(dedicatedNotesFile), "No source release-notes file must be created for fallback version.");

        // Historical safety: canonical v1.0.0 remains intact
        string hist100Zip = Path.Combine(root, "artifacts", "releases", "v1.0.0", "FootballTray-v1.0.0-win-x64.zip");
        Assert.IsTrue(File.Exists(hist100Zip), "Historical v1.0.0 ZIP must exist.");
    }

    [TestMethod]
    public void Test28_InstallerScript_IsStrictlyAsciiSafe()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        Assert.IsTrue(File.Exists(installerScript), "scripts/install-client.ps1 must exist.");

        byte[] bytes = File.ReadAllBytes(installerScript);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            Assert.IsTrue(b <= 0x7F,
                $"Non-ASCII byte 0x{b:X2} found at offset {i} in '{installerScript}'. " +
                "The trusted Windows PowerShell installer script must remain strictly ASCII-safe (all bytes <= 0x7F) " +
                "because it is executed by Windows PowerShell 5.1 without a BOM under system ANSI code pages.");
        }
    }

    [TestMethod]
    public void Test29_InstallerScript_ParsesCleanlyUnderWindowsPowerShell51()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        Assert.IsTrue(File.Exists(installerScript), "scripts/install-client.ps1 must exist.");

        var (errorCount, details) = ParseWithWindowsPowerShell51(installerScript);
        Assert.AreEqual(0, errorCount,
            $"scripts/install-client.ps1 failed parsing under Windows PowerShell 5.1 with {errorCount} error(s):\n{details}");
    }

    [TestMethod]
    public void Test30_PublishedInstallerPayload_ByteIdenticalAndParsesUnderWindowsPowerShell51()
    {
        string root = FindRepoRoot();
        string installerSource = Path.Combine(root, "scripts", "install-client.ps1");
        Assert.IsTrue(File.Exists(installerSource), "scripts/install-client.ps1 must exist.");

        string tempPublishDir = Path.Combine(Path.GetTempPath(), $"ft_test_pub_{Guid.NewGuid():N}");
        try
        {
            string csproj = Path.Combine(root, "src", "client", "FootballTray.Client", "FootballTray.Client.csproj");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish \"{csproj}\" -c Release -r win-x64 --self-contained false -o \"{tempPublishDir}\"",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(90000);
            Assert.AreEqual(0, proc.ExitCode, $"dotnet publish failed with exit code {proc.ExitCode}: {proc.StandardError.ReadToEnd()}");

            string publishedInstaller = Path.Combine(tempPublishDir, "install-client.ps1");
            Assert.IsTrue(File.Exists(publishedInstaller), "Published payload must contain install-client.ps1.");

            // Byte-identical check
            byte[] sourceBytes = File.ReadAllBytes(installerSource);
            byte[] publishedBytes = File.ReadAllBytes(publishedInstaller);
            CollectionAssert.AreEqual(sourceBytes, publishedBytes,
                "Published install-client.ps1 must be byte-identical to repository scripts/install-client.ps1.");

            // ASCII-safe check
            for (int i = 0; i < publishedBytes.Length; i++)
            {
                Assert.IsTrue(publishedBytes[i] <= 0x7F,
                    $"Published install-client.ps1 contains non-ASCII byte 0x{publishedBytes[i]:X2} at offset {i}.");
            }

            // Windows PowerShell 5.1 AST parser check
            var (errorCount, details) = ParseWithWindowsPowerShell51(publishedInstaller);
            Assert.AreEqual(0, errorCount,
                $"Published install-client.ps1 failed parsing under Windows PowerShell 5.1 with {errorCount} error(s):\n{details}");

            // Published uninstaller validation
            string uninstallerSource = Path.Combine(root, "scripts", "uninstall-client.ps1");
            Assert.IsTrue(File.Exists(uninstallerSource), "scripts/uninstall-client.ps1 must exist.");

            string publishedUninstaller = Path.Combine(tempPublishDir, "uninstall-client.ps1");
            Assert.IsTrue(File.Exists(publishedUninstaller), "Published payload must contain uninstall-client.ps1.");

            // Byte-identical check
            byte[] uninstallerSourceBytes = File.ReadAllBytes(uninstallerSource);
            byte[] publishedUninstallerBytes = File.ReadAllBytes(publishedUninstaller);
            CollectionAssert.AreEqual(uninstallerSourceBytes, publishedUninstallerBytes,
                "Published uninstall-client.ps1 must be byte-identical to repository scripts/uninstall-client.ps1.");

            // ASCII-safe check
            for (int i = 0; i < publishedUninstallerBytes.Length; i++)
            {
                Assert.IsTrue(publishedUninstallerBytes[i] <= 0x7F,
                    $"Published uninstall-client.ps1 contains non-ASCII byte 0x{publishedUninstallerBytes[i]:X2} at offset {i}.");
            }

            // Windows PowerShell 5.1 AST parser check
            var (uninstallerPs51Errors, uninstallerPs51Details) = ParseWithWindowsPowerShell51(publishedUninstaller);
            Assert.AreEqual(0, uninstallerPs51Errors,
                $"Published uninstall-client.ps1 failed parsing under Windows PowerShell 5.1 with {uninstallerPs51Errors} error(s):\n{uninstallerPs51Details}");

            // PowerShell 7 AST parser check
            var (uninstallerPs7Errors, uninstallerPs7Details) = ParseWithPowerShell7(publishedUninstaller);
            Assert.AreEqual(0, uninstallerPs7Errors,
                $"Published uninstall-client.ps1 failed parsing under PowerShell 7 with {uninstallerPs7Errors} error(s):\n{uninstallerPs7Details}");
        }
        finally
        {
            if (Directory.Exists(tempPublishDir))
            {
                try { Directory.Delete(tempPublishDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test31_InstallerScript_DoesNotUseDisallowedGetRelativePath()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        Assert.IsTrue(File.Exists(installerScript), "scripts/install-client.ps1 must exist.");

        string text = File.ReadAllText(installerScript);
        Assert.IsFalse(text.Contains("[System.IO.Path]::GetRelativePath", StringComparison.OrdinalIgnoreCase),
            "scripts/install-client.ps1 must NOT contain '[System.IO.Path]::GetRelativePath' because it is not supported in Windows PowerShell 5.1 (.NET Framework 4.8).");
    }

    [TestMethod]
    public void Test32_InstallerScript_GetRelativeChildPath_ExecutesCorrectlyUnderWindowsPowerShell51()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        Assert.IsTrue(File.Exists(installerScript), "scripts/install-client.ps1 must exist.");

        string functionDef = ExtractFunctionFromScript(installerScript, "Get-RelativeChildPath");
        Assert.IsFalse(string.IsNullOrWhiteSpace(functionDef), "Extracted function definition must not be empty.");
        Assert.IsFalse(functionDef.Contains("[System.IO.Path]::GetRelativePath", StringComparison.OrdinalIgnoreCase),
            "Extracted production helper must not contain GetRelativePath.");

        string tempScript = Path.Combine(Path.GetTempPath(), $"ft_test_relpath_{Guid.NewGuid():N}.ps1");
        try
        {
            string testHarness = $@"
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

{functionDef}

function Assert-Equal([string]$actual, [string]$expected, [string]$label) {{
    if ($actual -ne $expected) {{
        throw ""Assertion failed for $label. Expected: '$expected', Actual: '$actual'""
    }}
}}

function Assert-Throws([scriptblock]$action, [string]$label) {{
    $threw = $false
    try {{
        & $action
    }} catch {{
        $threw = $true
    }}
    if (-not $threw) {{
        throw ""Expected exception was not thrown for $label.""
    }}
}}

# PASS 1: Base without slash
Assert-Equal (Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'C:\Base\FootballTray.exe') 'FootballTray.exe' 'Base without slash'

# PASS 2: Base with slash, case-insensitive full path
Assert-Equal (Get-RelativeChildPath -BasePath 'C:\Base\' -FullPath 'c:\base\runtimes\win-x64\native\foo.dll') ('runtimes' + [System.IO.Path]::DirectorySeparatorChar + 'win-x64' + [System.IO.Path]::DirectorySeparatorChar + 'native' + [System.IO.Path]::DirectorySeparatorChar + 'foo.dll') 'Case-insensitive child'

# PASS 3: Deeply nested normal child path
Assert-Equal (Get-RelativeChildPath -BasePath 'C:\Base\Root' -FullPath 'C:\Base\Root\a\b\c\d\bar.dll') ('a' + [System.IO.Path]::DirectorySeparatorChar + 'b' + [System.IO.Path]::DirectorySeparatorChar + 'c' + [System.IO.Path]::DirectorySeparatorChar + 'd' + [System.IO.Path]::DirectorySeparatorChar + 'bar.dll') 'Deeply nested child'

# THROW 1: Sibling prefix
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'C:\Base_Extra\foo.dll' }} 'Sibling prefix'

# THROW 2: Traversal escaping base
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'C:\Base\..\outside.dll' }} 'Traversal outside base'

# THROW 3: Foreign drive
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'D:\Base\foo.dll' }} 'Foreign drive'

# THROW 4: Equal to base (empty relative result)
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'C:\Base' }} 'Exact base equal'

# THROW 5: Equal to base with trailing slash
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'C:\Base\' }} 'Exact base equal with slash'

# THROW 6: Parent of base
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base\Sub' -FullPath 'C:\Base' }} 'Parent of base'

# THROW 7: Relative BasePath
Assert-Throws {{ Get-RelativeChildPath -BasePath 'Base' -FullPath 'C:\Base\foo.dll' }} 'Relative BasePath'

# THROW 8: Relative FullPath
Assert-Throws {{ Get-RelativeChildPath -BasePath 'C:\Base' -FullPath 'Base\foo.dll' }} 'Relative FullPath'

# THROW 9: Both relative paths
Assert-Throws {{ Get-RelativeChildPath -BasePath 'Base' -FullPath 'Base\foo.dll' }} 'Both relative paths'

Write-Host 'RELPATH_MATRIX_PASSED'
";
            File.WriteAllText(tempScript, testHarness, System.Text.Encoding.ASCII);

            var (exitCode, stdout, stderr) = RunWithWindowsPowerShell51(tempScript, timeoutMs: 30000);
            Assert.AreEqual(0, exitCode, $"Get-RelativeChildPath test script failed under Windows PowerShell 5.1 with exit code {exitCode}.\nStdout: {stdout}\nStderr: {stderr}");
            Assert.IsTrue(stdout.Contains("RELPATH_MATRIX_PASSED"), $"Missing success marker in Windows PowerShell 5.1 output.\nStdout: {stdout}\nStderr: {stderr}");
        }
        finally
        {
            if (File.Exists(tempScript))
            {
                try { File.Delete(tempScript); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test33_UninstallerScript_IsStrictlyAsciiSafeAndParsesUnderPS51AndPS7()
    {
        string root = FindRepoRoot();
        string uninstallerScript = Path.Combine(root, "scripts", "uninstall-client.ps1");
        Assert.IsTrue(File.Exists(uninstallerScript), "scripts/uninstall-client.ps1 must exist.");

        // Strict ASCII check: all raw bytes <= 0x7F
        byte[] bytes = File.ReadAllBytes(uninstallerScript);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            Assert.IsTrue(b <= 0x7F,
                $"Non-ASCII byte 0x{b:X2} found at offset {i} in '{uninstallerScript}'. " +
                "The uninstall-client.ps1 script must remain strictly ASCII-safe (all bytes <= 0x7F) " +
                "because it may be executed by Windows PowerShell 5.1 without a BOM under system ANSI code pages.");
        }

        // Windows PowerShell 5.1 AST parser check
        var (ps51Errors, ps51Details) = ParseWithWindowsPowerShell51(uninstallerScript);
        Assert.AreEqual(0, ps51Errors,
            $"scripts/uninstall-client.ps1 failed parsing under Windows PowerShell 5.1 with {ps51Errors} error(s):\n{ps51Details}");

        // PowerShell 7 AST parser check
        var (ps7Errors, ps7Details) = ParseWithPowerShell7(uninstallerScript);
        Assert.AreEqual(0, ps7Errors,
            $"scripts/uninstall-client.ps1 failed parsing under PowerShell 7 with {ps7Errors} error(s):\n{ps7Details}");
    }

    [TestMethod]
    public void Test34_ValidateReleaseScript_RequiresUninstallClientScriptWhenAutoUpdateEnabled()
    {
        string root = FindRepoRoot();
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root, makeA2Compliant: true);

        string fixturePublish = Path.Combine(fixture.FixturePath, "win-x64");
        string uninstallerInPublish = Path.Combine(fixturePublish, "uninstall-client.ps1");
        string installerInPublish = Path.Combine(fixturePublish, "install-client.ps1");

        // Ensure install-client.ps1 exists in the fixture publish directory
        if (!File.Exists(installerInPublish))
        {
            File.Copy(Path.Combine(root, "scripts", "install-client.ps1"), installerInPublish);
        }

        // Case A: uninstall-client.ps1 is absent -> validation MUST fail when -RequireAutoUpdateInstaller is specified
        if (File.Exists(uninstallerInPublish))
        {
            File.Delete(uninstallerInPublish);
        }

        var resultMissing = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\" -RequireAutoUpdateInstaller", root);
        Assert.AreNotEqual(0, resultMissing.ExitCode,
            $"validate-release must fail when uninstall-client.ps1 is absent under -RequireAutoUpdateInstaller. Output: {resultMissing.Stdout} {resultMissing.Stderr}");
        Assert.IsTrue(resultMissing.Stdout.Contains("Missing required release component: uninstall-client.ps1") ||
                      resultMissing.Stderr.Contains("Missing required release component: uninstall-client.ps1"),
            $"Expected missing uninstall-client.ps1 error message. Output: {resultMissing.Stdout} {resultMissing.Stderr}");

        // Case B: uninstall-client.ps1 contains non-ASCII -> validation MUST fail
        File.WriteAllBytes(uninstallerInPublish, new byte[] { (byte)'#', (byte)' ', 0xE2, 0x80, 0x94, (byte)'\n' });
        var resultNonAscii = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\" -RequireAutoUpdateInstaller", root);
        Assert.AreNotEqual(0, resultNonAscii.ExitCode,
            $"validate-release must fail when uninstall-client.ps1 contains non-ASCII bytes. Output: {resultNonAscii.Stdout} {resultNonAscii.Stderr}");
        Assert.IsTrue(resultNonAscii.Stdout.Contains("non-ASCII characters") || resultNonAscii.Stderr.Contains("non-ASCII characters"),
            $"Expected non-ASCII error message. Output: {resultNonAscii.Stdout} {resultNonAscii.Stderr}");

        // Case C: Valid uninstall-client.ps1 copied from repository -> succeeds for lifecycle scripts
        File.Copy(Path.Combine(root, "scripts", "uninstall-client.ps1"), uninstallerInPublish, overwrite: true);
        var resultValid = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\" -RequireAutoUpdateInstaller", root);
        Assert.AreEqual(0, resultValid.ExitCode,
            $"validate-release failed with valid lifecycle scripts: {resultValid.Stdout} {resultValid.Stderr}");
        Assert.IsTrue(resultValid.Stdout.Contains("uninstall-client.ps1 verified strictly ASCII"),
            $"Expected strictly ASCII verification output. Stdout: {resultValid.Stdout}");
        Assert.IsTrue(resultValid.Stdout.Contains("uninstall-client.ps1 verified Windows PowerShell 5.1 parser clean"),
            $"Expected PS5.1 parser clean output. Stdout: {resultValid.Stdout}");
    }

    [TestMethod]
    public void Test35_InstallerScript_ResolveCompanionUninstaller_ResolvesFromSourceNotHandoffUnderWindowsPowerShell51()
    {
        string root = FindRepoRoot();
        string installerScript = Path.Combine(root, "scripts", "install-client.ps1");
        Assert.IsTrue(File.Exists(installerScript), "scripts/install-client.ps1 must exist.");

        // Structurally extract Resolve-CompanionUninstaller via PowerShell FunctionDefinitionAst
        string functionDef = ExtractFunctionFromScript(installerScript, "Resolve-CompanionUninstaller");
        Assert.IsFalse(string.IsNullOrWhiteSpace(functionDef), "Extracted function definition must not be empty.");

        string tempRoot = Path.Combine(Path.GetTempPath(), $"ft_test_handoff_companion_{Guid.NewGuid():N}");
        string handoffDir = Path.Combine(tempRoot, "handoff");
        string sourceDir = Path.Combine(tempRoot, "source");
        string emptySourceDir = Path.Combine(tempRoot, "empty_source");
        string dirWithFolderUninstaller = Path.Combine(tempRoot, "folder_source");
        string dirWithZeroByteUninstaller = Path.Combine(tempRoot, "zerobyte_source");

        Directory.CreateDirectory(handoffDir);
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(emptySourceDir);
        Directory.CreateDirectory(dirWithFolderUninstaller);
        Directory.CreateDirectory(dirWithZeroByteUninstaller);

        // Real layout: handoff contains ONLY install-client.ps1; NO uninstaller
        File.Copy(installerScript, Path.Combine(handoffDir, "install-client.ps1"));
        Assert.IsFalse(File.Exists(Path.Combine(handoffDir, "uninstall-client.ps1")),
            "CRITICAL: Handoff directory must NOT contain uninstall-client.ps1.");

        // Source payload contains verified release uninstaller
        string expectedSourceUninstaller = Path.Combine(sourceDir, "uninstall-client.ps1");
        File.WriteAllText(expectedSourceUninstaller, "# FootballTray companion uninstaller release payload\n");

        // Edge case fixtures
        Directory.CreateDirectory(Path.Combine(dirWithFolderUninstaller, "uninstall-client.ps1"));
        File.WriteAllBytes(Path.Combine(dirWithZeroByteUninstaller, "uninstall-client.ps1"), Array.Empty<byte>());

        string tempScript = Path.Combine(Path.GetTempPath(), $"ft_test_uninstaller_res_{Guid.NewGuid():N}.ps1");
        try
        {
            string testHarness = $@"
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

{functionDef}

function Assert-Equal([string]$actual, [string]$expected, [string]$label) {{
    if ($actual -ne $expected) {{
        throw ""Assertion failed for $label. Expected: '$expected', Actual: '$actual'""
    }}
}}

function Assert-Throws([scriptblock]$action, [string]$label) {{
    $threw = $false
    try {{
        & $action
    }} catch {{
        $threw = $true
    }}
    if (-not $threw) {{
        throw ""Expected exception was not thrown for $label.""
    }}
}}

# Emulate handoff environment: set location and script context to handoff directory
Set-Location -LiteralPath '{handoffDir.Replace("'", "''")}'
$PSScriptRoot = '{handoffDir.Replace("'", "''")}'

# PASS 1: Resolves strictly to source\uninstall-client.ps1 even when invoked from handoff
$resolved = Resolve-CompanionUninstaller -SourceDir '{sourceDir.Replace("'", "''")}'
Assert-Equal $resolved '{expectedSourceUninstaller.Replace("'", "''")}' 'Resolves strictly from SourceDir'

# Verify directory of resolved uninstaller is SourceDir, NOT handoff
$resolvedParent = [System.IO.Path]::GetDirectoryName($resolved).TrimEnd('\')
$expectedParent = '{sourceDir.Replace("'", "''")}'.TrimEnd('\')
Assert-Equal $resolvedParent $expectedParent 'Resolved uninstaller parent directory must match SourceDir'

# THROW 1: Hand-off directory as SourceDir throws because handoff contains NO uninstaller
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '{handoffDir.Replace("'", "''")}' }} 'Handoff directory lacks companion uninstaller'

# THROW 2: Empty SourceDir throws
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '' }} 'Empty SourceDir'

# THROW 3: Relative SourceDir throws (must be rooted)
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir 'source' }} 'Relative SourceDir'
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '.\source' }} 'Dot-relative SourceDir'

# THROW 4: Non-existent SourceDir throws
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '{Path.Combine(tempRoot, "nonexistent").Replace("'", "''")}' }} 'Non-existent SourceDir'

# THROW 5: SourceDir missing uninstall-client.ps1 throws
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '{emptySourceDir.Replace("'", "''")}' }} 'SourceDir missing uninstaller'

# THROW 6: uninstall-client.ps1 is a directory throws
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '{dirWithFolderUninstaller.Replace("'", "''")}' }} 'Uninstaller is directory'

# THROW 7: uninstall-client.ps1 is 0 bytes throws
Assert-Throws {{ Resolve-CompanionUninstaller -SourceDir '{dirWithZeroByteUninstaller.Replace("'", "''")}' }} 'Zero-byte uninstaller'

Write-Host 'RESOLVE_COMPANION_MATRIX_PASSED'
";
            File.WriteAllText(tempScript, testHarness, System.Text.Encoding.ASCII);

            var (exitCode, stdout, stderr) = RunWithWindowsPowerShell51(tempScript, timeoutMs: 30000);
            Assert.AreEqual(0, exitCode, $"Resolve-CompanionUninstaller test harness failed under Windows PowerShell 5.1 with exit code {exitCode}.\nStdout: {stdout}\nStderr: {stderr}");
            Assert.IsTrue(stdout.Contains("RESOLVE_COMPANION_MATRIX_PASSED"), $"Missing success marker in Windows PowerShell 5.1 output.\nStdout: {stdout}\nStderr: {stderr}");
        }
        finally
        {
            if (File.Exists(tempScript))
            {
                try { File.Delete(tempScript); } catch { }
            }
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }

    private static (int ErrorCount, string Details) ParseWithWindowsPowerShell51(string scriptPath)
    {
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string ps51Exe = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.IsTrue(File.Exists(ps51Exe), $"Windows PowerShell 5.1 executable must exist at '{ps51Exe}'.");

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ps51Exe,
            Arguments = $"-NoProfile -Command \"$errs = $null; $toks = $null; [System.Management.Automation.Language.Parser]::ParseFile('{scriptPath.Replace("'", "''")}', [ref]$toks, [ref]$errs) | Out-Null; Write-Host ('ERRORS=' + $errs.Count); foreach ($e in $errs) {{ Write-Host ('LINE=' + $e.Extent.StartLineNumber + ' COL=' + $e.Extent.StartColumnNumber + ' MSG=' + $e.Message) }}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!process.Start())
        {
            Assert.Fail($"Failed to start Windows PowerShell 5.1 process: '{ps51Exe}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30000))
        {
            string? killException = null;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                killException = ex.ToString();
            }

            bool terminated = false;
            string? postKillWaitException = null;
            try
            {
                terminated = process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                postKillWaitException = ex.ToString();
            }

            if (!terminated)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Windows PowerShell 5.1 parser process timed out after 30000ms for script: '{scriptPath}'.");
                sb.AppendLine("Process termination could NOT be confirmed within 5000ms post-kill wait.");
                if (killException != null)
                {
                    sb.AppendLine($"process.Kill threw: {killException}");
                }
                else
                {
                    sb.AppendLine("process.Kill was called without throwing, but process did not exit.");
                }
                if (postKillWaitException != null)
                {
                    sb.AppendLine($"process.WaitForExit(5000) threw: {postKillWaitException}");
                }
                Assert.Fail(sb.ToString());
            }

            string? outputDrainException = null;
            try
            {
                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromMilliseconds(2000));
            }
            catch (Exception ex)
            {
                outputDrainException = ex.ToString();
            }

            var timeoutMsg = new System.Text.StringBuilder();
            timeoutMsg.AppendLine($"Windows PowerShell 5.1 parser process timed out after 30000ms for script: '{scriptPath}' and was terminated.");
            if (killException != null)
            {
                timeoutMsg.AppendLine($"Note: process.Kill threw an exception: {killException}");
            }
            if (outputDrainException != null)
            {
                timeoutMsg.AppendLine($"Note: output task drain threw an exception: {outputDrainException}");
            }
            Assert.Fail(timeoutMsg.ToString());
        }

        process.WaitForExit();

        string stdout;
        string stderr;
        try
        {
            Task.WaitAll(stdoutTask, stderrTask);
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Failed while awaiting Windows PowerShell 5.1 process output tasks: {ex.Message}");
            return (0, string.Empty);
        }

        Assert.AreEqual(0, process.ExitCode,
            $"Windows PowerShell 5.1 exited with nonzero code {process.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}");

        bool markerFound = false;
        int errorCount = 0;
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("ERRORS="))
            {
                markerFound = true;
                Assert.IsTrue(int.TryParse(line.Substring("ERRORS=".Length), out errorCount),
                    $"Failed to parse error count from marker line: '{line}'.");
                break;
            }
        }

        Assert.IsTrue(markerFound,
            $"Windows PowerShell 5.1 output did not contain expected 'ERRORS=' marker line.\nStdout: {stdout}\nStderr: {stderr}");

        string details = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\nStderr: {stderr}";
        return (errorCount, details);
    }

    private static (int ErrorCount, string Details) ParseWithPowerShell7(string scriptPath)
    {
        string root = FindRepoRoot();
        string command = $"$errs = $null; $toks = $null; [System.Management.Automation.Language.Parser]::ParseFile('{scriptPath.Replace("'", "''")}', [ref]$toks, [ref]$errs) | Out-Null; Write-Host ('ERRORS=' + $errs.Count); foreach ($e in $errs) {{ Write-Host ('LINE=' + $e.Extent.StartLineNumber + ' COL=' + $e.Extent.StartColumnNumber + ' MSG=' + $e.Message) }}";
        var result = RunPowerShell(command, root);
        Assert.AreEqual(0, result.ExitCode, $"PowerShell 7 parser execution failed with code {result.ExitCode}.\nStdout: {result.Stdout}\nStderr: {result.Stderr}");

        bool markerFound = false;
        int errorCount = 0;
        foreach (var line in result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("ERRORS="))
            {
                markerFound = true;
                Assert.IsTrue(int.TryParse(line.Substring("ERRORS=".Length), out errorCount),
                    $"Failed to parse error count from marker line: '{line}'.");
                break;
            }
        }

        Assert.IsTrue(markerFound,
            $"PowerShell 7 output did not contain expected 'ERRORS=' marker line.\nStdout: {result.Stdout}\nStderr: {result.Stderr}");

        string details = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : $"{result.Stdout}\nStderr: {result.Stderr}";
        return (errorCount, details);
    }

    private static string ExtractFunctionFromScript(string scriptPath, string functionName)
    {
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string ps51Exe = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.IsTrue(File.Exists(ps51Exe), $"Windows PowerShell 5.1 executable must exist at '{ps51Exe}'.");

        string escapedScriptPath = scriptPath.Replace("'", "''");
        string escapedFnName = functionName.Replace("'", "''");

        string astExtractCommand = $"$toks = $null; $errs = $null; $ast = [System.Management.Automation.Language.Parser]::ParseFile('{escapedScriptPath}', [ref]$toks, [ref]$errs); if ($errs.Count -gt 0) {{ Write-Error 'Parser failed on target script'; exit 1 }}; $fn = $ast.Find({{ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq '{escapedFnName}' }}, $true); if ($null -eq $fn) {{ Write-Error 'Function not found in AST'; exit 2 }}; $bytes = [System.Text.Encoding]::UTF8.GetBytes($fn.Extent.Text); Write-Output ([System.Convert]::ToBase64String($bytes));";

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ps51Exe,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{astExtractCommand}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!process.Start())
        {
            Assert.Fail($"Failed to start Windows PowerShell 5.1 for AST extraction from '{scriptPath}'.");
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        Assert.AreEqual(0, process.ExitCode,
            $"AST extraction for function '{functionName}' failed with exit code {process.ExitCode}.\nStdout: {stdout}\nStderr: {stderr}");

        string base64 = stdout.Trim();
        Assert.IsFalse(string.IsNullOrWhiteSpace(base64), "AST extraction returned empty output.");

        byte[] textBytes = Convert.FromBase64String(base64);
        string functionText = System.Text.Encoding.UTF8.GetString(textBytes);

        Assert.IsTrue(functionText.StartsWith($"function {functionName}", StringComparison.OrdinalIgnoreCase),
            $"Extracted AST text does not start with 'function {functionName}'. Text:\n{functionText}");
        Assert.IsTrue(functionText.TrimEnd().EndsWith("}"),
            $"Extracted AST text does not end with closing brace. Text:\n{functionText}");

        return functionText;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunWithWindowsPowerShell51(string scriptPath, int timeoutMs = 30000)
    {
        string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string ps51Exe = Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.IsTrue(File.Exists(ps51Exe), $"Windows PowerShell 5.1 executable must exist at '{ps51Exe}'.");

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ps51Exe,
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!process.Start())
        {
            Assert.Fail($"Failed to start Windows PowerShell 5.1 process: '{ps51Exe}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            string? killException = null;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                killException = ex.ToString();
            }

            bool postKillExited = false;
            string? postKillWaitException = null;
            try
            {
                postKillExited = process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                postKillWaitException = ex.ToString();
            }

            if (!postKillExited || postKillWaitException != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Process kill cleanup failed for process PID {process.Id}.");
                if (!postKillExited)
                {
                    sb.AppendLine("process.Kill was called without throwing, but process did not exit.");
                }
                if (postKillWaitException != null)
                {
                    sb.AppendLine($"process.WaitForExit(5000) threw: {postKillWaitException}");
                }
                Assert.Fail(sb.ToString());
            }

            string? outputDrainException = null;
            try
            {
                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromMilliseconds(2000));
            }
            catch (Exception ex)
            {
                outputDrainException = ex.ToString();
            }

            var timeoutMsg = new System.Text.StringBuilder();
            timeoutMsg.AppendLine($"Windows PowerShell 5.1 process timed out after {timeoutMs}ms for script: '{scriptPath}' and was terminated.");
            if (killException != null)
            {
                timeoutMsg.AppendLine($"Note: process.Kill threw an exception: {killException}");
            }
            if (outputDrainException != null)
            {
                timeoutMsg.AppendLine($"Note: output task drain threw an exception: {outputDrainException}");
            }
            Assert.Fail(timeoutMsg.ToString());
        }

        process.WaitForExit();

        string stdout;
        string stderr;
        try
        {
            Task.WaitAll(stdoutTask, stderrTask);
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Failed while awaiting Windows PowerShell 5.1 process output tasks: {ex.Message}");
            return (-1, string.Empty, string.Empty);
        }

        return (process.ExitCode, stdout, stderr);
    }

    #region Phase 5 AI/ML Distribution Optimization Guardrail Tests

    [TestMethod]
    public void Test36_ValidateReleaseScript_RejectsForbiddenAiMlPayloadFiles()
    {
        string root = FindRepoRoot();
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root, makeA2Compliant: true);

        // Inject forbidden onnxruntime.dll into the publish directory
        string fixturePublish = Path.Combine(fixture.FixturePath, "win-x64");
        string forbiddenFile = Path.Combine(fixturePublish, "onnxruntime.dll");
        File.WriteAllBytes(forbiddenFile, "FAKE_ONNXRUNTIME_PAYLOAD"u8.ToArray());

        var result = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, result.ExitCode,
            $"validate-release must fail when forbidden onnxruntime.dll is present. Output: {result.Stdout} {result.Stderr}");
        Assert.IsTrue(result.Stdout.Contains("Forbidden AI/ML payload file 'onnxruntime.dll'") ||
                      result.Stderr.Contains("Forbidden AI/ML payload file 'onnxruntime.dll'"),
            $"Expected forbidden file diagnostic. Output: {result.Stdout} {result.Stderr}");
    }

    [TestMethod]
    public void Test37_ValidateReleaseScript_RejectsForbiddenAiMlDepsMetadata()
    {
        string root = FindRepoRoot();
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root, makeA2Compliant: true);

        // Inject forbidden Microsoft.Windows.AI.MachineLearning into FootballTray.deps.json
        string fixturePublish = Path.Combine(fixture.FixturePath, "win-x64");
        string fixtureDeps = Path.Combine(fixturePublish, "FootballTray.deps.json");
        Assert.IsTrue(File.Exists(fixtureDeps), "deps.json must exist in fixture.");

        var depsNode = JsonNode.Parse(File.ReadAllText(fixtureDeps))!;
        var libsObj = depsNode["libraries"]?.AsObject()!;
        libsObj["Microsoft.Windows.AI.MachineLearning/2.1.74"] = new JsonObject
        {
            ["type"] = "package",
            ["serviceable"] = true
        };
        File.WriteAllText(fixtureDeps, depsNode.ToJsonString());

        var result = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(1, result.ExitCode,
            $"validate-release must fail when forbidden metadata is present in deps.json. Output: {result.Stdout} {result.Stderr}");
        Assert.IsTrue(result.Stdout.Contains("Forbidden package identity 'Microsoft.Windows.AI.MachineLearning/2.1.74'") ||
                      result.Stdout.Contains("AI/ML METADATA REGRESSION") ||
                      result.Stderr.Contains("AI/ML METADATA REGRESSION"),
            $"Expected forbidden metadata diagnostic. Output: {result.Stdout} {result.Stderr}");
    }

    [TestMethod]
    public void Test38_ValidateReleaseScript_AcceptsCleanA2ReleaseWithSearchPackage()
    {
        string root = FindRepoRoot();
        string validateScript = Path.Combine(root, "scripts", "validate-release.ps1");
        using var fixture = new IsolatedReleaseFixture(root, makeA2Compliant: true);

        // Ensure clean fixture contains Microsoft.WindowsAppSDK.Search assembly/dependency
        string fixturePublish = Path.Combine(fixture.FixturePath, "win-x64");
        string searchDll = Path.Combine(fixturePublish, "Microsoft.Windows.Search.Projection.dll");
        if (!File.Exists(searchDll))
        {
            File.WriteAllBytes(searchDll, "FAKE_SEARCH_PROJECTION"u8.ToArray());
        }

        string fixtureDeps = Path.Combine(fixturePublish, "FootballTray.deps.json");
        var depsNode = JsonNode.Parse(File.ReadAllText(fixtureDeps))!;
        var libsObj = depsNode["libraries"]?.AsObject()!;
        if (libsObj["Microsoft.WindowsAppSDK.Search/2.4.4"] == null)
        {
            libsObj["Microsoft.WindowsAppSDK.Search/2.4.4"] = new JsonObject
            {
                ["type"] = "package",
                ["serviceable"] = true
            };
            File.WriteAllText(fixtureDeps, depsNode.ToJsonString());
        }

        var result = RunScriptDirect(validateScript, $"-ReleaseDir \"{fixture.FixturePath}\"", root);
        Assert.AreEqual(0, result.ExitCode,
            $"validate-release must succeed for clean A2 release. Output: {result.Stdout} {result.Stderr}");
        Assert.IsTrue(result.Stdout.Contains("Zero forbidden AI/ML payload files detected"),
            $"Expected clean AI/ML file check output. Output: {result.Stdout}");
        Assert.IsTrue(result.Stdout.Contains("deps.json metadata verified free of forbidden AI/ML dependencies"),
            $"Expected clean deps.json check output. Output: {result.Stdout}");
        Assert.IsTrue(result.Stdout.Contains("VALIDATION PASSED"),
            $"Expected overall validation pass. Output: {result.Stdout}");
    }

    [TestMethod]
    public void Test39_IsolatedReleaseFixture_DefaultPreservesHistoricalPayload_OptInStripsAiMl()
    {
        string root = FindRepoRoot();

        // 1. Default fixture preserves historical v1.0.0 payload (onnxruntime.dll and dependencies remain)
        using (var defaultFixture = new IsolatedReleaseFixture(root))
        {
            string onnxPath = Path.Combine(defaultFixture.FixturePath, "win-x64", "onnxruntime.dll");
            Assert.IsTrue(File.Exists(onnxPath), "Default IsolatedReleaseFixture must preserve historical AI/ML files.");
        }

        // 2. Opt-in A2 fixture strips all 13 forbidden AI/ML payload files and cleans deps.json
        using (var a2Fixture = new IsolatedReleaseFixture(root, makeA2Compliant: true))
        {
            string fixturePublish = Path.Combine(a2Fixture.FixturePath, "win-x64");
            string[] forbiddenAiMlFiles =
            [
                "DirectML.dll", "Microsoft.Graphics.Imaging.Projection.dll", "Microsoft.ML.OnnxRuntime.dll",
                "Microsoft.Windows.AI.ContentSafety.Projection.dll", "Microsoft.Windows.AI.Foundation.Projection.dll",
                "Microsoft.Windows.AI.Imaging.Projection.dll", "Microsoft.Windows.AI.MachineLearning.dll",
                "Microsoft.Windows.AI.MachineLearning.Projection.dll", "Microsoft.Windows.AI.Projection.dll",
                "Microsoft.Windows.AI.Text.Projection.dll", "Microsoft.Windows.AI.Video.Projection.dll",
                "onnxruntime.dll", "System.Numerics.Tensors.dll"
            ];
            foreach (var file in forbiddenAiMlFiles)
            {
                Assert.IsFalse(File.Exists(Path.Combine(fixturePublish, file)),
                    $"Opt-in A2 fixture must strip forbidden file '{file}'.");
            }

            string fixtureDeps = Path.Combine(fixturePublish, "FootballTray.deps.json");
            Assert.IsTrue(File.Exists(fixtureDeps), "deps.json must exist in fixture.");
            string depsContent = File.ReadAllText(fixtureDeps);
            Assert.IsFalse(depsContent.Contains("Microsoft.Windows.AI.MachineLearning/"),
                "Opt-in A2 fixture deps.json must not contain Microsoft.Windows.AI.MachineLearning.");
        }
    }

    #endregion

    #endregion
}
