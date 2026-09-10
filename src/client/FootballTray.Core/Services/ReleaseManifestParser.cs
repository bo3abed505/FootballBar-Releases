using System;
using System.Text.Json;
using FootballTray.Core.Dto;
using FootballTray.Core.Serialization;

namespace FootballTray.Core.Services;

/// <summary>
/// Reflection-free JSON parser and validator for FootballTray release manifests.
/// Ignores unknown fields for forward compatibility while strictly validating schemaVersion.
/// </summary>
public static class ReleaseManifestParser
{
    /// <summary>
    /// Parses JSON into ReleaseManifest DTO using source-generated serializer context.
    /// Returns null if JSON is empty, null, or malformed.
    /// </summary>
    public static ReleaseManifest? TryParse(string? json, out string? parseError)
    {
        parseError = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            parseError = "Manifest JSON content is null or whitespace.";
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(json, ReleaseManifestJsonContext.Default.ReleaseManifest);
            if (manifest is null)
            {
                parseError = "Deserialized manifest is null.";
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            parseError = $"Malformed JSON: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Parses JSON and executes full structural and domain validation.
    /// </summary>
    public static ManifestValidationResult ParseAndValidate(string? json)
    {
        var manifest = TryParse(json, out var parseError);
        if (manifest is null)
        {
            return ManifestValidationResult.Failure(parseError ?? "Failed to parse manifest JSON.");
        }

        return ReleaseManifestValidator.Validate(manifest);
    }
}
