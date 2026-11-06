using System;
using System.Text.RegularExpressions;

namespace FootballTray.Core.Diagnostics;

/// <summary>
/// Privacy and security sanitizer ensuring local file paths, usernames, and secret tokens
/// are cleanly redacted before logs or diagnostics reports are displayed or copied.
/// </summary>
public static class DiagnosticLogSanitizer
{
    private static readonly Regex AppDataSyntheticRegex = new(
        @"(?i)[a-zA-Z]:[\\/]Users[\\/][^\\/\r\n\t ""':;]+[\\/]AppData[\\/]Local",
        RegexOptions.Compiled);

    private static readonly Regex UserProfileSyntheticRegex = new(
        @"(?i)[a-zA-Z]:[\\/]Users[\\/](?!Default\b|Public\b|All Users\b)[^\\/\r\n\t ""':;]+",
        RegexOptions.Compiled);

    private static readonly Regex QueryStringTokenRegex = new(
        @"(?i)([\?&])(token|access_token|key|api_key|apikey|secret|password|sig|signature)=[^&\s#""']*",
        RegexOptions.Compiled);

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)\bBearer\s+[a-zA-Z0-9_\-\.]+",
        RegexOptions.Compiled);

    private static readonly Regex KeyAssignmentRegex = new(
        @"(?i)\b(password|key|token)=[a-zA-Z0-9_\-\.]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Replaces occurrences of the local user's AppData\Local, UserProfile, synthetic user folders,
    /// and secret tokens/credentials with standardized environment placeholders.
    /// </summary>
    /// <param name="text">The raw text to sanitize.</param>
    /// <returns>The sanitized text with user credentials and account paths redacted.</returns>
    public static string RedactSensitivePaths(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = text;

        // 1. Redact specific runtime LocalApplicationData path first
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                result = Regex.Replace(result, Regex.Escape(localAppData), "%LOCALAPPDATA%", RegexOptions.IgnoreCase);
                result = Regex.Replace(result, Regex.Escape(localAppData.Replace('\\', '/')), "%LOCALAPPDATA%", RegexOptions.IgnoreCase);
            }
        }
        catch { }

        // 2. Redact synthetic Windows AppData\Local paths (e.g. C:\Users\JohnDoe\AppData\Local)
        result = AppDataSyntheticRegex.Replace(result, "%LOCALAPPDATA%");

        // 3. Redact specific runtime UserProfile path
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                result = Regex.Replace(result, Regex.Escape(userProfile), "%USERPROFILE%", RegexOptions.IgnoreCase);
                result = Regex.Replace(result, Regex.Escape(userProfile.Replace('\\', '/')), "%USERPROFILE%", RegexOptions.IgnoreCase);
            }
        }
        catch { }

        // 4. Redact synthetic Windows UserProfile paths (e.g. C:\Users\JohnDoe)
        result = UserProfileSyntheticRegex.Replace(result, "%USERPROFILE%");

        // 5. Redact query-string secrets and token parameters
        result = QueryStringTokenRegex.Replace(result, "$1$2=[REDACTED]");
        result = BearerTokenRegex.Replace(result, "Bearer [REDACTED]");
        result = KeyAssignmentRegex.Replace(result, "$1=[REDACTED]");

        return result;
    }
}
