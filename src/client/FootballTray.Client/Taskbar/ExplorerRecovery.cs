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
    private readonly Func<Task<bool>> _reconnectAction;
    private bool _isRecovering;
    private bool _isDisposed;
    private DateTime _lastTaskbarCreatedTime = DateTime.MinValue;

    public bool IsRecovering => _isRecovering;

    public ExplorerRecovery(DispatcherQueue dispatcherQueue, Func<Task<bool>> reconnectAction)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _reconnectAction = reconnectAction ?? throw new ArgumentNullException(nameof(reconnectAction));
    }

    /// <summary>
    /// Invoked when TaskbarCreated message is caught by the persistent message receiver.
    /// </summary>
    public void HandleTaskbarCreated(IntPtr receiverHwnd, uint msgId)
    {
        if (_isDisposed) return;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastTaskbarCreatedTime).TotalMilliseconds < 500)
        {
            TaskbarLogger.Debug($"[RECOVERY] Dropping duplicate TaskbarCreated event within debounce interval (Receiver=0x{receiverHwnd.ToInt64():X}, Msg=0x{msgId:X})");
            return;
        }
        _lastTaskbarCreatedTime = now;

        TaskbarLogger.Info($"[RECOVERY] TaskbarCreated message (0x{msgId:X}) received on Persistent MessageReceiver HWND 0x{receiverHwnd.ToInt64():X}. Initiating recovery pipeline...");

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (_isRecovering || _isDisposed) return;
            _isRecovering = true;

            try
            {
                // Wait briefly for Shell_TrayWnd to finish its initial window tree creation and layout
                TaskbarLogger.Debug("[RECOVERY] Waiting 250ms for Explorer taskbar hierarchy stabilization...");
                await Task.Delay(250);

                int maxAttempts = 5;
                int[] backoffDelaysMs = [250, 500, 1000, 1500, 2000];

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (_isDisposed) break;

                    TaskbarLogger.Info($"[RECOVERY] Recovery attempt {attempt}/{maxAttempts} executing...");

                    bool success = await _reconnectAction();
                    if (success)
                    {
                        TaskbarLogger.Info($"[RECOVERY] Explorer recovery succeeded on attempt {attempt}/{maxAttempts}.");
                        break;
                    }

                    int delay = backoffDelaysMs[Math.Min(attempt - 1, backoffDelaysMs.Length - 1)];
                    TaskbarLogger.Warn($"[RECOVERY] Attempt {attempt} failed or Shell_TrayWnd was not ready. Backing off for {delay}ms...");
                    await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                TaskbarLogger.Error("[RECOVERY] Unhandled exception occurred during Explorer recovery.", ex);
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
