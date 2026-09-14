using System;
using System.Collections.Generic;
using Microsoft.Windows.AppLifecycle;

namespace FootballTray.Client;

/// <summary>
/// Generic thread-safe coordinator for buffering and dispatching activation payloads
/// in strict FIFO order during cold-start initialization and runtime redirection.
/// </summary>
internal sealed class ActivationCoordinator<T> where T : class
{
    private readonly object _syncLock = new();
    private readonly Queue<T> _pendingQueue = new();
    private Action<T>? _uiHandler;
    private bool _isUiRegistered;
    private bool _isDispatching;

    /// <summary>
    /// Enqueues an incoming activation payload. If the UI handler is already registered
    /// and no active drain loop is executing, the calling thread drains the queue.
    /// </summary>
    public void OnActivated(T payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        bool becomeDrainer = false;
        lock (_syncLock)
        {
            _pendingQueue.Enqueue(payload);

            if (_isUiRegistered && _uiHandler != null && !_isDispatching)
            {
                _isDispatching = true;
                becomeDrainer = true;
            }
        }

        if (becomeDrainer)
        {
            DrainPending();
        }
    }

    /// <summary>
    /// Registers the UI activation handler. If queued activations exist and no active
    /// drain loop is executing, the calling thread drains the queue in FIFO order.
    /// </summary>
    public void RegisterUiHandler(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        bool becomeDrainer = false;
        lock (_syncLock)
        {
            _uiHandler = handler;
            _isUiRegistered = true;

            if (_pendingQueue.Count > 0 && !_isDispatching)
            {
                _isDispatching = true;
                becomeDrainer = true;
            }
        }

        if (becomeDrainer)
        {
            DrainPending();
        }
    }

    /// <summary>
    /// Serialized drain loop. Dequeues and invokes payloads one by one outside the lock.
    /// If an exception occurs in a handler, _isDispatching is reset to false in finally
    /// so the coordinator never becomes wedged.
    /// </summary>
    private void DrainPending()
    {
        while (true)
        {
            Action<T> handler;
            T payload;

            lock (_syncLock)
            {
                if (!_isUiRegistered || _uiHandler == null || _pendingQueue.Count == 0)
                {
                    _isDispatching = false;
                    return;
                }

                handler = _uiHandler;
                payload = _pendingQueue.Dequeue();
            }

            bool succeeded = false;
            try
            {
                handler(payload);
                succeeded = true;
            }
            finally
            {
                if (!succeeded)
                {
                    lock (_syncLock)
                    {
                        _isDispatching = false;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resets all coordinator state: clears the pending queue, unregisters the UI handler,
    /// and clears dispatching status.
    /// </summary>
    public void Reset()
    {
        lock (_syncLock)
        {
            _uiHandler = null;
            _pendingQueue.Clear();
            _isUiRegistered = false;
            _isDispatching = false;
        }
    }

    /// <summary>
    /// Indicates whether one or more activations are currently queued waiting for dispatch.
    /// </summary>
    public bool HasPending
    {
        get
        {
            lock (_syncLock)
            {
                return _pendingQueue.Count > 0;
            }
        }
    }

    /// <summary>
    /// Indicates whether the UI handler has been registered.
    /// </summary>
    public bool IsUiRegistered
    {
        get
        {
            lock (_syncLock)
            {
                return _isUiRegistered;
            }
        }
    }

    /// <summary>
    /// Returns the number of currently queued activation payloads.
    /// </summary>
    internal int PendingCount
    {
        get
        {
            lock (_syncLock)
            {
                return _pendingQueue.Count;
            }
        }
    }

    /// <summary>
    /// Indicates whether an active drain loop is currently executing.
    /// </summary>
    internal bool IsDispatching
    {
        get
        {
            lock (_syncLock)
            {
                return _isDispatching;
            }
        }
    }
}

/// <summary>
/// Thread-safe coordinator for bridging single-instance activation redirection
/// to the UI layer without dropping activations during cold-start initialization.
/// </summary>
public static class AppActivationCoordinator
{
    private static readonly ActivationCoordinator<AppActivationArguments> _coordinator = new();

    /// <summary>
    /// Invoked when a redirected activation arrives from a secondary instance (or primary cold start).
    /// </summary>
    public static void OnActivated(AppActivationArguments args) => _coordinator.OnActivated(args);

    /// <summary>
    /// Registers the UI activation handler once the UI layer (MainWindow / DispatcherQueue) is ready.
    /// Any pending activation buffered during startup will be immediately flushed to this handler in FIFO order.
    /// </summary>
    public static void RegisterUiHandler(Action<AppActivationArguments> handler) => _coordinator.RegisterUiHandler(handler);

    /// <summary>
    /// Internal test helper to reset coordinator state during unit tests.
    /// </summary>
    internal static void ResetForTesting() => _coordinator.Reset();

    /// <summary>
    /// Indicates whether a pending activation is currently buffered waiting for UI registration.
    /// </summary>
    public static bool HasPendingActivation => _coordinator.HasPending;

    /// <summary>
    /// Indicates whether the UI handler has been registered.
    /// </summary>
    public static bool IsUiRegistered => _coordinator.IsUiRegistered;

    /// <summary>
    /// Creates an isolated instance of the underlying generic coordinator for test suites.
    /// </summary>
    internal static ActivationCoordinator<T> CreateCoordinatorForTesting<T>() where T : class => new();
}
