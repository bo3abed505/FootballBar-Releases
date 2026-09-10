using System;
using System.IO;
using System.Security.Cryptography;
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

        public IsolatedReleaseFixture(string root, string version = "1.0.0")
        {
            string realRelease = Path.Combine(root, "artifacts", "releases", $"v{version}");
            FixturePath = Path.Combine(root, "artifacts", $".tmp_fixture_{Guid.NewGuid():N}");
            Directory.CreateDirectory(FixturePath);

            // Create junction for win-x64 to avoid copying 100MB of binaries
            string realPublish = Path.Combine(realRelease, "win-x64");
            string fixturePublish = Path.Combine(FixturePath, "win-x64");
            RunPowerShell($"New-Item -ItemType Junction -Path '{fixturePublish}' -Target '{realPublish}' | Out-Null", root);

            // Create hardlink for ZIP to avoid copying 62MB archive
            string zipName = $"FootballTray-v{version}-win-x64.zip";
            string realZip = Path.Combine(realRelease, zipName);
            ArchivePath = Path.Combine(FixturePath, zipName);
            RunPowerShell($"New-Item -ItemType HardLink -Path '{ArchivePath}' -Target '{realZip}' | Out-Null", root);

            // Copy small metadata files
            File.Copy(Path.Combine(realRelease, "SHA256SUMS.txt"), Path.Combine(FixturePath, "SHA256SUMS.txt"));
            File.Copy(Path.Combine(realRelease, "VERSION.txt"), Path.Combine(FixturePath, "VERSION.txt"));
            if (File.Exists(Path.Combine(realRelease, "RELEASE_NOTES.txt")))
            {
                File.Copy(Path.Combine(realRelease, "RELEASE_NOTES.txt"), Path.Combine(FixturePath, "RELEASE_NOTES.txt"));
            }

            ManifestPath = Path.Combine(FixturePath, "release-manifest.json");
            ChecksumPath = Path.Combine(FixturePath, "SHA256SUMS.txt");
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
        using var fixture = new IsolatedReleaseFixture(root);

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
        }
        finally
        {
            if (Directory.Exists(releaseDir))
            {
                try { Directory.Delete(releaseDir, recursive: true); } catch { }
            }
        }
    }

    #endregion
}
