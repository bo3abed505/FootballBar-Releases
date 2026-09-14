using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FootballTray.Client;

/// <summary>
/// Custom application entrypoint enforcing single-instance execution via Windows App SDK AppLifecycle.
/// </summary>
public static class Program
{
    private const string AppInstanceKey = "FootballTray.Primary";

    [STAThread]
    static async Task Main(string[] args)
    {
        // 1. Mandatory WinRT projection initialization for WinUI 3 / CsWinRT
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // 2. Capture incoming activation arguments before registration
        var currentArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        // 3. Atomically find or register the primary instance key for this session
        var keyInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);

        if (!keyInstance.IsCurrent)
        {
            // SECONDARY INSTANCE:
            // Grant foreground activation permission to the primary process if possible.
            try
            {
                _ = AllowSetForegroundWindow(keyInstance.ProcessId);
            }
            catch
            {
                // Non-fatal if OS restricts or API throws
            }

            // Redirect activation arguments to the primary instance
            await keyInstance.RedirectActivationToAsync(currentArgs);

            // Exit secondary instance cleanly before initializing XAML, App, or any subsystem
            return;
        }

        // PRIMARY INSTANCE:
        // Subscribe to redirected activation events before starting the XAML message pump
        keyInstance.Activated += (sender, activatedArgs) =>
        {
            AppActivationCoordinator.OnActivated(activatedArgs);
        };

        // Hand initial activation to coordinator before entering Application.Start
        AppActivationCoordinator.OnActivated(currentArgs);

        // Reproduce the exact XAML-generated startup contract
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
