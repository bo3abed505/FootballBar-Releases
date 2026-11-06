using System;

namespace FootballTray.Core.Security;

/// <summary>
/// Strict URL validation helper enforcing HTTPS scheme pinning and preventing
/// path traversal, local file access, UNC execution, and dangerous pseudo-protocols.
/// </summary>
public static class UrlSecurityValidator
{
    /// <summary>
    /// Validates whether a given URL is a safe, absolute HTTPS address.
    /// Rejects relative URLs, UNC paths, and non-HTTPS schemes (e.g. file://, ftp://, javascript:).
    /// </summary>
    /// <param name="url">The URL string to evaluate.</param>
    /// <param name="allowLoopbackHttpForTests">When true, allows http://localhost or http://127.0.0.1 for local integration tests.</param>
    /// <returns>True if the URL is an absolute HTTPS address or valid test loopback; otherwise false.</returns>
    public static bool IsSafeHttpsUrl(string? url, bool allowLoopbackHttpForTests = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string trimmed = url.Trim();

        // Reject UNC paths explicitly (e.g. \\server\share or //server/share)
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Must not be a UNC path
        if (uri.IsUnc)
        {
            return false;
        }

        // Host must be present
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        // Strict HTTPS scheme check
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Controlled loopback HTTP exception exclusively for test mock servers
        if (allowLoopbackHttpForTests && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.IsLoopback ||
                string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

