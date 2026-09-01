using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Taskbar;
using static FootballTray.Client.Taskbar.TaskbarNativeMethods;

namespace FootballTray.Client.Tests;

[TestClass]
public class TaskbarGeometryTests
{
    [TestMethod]
    public void TryCalculatePlacement_Standard1080p_PlacesLeftOfTray()
    {
        // 1920x1080 display, 48px bottom taskbar, TrayNotifyWnd at (1600, 1032) to (1920, 1080)
        var taskbarRect = new RECT(0, 1032, 1920, 1080);
        var trayNotifyRect = new RECT(1600, 1032, 1920, 1080);

        bool success = TaskbarGeometry.TryCalculatePlacement(
            taskbarRect: taskbarRect,
            trayNotifyRect: trayNotifyRect,
            edge: ABE_BOTTOM,
            dpi: 96,
            placement: out TaskbarPlacement placement,
            baseWidth: 140,
            baseHeight: 32,
            baseMargin: 6);

        Assert.IsTrue(success);
        Assert.IsNotNull(placement);
        Assert.AreEqual(140, placement.Width);
        Assert.AreEqual(32, placement.Height);
        Assert.AreEqual(96u, placement.Dpi);
        Assert.AreEqual(1.0, placement.Scale);
        
        // Relative tray left = 1600 - 0 = 1600.
        // widgetX = 1600 - 140 - 6 = 1454.
        Assert.AreEqual(1454, placement.X);
        
        // Taskbar height = 48. widgetY = (48 - 32) / 2 = 8.
        Assert.AreEqual(8, placement.Y);
    }

    [TestMethod]
    public void TryCalculatePlacement_4KWith200PercentScale_ScalesCorrectly()
    {
        // 3840x2160 display, 96px bottom taskbar, TrayNotifyWnd at (3200, 2064) to (3840, 2160) at 192 DPI (200%)
        var taskbarRect = new RECT(0, 2064, 3840, 2160);
        var trayNotifyRect = new RECT(3200, 2064, 3840, 2160);

        bool success = TaskbarGeometry.TryCalculatePlacement(
            taskbarRect: taskbarRect,
            trayNotifyRect: trayNotifyRect,
            edge: ABE_BOTTOM,
            dpi: 192,
            placement: out TaskbarPlacement placement,
            baseWidth: 140,
            baseHeight: 32,
            baseMargin: 6);

        Assert.IsTrue(success);
        Assert.IsNotNull(placement);
        Assert.AreEqual(280, placement.Width); // 140 * 2
        Assert.AreEqual(64, placement.Height); // 32 * 2
        Assert.AreEqual(192u, placement.Dpi);
        Assert.AreEqual(2.0, placement.Scale);

        // Relative tray left = 3200.
        // scaledMargin = 12.
        // widgetX = 3200 - 280 - 12 = 2908.
        Assert.AreEqual(2908, placement.X);

        // Taskbar height = 96. widgetY = (96 - 64) / 2 = 16.
        Assert.AreEqual(16, placement.Y);
    }

    [TestMethod]
    public void TryCalculatePlacement_WithoutTrayNotify_FallsBackToRightEdge()
    {
        var taskbarRect = new RECT(0, 1032, 1920, 1080);

        bool success = TaskbarGeometry.TryCalculatePlacement(
            taskbarRect: taskbarRect,
            trayNotifyRect: null,
            edge: ABE_BOTTOM,
            dpi: 96,
            placement: out TaskbarPlacement placement,
            baseWidth: 140,
            baseHeight: 32,
            baseMargin: 6);

        Assert.IsTrue(success);
        Assert.IsNotNull(placement);
        // Fallback: 1920 - 140 - (6 * 4) = 1756.
        Assert.AreEqual(1756, placement.X);
        Assert.AreEqual(8, placement.Y);
    }

    [TestMethod]
    public void TryCalculatePlacement_ZeroDimensions_ReturnsFalse()
    {
        var taskbarRect = new RECT(0, 0, 0, 0);

        bool success = TaskbarGeometry.TryCalculatePlacement(
            taskbarRect: taskbarRect,
            trayNotifyRect: null,
            edge: ABE_BOTTOM,
            dpi: 96,
            placement: out TaskbarPlacement placement);

        Assert.IsFalse(success);
        Assert.IsNull(placement);
    }

    [TestMethod]
    public void TryCalculatePlacement_InvalidNegativePlacement_ReturnsFalse()
    {
        // Tray left is only 50px from left edge, but widget needs 140px + 6px margin
        var taskbarRect = new RECT(0, 0, 1920, 48);
        var trayNotifyRect = new RECT(50, 0, 100, 48);

        bool success = TaskbarGeometry.TryCalculatePlacement(
            taskbarRect: taskbarRect,
            trayNotifyRect: trayNotifyRect,
            edge: ABE_BOTTOM,
            dpi: 96,
            placement: out TaskbarPlacement placement);

        Assert.IsFalse(success);
        Assert.IsNull(placement);
    }
}
