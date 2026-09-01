using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// Coordinates detection and recovery when Windows Explorer restarts (via TaskbarCreated broadcast).
/// </summary>
public sealed class ExplorerRecovery : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<Task> _reconnectAction;
    private bool _isRecovering;
    private bool _isDisposed;

    public ExplorerRecovery(DispatcherQueue dispatcherQueue, Func<Task> reconnectAction)
    {
        _dispatcherQueue = dispatcherQueue;
        _reconnectAction = reconnectAction;
    }

    /// <summary>
    /// Invoked when TaskbarCreated message is received by the application.
    /// </summary>
    public void HandleTaskbarCreated()
    {
        if (_isDisposed) return;

        TaskbarLogger.Info("Explorer recovery sequence initiated due to TaskbarCreated event.");

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_isRecovering || _isDisposed) return;
            _isRecovering = true;

            try
            {
                // Wait briefly for Shell_TrayWnd to finish its initial window creation and layout
                await Task.Delay(300);

                int maxAttempts = 5;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (_isDisposed) break;

                    TaskbarLogger.Info($"Explorer recovery attempt {attempt}/{maxAttempts}...");

                    IntPtr taskbarHwnd = TaskbarInjection.FindPrimaryTaskbar();
                    if (taskbarHwnd != IntPtr.Zero)
                    {
                        await _reconnectAction();
                        TaskbarLogger.Info("Explorer recovery completed successfully.");
                        break;
                    }

                    await Task.Delay(500 * attempt);
                }
            }
            catch (Exception ex)
            {
                TaskbarLogger.Error("Exception occurred during Explorer recovery.", ex);
            }
            finally
            {
                _isRecovering = false;
            }
        });
    }

    public void Dispose()
    {
        _isDisposed = true;
    }
}
