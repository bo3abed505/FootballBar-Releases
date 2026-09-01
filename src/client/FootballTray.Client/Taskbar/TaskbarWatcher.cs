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

    public event Action? TaskbarChanged;

    public TaskbarWatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _winEventDelegate = OnWinEvent;
    }

    /// <summary>
    /// Starts watching the specified taskbar HWND for location or layout changes.
    /// </summary>
    public bool Start(IntPtr taskbarHwnd)
    {
        _targetTaskbarHwnd = taskbarHwnd;

        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        try
        {
            // Hook EVENT_OBJECT_LOCATIONCHANGE for all processes
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
                TaskbarLogger.Warn($"SetWinEventHook failed with Win32Error={err}. Falling back to message notifications.");
                return false;
            }

            TaskbarLogger.Info($"TaskbarWatcher hooked successfully (Hook=0x{_winEventHook.ToInt64():X})");
            return true;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Failed to initialize TaskbarWatcher hook.", ex);
            return false;
        }
    }

    /// <summary>
    /// Stops watching taskbar events.
    /// </summary>
    public void Stop()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
            TaskbarLogger.Debug("TaskbarWatcher unhooked.");
        }
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed) return;

        // Check if event relates to Shell_TrayWnd or TrayNotifyWnd or child components
        if (_targetTaskbarHwnd != IntPtr.Zero && (hwnd == _targetTaskbarHwnd || IsDescendantOrSame(hwnd, _targetTaskbarHwnd)))
        {
            // Debounce event within 50ms to prevent thrashing during smooth resizing/animations
            DateTime now = DateTime.UtcNow;
            if ((now - _lastTriggerTime).TotalMilliseconds < 50)
            {
                return;
            }
            _lastTriggerTime = now;

            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!_isDisposed)
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

    public void TriggerManualRecalculation()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
            {
                TaskbarChanged?.Invoke();
            }
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
    }
}
