using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using FootballTray.Client.Services.Preferences;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using static FootballTray.Client.Taskbar.TaskbarNativeMethods;

namespace FootballTray.Client.Taskbar;

/// <summary>
/// High-level host controller providing a clean, isolated application-level interface
/// for Windows 11 taskbar widget injection, geometry management, and Explorer lifecycle recovery.
/// </summary>
public sealed class TaskbarHost : IDisposable
{
    private const string MessageReceiverClassName = "FootballTray_MessageReceiverClass";
    private const string HostWindowClassName = "FootballTray_TaskbarHostClass";

    private readonly WndProc _messageReceiverWndProcDelegate;
    private readonly WndProc _hostWndProcDelegate;
    private readonly DispatcherQueue _dispatcherQueue;

    private IntPtr _messageReceiverHwnd = IntPtr.Zero;
    private IntPtr _hostHwnd = IntPtr.Zero;
    private IntPtr _taskbarHwnd = IntPtr.Zero;

    private DesktopWindowXamlSource? _xamlSource;
    private UIElement? _hostedContent;
    private TaskbarWatcher? _watcher;
    private ExplorerRecovery? _recovery;
    private IPreferencesService? _preferencesService;

    private bool _isDisposed;
    private bool _receiverClassRegistered;
    private bool _hostClassRegistered;
    private uint _taskbarCreatedMsgId;
    private bool _isAttached;
    private bool _isIntentionalHostTeardown;
    private int _stabilizationGeneration;
    private CancellationTokenSource? _stabilizationCts;
    private static readonly int[] PostRecoveryStabilizationDelaysMs = [250, 750, 1500, 3000];

    public bool IsAttached => _isAttached;
    public IntPtr MessageReceiverHwnd => _messageReceiverHwnd;
    public IntPtr HostHwnd => _hostHwnd;
    public IntPtr TaskbarHwnd => _taskbarHwnd;
    public int LastAppliedOffsetX { get; private set; }

    public event Action? Repositioned;
    public event Action<bool>? AttachmentChanged;

    public IPreferencesService? PreferencesService
    {
        get => _preferencesService;
        set => _preferencesService = value;
    }

    public TaskbarHost(DispatcherQueue? dispatcherQueue = null, IPreferencesService? preferencesService = null)
    {
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _preferencesService = preferencesService;
        
        // Retain delegates in instance fields to prevent GC collection
        _messageReceiverWndProcDelegate = MessageReceiverWndProc;
        _hostWndProcDelegate = HostWndProc;

        // Register TaskbarCreated broadcast message
        _taskbarCreatedMsgId = RegisterWindowMessage(TaskbarCreatedMessage);
        TaskbarLogger.Info($"[INIT] TaskbarCreated message registered with ID: 0x{_taskbarCreatedMsgId:X}");
    }

