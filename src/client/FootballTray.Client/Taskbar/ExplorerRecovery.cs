using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// Coordinates detection and single-flight recovery when Windows Explorer restarts or taskbar host is destroyed.
/// Provides bounded retry with exponential backoff, coalesced triggers, and clean cancellation on shutdown.
/// </summary>
public sealed class ExplorerRecovery : IDisposable
{
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly Func<Task<bool>> _reconnectAction;
    private readonly Func<Action, bool>? _customDispatcher;
    private readonly int[] _backoffDelaysMs;
    private readonly object _gate = new();

    private bool _isRecovering;
    private bool _pendingRecoveryRequested;
    private bool _isDisposed;
    private CancellationTokenSource? _overallCts;
    private CancellationTokenSource? _stepDelayCts;

    private static readonly int[] DefaultBackoffDelaysMs = [250, 500, 1000, 1500, 2000, 3000, 4000, 5000, 5000, 5000];

    public bool IsRecovering
    {
        get
        {
            lock (_gate)
            {
                return _isRecovering;
            }
        }
    }

    public event Action<bool>? RecoveryCompleted;

    public ExplorerRecovery(
        DispatcherQueue? dispatcherQueue,
        Func<Task<bool>> reconnectAction,
        Action<Action>? customDispatcher = null,
        int[]? customBackoffDelaysMs = null)
        : this(
            dispatcherQueue,
            reconnectAction,
            customDispatcher != null ? (Action a) => { customDispatcher(a); return true; } : (Func<Action, bool>?)null,
            customBackoffDelaysMs)
    {
    }

    public ExplorerRecovery(
        DispatcherQueue? dispatcherQueue,
        Func<Task<bool>> reconnectAction,
        Func<Action, bool>? customDispatcherWithResult,
        int[]? customBackoffDelaysMs = null)
    {
        _dispatcherQueue = dispatcherQueue;
        _reconnectAction = reconnectAction ?? throw new ArgumentNullException(nameof(reconnectAction));
        _customDispatcher = customDispatcherWithResult;
        _backoffDelaysMs = customBackoffDelaysMs ?? DefaultBackoffDelaysMs;
    }

