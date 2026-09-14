using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Windows.AppLifecycle;
using FootballTray.Client;

namespace FootballTray.Client.Tests;

[TestClass]
public class AppActivationCoordinatorTests
{
    private static string FindRepoRoot()
    {
        string current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "FootballTray.sln")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [TestInitialize]
    public void Setup()
    {
        AppActivationCoordinator.ResetForTesting();
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppActivationCoordinator.ResetForTesting();
    }

    [TestMethod]
    public void Test01_PreUiActivation_BuffersPayloadUntilUiRegistration()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        coordinator.OnActivated("Payload1");

        Assert.IsTrue(coordinator.HasPending, "Coordinator must report a pending activation.");
        Assert.AreEqual(1, coordinator.PendingCount, "Coordinator must report pending count of 1.");
        Assert.IsFalse(coordinator.IsUiRegistered, "UI should not be marked as registered yet.");
        Assert.AreEqual(0, delivered.Count, "Handler must not fire before it is registered.");
    }

    [TestMethod]
    public void Test02_FlushOnRegistration_DeliversPendingPayloadImmediately()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        coordinator.OnActivated("Payload1");
        coordinator.RegisterUiHandler(p => delivered.Add(p));

        Assert.IsTrue(coordinator.IsUiRegistered, "UI must be marked as registered.");
        Assert.IsFalse(coordinator.HasPending, "Pending state must be cleared after registration flush.");
        Assert.AreEqual(0, coordinator.PendingCount, "Pending count must be 0 after flush.");
        Assert.AreEqual(1, delivered.Count, "Pending payload must be delivered exactly once.");
        Assert.AreEqual("Payload1", delivered[0]);
    }

    [TestMethod]
    public void Test03_PostUiDirectDelivery_InvokesHandlerDirectlyWithoutBuffering()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        coordinator.RegisterUiHandler(p => delivered.Add(p));
        coordinator.OnActivated("DirectPayload");

        Assert.IsTrue(coordinator.IsUiRegistered, "UI must be registered.");
        Assert.IsFalse(coordinator.HasPending, "Direct delivery must not leave a pending payload.");
        Assert.AreEqual(0, coordinator.PendingCount);
        Assert.AreEqual(1, delivered.Count, "Handler must be invoked immediately.");
        Assert.AreEqual("DirectPayload", delivered[0]);
    }

    [TestMethod]
    public void Test04_FifoPendingQueue_PreservesAndDeliversAllPreRegistrationPayloadsInOrder()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        coordinator.OnActivated("FirstLaunchIntent");
        coordinator.OnActivated("SecondLaunchIntent");
        coordinator.OnActivated("ThirdLaunchIntent");

        Assert.IsTrue(coordinator.HasPending, "Coordinator must have pending activation.");
        Assert.AreEqual(3, coordinator.PendingCount, "Coordinator must retain all 3 activations in pending queue.");

        coordinator.RegisterUiHandler(p => delivered.Add(p));

        Assert.AreEqual(3, delivered.Count, "All queued pending activations must be delivered.");
        Assert.AreEqual("FirstLaunchIntent", delivered[0], "First delivered payload must match first enqueued.");
        Assert.AreEqual("SecondLaunchIntent", delivered[1], "Second delivered payload must match second enqueued.");
        Assert.AreEqual("ThirdLaunchIntent", delivered[2], "Third delivered payload must match third enqueued.");
        Assert.IsFalse(coordinator.HasPending, "Pending state must be cleared after flush.");
        Assert.AreEqual(0, coordinator.PendingCount, "Pending count must be 0 after flush.");
    }

    [TestMethod]
    public void Test05_NoDuplicateDelivery_PendingActivationFlushedOnceDoesNotFireAgain()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        coordinator.OnActivated("UniquePayload");
        coordinator.RegisterUiHandler(p => delivered.Add(p));

        Assert.AreEqual(1, delivered.Count);

        // Verify coordinator has cleared pending and subsequent operations do not re-emit "UniquePayload"
        Assert.IsFalse(coordinator.HasPending);
        coordinator.OnActivated("NewPayload");

        Assert.AreEqual(2, delivered.Count);
        Assert.AreEqual("UniquePayload", delivered[0]);
        Assert.AreEqual("NewPayload", delivered[1]);
    }

    [TestMethod]
    public void Test06_ReRegistrationSemantics_UpdatesActiveHandlerWithoutReFlushing()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var handler1Calls = new List<string>();
        var handler2Calls = new List<string>();

        coordinator.OnActivated("InitialPayload");
        coordinator.RegisterUiHandler(p => handler1Calls.Add(p));

        Assert.AreEqual(1, handler1Calls.Count);
        Assert.AreEqual("InitialPayload", handler1Calls[0]);

        // Re-register with a second handler
        coordinator.RegisterUiHandler(p => handler2Calls.Add(p));

        Assert.AreEqual(0, handler2Calls.Count, "Re-registration must not re-flush an already-consumed payload.");

        // Subsequent activation must route to the updated handler
        coordinator.OnActivated("SubsequentPayload");

        Assert.AreEqual(1, handler1Calls.Count, "First handler must not receive subsequent activations.");
        Assert.AreEqual(1, handler2Calls.Count, "Second handler must receive new activations.");
        Assert.AreEqual("SubsequentPayload", handler2Calls[0]);
    }

    [TestMethod]
    public void Test07_ResetIsolation_ReliablyClearsFullQueueAndState()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        coordinator.OnActivated("PendingBeforeReset1");
        coordinator.OnActivated("PendingBeforeReset2");
        Assert.IsTrue(coordinator.HasPending);
        Assert.AreEqual(2, coordinator.PendingCount);

        coordinator.Reset();

        Assert.IsFalse(coordinator.HasPending, "HasPending must be false after Reset.");
        Assert.AreEqual(0, coordinator.PendingCount, "PendingCount must be 0 after Reset.");
        Assert.IsFalse(coordinator.IsUiRegistered, "IsUiRegistered must be false after Reset.");
        Assert.IsFalse(coordinator.IsDispatching, "IsDispatching must be false after Reset.");

        // Registering after reset must not flush the cleared payload
        coordinator.RegisterUiHandler(p => delivered.Add(p));
        Assert.AreEqual(0, delivered.Count, "No payload should flush after Reset.");

        // Global static coordinator reset test
        Assert.IsFalse(AppActivationCoordinator.HasPendingActivation);
        Assert.IsFalse(AppActivationCoordinator.IsUiRegistered);
    }

    [TestMethod]
    public void Test08_DeterministicConcurrency_HandlesConcurrentActivationAndRegistration()
    {
        const int iterations = 50;

        for (int i = 0; i < iterations; i++)
        {
            var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
            var delivered = new List<string>();
            var barrier = new Barrier(2);
            string expectedPayload = $"ConcurrentPayload_{i}";

            var activationTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                coordinator.OnActivated(expectedPayload);
            });

            var registrationTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                coordinator.RegisterUiHandler(p =>
                {
                    lock (delivered)
                    {
                        delivered.Add(p);
                    }
                });
            });

            Task.WaitAll(activationTask, registrationTask);

            lock (delivered)
            {
                Assert.AreEqual(1, delivered.Count, $"Iteration {i}: Payload must be delivered exactly once.");
                Assert.AreEqual(expectedPayload, delivered[0], $"Iteration {i}: Delivered payload must match.");
            }
            Assert.IsFalse(coordinator.HasPending, $"Iteration {i}: Pending must be false after completion.");
            Assert.IsTrue(coordinator.IsUiRegistered, $"Iteration {i}: UI must be registered.");
        }
    }

    [TestMethod]
    public void Test09_SingleInstanceStructuralContract_GuardsArchitecturalInvariants()
    {
        string root = FindRepoRoot();

        // 1. Verify FootballTray.Client.csproj defines DISABLE_XAML_GENERATED_MAIN
        string csprojPath = Path.Combine(root, "src", "client", "FootballTray.Client", "FootballTray.Client.csproj");
        Assert.IsTrue(File.Exists(csprojPath), "Client csproj file must exist.");
        string csprojContent = File.ReadAllText(csprojPath);
        Assert.IsTrue(csprojContent.Contains("DISABLE_XAML_GENERATED_MAIN"),
            "Client csproj must define DISABLE_XAML_GENERATED_MAIN.");

        // 2. Verify Program.cs enforces single instance via AppInstance
        string programPath = Path.Combine(root, "src", "client", "FootballTray.Client", "Program.cs");
        Assert.IsTrue(File.Exists(programPath), "Program.cs must exist.");
        string programContent = File.ReadAllText(programPath);
        Assert.IsTrue(programContent.Contains("AppInstance.FindOrRegisterForKey"),
            "Program.cs must use AppInstance.FindOrRegisterForKey.");
        Assert.IsTrue(programContent.Contains("RedirectActivationToAsync"),
            "Program.cs must call RedirectActivationToAsync on secondary instance.");
        Assert.IsFalse(programContent.Contains("new Mutex"),
            "Program.cs must not use Mutex for single-instance enforcement.");

        // 3. Verify TaskbarHost.cs remains completely decoupled from single-instance IPC
        string taskbarHostPath = Path.Combine(root, "src", "client", "FootballTray.Client", "Taskbar", "TaskbarHost.cs");
        Assert.IsTrue(File.Exists(taskbarHostPath), "TaskbarHost.cs must exist.");
        string taskbarContent = File.ReadAllText(taskbarHostPath);
        Assert.IsFalse(taskbarContent.Contains("AppActivationCoordinator"),
            "TaskbarHost.cs must not contain single-instance coordinator logic.");
        Assert.IsFalse(taskbarContent.Contains("FindOrRegisterForKey"),
            "TaskbarHost.cs must not contain AppInstance registration logic.");
    }

    [TestMethod]
    public void Test10_AppNotificationClassification_MapsActivationKindsCorrectly()
    {
        // AppNotification must route to ProcessNotification
        Assert.AreEqual(
            App.ActivationDispatchAction.ProcessNotification,
            App.ClassifyActivation(ExtendedActivationKind.AppNotification),
            "ExtendedActivationKind.AppNotification must map to ProcessNotification.");

        // All other activation kinds must map to BringToForeground
        Assert.AreEqual(
            App.ActivationDispatchAction.BringToForeground,
            App.ClassifyActivation(ExtendedActivationKind.Launch),
            "ExtendedActivationKind.Launch must map to BringToForeground.");

        Assert.AreEqual(
            App.ActivationDispatchAction.BringToForeground,
            App.ClassifyActivation(ExtendedActivationKind.CommandLineLaunch),
            "ExtendedActivationKind.CommandLineLaunch must map to BringToForeground.");

        Assert.AreEqual(
            App.ActivationDispatchAction.BringToForeground,
            App.ClassifyActivation(ExtendedActivationKind.Protocol),
            "ExtendedActivationKind.Protocol must map to BringToForeground.");

        Assert.AreEqual(
            App.ActivationDispatchAction.BringToForeground,
            App.ClassifyActivation(ExtendedActivationKind.StartupTask),
            "ExtendedActivationKind.StartupTask must map to BringToForeground.");
    }

    [TestMethod]
    public void Test11_AppNotificationActivationContract_GuardsNotificationDispatchInvariant()
    {
        string root = FindRepoRoot();
        string appXamlCsPath = Path.Combine(root, "src", "client", "FootballTray.Client", "App.xaml.cs");
        Assert.IsTrue(File.Exists(appXamlCsPath), "App.xaml.cs must exist.");

        string content = File.ReadAllText(appXamlCsPath);

        // 1. Verify App.xaml.cs inspects ExtendedActivationKind.AppNotification
        Assert.IsTrue(
            content.Contains("ExtendedActivationKind.AppNotification"),
            "App.xaml.cs must inspect ExtendedActivationKind.AppNotification.");

        // 2. Verify App.xaml.cs casts/checks AppNotificationActivatedEventArgs
        Assert.IsTrue(
            content.Contains("AppNotificationActivatedEventArgs"),
            "App.xaml.cs must handle AppNotificationActivatedEventArgs payload.");

        // 3. Verify App.xaml.cs reuses existing OnNotificationActivated
        Assert.IsTrue(
            content.Contains("OnNotificationActivated(result)"),
            "App.xaml.cs must reuse OnNotificationActivated for notification payload handling.");

        // 4. Verify App.xaml.cs uses NotificationActivationHandler to parse arguments
        Assert.IsTrue(
            content.Contains("NotificationActivationHandler.Parse"),
            "App.xaml.cs must use NotificationActivationHandler.Parse to decode notification arguments.");
    }

    [TestMethod]
    public void Test12_ActivationArrivingDuringFlush_QueuesBehindCurrentPayloadAndDeliversInOrder()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();
        using var handlerProcessingFirst = new ManualResetEventSlim(false);
        using var secondEnqueued = new ManualResetEventSlim(false);

        // Pre-queue payload A
        coordinator.OnActivated("PayloadA");

        // Register UI handler in a background task so we can control concurrency
        var regTask = Task.Run(() =>
        {
            coordinator.RegisterUiHandler(p =>
            {
                lock (delivered)
                {
                    delivered.Add(p);
                }

                if (p == "PayloadA")
                {
                    // Signal that we are currently inside the handler for A
                    handlerProcessingFirst.Set();
                    // Wait until test thread has enqueued B before completing A
                    secondEnqueued.Wait(TimeSpan.FromSeconds(5));
                }
            });
        });

        // Wait until handler is actively processing A
        Assert.IsTrue(handlerProcessingFirst.Wait(TimeSpan.FromSeconds(5)), "Handler did not begin processing PayloadA in time.");

        // Now while A is being processed, enqueue PayloadB
        coordinator.OnActivated("PayloadB");

        // Release the handler processing A
        secondEnqueued.Set();

        // Wait for registration and drain to complete
        Assert.IsTrue(regTask.Wait(TimeSpan.FromSeconds(5)), "Registration and drain task timed out.");

        lock (delivered)
        {
            Assert.AreEqual(2, delivered.Count, "Both activations must be delivered.");
            Assert.AreEqual("PayloadA", delivered[0], "PayloadA must be delivered first.");
            Assert.AreEqual("PayloadB", delivered[1], "PayloadB must be delivered second.");
        }
        Assert.IsFalse(coordinator.HasPending, "Pending queue must be empty.");
        Assert.AreEqual(0, coordinator.PendingCount);
        Assert.IsFalse(coordinator.IsDispatching, "Dispatching state must be false after completion.");
    }

    [TestMethod]
    public void Test13_HandlerException_DoesNotPermanentlyWedgeCoordinator()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        // Pre-enqueue two payloads
        coordinator.OnActivated("FailingPayload");
        coordinator.OnActivated("SubsequentPayload");

        bool threw = false;
        try
        {
            coordinator.RegisterUiHandler(p =>
            {
                if (p == "FailingPayload")
                {
                    throw new InvalidOperationException("Simulated UI handler failure.");
                }
                delivered.Add(p);
            });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Exception from handler must propagate to caller.");
        Assert.IsFalse(coordinator.IsDispatching, "Coordinator must not remain locked in dispatching state after exception.");

        // Now register a healthy handler; coordinator must make progress and deliver remaining items
        coordinator.RegisterUiHandler(p => delivered.Add(p));

        Assert.AreEqual(1, delivered.Count, "Remaining queued payload must be delivered by subsequent valid registration.");
        Assert.AreEqual("SubsequentPayload", delivered[0]);
        Assert.IsFalse(coordinator.HasPending, "Pending queue must be completely drained.");
        Assert.AreEqual(0, coordinator.PendingCount);

        // Verify a new activation is also delivered cleanly
        coordinator.OnActivated("PostRecoveryPayload");
        Assert.AreEqual(2, delivered.Count);
        Assert.AreEqual("PostRecoveryPayload", delivered[1]);
        Assert.IsFalse(coordinator.IsDispatching);
    }

    [TestMethod]
    public void Test14_ColdNotificationScenario_PreservesNotificationPayloadWhenFollowedByLaunch()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();

        // 1. Cold notification activation arrives during cold start
        coordinator.OnActivated("Notification_GoalAlert_Match123");

        // 2. Ordinary Launch activation arrives before UI registers (e.g. user clicked tray or app shortcut)
        coordinator.OnActivated("Launch_BringToForeground");

        // 3. Second notification arrives before UI registers
        coordinator.OnActivated("Notification_RedCard_Match456");

        Assert.IsTrue(coordinator.HasPending);
        Assert.AreEqual(3, coordinator.PendingCount);

        // 4. UI registers
        coordinator.RegisterUiHandler(p => delivered.Add(p));

        // 5. Verify all three delivered in exact FIFO order without any loss
        Assert.AreEqual(3, delivered.Count, "All cold/redirected activations must be preserved and delivered.");
        Assert.AreEqual("Notification_GoalAlert_Match123", delivered[0]);
        Assert.AreEqual("Launch_BringToForeground", delivered[1]);
        Assert.AreEqual("Notification_RedCard_Match456", delivered[2]);
        Assert.IsFalse(coordinator.HasPending);
        Assert.AreEqual(0, coordinator.PendingCount);
    }

    [TestMethod]
    public void Test15_ConcurrentPostRegistrationActivations_DeliveredSafelyWithoutLoss()
    {
        var coordinator = AppActivationCoordinator.CreateCoordinatorForTesting<string>();
        var delivered = new List<string>();
        coordinator.RegisterUiHandler(p =>
        {
            lock (delivered)
            {
                delivered.Add(p);
            }
        });

        const int activationCount = 20;
        var tasks = new Task[activationCount];
        var barrier = new Barrier(activationCount);

        for (int i = 0; i < activationCount; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                coordinator.OnActivated($"Concurrent_{index}");
            });
        }

        Task.WaitAll(tasks);

        lock (delivered)
        {
            Assert.AreEqual(activationCount, delivered.Count, "Every concurrent activation must be delivered exactly once.");
            for (int i = 0; i < activationCount; i++)
            {
                Assert.IsTrue(delivered.Contains($"Concurrent_{i}"), $"Payload Concurrent_{i} must be in delivered list.");
            }
        }
        Assert.IsFalse(coordinator.HasPending);
        Assert.AreEqual(0, coordinator.PendingCount);
        Assert.IsFalse(coordinator.IsDispatching);
    }
}