    /// <summary>
    /// Initializes the persistent message receiver, watcher, recovery handler,
    /// creates the initial taskbar host HWND, and injects into the primary taskbar.
    /// </summary>
    public async Task<bool> InitializeAsync(UIElement content)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(TaskbarHost));

        _hostedContent = content ?? throw new ArgumentNullException(nameof(content));

        TaskbarLogger.Info("[INIT] Initializing TaskbarHost...");

        // 1. Create Persistent Message Receiver (never reparented, survives Explorer restart)
        if (!CreatePersistentMessageReceiver())
        {
            TaskbarLogger.Error("[INIT] Failed to create persistent message receiver window.");
            return false;
        }

        // 2. Initialize Explorer Recovery (wired to RecreateAndAttachAsync)
        _recovery = new ExplorerRecovery(_dispatcherQueue, async () =>
        {
            return await RecreateAndAttachAsync();
        });
        _recovery.RecoveryCompleted += success =>
        {
            if (!success && !_isAttached)
            {
                AttachmentChanged?.Invoke(false);
            }
        };

        // 3. Initialize Taskbar Watcher
        _watcher = new TaskbarWatcher(_dispatcherQueue);
        _watcher.TaskbarChanged += () =>
        {
            UpdatePosition();
        };

        // 4. Perform initial creation and injection
        bool attached = await RecreateAndAttachAsync();
        return attached;
    }

    /// <summary>
    /// Performs full teardown of any previous/dead host state and creates a fresh
    /// host HWND, fresh XAML Island, finds the current Shell_TrayWnd, and attaches cleanly.
    /// </summary>
    public async Task<bool> RecreateAndAttachAsync()
    {
        if (_isDisposed) return false;

        // 0. Idempotency check: If a valid host is already attached to the current valid Shell_TrayWnd, do not duplicate
        IntPtr currentShellTray = TaskbarInjection.FindPrimaryTaskbar();
        bool isHostValid = IsWindowTestHook?.Invoke(_hostHwnd) ?? IsWindow(_hostHwnd);
        bool isTrayValid = IsWindowTestHook?.Invoke(currentShellTray) ?? IsWindow(currentShellTray);
        IntPtr parent = GetParentTestHook?.Invoke(_hostHwnd) ?? GetParent(_hostHwnd);

        if (currentShellTray != IntPtr.Zero &&
            _isAttached &&
            _hostHwnd != IntPtr.Zero &&
            _taskbarHwnd == currentShellTray &&
            isHostValid &&
            isTrayValid &&
            parent == currentShellTray)
        {
            TaskbarLogger.Info($"[RECOVERY] Host 0x{_hostHwnd.ToInt64():X} is already attached and valid on current Shell_TrayWnd 0x{currentShellTray.ToInt64():X}. Skipping duplicate creation.");
            Reposition();
            EnsureHostZOrder();
            return true;
        }

        IntPtr oldHost = _hostHwnd;
        IntPtr oldTaskbar = _taskbarHwnd;
        TaskbarLogger.Info($"[RECOVERY] RecreateAndAttachAsync executing. (OldHost=0x{oldHost.ToInt64():X}, OldShellTray=0x{oldTaskbar.ToInt64():X})");

        // 1. Stop and unhook previous watcher
        _watcher?.Stop();

        // 2. Safely dispose dead/old XAML Island
        if (_xamlSource != null)
        {
            try
            {
                _xamlSource.Dispose();
                TaskbarLogger.Info("[RECOVERY] Old DesktopWindowXamlSource disposed.");
            }
            catch (Exception ex)
            {
                TaskbarLogger.Warn($"[RECOVERY] Non-fatal exception disposing old DesktopWindowXamlSource: {ex.Message}");
            }
            finally
            {
                _xamlSource = null;
            }
        }

        // 3. Detach and destroy old host HWND if it still exists (mark intentional teardown to prevent re-entrant recovery)
        CancelPostRecoveryStabilization();
        if (_hostHwnd != IntPtr.Zero)
        {
            _isIntentionalHostTeardown = true;
            try
            {
                TaskbarInjection.Detach(_hostHwnd);
                DestroyWindow(_hostHwnd);
                TaskbarLogger.Info($"[RECOVERY] Old host HWND 0x{oldHost.ToInt64():X} destroyed.");
            }
            catch (Exception ex)
            {
                TaskbarLogger.Warn($"[RECOVERY] Non-fatal exception destroying old host HWND: {ex.Message}");
            }
            finally
            {
                _hostHwnd = IntPtr.Zero;
                _isIntentionalHostTeardown = false;
            }
        }
        else
        {
            _isIntentionalHostTeardown = false;
        }

        // 4. Find the current/new primary Shell_TrayWnd
        if (currentShellTray == IntPtr.Zero)
        {
            currentShellTray = TaskbarInjection.FindPrimaryTaskbar();
        }

        if (currentShellTray == IntPtr.Zero)
        {
            TaskbarLogger.Warn("[RECOVERY] Primary taskbar (Shell_TrayWnd) not found. Awaiting recovery retry...");
            _taskbarHwnd = IntPtr.Zero;
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        _taskbarHwnd = currentShellTray;
        TaskbarLogger.Info($"[RECOVERY] Discovered primary Shell_TrayWnd HWND: 0x{_taskbarHwnd.ToInt64():X}");

        // 5. Calculate fresh taskbar geometry with multi-monitor and offset support
        var prefs = GetCurrentPreferences();
        var targetScreen = System.Windows.Forms.Screen.AllScreens
            .FirstOrDefault(s => !string.IsNullOrEmpty(prefs?.TargetScreenDeviceName) &&
                                 string.Equals(s.DeviceName, prefs.TargetScreenDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? System.Windows.Forms.Screen.PrimaryScreen
            ?? (System.Windows.Forms.Screen.AllScreens.Length > 0 ? System.Windows.Forms.Screen.AllScreens[0] : null);

        IntPtr targetTaskbar = targetScreen != null ? FindTaskbarForScreen(targetScreen) : TaskbarInjection.FindPrimaryTaskbar();
        if (targetTaskbar != IntPtr.Zero)
        {
            _taskbarHwnd = targetTaskbar;
        }

        bool placementFound;
        TaskbarPlacement placement;
        if (TryGetPlacementHook != null)
        {
            placementFound = TryGetPlacementHook(_taskbarHwnd, out placement);
        }
        else
        {
            int initialOffset = prefs?.TickerOffsetX ?? 0;
            placementFound = TryCalculateTargetPlacement(targetScreen, _taskbarHwnd, ref initialOffset, out placement);
            LastAppliedOffsetX = initialOffset;
        }

        if (!placementFound)
        {
            TaskbarLogger.Warn("[RECOVERY] Failed to calculate primary taskbar placement geometry.");
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        TaskbarLogger.Info($"[GEOMETRY] Calculated placement: Pos=({placement.X},{placement.Y}), Size={placement.Width}x{placement.Height}, DPI={placement.Dpi}");

        // 6. Create brand-new native host HWND
        _hostHwnd = CreateHostHwnd(placement.Width, placement.Height);
        if (_hostHwnd == IntPtr.Zero)
        {
            TaskbarLogger.Error("[RECOVERY] Failed to create new native host HWND.");
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        TaskbarLogger.Info($"[HOST] Created fresh native host HWND: 0x{_hostHwnd.ToInt64():X}");

        // 7. Create and initialize fresh DesktopWindowXamlSource
        try
        {
            _xamlSource = new DesktopWindowXamlSource();
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hostHwnd);
            _xamlSource.Initialize(windowId);
            _xamlSource.Content = _hostedContent;

            if (_xamlSource.SiteBridge != null)
            {
                _xamlSource.SiteBridge.MoveAndResize(new RectInt32(0, 0, placement.Width, placement.Height));
                _xamlSource.SiteBridge.Show();
            }

            TaskbarLogger.Info("[HOST] Fresh DesktopWindowXamlSource initialized and content attached.");
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("[HOST] Exception initializing DesktopWindowXamlSource on fresh host HWND.", ex);
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        // 8. Attach fresh host HWND into Shell_TrayWnd
        bool attached = TaskbarInjection.Attach(_hostHwnd, _taskbarHwnd, placement);
        if (attached && IsWindow(_taskbarHwnd) && IsWindow(_hostHwnd) && GetParent(_hostHwnd) == _taskbarHwnd)
        {
            _isAttached = true;
            ApplyVisibility();
            EnsureHostZOrder();
            _watcher?.Start(_taskbarHwnd);
            StartPostRecoveryStabilization(_hostHwnd, _taskbarHwnd);

            TaskbarLogger.Info($"[RECOVERY] RecreateAndAttachAsync completed successfully. Host 0x{_hostHwnd.ToInt64():X} docked into Shell_TrayWnd 0x{_taskbarHwnd.ToInt64():X}.");
            AttachmentChanged?.Invoke(true);
            Repositioned?.Invoke();
        }
        else
        {
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            TaskbarLogger.Warn("[RECOVERY] TaskbarInjection.Attach failed or taskbar window invalidated during RecreateAndAttachAsync.");
            attached = false;
        }

        await Task.CompletedTask;
        return attached;
    }

    /// <summary>
    /// Recalculates geometry using the configured display screen, taskbar bounds, and user X-offset,
    /// updating the host window's position inside the taskbar in real time.
    /// Also reasserts HWND_TOP sibling Z-order on successful repositioning.
    /// </summary>
    public bool UpdatePosition(int? overrideOffsetX = null)
    {
        if (_isDisposed || _hostHwnd == IntPtr.Zero) return false;

        var prefs = GetCurrentPreferences();
        int offsetX = overrideOffsetX ?? (prefs?.TickerOffsetX ?? 0);

        // 1. Identify target screen via Screen.AllScreens matching TargetScreenDeviceName (fallback to PrimaryScreen)
        System.Windows.Forms.Screen? targetScreen = null;
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (!string.IsNullOrEmpty(prefs?.TargetScreenDeviceName))
            {
                targetScreen = screens.FirstOrDefault(s => string.Equals(s.DeviceName, prefs.TargetScreenDeviceName, StringComparison.OrdinalIgnoreCase));
            }
            targetScreen ??= System.Windows.Forms.Screen.PrimaryScreen ?? (screens.Length > 0 ? screens[0] : null);
        }
        catch (Exception ex)
        {
            TaskbarLogger.Warn($"Failed to query System.Windows.Forms.Screen: {ex.Message}");
        }

        // 2. Discover taskbar handle for target screen and reparent if moved across displays
        IntPtr targetTaskbar = targetScreen != null ? FindTaskbarForScreen(targetScreen) : TaskbarInjection.FindPrimaryTaskbar();
        if (targetTaskbar != IntPtr.Zero && targetTaskbar != _taskbarHwnd)
        {
            _taskbarHwnd = targetTaskbar;
            SetParent(_hostHwnd, _taskbarHwnd);
        }
        else if (_taskbarHwnd == IntPtr.Zero)
        {
            _taskbarHwnd = TaskbarInjection.FindPrimaryTaskbar();
            if (_taskbarHwnd == IntPtr.Zero) return false;
        }

        // 3. Placement calculation (respecting testing placement hook if active)
        TaskbarPlacement placement;
        bool placementFound;
        if (TryGetPlacementHook != null)
        {
            placementFound = TryGetPlacementHook(_taskbarHwnd, out placement);
        }
        else
        {
            placementFound = TryCalculateTargetPlacement(targetScreen, _taskbarHwnd, ref offsetX, out placement);
        }

        if (placementFound)
        {
            LastAppliedOffsetX = offsetX;
            bool moved = TaskbarInjection.UpdatePosition(_hostHwnd, placement);
            if (moved)
            {
                UpdateHostSize(placement.Width, placement.Height);
                EnsureHostZOrder();
                Repositioned?.Invoke();
            }
            return moved;
        }

        return false;
    }

    /// <summary>
    /// Legacy/compatibility alias for UpdatePosition().
    /// </summary>
    public bool Reposition()
    {
        return UpdatePosition();
    }

    /// <summary>
    /// Evaluates current preferences (IsTickerVisible) and displays or hides the taskbar host widget.
    /// </summary>
    public void ApplyVisibility()
    {
        var prefs = GetCurrentPreferences();
        bool isVisible = prefs?.IsTickerVisible ?? true;
        ApplyVisibility(isVisible);
    }

    /// <summary>
    /// Applies the specified visibility state directly to the taskbar host widget.
    /// </summary>
    public void ApplyVisibility(bool isVisible)
    {
        if (isVisible)
        {
            Show();
        }
        else
        {
            Hide();
        }
    }

    private UserPreferences? GetCurrentPreferences()
    {
        return _preferencesService?.Current ?? App.CurrentMainViewModel?.PreferencesService?.Current;
    }

    private static bool TryCalculateTargetPlacement(
        System.Windows.Forms.Screen? screen,
        IntPtr taskbarHwnd,
        ref int offsetX,
        out TaskbarPlacement placement)
    {
        placement = null!;

        // 1. Get taskbar rectangle
        RECT taskbarRect = default;
        bool hasRect = taskbarHwnd != IntPtr.Zero && GetWindowRect(taskbarHwnd, out taskbarRect) && taskbarRect.Width > 0 && taskbarRect.Height > 0;
        if (!hasRect)
        {
            if (screen != null)
            {
                int tbHeight = screen.Bounds.Height - screen.WorkingArea.Height;
                if (tbHeight <= 0) tbHeight = 48;
                taskbarRect = new RECT(screen.Bounds.Left, screen.Bounds.Bottom - tbHeight, screen.Bounds.Right, screen.Bounds.Bottom);
            }
            else
            {
                return false;
            }
        }

        // 2. Query TrayNotifyWnd (System Tray area) if on primary taskbar
        RECT? trayNotifyRect = null;
        if (taskbarHwnd != IntPtr.Zero)
        {
            IntPtr trayNotifyHwnd = FindWindowEx(taskbarHwnd, IntPtr.Zero, TrayNotifyWndClass, null);
            if (trayNotifyHwnd != IntPtr.Zero && GetWindowRect(trayNotifyHwnd, out RECT notifyRect) && notifyRect.Width > 0)
            {
                trayNotifyRect = notifyRect;
            }
        }

        // 3. Query DPI
        uint dpi = 0;
        if (taskbarHwnd != IntPtr.Zero)
        {
            dpi = GetDpiForWindow(taskbarHwnd);
        }
        if (dpi == 0)
        {
            dpi = 96;
        }

        double scale = dpi / 96.0;
        int scaledWidth = (int)Math.Round(TaskbarGeometry.DefaultBaseWidth * scale);
        int scaledHeight = (int)Math.Round(TaskbarGeometry.DefaultBaseHeight * scale);
        int scaledMargin = (int)Math.Round(TaskbarGeometry.DefaultBaseMargin * scale);

        int taskbarWidth = taskbarRect.Width;
        int taskbarHeight = taskbarRect.Height;

        if (scaledHeight > taskbarHeight - (scaledMargin * 2))
        {
            scaledHeight = Math.Max(16, taskbarHeight - (scaledMargin * 2));
        }

        int targetY = Math.Max(0, (taskbarHeight - scaledHeight) / 2);
        int baseTargetX;

        if (trayNotifyRect.HasValue && trayNotifyRect.Value.Width > 0)
        {
            baseTargetX = (trayNotifyRect.Value.Left - taskbarRect.Left) - scaledWidth - scaledMargin;
        }
        else
        {
            int estimatedTrayWidth = (int)Math.Round(180 * scale);
            baseTargetX = taskbarWidth - estimatedTrayWidth - scaledWidth - scaledMargin;
        }

        offsetX = Math.Clamp(offsetX, 0, Math.Max(0, baseTargetX));

        int targetX = baseTargetX - offsetX;

        placement = new TaskbarPlacement(
            X: targetX,
            Y: targetY,
            Width: scaledWidth,
            Height: scaledHeight,
            Dpi: dpi,
            Scale: scale,
            Edge: TaskbarEdge.Bottom,
            TaskbarRect: taskbarRect,
            TrayNotifyRect: trayNotifyRect);

        return true;
    }

    private static IntPtr FindTaskbarForScreen(System.Windows.Forms.Screen screen)
    {
        if (screen.Primary)
        {
            return TaskbarInjection.FindPrimaryTaskbar();
        }

        IntPtr targetMonitor = MonitorFromPoint(new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2);

        IntPtr secWnd = IntPtr.Zero;
        while ((secWnd = FindWindowEx(IntPtr.Zero, secWnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (MonitorFromWindow(secWnd, 2) == targetMonitor)
            {
                return secWnd;
            }
        }

        return TaskbarInjection.FindPrimaryTaskbar();
    }

    /// <summary>
    /// Shows the host widget.
    /// </summary>
    public void Show()
    {
        if (_hostHwnd != IntPtr.Zero)
        {
            ShowWindow(_hostHwnd, 5 /* SW_SHOW */);
            _xamlSource?.SiteBridge?.Show();
        }
    }

    /// <summary>
    /// Hides the host widget.
    /// </summary>
    public void Hide()
    {
        if (_hostHwnd != IntPtr.Zero)
        {
            ShowWindow(_hostHwnd, 0 /* SW_HIDE */);
            _xamlSource?.SiteBridge?.Hide();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private static IntPtr GetAppIconHandle()
    {
        try
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                IntPtr hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (hIcon != IntPtr.Zero)
                {
                    return hIcon;
                }
            }
        }
        catch
        {
            // Best effort icon load
        }
        // Fallback to standard application icon to guarantee an icon handle is provided to the window class
        return LoadIcon(IntPtr.Zero, (IntPtr)32512);
    }

    private bool CreatePersistentMessageReceiver()
    {
        if (_messageReceiverHwnd != IntPtr.Zero) return true;

        try
        {
            IntPtr hInstance = GetModuleHandle(null);
            IntPtr hAppIcon = GetAppIconHandle();

            WNDCLASSEX wcx = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _messageReceiverWndProcDelegate,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = hAppIcon,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = MessageReceiverClassName,
                hIconSm = hAppIcon
            };

            ushort atom = RegisterClassEx(ref wcx);
            if (atom != 0)
            {
                _receiverClassRegistered = true;
            }

            // Top-level popup window, not a child, so it receives HWND_BROADCAST
            int exStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            int style = WS_POPUP;

            _messageReceiverHwnd = CreateWindowEx(
                exStyle,
                MessageReceiverClassName,
                "FootballTray_MessageReceiver",
                style,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_messageReceiverHwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                TaskbarLogger.Error($"[RECEIVER] CreateWindowEx failed for Persistent MessageReceiver. Win32Error={err}");
                return false;
            }

            TaskbarLogger.Info($"[RECEIVER] Persistent MessageReceiver HWND created: 0x{_messageReceiverHwnd.ToInt64():X}");

            // Defensive UIPI message filter configuration for TaskbarCreated broadcast
            if (_taskbarCreatedMsgId != 0)
            {
                try
                {
                    bool filtered = ChangeWindowMessageFilterEx(_messageReceiverHwnd, _taskbarCreatedMsgId, MSGFLT_ALLOW, IntPtr.Zero);
                    if (filtered)
                    {
                        TaskbarLogger.Info($"[RECEIVER] ChangeWindowMessageFilterEx successfully allowed TaskbarCreated message (Msg=0x{_taskbarCreatedMsgId:X}).");
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        TaskbarLogger.Warn($"[RECEIVER] ChangeWindowMessageFilterEx returned false for TaskbarCreated (Msg=0x{_taskbarCreatedMsgId:X}). Win32Error={err}");
                    }
                }
                catch (Exception ex)
                {
                    TaskbarLogger.Warn($"[RECEIVER] Non-fatal exception calling ChangeWindowMessageFilterEx: {ex.Message}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("[RECEIVER] Exception creating Persistent MessageReceiver window.", ex);
            return false;
        }
    }

    private IntPtr CreateHostHwnd(int width, int height)
    {
        try
        {
            IntPtr hInstance = GetModuleHandle(null);
            IntPtr hAppIcon = GetAppIconHandle();

            if (!_hostClassRegistered)
            {
                WNDCLASSEX wcx = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0,
                    lpfnWndProc = _hostWndProcDelegate,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = hAppIcon,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = HostWindowClassName,
                    hIconSm = hAppIcon
                };

                ushort atom = RegisterClassEx(ref wcx);
                if (atom != 0)
                {
                    _hostClassRegistered = true;
                }
            }

            int exStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            int style = WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

            IntPtr hwnd = CreateWindowEx(
                exStyle,
                HostWindowClassName,
                "FootballTray_Host",
                style,
                0,
                0,
                width,
                height,
                IntPtr.Zero,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            return hwnd;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("[HOST] Exception creating native host HWND.", ex);
            return IntPtr.Zero;
        }
    }

    private void UpdateHostSize(int width, int height)
    {
        if (_xamlSource?.SiteBridge != null && width > 0 && height > 0)
        {
            _xamlSource.SiteBridge.MoveAndResize(new RectInt32(0, 0, width, height));
        }
    }

    private IntPtr MessageReceiverWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsgId && _taskbarCreatedMsgId != 0)
        {
            TaskbarLogger.Info($"[RECEIVER] Caught TaskbarCreated broadcast (Msg=0x{msg:X}) on MessageReceiver HWND 0x{hWnd.ToInt64():X}. Requesting recovery.");
            _recovery?.RequestRecovery("MessageReceiver:TaskbarCreatedBroadcast");
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_DESTROY:
                TaskbarLogger.Debug($"[RECEIVER] MessageReceiver HWND 0x{hWnd.ToInt64():X} received WM_DESTROY.");
                return IntPtr.Zero;

            case WM_NCDESTROY:
                TaskbarLogger.Debug($"[RECEIVER] MessageReceiver HWND 0x{hWnd.ToInt64():X} received WM_NCDESTROY.");
                _messageReceiverHwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                int width = (short)(lParam.ToInt64() & 0xFFFF);
                int height = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                UpdateHostSize(width, height);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1); // Suppress background repaint flicker

            case WM_DESTROY:
                TaskbarLogger.Debug($"[HOST] Ephemeral Host HWND 0x{hWnd.ToInt64():X} received WM_DESTROY.");
                return IntPtr.Zero;

            case WM_NCDESTROY:
                bool isMatchingHost = (_hostHwnd == hWnd);
                bool isIntentional = _isIntentionalHostTeardown;
                bool isDisposing = _isDisposed;

                TaskbarLogger.Info($"[HOST] Ephemeral Host HWND 0x{hWnd.ToInt64():X} received WM_NCDESTROY (MatchingHost={isMatchingHost}, Intentional={isIntentional}, Disposing={isDisposing}).");

                if (isMatchingHost)
                {
                    _hostHwnd = IntPtr.Zero;
                }

                if (!isDisposing && !isIntentional && isMatchingHost)
                {
                    _isAttached = false;
                    AttachmentChanged?.Invoke(false);
                    TaskbarLogger.Warn("[HOST] Unexpected active host destruction detected. Requesting Explorer recovery pipeline.");
                    _recovery?.RequestRecovery("HostWndProc:UnexpectedWM_NCDESTROY");
                }
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Reasserts the active host window at the top of the Shell_TrayWnd child sibling Z-order.
    /// Validates handle liveness and parent relationship before invoking SetWindowPos(HWND_TOP).
    /// </summary>
    public bool EnsureHostZOrder()
    {
        if (_isDisposed || _hostHwnd == IntPtr.Zero || _taskbarHwnd == IntPtr.Zero)
        {
            return false;
        }

        bool isHostValid = IsWindowTestHook?.Invoke(_hostHwnd) ?? IsWindow(_hostHwnd);
        bool isTaskbarValid = IsWindowTestHook?.Invoke(_taskbarHwnd) ?? IsWindow(_taskbarHwnd);
        if (!isHostValid || !isTaskbarValid)
        {
            return false;
        }

        IntPtr parent = GetParentTestHook?.Invoke(_hostHwnd) ?? GetParent(_hostHwnd);
        if (parent != _taskbarHwnd)
        {
            return false;
        }

        bool success;
        if (SetWindowPosTestHook != null)
        {
            success = SetWindowPosTestHook(_hostHwnd, HWND_TOP, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        else
        {
            success = SetWindowPos(
                _hostHwnd,
                HWND_TOP,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        if (success)
        {
            TaskbarLogger.Debug($"[ZORDER] Successfully asserted host 0x{_hostHwnd.ToInt64():X} to HWND_TOP in Shell_TrayWnd 0x{_taskbarHwnd.ToInt64():X}.");
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            TaskbarLogger.Warn($"[ZORDER] SetWindowPos(HWND_TOP) failed for host 0x{_hostHwnd.ToInt64():X}. Win32Error={err}");
        }

        return success;
    }

    private void StartPostRecoveryStabilization(IntPtr targetHost, IntPtr targetTaskbar)
    {
        if (_isDisposed || targetHost == IntPtr.Zero || targetTaskbar == IntPtr.Zero)
        {
            return;
        }

        CancelPostRecoveryStabilization();

        int gen = Interlocked.Increment(ref _stabilizationGeneration);
        var cts = new CancellationTokenSource();
        _stabilizationCts = cts;
        var token = cts.Token;
        int[] delays = CustomStabilizationDelaysMsForTesting ?? PostRecoveryStabilizationDelaysMs;

        Action work = async () =>
        {
            foreach (int delayMs in delays)
            {
                try
                {
                    await Task.Delay(delayMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_isDisposed || token.IsCancellationRequested)
                {
                    return;
                }

                // Guard against stale callbacks targeting an older/replaced host
                if (_stabilizationGeneration != gen || _hostHwnd != targetHost || _taskbarHwnd != targetTaskbar)
                {
                    TaskbarLogger.Debug("[ZORDER] Stabilization aborted: host generation or HWND identity changed.");
                    return;
                }

                if (!EnsureHostZOrder())
                {
                    TaskbarLogger.Debug("[ZORDER] Stabilization aborted: host or taskbar handle no longer valid/parented.");
                    return;
                }

                StabilizationTickForTesting?.Invoke(gen);
            }
        };

        if (CustomDispatcherForTesting != null)
        {
            CustomDispatcherForTesting(work);
        }
        else if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() => work());
        }
        else
        {
            Task.Run(work);
        }
    }

    private void CancelPostRecoveryStabilization()
    {
        Interlocked.Increment(ref _stabilizationGeneration);
        try
        {
            _stabilizationCts?.Cancel();
            _stabilizationCts?.Dispose();
        }
        catch { }
        finally
        {
            _stabilizationCts = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        CancelPostRecoveryStabilization();
        _isDisposed = true;
        _isIntentionalHostTeardown = true;

        TaskbarLogger.Info("[SHUTDOWN] Disposing TaskbarHost, MessageReceiver, and cleaning up taskbar hooks...");

        try
        {
            _watcher?.Dispose();
            _watcher = null;

            _recovery?.Dispose();
            _recovery = null;

            if (_hostHwnd != IntPtr.Zero)
            {
                TaskbarInjection.Detach(_hostHwnd);
            }

            if (_xamlSource != null)
            {
                _xamlSource.Dispose();
                _xamlSource = null;
            }

            if (_hostHwnd != IntPtr.Zero)
            {
                DestroyWindow(_hostHwnd);
                _hostHwnd = IntPtr.Zero;
            }

            if (_messageReceiverHwnd != IntPtr.Zero)
            {
                DestroyWindow(_messageReceiverHwnd);
                _messageReceiverHwnd = IntPtr.Zero;
            }

            IntPtr hInstance = GetModuleHandle(null);

            if (_hostClassRegistered)
            {
                UnregisterClass(HostWindowClassName, hInstance);
                _hostClassRegistered = false;
            }

            if (_receiverClassRegistered)
            {
                UnregisterClass(MessageReceiverClassName, hInstance);
                _receiverClassRegistered = false;
            }
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("[SHUTDOWN] Exception during TaskbarHost disposal.", ex);
        }
    }

    internal delegate bool TryGetPlacementDelegate(IntPtr taskbarHwnd, out TaskbarPlacement placement);
    internal TryGetPlacementDelegate? TryGetPlacementHook { get; set; }
    internal Func<IntPtr, bool>? IsWindowTestHook { get; set; }
    internal Func<IntPtr, IntPtr>? GetParentTestHook { get; set; }
    internal Action<Action>? CustomDispatcherForTesting { get; set; }
    internal int[]? CustomStabilizationDelaysMsForTesting { get; set; }
    internal event Action<int>? StabilizationTickForTesting;
    internal void SetHostHwndForTesting(IntPtr hwnd) => _hostHwnd = hwnd;
    internal void SetTaskbarHwndForTesting(IntPtr hwnd) => _taskbarHwnd = hwnd;
    internal void SetIsAttachedForTesting(bool isAttached) => _isAttached = isAttached;
    internal void SetRecoveryForTesting(ExplorerRecovery recovery) => _recovery = recovery;
    internal void SetIntentionalTeardownForTesting(bool intentional) => _isIntentionalHostTeardown = intentional;
    internal int StabilizationGenerationForTesting => _stabilizationGeneration;
    internal bool HasPendingStabilizationForTesting => _stabilizationCts != null && !_stabilizationCts.IsCancellationRequested;
    internal void StartPostRecoveryStabilizationForTesting(IntPtr host, IntPtr taskbar) => StartPostRecoveryStabilization(host, taskbar);
    internal void CancelPostRecoveryStabilizationForTesting() => CancelPostRecoveryStabilization();
    internal Func<IntPtr, IntPtr, uint, bool>? SetWindowPosTestHook { get; set; }
    internal IntPtr HandleHostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => HostWndProc(hWnd, msg, wParam, lParam);
    internal IntPtr HandleReceiverMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) => MessageReceiverWndProc(hWnd, msg, wParam, lParam);
}
