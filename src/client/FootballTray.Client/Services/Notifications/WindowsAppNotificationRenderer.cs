using System;
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
                System.Diagnostics.Debug.WriteLine($"[NOTIFICATION] AppNotificationManager unavailable: [{payload.Title}] {payload.Body}");
                return false;
            }

            var builder = new AppNotificationBuilder()
                .AddText(payload.Title)
                .AddText(payload.Body)
                .MuteAudio();

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
            manager.Show(appNotification);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATION] Failed to dispatch Windows AppNotification: {ex.Message}");
            return false;
        }
    }
}
