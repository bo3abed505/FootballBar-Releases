namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Abstraction for rendering user-facing Windows alerts.
/// Enables 100% deterministic unit testing without Windows App SDK dependencies.
/// </summary>
public interface INotificationRenderer
{
    bool RenderNotification(NotificationPayload payload);
}