    /// <summary>
    /// Enqueues a recovery request. Coalesces concurrent or rapid triggers, ensures single-flight execution,
    /// and interrupts active backoff delays if a new signal arrives while recovering.
    /// </summary>
    public void RequestRecovery(string reason)
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                TaskbarLogger.Debug($"[RECOVERY] Ignoring recovery request ('{reason}') because ExplorerRecovery is disposed.");
                return;
            }

            if (_isRecovering)
            {
                _pendingRecoveryRequested = true;
                TaskbarLogger.Info($"[RECOVERY] Request arrived while recovery is active (Reason: '{reason}'). Marked pending and waking active delay.");
                try
                {
                    _stepDelayCts?.Cancel();
                }
                catch (ObjectDisposedException) { }
                return;
            }

            _isRecovering = true;
            _pendingRecoveryRequested = false;
            _overallCts = new CancellationTokenSource();
            TaskbarLogger.Info($"[RECOVERY] Enqueuing recovery pipeline (Trigger: '{reason}').");
        }

        bool enqueued = EnqueueWork(async () =>
        {
            try
            {
                await RunRecoveryLoopAsync();
            }
            catch (Exception ex)
            {
                TaskbarLogger.Error("[RECOVERY] Top-level unhandled exception in async recovery dispatch callback.", ex);
            }
        });

        if (!enqueued)
        {
            lock (_gate)
            {
                _isRecovering = false;
                _pendingRecoveryRequested = false;
                _overallCts?.Dispose();
                _overallCts = null;
            }
            TaskbarLogger.Warn($"[RECOVERY] Dispatcher failed to enqueue recovery work (Trigger: '{reason}'). Resetting recovery state.");
        }
    }

    /// <summary>
    /// Backward-compatible entrypoint for TaskbarCreated broadcast from persistent message receiver.
    /// </summary>
    public void HandleTaskbarCreated(IntPtr receiverHwnd, uint msgId)
    {
        RequestRecovery($"TaskbarCreated(Receiver=0x{receiverHwnd.ToInt64():X},Msg=0x{msgId:X})");
    }

    private bool EnqueueWork(Action action)
    {
        try
        {
            if (_customDispatcher != null)
            {
                return _customDispatcher(action);
            }
            if (_dispatcherQueue != null)
            {
                return _dispatcherQueue.TryEnqueue(() => action());
            }
            Task.Run(action);
            return true;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Warn($"[RECOVERY] Exception during recovery work dispatch: {ex.Message}");
            return false;
        }
    }

    private async Task RunRecoveryLoopAsync()
    {
        CancellationToken overallToken;
        lock (_gate)
        {
            if (_isDisposed || _overallCts == null)
            {
                _isRecovering = false;
                return;
            }
            overallToken = _overallCts.Token;
        }

        bool finalSuccess = false;
        try
        {
            while (!overallToken.IsCancellationRequested && !_isDisposed)
            {
                lock (_gate)
                {
                    _pendingRecoveryRequested = false;
                }

                finalSuccess = await ExecuteRetrySequenceAsync(overallToken);
                if (finalSuccess)
                {
                    TaskbarLogger.Info("[RECOVERY] Recovery pipeline succeeded; host reattached.");
                }
                else
                {
                    TaskbarLogger.Warn("[RECOVERY] Recovery pipeline concluded without reattaching (taskbar unavailable or retries exhausted).");
                }

                lock (_gate)
                {
                    if (!_pendingRecoveryRequested || overallToken.IsCancellationRequested || _isDisposed || finalSuccess)
                    {
                        _isRecovering = false;
                        _overallCts?.Dispose();
                        _overallCts = null;
                        break;
                    }

                    TaskbarLogger.Info("[RECOVERY] Processing pending recovery request queued during previous pass.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            TaskbarLogger.Info("[RECOVERY] Recovery pipeline cancelled.");
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("[RECOVERY] Unexpected exception in recovery pipeline.", ex);
        }
        finally
        {
            lock (_gate)
            {
                _isRecovering = false;
                _stepDelayCts?.Dispose();
                _stepDelayCts = null;
                _overallCts?.Dispose();
                _overallCts = null;
            }

            try
            {
                RecoveryCompleted?.Invoke(finalSuccess);
            }
            catch (Exception ex)
            {
                TaskbarLogger.Error("[RECOVERY] Error invoking RecoveryCompleted event.", ex);
            }
        }
    }

    private async Task<bool> ExecuteRetrySequenceAsync(CancellationToken cancellationToken)
    {
        int maxAttempts = _backoffDelaysMs.Length;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested || _isDisposed)
            {
                return false;
            }

            TaskbarLogger.Info($"[RECOVERY] Attempt {attempt}/{maxAttempts} executing...");

            bool success = false;
            try
            {
                success = await _reconnectAction();
            }
            catch (Exception ex)
            {
                TaskbarLogger.Error($"[RECOVERY] Attempt {attempt} threw exception.", ex);
            }

            if (success)
            {
                TaskbarLogger.Info($"[RECOVERY] Explorer recovery succeeded on attempt {attempt}/{maxAttempts}.");
                return true;
            }

            if (attempt == maxAttempts)
            {
                TaskbarLogger.Warn($"[RECOVERY] Reached maximum attempts ({maxAttempts}). Bounded recovery window exhausted.");
                break;
            }

            int delay = _backoffDelaysMs[attempt - 1];
            TaskbarLogger.Debug($"[RECOVERY] Attempt {attempt} failed or taskbar not ready. Backing off for {delay}ms (or until next signal)...");

            CancellationTokenSource stepCts;
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested || _isDisposed)
                {
                    return false;
                }
                _stepDelayCts?.Dispose();
                _stepDelayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stepCts = _stepDelayCts;
            }

            try
            {
                await Task.Delay(delay, stepCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested || _isDisposed)
                {
                    return false;
                }
                TaskbarLogger.Info("[RECOVERY] Backoff delay interrupted by incoming recovery request; advancing to next attempt immediately.");
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isRecovering = false;
            _pendingRecoveryRequested = false;

            try
            {
                _stepDelayCts?.Cancel();
                _stepDelayCts?.Dispose();
                _stepDelayCts = null;
            }
            catch { }

            try
            {
                _overallCts?.Cancel();
                _overallCts?.Dispose();
                _overallCts = null;
            }
            catch { }
        }
    }
}
