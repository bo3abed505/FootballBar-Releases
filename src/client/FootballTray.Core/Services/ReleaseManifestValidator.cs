using System;
using System.Text.RegularExpressions;
using FootballTray.Core.Dto;

namespace FootballTray.Core.Services;

/// <summary>
/// Result of release manifest structural and domain validation.
/// </summary>
public sealed record ManifestValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public ReleaseManifest? ValidatedManifest { get; init; }
    public SemanticVersion? ParsedVersion { get; init; }

    public static ManifestValidationResult Success(ReleaseManifest manifest, SemanticVersion parsedVersion) =>
        new() { IsValid = true, ValidatedManifest = manifest, ParsedVersion = parsedVersion };

    public static ManifestValidationResult Failure(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Pure, deterministic validator for FootballTray release manifests.
///
/// Security & Trust Boundary:
/// - HTTPS protects transport security and authenticates the configured remote release endpoint.
/// - The SHA-256 digest ensures payload bytes downloaded match this manifest; it does NOT constitute
///   independent publisher authentication without a separate digital signature.
/// - The per-user installer (install-client.ps1) provides local payload, version, and rollback safety.
/// - Future Authenticode code signing remains a distinct Phase 5 hardening capability.
/// </summary>
public static class ReleaseManifestValidator
{
    public const string ExpectedSchemaVersion = "1.0";
    public const string ExpectedArchitecture = "win-x64";
    public const int MaxReleaseNotesLength = 10000;

    private static readonly Regex Sha256Regex = new(@"^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Validates a ReleaseManifest against all schema and domain integrity invariants.
    /// Returns a domain ManifestValidationResult without throwing exceptions for invalid remote data.
    /// </summary>
    public static ManifestValidationResult Validate(ReleaseManifest? manifest)
    {
        if (manifest is null)
        {
            return ManifestValidationResult.Failure("Manifest is null.");
        }

        // 1. Schema Version Check
        if (string.IsNullOrWhiteSpace(manifest.SchemaVersion))
        {
            return ManifestValidationResult.Failure("Manifest 'schemaVersion' is missing or empty.");
        }
        if (!string.Equals(manifest.SchemaVersion.Trim(), ExpectedSchemaVersion, StringComparison.Ordinal))
        {
            return ManifestValidationResult.Failure($"Unsupported manifest schemaVersion '{manifest.SchemaVersion}'. Expected '{ExpectedSchemaVersion}'.");
        }

        // 2. Semantic Version Check
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return ManifestValidationResult.Failure("Manifest 'version' is missing or empty.");
        }
        if (!SemanticVersion.TryParse(manifest.Version, out var parsedVersion) || parsedVersion is null)
        {
            return ManifestValidationResult.Failure($"Manifest 'version' '{manifest.Version}' is not a valid semantic version.");
        }

        // 3. Architecture Check (exact match: win-x64)
        if (string.IsNullOrWhiteSpace(manifest.Architecture))
        {
            return ManifestValidationResult.Failure("Manifest 'architecture' is missing or empty.");
        }
        if (!string.Equals(manifest.Architecture.Trim(), ExpectedArchitecture, StringComparison.Ordinal))
        {
            return ManifestValidationResult.Failure($"Incompatible architecture '{manifest.Architecture}'. Expected '{ExpectedArchitecture}'.");
        }

        // 4. Download URL Check (HTTPS only, absolute URI, no file:// or ordinary http://)
        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl))
        {
            return ManifestValidationResult.Failure("Manifest 'downloadUrl' is missing or empty.");
        }
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri))
        {
            return ManifestValidationResult.Failure($"Manifest 'downloadUrl' '{manifest.DownloadUrl}' is not a valid absolute URI.");
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ManifestValidationResult.Failure($"Manifest 'downloadUrl' must use HTTPS scheme. Insecure or non-web scheme '{uri.Scheme}' is rejected.");
        }

        // 5. SHA-256 Checksum Check (64 hex chars, case-insensitive, normalized to lowercase)
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            return ManifestValidationResult.Failure("Manifest 'sha256' checksum is missing or empty.");
        }
        string trimmedHash = manifest.Sha256.Trim();
        if (!Sha256Regex.IsMatch(trimmedHash))
        {
            return ManifestValidationResult.Failure($"Manifest 'sha256' must be exactly 64 hexadecimal characters. Received '{trimmedHash}'.");
        }
        string normalizedHash = trimmedHash.ToLowerInvariant();

        // 6. Size Check (positive integer > 0)
        if (manifest.SizeBytes <= 0)
        {
            return ManifestValidationResult.Failure($"Manifest 'sizeBytes' must be a positive integer greater than zero. Received {manifest.SizeBytes}.");
        }

        // 7. PublishedAt Check (valid timestamp normalized to UTC)
        // DateTimeOffset parsing preserves explicit offset; ensure normalized to UTC
        if (manifest.PublishedAt == default)
        {
            return ManifestValidationResult.Failure("Manifest 'publishedAt' is missing or default timestamp.");
        }
        var normalizedPublishedAt = manifest.PublishedAt.ToUniversalTime();

        // 8. Release Notes Check (optional, bounded length)
        if (manifest.ReleaseNotes != null && manifest.ReleaseNotes.Length > MaxReleaseNotesLength)
        {
            return ManifestValidationResult.Failure($"Manifest 'releaseNotes' exceeds maximum allowed length of {MaxReleaseNotesLength} characters.");
        }

        var normalizedManifest = manifest with
        {
            Sha256 = normalizedHash,
            PublishedAt = normalizedPublishedAt
        };

        return ManifestValidationResult.Success(normalizedManifest, parsedVersion);
    }
}
