using System;
using System.Runtime.InteropServices;
using static FootballTray.Client.Taskbar.TaskbarNativeMethods;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// Handles native Win32 window reparenting and style management for docking into the Windows 11 taskbar.
/// </summary>
public static class TaskbarInjection
{
    internal static Func<IntPtr>? FindPrimaryTaskbarHook { get; set; }
    internal static Func<IntPtr, IntPtr, int, int, int, int, uint, bool>? SetWindowPosHook { get; set; }

    /// <summary>
    /// Finds the primary Windows 11 taskbar window handle (Shell_TrayWnd).
    /// </summary>
    public static IntPtr FindPrimaryTaskbar()
    {
        if (FindPrimaryTaskbarHook != null)
        {
            return FindPrimaryTaskbarHook();
        }

        IntPtr hwnd = FindWindow(ShellTrayWndClass, null);
        if (hwnd == IntPtr.Zero)
        {
            TaskbarLogger.Warn($"Failed to find primary taskbar window '{ShellTrayWndClass}'.");
        }
        else
        {
            TaskbarLogger.Debug($"Discovered primary taskbar window handle: 0x{hwnd.ToInt64():X}");
        }
        return hwnd;
    }

    /// <summary>
    /// Attaches the native host HWND as a child of the taskbar HWND with appropriate Win32 styles.
    /// </summary>
    public static bool Attach(IntPtr hostHwnd, IntPtr taskbarHwnd, TaskbarPlacement placement)
    {
        if (hostHwnd == IntPtr.Zero)
        {
            TaskbarLogger.Error("Cannot attach: host HWND is zero.");
            return false;
        }

        if (taskbarHwnd == IntPtr.Zero)
        {
            TaskbarLogger.Error("Cannot attach: taskbar HWND is zero.");
            return false;
        }

        try
        {
            // 1. Reparent host window to Shell_TrayWnd
            IntPtr prevParent = SetParent(hostHwnd, taskbarHwnd);
            TaskbarLogger.Debug($"SetParent: host=0x{hostHwnd.ToInt64():X} to taskbar=0x{taskbarHwnd.ToInt64():X} (previous parent=0x{prevParent.ToInt64():X})");

            // 2. Set child styles
            int childStyle = WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | WS_VISIBLE;
            SetWindowLongPtr(hostHwnd, GWL_STYLE, new IntPtr(childStyle));

            // 3. Set extended styles (ToolWindow, NoActivate)
            int childExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hostHwnd, GWL_EXSTYLE, new IntPtr(childExStyle));

            // 4. Position, show host, and assert HWND_TOP sibling Z-order
            bool posResult = SetWindowPosHook != null
                ? SetWindowPosHook(hostHwnd, HWND_TOP, placement.X, placement.Y, placement.Width, placement.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED)
                : SetWindowPos(
                    hostHwnd,
                    HWND_TOP,
                    placement.X,
                    placement.Y,
                    placement.Width,
                    placement.Height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

            if (!posResult)
            {
                int error = Marshal.GetLastWin32Error();
                TaskbarLogger.Warn($"SetWindowPos returned false during attach. Win32Error={error}");
            }

            TaskbarLogger.Info($"Successfully attached host 0x{hostHwnd.ToInt64():X} to Shell_TrayWnd at ({placement.X},{placement.Y}) [{placement.Width}x{placement.Height}]");
            return true;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Exception occurred during taskbar attachment.", ex);
            return false;
        }
    }

    /// <summary>
    /// Detaches the host HWND from the taskbar, resetting parent to Desktop and hiding the window.
    /// </summary>
    public static void Detach(IntPtr hostHwnd)
    {
        if (hostHwnd == IntPtr.Zero) return;

        try
        {
            ShowWindow(hostHwnd, 0 /* SW_HIDE */);
            SetParent(hostHwnd, IntPtr.Zero);

            int popupStyle = WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
            SetWindowLongPtr(hostHwnd, GWL_STYLE, new IntPtr(popupStyle));

            TaskbarLogger.Info($"Detached host 0x{hostHwnd.ToInt64():X} from taskbar.");
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Exception occurred during taskbar detach.", ex);
        }
    }

    /// <summary>
    /// Updates the position and size of the attached host window.
    /// </summary>
    public static bool UpdatePosition(IntPtr hostHwnd, TaskbarPlacement placement)
    {
        if (hostHwnd == IntPtr.Zero) return false;

        bool result = SetWindowPosHook != null
            ? SetWindowPosHook(hostHwnd, HWND_TOP, placement.X, placement.Y, placement.Width, placement.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW)
            : SetWindowPos(
                hostHwnd,
                HWND_TOP,
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);

        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            TaskbarLogger.Warn($"SetWindowPos failed during position update. Win32Error={error}");
        }

        return result;
    }
}
