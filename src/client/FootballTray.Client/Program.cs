using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FootballTray.Client;

/// <summary>
/// Custom application entrypoint enforcing single-instance execution via Windows App SDK AppLifecycle
/// and named single-instance mutex (Policy 10.1 & Taskbar ticker guard).
/// </summary>
public static class Program
{
    private const string AppInstanceKey = "FootballTray.Primary";

    [STAThread]
    static async Task Main(string[] args)
    {
        // 0. Ensure working directory is always set to application base directory (prevents resource load failures)
        try
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;
        }
        catch { }

        // 1. Mandatory WinRT projection initialization for WinUI 3 / CsWinRT
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // 2. Named single-instance mutex check (Microsoft Store Policy 10.1 & Taskbar ticker guard)
        if (App.AcquireSingleInstanceMutex(out bool isNewInstance) && !isNewInstance)
        {
            // Secondary instance: signal running primary instance and exit with 0 immediately
            App.SignalExistingInstanceAndExit();

            try
            {
                var currentArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                var keyInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);
                if (!keyInstance.IsCurrent)
                {
                    _ = AllowSetForegroundWindow(keyInstance.ProcessId);
                    await keyInstance.RedirectActivationToAsync(currentArgs);
                }
            }
            catch
            {
                // AppInstance redirection fallback
            }

            // Exit secondary instance cleanly with code 0
            return;
        }

        // Start listening on the event handle in the primary instance
        App.StartSingleInstanceEventListener();

        // 3. Capture incoming activation arguments before registration
        var initialArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

        // 4. Atomically find or register the primary instance key for this session
        var primaryInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);

        if (!primaryInstance.IsCurrent)
        {
            // Fallback secondary detection
            try
            {
                _ = AllowSetForegroundWindow(primaryInstance.ProcessId);
                await primaryInstance.RedirectActivationToAsync(initialArgs);
            }
            catch { }
            return;
        }

        // PRIMARY INSTANCE:
        // Subscribe to redirected activation events before starting the XAML message pump
        primaryInstance.Activated += (sender, activatedArgs) =>
        {
            AppActivationCoordinator.OnActivated(activatedArgs);
        };

        // Hand initial activation to coordinator before entering Application.Start
        AppActivationCoordinator.OnActivated(initialArgs);

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
