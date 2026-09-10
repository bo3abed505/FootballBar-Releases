using System;
using FootballTray.Core;
using FootballTray.Core.Dto;
using FootballTray.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class ReleaseManifestAndVersionTests
{
    private const string ValidManifestJson = """
    {
      "schemaVersion": "1.0",
      "version": "1.1.0",
      "publishedAt": "2026-09-15T12:00:00Z",
      "architecture": "win-x64",
      "downloadUrl": "https://releases.example.invalid/v1.1.0/FootballTray-v1.1.0-win-x64.zip",
      "sha256": "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54",
      "sizeBytes": 65327531,
      "releaseNotes": "• Taskbar ticker improvements\n• Bug fixes"
    }
    """;

    #region 1. Valid Manifest Tests

    [TestMethod]
    public void Test01_ValidManifest_ParsesAndValidatesCleanly()
    {
        var result = ReleaseManifestParser.ParseAndValidate(ValidManifestJson);

        Assert.IsTrue(result.IsValid, result.ErrorMessage);
        Assert.IsNotNull(result.ValidatedManifest);
        Assert.AreEqual("1.0", result.ValidatedManifest.SchemaVersion);
        Assert.AreEqual("1.1.0", result.ValidatedManifest.Version);
        Assert.AreEqual("win-x64", result.ValidatedManifest.Architecture);
        Assert.AreEqual("https://releases.example.invalid/v1.1.0/FootballTray-v1.1.0-win-x64.zip", result.ValidatedManifest.DownloadUrl);
        Assert.AreEqual("87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54", result.ValidatedManifest.Sha256);
        Assert.AreEqual(65327531L, result.ValidatedManifest.SizeBytes);
        Assert.IsNotNull(result.ValidatedManifest.ReleaseNotes);
        Assert.AreEqual(1, result.ParsedVersion!.Major);
        Assert.AreEqual(1, result.ParsedVersion.Minor);
        Assert.AreEqual(0, result.ParsedVersion.Patch);
    }

    #endregion

    #region 2. Schema Validation & Forward Compatibility

    [TestMethod]
    public void Test02_UnsupportedSchemaVersion_Rejected()
    {
        string json = ValidManifestJson.Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"2.0\"");
        var result = ReleaseManifestParser.ParseAndValidate(json);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.ErrorMessage!.Contains("Unsupported manifest schemaVersion '2.0'"));
    }

    [TestMethod]
    public void Test03_MissingSchemaVersion_Rejected()
    {
        string json = """
        {
          "version": "1.1.0",
          "publishedAt": "2026-09-15T12:00:00Z",
          "architecture": "win-x64",
          "downloadUrl": "https://releases.example.invalid/v1.1.0/app.zip",
          "sha256": "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54",
          "sizeBytes": 1000
        }
        """;
        var result = ReleaseManifestParser.ParseAndValidate(json);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.ErrorMessage!.Contains("'schemaVersion' is missing or empty"));
    }

    [TestMethod]
    public void Test04_UnknownFields_IgnoredForForwardCompatibility()
    {
        string jsonWithExtra = """
        {
          "schemaVersion": "1.0",
          "version": "1.1.0",
          "publishedAt": "2026-09-15T12:00:00Z",
          "architecture": "win-x64",
          "downloadUrl": "https://releases.example.invalid/v1.1.0/FootballTray-v1.1.0-win-x64.zip",
          "sha256": "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54",
          "sizeBytes": 65327531,
          "releaseNotes": "Notes",
          "futureField1": "some-value",
          "futureMetadata": { "channel": "stable", "priority": 10 }
        }
        """;

        var result = ReleaseManifestParser.ParseAndValidate(jsonWithExtra);

        Assert.IsTrue(result.IsValid, result.ErrorMessage);
        Assert.AreEqual("1.1.0", result.ValidatedManifest!.Version);
    }

    [TestMethod]
    public void Test05_EmptyOrMalformedJson_RejectedWithoutExceptions()
    {
        var r1 = ReleaseManifestParser.ParseAndValidate(null);
        Assert.IsFalse(r1.IsValid);

        var r2 = ReleaseManifestParser.ParseAndValidate("   ");
        Assert.IsFalse(r2.IsValid);

        var r3 = ReleaseManifestParser.ParseAndValidate("{ invalid json");
        Assert.IsFalse(r3.IsValid);
        Assert.IsTrue(r3.ErrorMessage!.Contains("Malformed JSON"));
    }

    #endregion

    #region 3. Architecture Validation

    [TestMethod]
    public void Test06_Architecture_WinX64AcceptedExactly()
    {
        var result = ReleaseManifestParser.ParseAndValidate(ValidManifestJson);

        Assert.IsTrue(result.IsValid, result.ErrorMessage);
        Assert.AreEqual("win-x64", result.ValidatedManifest!.Architecture);
    }

    [TestMethod]
    public void Test07_Architecture_CaseSensitiveAndNonX64Rejected()
    {
        // Mixed and uppercase rejected by exact ordinal check
        string jsonUpper = ValidManifestJson.Replace("\"architecture\": \"win-x64\"", "\"architecture\": \"WIN-X64\"");
        var rUpper = ReleaseManifestParser.ParseAndValidate(jsonUpper);
        Assert.IsFalse(rUpper.IsValid);
        Assert.IsTrue(rUpper.ErrorMessage!.Contains("Incompatible architecture 'WIN-X64'"));

        string jsonMixed = ValidManifestJson.Replace("\"architecture\": \"win-x64\"", "\"architecture\": \"Win-x64\"");
        var rMixed = ReleaseManifestParser.ParseAndValidate(jsonMixed);
        Assert.IsFalse(rMixed.IsValid);
        Assert.IsTrue(rMixed.ErrorMessage!.Contains("Incompatible architecture 'Win-x64'"));

        string jsonX86 = ValidManifestJson.Replace("\"architecture\": \"win-x64\"", "\"architecture\": \"x86\"");
        var r1 = ReleaseManifestParser.ParseAndValidate(jsonX86);
        Assert.IsFalse(r1.IsValid);
        Assert.IsTrue(r1.ErrorMessage!.Contains("Incompatible architecture 'x86'"));

        string jsonArm = ValidManifestJson.Replace("\"architecture\": \"win-x64\"", "\"architecture\": \"arm64\"");
        var r2 = ReleaseManifestParser.ParseAndValidate(jsonArm);
        Assert.IsFalse(r2.IsValid);
        Assert.IsTrue(r2.ErrorMessage!.Contains("Incompatible architecture 'arm64'"));
    }

    #endregion

    #region 4. Download URL Validation

    [TestMethod]
    public void Test08_DownloadUrl_HttpsAccepted()
    {
        string json = ValidManifestJson.Replace(
            "https://releases.example.invalid/v1.1.0/FootballTray-v1.1.0-win-x64.zip",
            "https://mycdn.example.org/downloads/FootballTray.zip");
        var result = ReleaseManifestParser.ParseAndValidate(json);

        Assert.IsTrue(result.IsValid, result.ErrorMessage);
    }

    [TestMethod]
    public void Test09_DownloadUrl_InsecureHttpAndFileSchemeRejected()
    {
        string jsonHttp = ValidManifestJson.Replace("https://", "http://");
        var r1 = ReleaseManifestParser.ParseAndValidate(jsonHttp);
        Assert.IsFalse(r1.IsValid);
        Assert.IsTrue(r1.ErrorMessage!.Contains("must use HTTPS scheme"));

        string jsonFile = ValidManifestJson.Replace("https://releases.example.invalid/v1.1.0/FootballTray-v1.1.0-win-x64.zip", "file:///C:/app.zip");
        var r2 = ReleaseManifestParser.ParseAndValidate(jsonFile);
        Assert.IsFalse(r2.IsValid);
        Assert.IsTrue(r2.ErrorMessage!.Contains("must use HTTPS scheme"));
    }

    [TestMethod]
    public void Test10_DownloadUrl_RelativeOrMalformedRejected()
    {
        string jsonRel = ValidManifestJson.Replace("https://releases.example.invalid/v1.1.0/FootballTray-v1.1.0-win-x64.zip", "/downloads/app.zip");
        var r1 = ReleaseManifestParser.ParseAndValidate(jsonRel);
        Assert.IsFalse(r1.IsValid);
        Assert.IsTrue(r1.ErrorMessage!.Contains("not a valid absolute URI"));
    }

    #endregion

    #region 5. SHA-256 Digest Validation

    [TestMethod]
    public void Test11_Sha256_ValidLowercaseAccepted()
    {
        var result = ReleaseManifestParser.ParseAndValidate(ValidManifestJson);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54", result.ValidatedManifest!.Sha256);
    }

    [TestMethod]
    public void Test12_Sha256_UppercaseAcceptedAndNormalizedToLowercase()
    {
        string uppercaseHash = "87DF7FA5A49EC2B964E466457DFAD6F5C1DBA22ED042BD5362627DE629E50C54";
        string json = ValidManifestJson.Replace("87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54", uppercaseHash);

        var result = ReleaseManifestParser.ParseAndValidate(json);
        Assert.IsTrue(result.IsValid, result.ErrorMessage);
        Assert.AreEqual(uppercaseHash.ToLowerInvariant(), result.ValidatedManifest!.Sha256);
    }

    [TestMethod]
    public void Test13_Sha256_TooShortOrNonHexRejected()
    {
        // 63 chars (too short)
        string shortHash = "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c5";
        string json1 = ValidManifestJson.Replace("87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54", shortHash);
        var r1 = ReleaseManifestParser.ParseAndValidate(json1);
        Assert.IsFalse(r1.IsValid);
        Assert.IsTrue(r1.ErrorMessage!.Contains("exactly 64 hexadecimal characters"));

        // 64 chars but contains 'Z'
        string nonHex = "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c5Z";
        string json2 = ValidManifestJson.Replace("87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54", nonHex);
        var r2 = ReleaseManifestParser.ParseAndValidate(json2);
        Assert.IsFalse(r2.IsValid);
        Assert.IsTrue(r2.ErrorMessage!.Contains("exactly 64 hexadecimal characters"));
    }

    #endregion

    #region 6. Size Validation

    [TestMethod]
    public void Test14_SizeBytes_ZeroOrNegativeRejected()
    {
        string jsonZero = ValidManifestJson.Replace("\"sizeBytes\": 65327531", "\"sizeBytes\": 0");
        var r1 = ReleaseManifestParser.ParseAndValidate(jsonZero);
        Assert.IsFalse(r1.IsValid);
        Assert.IsTrue(r1.ErrorMessage!.Contains("positive integer greater than zero"));

        string jsonNeg = ValidManifestJson.Replace("\"sizeBytes\": 65327531", "\"sizeBytes\": -500");
        var r2 = ReleaseManifestParser.ParseAndValidate(jsonNeg);
        Assert.IsFalse(r2.IsValid);
        Assert.IsTrue(r2.ErrorMessage!.Contains("positive integer greater than zero"));
    }

    #endregion

    #region 7. Publication Date Validation

    [TestMethod]
    public void Test15_PublishedAt_UtcZAndExplicitOffsetAcceptedAndNormalized()
    {
        // A. Explicit UTC 'Z'
        var r1 = ReleaseManifestParser.ParseAndValidate(ValidManifestJson);
        Assert.IsTrue(r1.IsValid);
        Assert.AreEqual(TimeSpan.Zero, r1.ValidatedManifest!.PublishedAt.Offset);
        Assert.AreEqual(12, r1.ValidatedManifest.PublishedAt.Hour);

        // B. Explicit +03:00 offset: 15:00+03:00 normalizes to 12:00:00 UTC
        string jsonPlus = ValidManifestJson.Replace("\"publishedAt\": \"2026-09-15T12:00:00Z\"", "\"publishedAt\": \"2026-09-15T15:00:00+03:00\"");
        var r2 = ReleaseManifestParser.ParseAndValidate(jsonPlus);
        Assert.IsTrue(r2.IsValid, r2.ErrorMessage);
        Assert.AreEqual(TimeSpan.Zero, r2.ValidatedManifest!.PublishedAt.Offset);
        Assert.AreEqual(12, r2.ValidatedManifest.PublishedAt.Hour);

        // C. Explicit -05:00 offset: 07:00-05:00 normalizes to 12:00:00 UTC
        string jsonMinus = ValidManifestJson.Replace("\"publishedAt\": \"2026-09-15T12:00:00Z\"", "\"publishedAt\": \"2026-09-15T07:00:00-05:00\"");
        var r3 = ReleaseManifestParser.ParseAndValidate(jsonMinus);
        Assert.IsTrue(r3.IsValid, r3.ErrorMessage);
        Assert.AreEqual(TimeSpan.Zero, r3.ValidatedManifest!.PublishedAt.Offset);
        Assert.AreEqual(12, r3.ValidatedManifest.PublishedAt.Hour);

        // Future-dated timestamp is accepted (user clock skew tolerance)
        string jsonFuture = ValidManifestJson.Replace("\"publishedAt\": \"2026-09-15T12:00:00Z\"", "\"publishedAt\": \"2029-01-01T00:00:00Z\"");
        var r4 = ReleaseManifestParser.ParseAndValidate(jsonFuture);
        Assert.IsTrue(r4.IsValid, r4.ErrorMessage);
    }

    [TestMethod]
    public void Test15B_PublishedAt_OffsetlessAndMalformedRejected()
    {
        // D. Offset-less timestamp rejected (must not infer local machine timezone)
        string jsonOffsetless = ValidManifestJson.Replace("\"publishedAt\": \"2026-09-15T12:00:00Z\"", "\"publishedAt\": \"2026-09-15T12:00:00\"");
        var r1 = ReleaseManifestParser.ParseAndValidate(jsonOffsetless);
        Assert.IsFalse(r1.IsValid);
        Assert.IsTrue(r1.ErrorMessage!.Contains("explicit UTC 'Z' or timezone offset") || r1.ErrorMessage.Contains("Malformed JSON"));

        // E. Malformed timestamp strings rejected
        string jsonInvalidDate = ValidManifestJson.Replace("\"publishedAt\": \"2026-09-15T12:00:00Z\"", "\"publishedAt\": \"invalid-timestamp\"");
        var r2 = ReleaseManifestParser.ParseAndValidate(jsonInvalidDate);
        Assert.IsFalse(r2.IsValid);
        Assert.IsTrue(r2.ErrorMessage!.Contains("Malformed JSON"));

        string jsonEmptyDate = ValidManifestJson.Replace("\"publishedAt\": \"2026-09-15T12:00:00Z\"", "\"publishedAt\": \"\"");
        var r3 = ReleaseManifestParser.ParseAndValidate(jsonEmptyDate);
        Assert.IsFalse(r3.IsValid);
        Assert.IsTrue(r3.ErrorMessage!.Contains("Malformed JSON"));
    }

    #endregion

    #region 8. SemVer Precedence & Installer Alignment

    [TestMethod]
    public void Test16_SemVer_1_1_0_IsNewerThan_1_0_0()
    {
        var v1 = SemanticVersion.Parse("1.1.0");
        var v2 = SemanticVersion.Parse("1.0.0");

        Assert.IsTrue(v1 > v2);
        Assert.AreEqual(1, v1.CompareTo(v2));
    }

    [TestMethod]
    public void Test17_SemVer_UppercaseAndLowercaseV_IdenticalToBareVersion()
    {
        var vBare = SemanticVersion.Parse("1.0.0");
        var vLower = SemanticVersion.Parse("v1.0.0");
        var vUpper = SemanticVersion.Parse("V1.0.0");

        Assert.AreEqual(vBare, vLower);
        Assert.AreEqual(vBare, vUpper);
        Assert.AreEqual(0, vUpper.CompareTo(vBare));
        Assert.AreEqual(0, vUpper.CompareTo(vLower));
        Assert.IsTrue(vUpper == vBare);
        Assert.IsTrue(vUpper == vLower);
    }

    [TestMethod]
    public void Test17B_SemVer_HostileAndOverflowCases_FailSafely()
    {
        // Absurdly huge integer component fails safely without throwing OverflowException
        Assert.IsFalse(SemanticVersion.TryParse("999999999999999999999999999999.0.0", out _));

        // Dangling hyphens and pluses fail safely
        Assert.IsFalse(SemanticVersion.TryParse("1.0.0-", out _));
        Assert.IsFalse(SemanticVersion.TryParse("1.0.0+", out _));

        // Huge prerelease identifier is safely parsed as string and compares without exception
        Assert.IsTrue(SemanticVersion.TryParse("1.0.0-999999999999999999999999999999", out var vHugePre));
        Assert.IsNotNull(vHugePre);
        var vNormal = SemanticVersion.Parse("1.0.0");
        Assert.IsTrue(vNormal > vHugePre);
    }

    [TestMethod]
    public void Test18_SemVer_0_9_0_IsOlderThan_1_0_0()
    {
        var vOld = SemanticVersion.Parse("0.9.0");
        var vCurrent = SemanticVersion.Parse("1.0.0");

        Assert.IsTrue(vOld < vCurrent);
        Assert.AreEqual(-1, vOld.CompareTo(vCurrent));
    }

    [TestMethod]
    public void Test19_SemVer_Stable_1_0_0_IsNewerThan_1_0_0_rc1()
    {
        var vStable = SemanticVersion.Parse("1.0.0");
        var vRc = SemanticVersion.Parse("1.0.0-rc.1");

        Assert.IsTrue(vStable > vRc);
        Assert.AreEqual(1, vStable.CompareTo(vRc));
    }

    [TestMethod]
    public void Test20_SemVer_BuildMetadata_DoesNotAffectPrecedence()
    {
        var vBuild5 = SemanticVersion.Parse("1.0.0+build.5");
        var vBuild6 = SemanticVersion.Parse("1.0.0+build.6");

        Assert.AreEqual(0, vBuild5.CompareTo(vBuild6));
        Assert.IsTrue(vBuild5.Equals(vBuild6));
    }

    [TestMethod]
    public void Test21_InstallerConsistency_MatchesInstallClientPs1Tests()
    {
        // Equivalent to Test B in install-client.test.ts: 1.0.0 newer than 1.0.0-beta
        var v1 = SemanticVersion.Parse("1.0.0");
        var vBeta = SemanticVersion.Parse("1.0.0-beta");
        Assert.AreEqual(1, v1.CompareTo(vBeta));

        // Equivalent to Test C in install-client.test.ts: 1.0.0-beta.2 newer than 1.0.0-beta.1
        var vBeta2 = SemanticVersion.Parse("1.0.0-beta.2");
        var vBeta1 = SemanticVersion.Parse("1.0.0-beta.1");
        Assert.AreEqual(1, vBeta2.CompareTo(vBeta1));

        // Equivalent to Test D in install-client.test.ts: 1.1.0 newer than 1.0.0
        var v110 = SemanticVersion.Parse("1.1.0");
        var v100 = SemanticVersion.Parse("1.0.0");
        Assert.AreEqual(1, v110.CompareTo(v100));

        // Equivalent to Test E in install-client.test.ts: Invalid version strings rejected
        Assert.IsFalse(SemanticVersion.TryParse("invalid-version", out _));
        Assert.IsFalse(SemanticVersion.TryParse("1.0", out _));
        Assert.IsFalse(SemanticVersion.TryParse("", out _));
        Assert.IsFalse(SemanticVersion.TryParse("v", out _));
    }

    #endregion

    #region 9. Update Eligibility & Stable Channel Policy

    [TestMethod]
    public void Test22_StableChannelPolicy_StableLocalDoesNotReceivePrereleaseUpdate()
    {
        // Local: 1.0.0 (stable)
        // Remote: 1.1.0-beta (prerelease)
        string jsonBeta = ValidManifestJson.Replace("\"version\": \"1.1.0\"", "\"version\": \"1.1.0-beta\"");
        var manifest = ReleaseManifestParser.ParseAndValidate(jsonBeta).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.0.0");

        Assert.IsFalse(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.PrereleaseNotEligible, eval.Status);
        Assert.IsTrue(eval.Reason!.Contains("Prerelease version"));
    }

    [TestMethod]
    public void Test23_StableChannelPolicy_StableLocalReceivesStableUpdate()
    {
        // Local: 1.0.0 (stable)
        // Remote: 1.1.0 (stable)
        var manifest = ReleaseManifestParser.ParseAndValidate(ValidManifestJson).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.0.0");

        Assert.IsTrue(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.UpdateAvailable, eval.Status);
        Assert.AreEqual("1.1.0", eval.TargetVersion!.ToString());
        Assert.AreEqual("1.0.0", eval.CurrentVersion!.ToString());
    }

    [TestMethod]
    public void Test24_UpdateEligibility_EqualVersionIsUpToDate()
    {
        // Local: 1.1.0
        // Remote: 1.1.0
        var manifest = ReleaseManifestParser.ParseAndValidate(ValidManifestJson).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.1.0");

        Assert.IsFalse(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.UpToDate, eval.Status);
    }

    [TestMethod]
    public void Test25_UpdateEligibility_OlderRemoteIsUpToDateDowngradeRejected()
    {
        // Local: 1.2.0
        // Remote: 1.1.0
        var manifest = ReleaseManifestParser.ParseAndValidate(ValidManifestJson).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.2.0");

        Assert.IsFalse(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.UpToDate, eval.Status);
        Assert.IsTrue(eval.Reason!.Contains("downgrade rejected"));
    }

    [TestMethod]
    public void Test26_UpdateEligibility_PrereleaseLocalReceivesStableUpgrade()
    {
        // Local: 1.0.0-rc.1 (running prerelease)
        // Remote: 1.0.0 (stable)
        string json100 = ValidManifestJson.Replace("\"version\": \"1.1.0\"", "\"version\": \"1.0.0\"");
        var manifest = ReleaseManifestParser.ParseAndValidate(json100).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.0.0-rc.1");

        Assert.IsTrue(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.UpdateAvailable, eval.Status);
        Assert.AreEqual("1.0.0", eval.TargetVersion!.ToString());
    }

    [TestMethod]
    public void Test27_UpdateEligibility_PrereleaseLocalReceivesNewerPrerelease()
    {
        // Local: 1.1.0-beta.1
        // Remote: 1.1.0-beta.2
        string jsonBeta2 = ValidManifestJson.Replace("\"version\": \"1.1.0\"", "\"version\": \"1.1.0-beta.2\"");
        var manifest = ReleaseManifestParser.ParseAndValidate(jsonBeta2).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.1.0-beta.1");

        Assert.IsTrue(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.UpdateAvailable, eval.Status);
        Assert.AreEqual("1.1.0-beta.2", eval.TargetVersion!.ToString());
    }

    [TestMethod]
    public void Test28_Evaluator_InvalidCurrentVersion_FailsSafelyWithoutException()
    {
        var manifest = ReleaseManifestParser.ParseAndValidate(ValidManifestJson).ValidatedManifest!;

        var r1 = UpdateVersionEvaluator.Evaluate(manifest, "not-a-version");
        Assert.IsFalse(r1.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.InvalidManifest, r1.Status);
        Assert.IsTrue(r1.Reason!.Contains("not a valid semantic version"));

        var r2 = UpdateVersionEvaluator.Evaluate(manifest, "");
        Assert.IsFalse(r2.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.InvalidManifest, r2.Status);

        var r3 = UpdateVersionEvaluator.Evaluate(manifest, (SemanticVersion?)null);
        Assert.IsFalse(r3.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.InvalidManifest, r3.Status);
    }

    [TestMethod]
    public void Test29_Evaluator_InvalidManifestVersion_ReturnsInvalidManifest()
    {
        var invalidManifest = new ReleaseManifest
        {
            SchemaVersion = "1.0",
            Version = "invalid-version-string",
            PublishedAt = DateTimeOffset.UtcNow,
            Architecture = "win-x64",
            DownloadUrl = "https://example.org/app.zip",
            Sha256 = "87df7fa5a49ec2b964e466457dfad6f5c1dba22ed042bd5362627de629e50c54",
            SizeBytes = 1000
        };

        var eval = UpdateVersionEvaluator.Evaluate(invalidManifest, "1.0.0");
        Assert.IsFalse(eval.IsUpdateAvailable);
        Assert.AreEqual(UpdateEvaluationStatus.InvalidManifest, eval.Status);
    }

    [TestMethod]
    public void Test30_Evaluator_TargetVersionAlwaysDerivedFromManifest()
    {
        var manifest = ReleaseManifestParser.ParseAndValidate(ValidManifestJson).ValidatedManifest!;

        var eval = UpdateVersionEvaluator.Evaluate(manifest, "1.0.0");
        Assert.IsTrue(eval.IsUpdateAvailable);
        Assert.AreEqual("1.1.0", eval.TargetVersion!.ToString());
        Assert.AreEqual(manifest.Version, eval.TargetVersion.ToString());
    }

    #endregion
}
