using System;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Services.Updates;

/// <summary>
/// Immutable package identity uniquely representing an update release.
/// </summary>
public readonly record struct PackageIdentity(
    string Version,
    string Architecture,
    string DownloadUrl,
    string Sha256,
    long SizeBytes)
{
    public static PackageIdentity FromManifest(ReleaseManifest manifest) =>
        new(
            manifest.Version,
            manifest.Architecture,
            manifest.DownloadUrl,
            manifest.Sha256,
            manifest.SizeBytes);

    public bool Matches(ReleaseManifest manifest) =>
        string.Equals(Version, manifest.Version, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Architecture, manifest.Architecture, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(DownloadUrl, manifest.DownloadUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase) &&
        SizeBytes == manifest.SizeBytes;
}
