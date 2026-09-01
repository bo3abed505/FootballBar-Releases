using System;
using System.Collections.Generic;
using Microsoft.Windows.AppNotifications;

namespace FootballTray.Client.Services.Notifications;

/// <summary>
/// Bridge interface abstracting Windows App SDK AppNotificationManager for testability and lifecycle management.
/// </summary>
public interface IAppNotificationManagerBridge
{
    event EventHandler<string>? NotificationInvoked;
    void Register();
    void Unregister();
    bool IsSupported { get; }
}

/// <summary>
/// Default production implementation delegating directly to Microsoft.Windows.AppNotifications.AppNotificationManager.Default.
/// </summary>
public sealed class WindowsAppNotificationManagerBridge : IAppNotificationManagerBridge
{
    private Windows.Foundation.TypedEventHandler<AppNotificationManager, AppNotificationActivatedEventArgs>? _nativeHandler;

    public event EventHandler<string>? NotificationInvoked;

    private static void EnsureBootstrap()
    {
        try
        {
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(0x00020004, out _);
        }
        catch
        {
            // Ignored if packaged or already initialized
        }
    }

    public bool IsSupported
    {
        get
        {
            try
            {
                EnsureBootstrap();
                return AppNotificationManager.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    public void Register()
    {
        EnsureBootstrap();
        var manager = AppNotificationManager.Default;
        if (manager == null)
        {
            throw new InvalidOperationException("AppNotificationManager.Default is null.");
        }

        _nativeHandler = (sender, args) =>
        {
            // Windows App SDK returns arguments in dictionary or string
            string rawArgs = string.Empty;
            if (args?.Arguments != null && args.Arguments.Count > 0)
            {
                var pairs = new List<string>();
                foreach (var kvp in args.Arguments)
                {
                    pairs.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
                }
                rawArgs = string.Join("&", pairs);
            }

            NotificationInvoked?.Invoke(this, rawArgs);
        };

        manager.NotificationInvoked += _nativeHandler;
        manager.Register();
    }

    public void Unregister()
    {
        var manager = AppNotificationManager.Default;
        if (manager != null)
        {
            if (_nativeHandler != null)
            {
                manager.NotificationInvoked -= _nativeHandler;
                _nativeHandler = null;
            }
            manager.Unregister();
        }
    }
}

/// <summary>
/// Hardened lifecycle manager governing AppNotificationManager registration, activation routing, and cleanup.
/// Strictly enforces single registration, atomic rollback on failure, and safe teardown.
/// </summary>
public sealed class AppNotificationRegistrationManager
{
    private readonly IAppNotificationManagerBridge _bridge;
    private readonly object _lock = new();
    private EventHandler<string>? _invokedHandler;
    private Action<NotificationActivationResult>? _activationCallback;

    public bool IsRegistered { get; private set; }
    public bool IsHandlerAttached { get; private set; }
    public IAppNotificationManagerBridge Bridge => _bridge;

    public AppNotificationRegistrationManager(IAppNotificationManagerBridge? bridge = null)
    {
        _bridge = bridge ?? new WindowsAppNotificationManagerBridge();
    }

    /// <summary>
    /// Initializes and registers AppNotificationManager with atomic rollback on failure.
    /// </summary>
    public bool Initialize(Action<NotificationActivationResult> activationCallback)
    {
        ArgumentNullException.ThrowIfNull(activationCallback);

        lock (_lock)
        {
            if (IsRegistered)
            {
                return true; // Already registered cleanly
            }

            _activationCallback = activationCallback;

            // Step 1: Prepare event handler
            _invokedHandler = (sender, rawArgs) =>
            {
                var result = NotificationActivationHandler.ParseQueryString(rawArgs);
                _activationCallback?.Invoke(result);
            };

            // Step 2: Attach handler before registration
            _bridge.NotificationInvoked += _invokedHandler;
            IsHandlerAttached = true;

            // Step 3: Attempt registration
            try
            {
                _bridge.Register();
                IsRegistered = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NOTIFICATIONS] AppNotificationManager.Register failed: {ex.Message}");

                // Rollback: safely detach handler and reset state
                if (_invokedHandler != null)
                {
                    _bridge.NotificationInvoked -= _invokedHandler;
                    _invokedHandler = null;
                }
                IsHandlerAttached = false;
                IsRegistered = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Cleanly unregisters AppNotificationManager and detaches event handlers.
    /// </summary>
    public bool Unregister()
    {
        lock (_lock)
        {
            if (!IsRegistered && !IsHandlerAttached)
            {
                return true; // No action needed
            }

            bool success = true;
            try
            {
                if (IsHandlerAttached && _invokedHandler != null)
                {
                    _bridge.NotificationInvoked -= _invokedHandler;
                    _invokedHandler = null;
                }

                _bridge.Unregister();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NOTIFICATIONS] AppNotificationManager.Unregister error: {ex.Message}");
                success = false;
            }
            finally
            {
                IsHandlerAttached = false;
                IsRegistered = false;
                _activationCallback = null;
            }

            return success;
        }
    }
}
