using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using static FootballTray.Client.Taskbar.TaskbarNativeMethods;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// Monitors taskbar state changes (location, size, resolution) and triggers geometry recalculation.
/// </summary>
public sealed class TaskbarWatcher : IDisposable
{
    private readonly WinEventDelegate _winEventDelegate;
    private readonly DispatcherQueue _dispatcherQueue;
    private IntPtr _winEventHook = IntPtr.Zero;
    private IntPtr _targetTaskbarHwnd = IntPtr.Zero;
    private bool _isDisposed;
    private DateTime _lastTriggerTime = DateTime.MinValue;

    public bool IsWatching => _winEventHook != IntPtr.Zero;
    public IntPtr TargetTaskbarHwnd => _targetTaskbarHwnd;

    public event Action? TaskbarChanged;

    public TaskbarWatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _winEventDelegate = OnWinEvent;
    }

    /// <summary>
    /// Starts watching the specified taskbar HWND for location or layout changes.
    /// Safely unhooks any previous hook before attaching.
    /// </summary>
    public bool Start(IntPtr taskbarHwnd)
    {
        if (_isDisposed) return false;

        Stop();

        if (taskbarHwnd == IntPtr.Zero)
        {
            TaskbarLogger.Warn("[WATCHER] Cannot start TaskbarWatcher: taskbar HWND is zero.");
            return false;
        }

        _targetTaskbarHwnd = taskbarHwnd;

        try
        {
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_LOCATIONCHANGE,
                EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (_winEventHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                TaskbarLogger.Warn($"[WATCHER] SetWinEventHook failed with Win32Error={err}.");
                return false;
            }

            TaskbarLogger.Info($"[WATCHER] TaskbarWatcher started successfully (Hook=0x{_winEventHook.ToInt64():X}, TargetShellTray=0x{_targetTaskbarHwnd.ToInt64():X})");
            return true;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("[WATCHER] Failed to initialize TaskbarWatcher hook.", ex);
            return false;
        }
    }

    /// <summary>
    /// Stops watching taskbar events and cleans up the WinEvent hook.
    /// </summary>
    public void Stop()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            IntPtr hookToRelease = _winEventHook;
            _winEventHook = IntPtr.Zero;
            _targetTaskbarHwnd = IntPtr.Zero;
            UnhookWinEvent(hookToRelease);
            TaskbarLogger.Info($"[WATCHER] TaskbarWatcher stopped and unhooked (Hook=0x{hookToRelease.ToInt64():X}).");
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || _winEventHook == IntPtr.Zero) return;

        if (_targetTaskbarHwnd != IntPtr.Zero && (hwnd == _targetTaskbarHwnd || IsDescendantOrSame(hwnd, _targetTaskbarHwnd)))
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastTriggerTime).TotalMilliseconds < 50)
            {
                return;
            }
            _lastTriggerTime = now;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isDisposed && _winEventHook != IntPtr.Zero)
                {
                    TaskbarChanged?.Invoke();
                }
            });
        }
    }

    private static bool IsDescendantOrSame(IntPtr childHwnd, IntPtr parentHwnd)
    {
        if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return false;
        if (childHwnd == parentHwnd) return true;

        IntPtr curr = GetParent(childHwnd);
        int depth = 0;
        while (curr != IntPtr.Zero && depth < 5)
        {
            if (curr == parentHwnd) return true;
            curr = GetParent(curr);
            depth++;
        }
        return false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
