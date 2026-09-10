using System;
using System.Text.RegularExpressions;

namespace FootballTray.Core;

/// <summary>
/// Strict Semantic Versioning 2.0.0 implementation with full precedence alignment to install-client.ps1.
/// Supports major.minor.patch, dot-separated prerelease identifiers, and build metadata.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex SemVerRegex = new(
        @"^[vV]?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$",
        RegexOptions.Compiled);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }
    public string Raw { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    private SemanticVersion(
        int major,
        int minor,
        int patch,
        string? prerelease,
        string? buildMetadata,
        string raw)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
        Raw = raw;
    }

    /// <summary>
    /// Attempts to parse a semantic version string. Accepts optional leading 'v' or 'V'.
    /// </summary>
    public static bool TryParse(string? versionString, out SemanticVersion? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return false;
        }

        string trimmed = versionString.Trim();
        var match = SemVerRegex.Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out int major) ||
            !int.TryParse(match.Groups[2].Value, out int minor) ||
            !int.TryParse(match.Groups[3].Value, out int patch))
        {
            return false;
        }

        string? prerelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        string? buildMetadata = match.Groups[5].Success ? match.Groups[5].Value : null;

        result = new SemanticVersion(major, minor, patch, prerelease, buildMetadata, trimmed);
        return true;
    }

    /// <summary>
    /// Parses a semantic version string or throws FormatException.
    /// </summary>
    public static SemanticVersion Parse(string versionString)
    {
        if (!TryParse(versionString, out var version) || version == null)
        {
            throw new FormatException($"Invalid semantic version format: '{versionString}'. Expected format like '1.0.0', 'v1.0.0', '1.0.0-beta', or '1.0.0-rc.1'.");
        }
        return version;
    }

    /// <summary>
    /// Compares two semantic versions according to SemVer 2.0.0 precedence rules,
    /// identical to install-client.ps1 Compare-SemVer.
    /// Build metadata is strictly ignored during comparison.
    /// </summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (other is null) return 1;

        // 1. Compare Major
        int majorCmp = Major.CompareTo(other.Major);
        if (majorCmp != 0) return majorCmp;

        // 2. Compare Minor
        int minorCmp = Minor.CompareTo(other.Minor);
        if (minorCmp != 0) return minorCmp;

        // 3. Compare Patch
        int patchCmp = Patch.CompareTo(other.Patch);
        if (patchCmp != 0) return patchCmp;

        // 4. Compare Prerelease
        // Stable release (null prerelease) has higher precedence than any prerelease of the same base version
        if (Prerelease == null && other.Prerelease != null) return 1;
        if (Prerelease != null && other.Prerelease == null) return -1;
        if (Prerelease == null && other.Prerelease == null) return 0;

        // Both have prereleases: split by '.' and compare identifiers
        var parts1 = Prerelease!.Split('.');
        var parts2 = other.Prerelease!.Split('.');
        int minLen = Math.Min(parts1.Length, parts2.Length);

        for (int i = 0; i < minLen; i++)
        {
            string id1 = parts1[i];
            string id2 = parts2[i];

            bool isNum1 = int.TryParse(id1, out int n1);
            bool isNum2 = int.TryParse(id2, out int n2);

            if (isNum1 && isNum2)
            {
                int numCmp = n1.CompareTo(n2);
                if (numCmp != 0) return numCmp;
            }
            else if (isNum1 && !isNum2)
            {
                // Numeric identifiers have lower precedence than non-numeric
                return -1;
            }
            else if (!isNum1 && isNum2)
            {
                return 1;
            }
            else
            {
                // Both non-numeric: lexical ASCII compare
                int strCmp = string.Compare(id1, id2, StringComparison.Ordinal);
                if (strCmp != 0) return strCmp;
            }
        }

        // The version with larger set of prerelease fields has higher precedence
        return parts1.Length.CompareTo(parts2.Length);
    }

    public bool Equals(SemanticVersion? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return CompareTo(other) == 0;
    }

    public override bool Equals(object? obj) =>
        obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString()
    {
        string ver = $"{Major}.{Minor}.{Patch}";
        if (!string.IsNullOrEmpty(Prerelease)) ver += $"-{Prerelease}";
        if (!string.IsNullOrEmpty(BuildMetadata)) ver += $"+{BuildMetadata}";
        return ver;
    }

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) =>
        !(left == right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;
}
