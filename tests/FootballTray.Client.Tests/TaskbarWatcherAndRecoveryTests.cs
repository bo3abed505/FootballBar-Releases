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

    [TestMethod]
    public void ActiveHostNcDestroy_RequestsRecovery()
    {
        string? requestedReason = null;
        var host = new TaskbarHost();
        var testHwnd = new IntPtr(0x12345);
        host.SetHostHwndForTesting(testHwnd);
        host.SetIsAttachedForTesting(true);

        bool attachmentChangedFired = false;
        bool attachmentValue = true;
        host.AttachmentChanged += val =>
        {
            attachmentChangedFired = true;
            attachmentValue = val;
        };

        var testRecovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                requestedReason = "reconnectAction_called";
                return Task.FromResult(true);
            },
            customDispatcher: act => act(),
            customBackoffDelaysMs: [10]);
        host.SetRecoveryForTesting(testRecovery);

        host.HandleHostMessage(testHwnd, WM_NCDESTROY, IntPtr.Zero, IntPtr.Zero);

        Assert.AreEqual(IntPtr.Zero, host.HostHwnd);
        Assert.IsFalse(host.IsAttached);
        Assert.IsTrue(attachmentChangedFired);
        Assert.IsFalse(attachmentValue);
        Assert.IsTrue(testRecovery.IsRecovering || requestedReason != null);
    }

    [TestMethod]
    public void HostNcDestroy_DuringDispose_DoesNotRecover()
    {
        int reconnectCalls = 0;
        var host = new TaskbarHost();
        var testHwnd = new IntPtr(0x12345);
        host.SetHostHwndForTesting(testHwnd);
        host.SetIsAttachedForTesting(true);

        var testRecovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                Interlocked.Increment(ref reconnectCalls);
                return Task.FromResult(true);
            },
            customDispatcher: act => act(),
            customBackoffDelaysMs: [10]);
        host.SetRecoveryForTesting(testRecovery);

        host.Dispose();

        host.HandleHostMessage(testHwnd, WM_NCDESTROY, IntPtr.Zero, IntPtr.Zero);

        Assert.AreEqual(0, reconnectCalls);
        Assert.IsFalse(testRecovery.IsRecovering);
    }

    [TestMethod]
    public void HostNcDestroy_DuringIntentionalRecoveryTeardown_DoesNotSpawnParallelRecovery()
    {
        int reconnectCalls = 0;
        var host = new TaskbarHost();
        var testHwnd = new IntPtr(0x12345);
        host.SetHostHwndForTesting(testHwnd);
        host.SetIsAttachedForTesting(true);
        host.SetIntentionalTeardownForTesting(true);

        var testRecovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                Interlocked.Increment(ref reconnectCalls);
                return Task.FromResult(true);
            },
            customDispatcher: act => act(),
            customBackoffDelaysMs: [10]);
        host.SetRecoveryForTesting(testRecovery);

        host.HandleHostMessage(testHwnd, WM_NCDESTROY, IntPtr.Zero, IntPtr.Zero);

        Assert.AreEqual(0, reconnectCalls);
        Assert.IsFalse(testRecovery.IsRecovering);
    }

    [TestMethod]
    public async Task MultipleRecoverySignals_AreSingleFlight()
    {
        int maxConcurrent = 0;
        int currentConcurrent = 0;
        int totalInvocations = 0;

        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: async () =>
            {
                int cur = Interlocked.Increment(ref currentConcurrent);
                lock (this)
                {
                    if (cur > maxConcurrent) maxConcurrent = cur;
                }
                Interlocked.Increment(ref totalInvocations);
                await Task.Delay(25);
                Interlocked.Decrement(ref currentConcurrent);
                return true;
            },
            customDispatcher: act => Task.Run(act),
            customBackoffDelaysMs: [10, 20]);

        var t1 = Task.Run(() => recovery.RequestRecovery("HostDestroyed"));
        var t2 = Task.Run(() => recovery.RequestRecovery("TaskbarCreated"));
        var t3 = Task.Run(() => recovery.RequestRecovery("WatcherDestroyed"));

        await Task.WhenAll(t1, t2, t3);

        var tcs = new TaskCompletionSource<bool>();
        recovery.RecoveryCompleted += success => tcs.TrySetResult(success);

        if (!recovery.IsRecovering && totalInvocations > 0)
        {
            tcs.TrySetResult(true);
        }
        else
        {
            await Task.WhenAny(tcs.Task, Task.Delay(1000));
        }

        Assert.AreEqual(1, maxConcurrent, "Recovery executions must never run concurrently (single-flight guarantee).");
        Assert.IsTrue(totalInvocations >= 1);
    }

    [TestMethod]
    public async Task Recovery_TaskbarInitiallyMissing_RetriesUntilAvailable()
    {
        int attempts = 0;
        var tcs = new TaskCompletionSource<bool>();

        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            },
            customDispatcher: act => Task.Run(act),
            customBackoffDelaysMs: [10, 10, 10, 10]);

        recovery.RecoveryCompleted += success => tcs.TrySetResult(success);
        recovery.RequestRecovery("TestRetry");

        bool completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        Assert.IsTrue(completed, "Recovery did not complete in expected time.");
        Assert.IsTrue(await tcs.Task, "Recovery should succeed on attempt 3.");
        Assert.AreEqual(3, attempts, "Expected exactly 3 attempts before success.");
    }

    [TestMethod]
    public async Task Recovery_TaskbarNeverAppears_StopsAfterBoundedWindow()
    {
        int attempts = 0;
        var tcs = new TaskCompletionSource<bool>();

        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                attempts++;
                return Task.FromResult(false);
            },
            customDispatcher: act => Task.Run(act),
            customBackoffDelaysMs: [5, 5, 5]);

        recovery.RecoveryCompleted += success => tcs.TrySetResult(success);
        recovery.RequestRecovery("TestNeverAppears");

        bool completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        Assert.IsTrue(completed, "Recovery did not stop after bounded window.");
        Assert.IsFalse(await tcs.Task, "Recovery should return false when window exhausted.");
        Assert.AreEqual(3, attempts, "Expected attempts to stop after bounded backoff array exhausted.");
        Assert.IsFalse(recovery.IsRecovering, "IsRecovering must be false after termination.");
    }

    [TestMethod]
    public async Task RepeatedRecoveryCycle_DoesNotCreateDuplicateHost()
    {
        int hostCreationCount = 0;
        int activeHostCount = 0;

        Func<Task<bool>> reconnectSim = () =>
        {
            if (activeHostCount > 0)
            {
                activeHostCount--;
            }
            activeHostCount++;
            hostCreationCount++;
            return Task.FromResult(true);
        };

        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: reconnectSim,
            customDispatcher: act => Task.Run(act),
            customBackoffDelaysMs: [5]);

        var tcs1 = new TaskCompletionSource<bool>();
        Action<bool>? h1 = null;
        h1 = s => { recovery.RecoveryCompleted -= h1; tcs1.TrySetResult(s); };
        recovery.RecoveryCompleted += h1;
        recovery.RequestRecovery("Cycle1");
        await tcs1.Task;
        Assert.AreEqual(1, activeHostCount);

        var tcs2 = new TaskCompletionSource<bool>();
        Action<bool>? h2 = null;
        h2 = s => { recovery.RecoveryCompleted -= h2; tcs2.TrySetResult(s); };
        recovery.RecoveryCompleted += h2;
        recovery.RequestRecovery("Cycle2");
        await tcs2.Task;

        Assert.AreEqual(1, activeHostCount, "Only exactly 1 active host must exist after repeated recovery.");
        Assert.AreEqual(2, hostCreationCount, "Expected exactly 2 sequential host creations across 2 cycles.");
    }

    [TestMethod]
    public void TaskbarCreated_UsesSameRecoveryCoordinator()
    {
        bool recoveryCalled = false;
        var host = new TaskbarHost();
        var testReceiverHwnd = new IntPtr(0x5555);

        var testRecovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                recoveryCalled = true;
                return Task.FromResult(true);
            },
            customDispatcher: act => act(),
            customBackoffDelaysMs: [10]);
        host.SetRecoveryForTesting(testRecovery);

        uint msgId = RegisterWindowMessage("TaskbarCreated");
        host.HandleReceiverMessage(testReceiverHwnd, msgId, IntPtr.Zero, IntPtr.Zero);

        Assert.IsTrue(testRecovery.IsRecovering || recoveryCalled);
    }

    [TestMethod]
    public async Task Shutdown_CancelsPendingRecovery()
    {
        var startedTcs = new TaskCompletionSource();
        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                startedTcs.TrySetResult();
                return Task.FromResult(false);
            },
            customDispatcher: act => Task.Run(act),
            customBackoffDelaysMs: [5000, 5000]);

        recovery.RequestRecovery("ShutdownTest");
        await startedTcs.Task;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        recovery.Dispose();
        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds < 1000, $"Disposal took too long: {sw.ElapsedMilliseconds}ms");
        Assert.IsFalse(recovery.IsRecovering);
    }

    [TestMethod]
    public void DiagnosticState_UnexpectedHostDeathAndRecovery_TransitionsCorrectly()
    {
        var diag = new FootballTray.Client.Diagnostics.DiagnosticsService();
        var host = new TaskbarHost();
        var testHwnd = new IntPtr(0x7777);
        host.SetHostHwndForTesting(testHwnd);
        host.SetIsAttachedForTesting(true);

        host.AttachmentChanged += attached =>
        {
            diag.RecordTaskbarStatus(attached ? "Attached" : "Detached (Recovering)");
        };

        // Initially attached
        diag.RecordTaskbarStatus("Attached");
        var snap1 = diag.GetCurrentSnapshot();
        Assert.AreEqual("Attached", snap1.TaskbarHostStatus);
        Assert.AreEqual(FootballTray.Client.Diagnostics.ApplicationHealthState.Healthy, snap1.HealthState);

        // Host unexpectedly dies
        host.HandleHostMessage(testHwnd, WM_NCDESTROY, IntPtr.Zero, IntPtr.Zero);

        var snap2 = diag.GetCurrentSnapshot();
        Assert.AreEqual("Detached (Recovering)", snap2.TaskbarHostStatus);
        Assert.AreEqual(FootballTray.Client.Diagnostics.ApplicationHealthState.Recovering, snap2.HealthState);

        // Recovery succeeds -> attachment restored
        host.SetIsAttachedForTesting(true);
        diag.RecordTaskbarStatus("Attached");
        var snap3 = diag.GetCurrentSnapshot();
        Assert.AreEqual("Attached", snap3.TaskbarHostStatus);
        Assert.AreEqual(FootballTray.Client.Diagnostics.ApplicationHealthState.Healthy, snap3.HealthState);
    }

    [TestMethod]
    public async Task Recovery_DispatchEnqueueFailure_DoesNotStrandSingleFlightState()
    {
        bool shouldFailDispatch = true;
        int executedRecoveryPasses = 0;
        var tcs = new TaskCompletionSource<bool>();

        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                Interlocked.Increment(ref executedRecoveryPasses);
                return Task.FromResult(true);
            },
            customDispatcherWithResult: (Action act) =>
            {
                if (shouldFailDispatch)
                {
                    return false; // Simulate TryEnqueue returning false
                }
                Task.Run(act);
                return true;
            },
            customBackoffDelaysMs: [5]);

        recovery.RecoveryCompleted += s => tcs.TrySetResult(s);

        // 1. First attempt fails to enqueue
        recovery.RequestRecovery("SimulatedFailedDispatch");

        // 2. State must NOT be stranded in IsRecovering == true
        Assert.IsFalse(recovery.IsRecovering, "Coordinator must not remain stranded in IsRecovering=true after enqueue failure.");
        Assert.AreEqual(0, executedRecoveryPasses, "No reconnectAction should have run on failed dispatch.");

        // 3. Subsequent recovery request succeeds when dispatch succeeds
        shouldFailDispatch = false;
        recovery.RequestRecovery("SubsequentValidDispatch");

        bool completed = await Task.WhenAny(tcs.Task, Task.Delay(1000)) == tcs.Task;
        Assert.IsTrue(completed, "Subsequent recovery did not complete in time.");
        Assert.IsTrue(await tcs.Task, "Subsequent recovery should succeed.");
        Assert.AreEqual(1, executedRecoveryPasses, "Expected exactly 1 execution pass after subsequent dispatch.");
        Assert.IsFalse(recovery.IsRecovering, "IsRecovering must be false after completion.");
    }

    [TestMethod]
    public async Task Recovery_UnhandledExceptionInDispatch_SafelyResetsState()
    {
        int attempts = 0;
        var tcs = new TaskCompletionSource<bool>();

        var recovery = new ExplorerRecovery(
            dispatcherQueue: null,
            reconnectAction: () =>
            {
                attempts++;
                throw new InvalidOperationException("Simulated unexpected exception during reconnect");
            },
            customDispatcher: act => Task.Run(act),
            customBackoffDelaysMs: [5]);

        recovery.RecoveryCompleted += s => tcs.TrySetResult(s);
        recovery.RequestRecovery("ExceptionSafetyTest");

        bool completed = await Task.WhenAny(tcs.Task, Task.Delay(1000)) == tcs.Task;
        Assert.IsTrue(completed, "Recovery completed event not fired after exception.");
        Assert.IsFalse(await tcs.Task, "Recovery should report failure on exception.");
        Assert.IsFalse(recovery.IsRecovering, "IsRecovering must reset to false after unhandled exception.");
    }

    [TestMethod]
    public void EnsureHostZOrder_ValidAttachedHost_UsesHwndTop()
    {
        var host = new TaskbarHost();
        var testHost = new IntPtr(0x1111);
        var testTray = new IntPtr(0x2222);

        host.SetHostHwndForTesting(testHost);
        host.SetTaskbarHwndForTesting(testTray);
        host.IsWindowTestHook = h => h == testHost || h == testTray;
        host.GetParentTestHook = h => h == testHost ? testTray : IntPtr.Zero;

        IntPtr capturedTarget = IntPtr.Zero;
        IntPtr capturedInsertAfter = new IntPtr(-99);
        uint capturedFlags = 0;
        int callCount = 0;

        host.SetWindowPosTestHook = (h, insertAfter, flags) =>
        {
            callCount++;
            capturedTarget = h;
            capturedInsertAfter = insertAfter;
            capturedFlags = flags;
            return true;
        };

        bool result = host.EnsureHostZOrder();

        Assert.IsTrue(result);
        Assert.AreEqual(1, callCount);
        Assert.AreEqual(testHost, capturedTarget);
        Assert.AreEqual(HWND_TOP, capturedInsertAfter, "Must use HWND_TOP (IntPtr.Zero), NOT HWND_TOPMOST.");
        Assert.AreNotEqual(HWND_TOPMOST, capturedInsertAfter, "HWND_TOPMOST is strictly prohibited.");
        Assert.AreEqual((uint)(SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW), capturedFlags);
    }

    [TestMethod]
    public void EnsureHostZOrder_InvalidHost_DoesNothing()
    {
        var host = new TaskbarHost();
        int callCount = 0;
        host.SetWindowPosTestHook = (h, i, f) => { callCount++; return true; };

        // 1. Zero host HWND
        host.SetHostHwndForTesting(IntPtr.Zero);
        host.SetTaskbarHwndForTesting(new IntPtr(0x2222));
        Assert.IsFalse(host.EnsureHostZOrder());
        Assert.AreEqual(0, callCount);

        // 2. Zero taskbar HWND
        host.SetHostHwndForTesting(new IntPtr(0x1111));
        host.SetTaskbarHwndForTesting(IntPtr.Zero);
        Assert.IsFalse(host.EnsureHostZOrder());
        Assert.AreEqual(0, callCount);

        // 3. IsWindow returns false for host
        host.SetHostHwndForTesting(new IntPtr(0x1111));
        host.SetTaskbarHwndForTesting(new IntPtr(0x2222));
        host.IsWindowTestHook = h => h != new IntPtr(0x1111);
        Assert.IsFalse(host.EnsureHostZOrder());
        Assert.AreEqual(0, callCount);

        // 4. IsWindow returns false for taskbar
        host.IsWindowTestHook = h => h != new IntPtr(0x2222);
        Assert.IsFalse(host.EnsureHostZOrder());
        Assert.AreEqual(0, callCount);
    }

    [TestMethod]
    public void EnsureHostZOrder_ParentMismatch_DoesNothing()
    {
        var host = new TaskbarHost();
        var testHost = new IntPtr(0x1111);
        var testTray = new IntPtr(0x2222);
        var differentParent = new IntPtr(0x3333);

        host.SetHostHwndForTesting(testHost);
        host.SetTaskbarHwndForTesting(testTray);
        host.IsWindowTestHook = _ => true;
        host.GetParentTestHook = h => differentParent;

        int callCount = 0;
        host.SetWindowPosTestHook = (h, i, f) => { callCount++; return true; };

        bool result = host.EnsureHostZOrder();

        Assert.IsFalse(result);
        Assert.AreEqual(0, callCount, "SetWindowPos must not be called when parent does not match taskbar.");
    }

    [TestMethod]
    public void Reposition_ReassertsSiblingZOrder()
    {
        try
        {
            var testHost = new IntPtr(0x4444);
            var placement = new TaskbarPlacement(
                X: 1500,
                Y: 1040,
                Width: 140,
                Height: 32,
                Dpi: 96,
                Scale: 1.0,
                Edge: TaskbarEdge.Bottom,
                TaskbarRect: new RECT(0, 1032, 1920, 1080),
                TrayNotifyRect: null);

            IntPtr capturedInsertAfter = new IntPtr(-99);
            uint capturedFlags = 0;
            int updateCount = 0;

            TaskbarInjection.SetWindowPosHook = (h, insertAfter, x, y, cx, cy, flags) =>
            {
                updateCount++;
                capturedInsertAfter = insertAfter;
                capturedFlags = flags;
                return true;
            };

            bool updated = TaskbarInjection.UpdatePosition(testHost, placement);

            Assert.IsTrue(updated);
            Assert.AreEqual(1, updateCount);
            Assert.AreEqual(HWND_TOP, capturedInsertAfter, "Reposition must assert HWND_TOP.");
            Assert.AreNotEqual(HWND_TOPMOST, capturedInsertAfter, "Reposition must never use HWND_TOPMOST.");
            Assert.AreEqual(0u, capturedFlags & (uint)SWP_NOZORDER, "SWP_NOZORDER must NOT be set when repositioning.");
            Assert.AreEqual((uint)(SWP_NOACTIVATE | SWP_SHOWWINDOW), capturedFlags);
        }
        finally
        {
            TaskbarInjection.SetWindowPosHook = null;
        }
    }

    [TestMethod]
    public async Task TaskbarCreated_IdempotentExistingHost_ReassertsZOrder()
    {
        try
        {
            var host = new TaskbarHost();
            var testHost = new IntPtr(0x7777);
            var testTray = new IntPtr(0x8888);

            host.SetHostHwndForTesting(testHost);
            host.SetTaskbarHwndForTesting(testTray);
            host.SetIsAttachedForTesting(true);

            host.IsWindowTestHook = h => h == testHost || h == testTray;
            host.GetParentTestHook = h => h == testHost ? testTray : IntPtr.Zero;
            TaskbarInjection.FindPrimaryTaskbarHook = () => testTray;

            int zOrderCallCount = 0;
            IntPtr capturedInsertAfter = new IntPtr(-99);
            host.SetWindowPosTestHook = (h, insertAfter, flags) =>
            {
                zOrderCallCount++;
                capturedInsertAfter = insertAfter;
                return true;
            };

            var placement = new TaskbarPlacement(100, 10, 140, 32, 96, 1.0, TaskbarEdge.Bottom, new RECT(0, 1032, 1920, 1080), null);
            host.TryGetPlacementHook = (IntPtr tray, out TaskbarPlacement p) =>
            {
                p = placement;
                return true;
            };

            TaskbarInjection.SetWindowPosHook = (h, insertAfter, x, y, cx, cy, flags) => true;

            // Trigger recovery idempotency path (simulating TaskbarCreated arriving when host is already valid)
            bool result = await host.RecreateAndAttachAsync();

            Assert.IsTrue(result);
            Assert.AreEqual(testHost, host.HostHwnd, "Existing host HWND must NOT be replaced/recreated.");
            Assert.IsTrue(zOrderCallCount >= 1, "Z-order assertion must occur on idempotent recovery path.");
            Assert.AreEqual(HWND_TOP, capturedInsertAfter, "Idempotent assertion must use HWND_TOP.");
        }
        finally
        {
            TaskbarInjection.FindPrimaryTaskbarHook = null;
            TaskbarInjection.SetWindowPosHook = null;
        }
    }

    [TestMethod]
    public async Task Recovery_StabilizationDoesNotRecreateHost()
    {
        var host = new TaskbarHost();
        var testHost = new IntPtr(0x3333);
        var testTray = new IntPtr(0x4444);

        host.SetHostHwndForTesting(testHost);
        host.SetTaskbarHwndForTesting(testTray);
        host.IsWindowTestHook = h => h == testHost || h == testTray;
        host.GetParentTestHook = h => h == testHost ? testTray : IntPtr.Zero;

        int zOrderAssertions = 0;
        host.SetWindowPosTestHook = (h, insertAfter, flags) =>
        {
            Interlocked.Increment(ref zOrderAssertions);
            return true;
        };

        // Use fast delays for testing
        host.CustomStabilizationDelaysMsForTesting = [10, 15, 20];

        var tcs = new TaskCompletionSource<bool>();
        int tickCount = 0;
        host.StabilizationTickForTesting += gen =>
        {
            if (Interlocked.Increment(ref tickCount) == 3)
            {
                tcs.TrySetResult(true);
            }
        };

        host.CustomDispatcherForTesting = act => Task.Run(act);

        host.StartPostRecoveryStabilizationForTesting(testHost, testTray);

        bool completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
        Assert.IsTrue(completed, "Stabilization ticks did not complete in time.");
        Assert.AreEqual(testHost, host.HostHwnd, "Host HWND must remain identical (no recreation).");
        Assert.AreEqual(3, zOrderAssertions, "Z-order must be asserted on each stabilization tick.");
    }

    [TestMethod]
    public async Task Recovery_StaleDelayedAssertionDoesNotTouchReplacementHost()
    {
        var host = new TaskbarHost();
        var host1 = new IntPtr(0x1001);
        var host2 = new IntPtr(0x2002);
        var testTray = new IntPtr(0x9009);

        host.SetHostHwndForTesting(host1);
        host.SetTaskbarHwndForTesting(testTray);
        host.IsWindowTestHook = h => true;
        host.GetParentTestHook = h => testTray;

        var assertedHosts = new System.Collections.Generic.List<IntPtr>();
        host.SetWindowPosTestHook = (h, insertAfter, flags) =>
        {
            lock (assertedHosts)
            {
                assertedHosts.Add(h);
            }
            return true;
        };

        host.CustomStabilizationDelaysMsForTesting = [40];
        host.CustomDispatcherForTesting = act => Task.Run(act);

        // Start stabilization targeting host1
        host.StartPostRecoveryStabilizationForTesting(host1, testTray);

        // Immediately simulate host1 being replaced by host2
        host.SetHostHwndForTesting(host2);
        // And cancel old stabilization sequence
        host.CancelPostRecoveryStabilizationForTesting();

        // Wait past delay
        await Task.Delay(100);

        lock (assertedHosts)
        {
            Assert.IsFalse(assertedHosts.Contains(host2), "Stale stabilization callback must never assert on replacement host.");
            Assert.IsFalse(assertedHosts.Contains(host1), "Cancelled stabilization callback must not execute.");
        }
    }

    [TestMethod]
    public void Dispose_CancelsPendingZOrderStabilization()
    {
        var host = new TaskbarHost();
        var testHost = new IntPtr(0x5005);
        var testTray = new IntPtr(0x6006);

        host.SetHostHwndForTesting(testHost);
        host.SetTaskbarHwndForTesting(testTray);
        host.CustomStabilizationDelaysMsForTesting = [500];
        host.CustomDispatcherForTesting = act => Task.Run(act);

        host.StartPostRecoveryStabilizationForTesting(testHost, testTray);
        Assert.IsTrue(host.HasPendingStabilizationForTesting, "Should have pending stabilization before dispose.");

        host.Dispose();

        Assert.IsFalse(host.HasPendingStabilizationForTesting, "Dispose must cancel pending stabilization CTS.");
    }
}
