using System;
using FootballTray.Client.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Production Windows notification renderer leveraging modern Windows App SDK AppNotificationManager.
/// Safely formats and presents notifications and handles headless test or unpacked environments gracefully.
/// </summary>
public sealed class WindowsAppNotificationRenderer : INotificationRenderer
{
    private readonly Func<AppNotificationManager>? _managerProvider;

    public WindowsAppNotificationRenderer(Func<AppNotificationManager>? managerProvider = null)
    {
        _managerProvider = managerProvider;
    }

    public bool RenderNotification(NotificationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            var manager = _managerProvider != null
                ? _managerProvider()
                : AppNotificationManager.Default;

            if (manager == null)
            {
                AppLogger.Warn("Notifications", "ManagerNull", $"AppNotificationManager unavailable: [{payload.Title}] {payload.Body}");
                return false;
            }

            string safeTitle = SanitizeText(payload.Title);
            string safeBody = SanitizeText(payload.Body);

            var builder = new AppNotificationBuilder()
                .AddText(safeTitle)
                .AddText(safeBody)
                .MuteAudio();

            var localLogoUri = GetLocalAppLogoUri();
            if (localLogoUri != null)
            {
                builder.SetAppLogoOverride(localLogoUri, AppNotificationImageCrop.Circle);
            }

            if (!string.IsNullOrWhiteSpace(payload.ActivationArguments))
            {
                var pairs = payload.ActivationArguments.Split('&', StringSplitOptions.RemoveEmptyEntries);
                foreach (var pair in pairs)
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2)
                    {
                        builder.AddArgument(Uri.UnescapeDataString(kv[0].Trim()), Uri.UnescapeDataString(kv[1].Trim()));
                    }
                }
            }

            var appNotification = builder.BuildNotification();
            appNotification.Priority = AppNotificationPriority.High;
            appNotification.ExpiresOnReboot = true;
            manager.Show(appNotification);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Notifications", "ToastShowFailed", $"Failed to dispatch Windows AppNotification: {ex.Message}", ex: ex);
            return false;
        }
    }

    private static Uri? GetLocalAppLogoUri()
    {
        string[] candidates =
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-24_altform-unplated.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.png"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.png")
        };

        foreach (var path in candidates)
        {
            if (System.IO.File.Exists(path))
            {
                return new Uri(path);
            }
        }

        return null;
    }

    private static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}
