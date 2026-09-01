using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Taskbar;
using static FootballTray.Client.Taskbar.TaskbarNativeMethods;

namespace FootballTray.Client.Tests;

[TestClass]
public class TaskbarWatcherAndRecoveryTests
{
    [TestMethod]
    public void TaskbarInjection_Attach_WithZeroHwnd_ReturnsFalse()
    {
        var placement = new TaskbarPlacement(
            X: 100,
            Y: 10,
            Width: 140,
            Height: 32,
            Dpi: 96,
            Scale: 1.0,
            Edge: TaskbarEdge.Bottom,
            TaskbarRect: new RECT(0, 1032, 1920, 1080),
            TrayNotifyRect: null);

        bool resultWithZeroHost = TaskbarInjection.Attach(IntPtr.Zero, new IntPtr(0x1234), placement);
        Assert.IsFalse(resultWithZeroHost);

        bool resultWithZeroTray = TaskbarInjection.Attach(new IntPtr(0x5678), IntPtr.Zero, placement);
        Assert.IsFalse(resultWithZeroTray);
    }

    [TestMethod]
    public void TaskbarInjection_Detach_WithZeroHwnd_DoesNotThrow()
    {
        TaskbarInjection.Detach(IntPtr.Zero);
    }

    [TestMethod]
    public void TaskbarInjection_UpdatePosition_WithZeroHwnd_ReturnsFalse()
    {
        var placement = new TaskbarPlacement(
            X: 100,
            Y: 10,
            Width: 140,
            Height: 32,
            Dpi: 96,
            Scale: 1.0,
            Edge: TaskbarEdge.Bottom,
            TaskbarRect: new RECT(0, 1032, 1920, 1080),
            TrayNotifyRect: null);

        bool result = TaskbarInjection.UpdatePosition(IntPtr.Zero, placement);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TaskbarGeometry_AfterRecreation_CalculatesCorrectPosition()
    {
        // Simulate taskbar recreated on 1440p monitor (2560x1440 at 125% DPI scale = 120 DPI)
        var newTrayRect = new RECT(0, 1380, 2560, 1440);
        var newNotifyRect = new RECT(2200, 1380, 2560, 1440);

        bool success = TaskbarGeometry.TryCalculatePlacement(
            taskbarRect: newTrayRect,
            trayNotifyRect: newNotifyRect,
            edge: ABE_BOTTOM,
            dpi: 120,
            placement: out TaskbarPlacement placement,
            baseWidth: 140,
            baseHeight: 32,
            baseMargin: 6);

        Assert.IsTrue(success);
        Assert.IsNotNull(placement);
        Assert.AreEqual(175, placement.Width); // 140 * 1.25
        Assert.AreEqual(40, placement.Height); // 32 * 1.25
        Assert.AreEqual(120u, placement.Dpi);
        Assert.AreEqual(1.25, placement.Scale);

        // Relative tray left = 2200.
        // Scaled margin = 6 * 1.25 = 8.
        // widgetX = 2200 - 175 - 8 = 2017.
        Assert.AreEqual(2017, placement.X);
    }
}
