using System;
using System.Runtime.InteropServices;
using static FootballTray.Client.Taskbar.TaskbarNativeMethods;

namespace FootballTray.Client.Taskbar;

public enum TaskbarEdge : uint
{
    Left = ABE_LEFT,
    Top = ABE_TOP,
    Right = ABE_RIGHT,
    Bottom = ABE_BOTTOM
}

public sealed record TaskbarPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    uint Dpi,
    double Scale,
    TaskbarEdge Edge,
    RECT TaskbarRect,
    RECT? TrayNotifyRect);

/// <summary>
/// Calculates runtime geometry and positioning for the taskbar widget host.
/// </summary>
public static class TaskbarGeometry
{
    public const int DefaultBaseWidth = 140;
    public const int DefaultBaseHeight = 32;
    public const int DefaultBaseMargin = 6;

    /// <summary>
    /// Pure calculation of widget placement within the primary taskbar.
    /// </summary>
    public static bool TryCalculatePlacement(
        RECT taskbarRect,
        RECT? trayNotifyRect,
        uint edge,
        uint dpi,
        out TaskbarPlacement placement,
        int baseWidth = DefaultBaseWidth,
        int baseHeight = DefaultBaseHeight,
        int baseMargin = DefaultBaseMargin)
    {
        placement = null!;

        if (taskbarRect.Width <= 0 || taskbarRect.Height <= 0)
        {
            TaskbarLogger.Warn($"Invalid taskbar dimensions: {taskbarRect.Width}x{taskbarRect.Height}");
            return false;
        }

        uint effectiveDpi = dpi > 0 ? dpi : 96;
        double scale = effectiveDpi / 96.0;

        int scaledWidth = (int)Math.Round(baseWidth * scale);
        int scaledHeight = (int)Math.Round(baseHeight * scale);
        int scaledMargin = (int)Math.Round(baseMargin * scale);

        int taskbarWidth = taskbarRect.Width;
        int taskbarHeight = taskbarRect.Height;

        // Cap height to fit inside taskbar with vertical margin
        if (scaledHeight > taskbarHeight - (scaledMargin * 2))
        {
            scaledHeight = Math.Max(16, taskbarHeight - (scaledMargin * 2));
        }

        int widgetX;
        int widgetY = Math.Max(0, (taskbarHeight - scaledHeight) / 2);

        if (trayNotifyRect.HasValue && trayNotifyRect.Value.Width > 0)
        {
            int trayLeftInTaskbar = trayNotifyRect.Value.Left - taskbarRect.Left;
            widgetX = trayLeftInTaskbar - scaledWidth - scaledMargin;

            if (widgetX < 0)
            {
                TaskbarLogger.Warn($"Calculated widget X position is negative ({widgetX}) with TrayNotifyLeft={trayLeftInTaskbar}");
                return false;
            }
        }
        else
        {
            // Fallback when TrayNotifyWnd is not exposed: align relative to right edge
            widgetX = taskbarWidth - scaledWidth - (scaledMargin * 4);
            if (widgetX < 0)
            {
                TaskbarLogger.Warn($"Calculated widget X position without tray notify is negative ({widgetX})");
                return false;
            }
        }

        placement = new TaskbarPlacement(
            X: widgetX,
            Y: widgetY,
            Width: scaledWidth,
            Height: scaledHeight,
            Dpi: effectiveDpi,
            Scale: scale,
            Edge: (TaskbarEdge)edge,
            TaskbarRect: taskbarRect,
            TrayNotifyRect: trayNotifyRect);

        return true;
    }

    /// <summary>
    /// Queries the runtime geometry of the primary Windows 11 taskbar and determines the placement rectangle.
    /// </summary>
    public static bool TryGetPrimaryTaskbarPlacement(
        IntPtr shellTrayHwnd,
        out TaskbarPlacement placement,
        int baseWidth = DefaultBaseWidth,
        int baseHeight = DefaultBaseHeight,
        int baseMargin = DefaultBaseMargin)
    {
        placement = null!;

        if (shellTrayHwnd == IntPtr.Zero)
        {
            TaskbarLogger.Warn("Cannot get geometry: Shell_TrayWnd HWND is null/zero.");
            return false;
        }

        // 1. Get Taskbar dimensions and edge via SHAppBarMessage
        APPBARDATA abd = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = shellTrayHwnd
        };

        RECT taskbarRect;
        uint edge = ABE_BOTTOM;

        IntPtr res = SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
        if (res != IntPtr.Zero && abd.rc.Width > 0 && abd.rc.Height > 0)
        {
            taskbarRect = abd.rc;
            edge = abd.uEdge;
        }
        else
        {
            // Fallback to GetWindowRect
            if (!GetWindowRect(shellTrayHwnd, out taskbarRect) || taskbarRect.Width <= 0 || taskbarRect.Height <= 0)
            {
                TaskbarLogger.Error("Failed to determine Shell_TrayWnd bounds via GetWindowRect.");
                return false;
            }
        }

        // 2. Query TrayNotifyWnd (System Tray area)
        RECT? trayNotifyRect = null;
        IntPtr trayNotifyHwnd = FindWindowEx(shellTrayHwnd, IntPtr.Zero, TrayNotifyWndClass, null);
        if (trayNotifyHwnd != IntPtr.Zero && GetWindowRect(trayNotifyHwnd, out RECT notifyRect) && notifyRect.Width > 0)
        {
            trayNotifyRect = notifyRect;
        }
        else
        {
            TaskbarLogger.Debug("TrayNotifyWnd was not found or has 0 width, using right-aligned layout calculation.");
        }

        // 3. Query DPI
        uint dpi = GetDpiForWindow(shellTrayHwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        bool success = TryCalculatePlacement(taskbarRect, trayNotifyRect, edge, dpi, out placement, baseWidth, baseHeight, baseMargin);
        if (success)
        {
            TaskbarLogger.Debug($"Taskbar geometry calculated: Pos=({placement.X}, {placement.Y}), Size={placement.Width}x{placement.Height}, DPI={placement.Dpi} ({placement.Scale:P0}), Edge={placement.Edge}");
        }
        else
        {
            TaskbarLogger.Warn("Taskbar geometry calculation failed.");
        }

        return success;
    }
}
