using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Core;

namespace FootballTray.Client.Tests;

[TestClass]
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
}
