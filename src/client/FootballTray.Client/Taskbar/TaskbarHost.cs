using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
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

    private bool _isDisposed;
    private bool _receiverClassRegistered;
    private bool _hostClassRegistered;
    private uint _taskbarCreatedMsgId;
    private bool _isAttached;

    public bool IsAttached => _isAttached;
    public IntPtr MessageReceiverHwnd => _messageReceiverHwnd;
    public IntPtr HostHwnd => _hostHwnd;
    public IntPtr TaskbarHwnd => _taskbarHwnd;

    public event Action? Repositioned;
    public event Action<bool>? AttachmentChanged;

    public TaskbarHost(DispatcherQueue? dispatcherQueue = null)
    {
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        
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

        // 3. Initialize Taskbar Watcher
        _watcher = new TaskbarWatcher(_dispatcherQueue);
        _watcher.TaskbarChanged += () =>
        {
            Reposition();
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

        // 3. Detach and destroy old host HWND if it still exists
        if (_hostHwnd != IntPtr.Zero)
        {
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
            }
        }

        // 4. Find the current/new primary Shell_TrayWnd
        IntPtr newShellTray = TaskbarInjection.FindPrimaryTaskbar();
        if (newShellTray == IntPtr.Zero)
        {
            TaskbarLogger.Warn("[RECOVERY] Primary taskbar (Shell_TrayWnd) not found. Awaiting recovery retry...");
            _taskbarHwnd = IntPtr.Zero;
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        _taskbarHwnd = newShellTray;
        TaskbarLogger.Info($"[RECOVERY] Discovered primary Shell_TrayWnd HWND: 0x{_taskbarHwnd.ToInt64():X}");

        // 5. Calculate fresh taskbar geometry
        if (!TaskbarGeometry.TryGetPrimaryTaskbarPlacement(_taskbarHwnd, out TaskbarPlacement placement))
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
        if (attached)
        {
            _isAttached = true;
            ShowWindow(_hostHwnd, 5 /* SW_SHOW */);
            _watcher?.Start(_taskbarHwnd);

            TaskbarLogger.Info($"[RECOVERY] RecreateAndAttachAsync completed successfully. Host 0x{_hostHwnd.ToInt64():X} docked into Shell_TrayWnd 0x{_taskbarHwnd.ToInt64():X}.");
            AttachmentChanged?.Invoke(true);
            Repositioned?.Invoke();
        }
        else
        {
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            TaskbarLogger.Warn("[RECOVERY] TaskbarInjection.Attach failed during RecreateAndAttachAsync.");
        }

        await Task.CompletedTask;
        return attached;
    }

    /// <summary>
    /// Recalculates geometry and updates the host's position inside the taskbar.
    /// </summary>
    public bool Reposition()
    {
        if (_isDisposed || _hostHwnd == IntPtr.Zero) return false;

        if (_taskbarHwnd == IntPtr.Zero)
        {
            _taskbarHwnd = TaskbarInjection.FindPrimaryTaskbar();
            if (_taskbarHwnd == IntPtr.Zero) return false;
        }

        if (TaskbarGeometry.TryGetPrimaryTaskbarPlacement(_taskbarHwnd, out TaskbarPlacement placement))
        {
            bool moved = TaskbarInjection.UpdatePosition(_hostHwnd, placement);
            if (moved)
            {
                UpdateHostSize(placement.Width, placement.Height);
                Repositioned?.Invoke();
            }
            return moved;
        }

        return false;
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

    private bool CreatePersistentMessageReceiver()
    {
        if (_messageReceiverHwnd != IntPtr.Zero) return true;

        try
        {
            IntPtr hInstance = GetModuleHandle(null);

            WNDCLASSEX wcx = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _messageReceiverWndProcDelegate,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = MessageReceiverClassName,
                hIconSm = IntPtr.Zero
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
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = HostWindowClassName,
                    hIconSm = IntPtr.Zero
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
            TaskbarLogger.Info($"[RECEIVER] Caught TaskbarCreated broadcast (Msg=0x{msg:X}) on MessageReceiver HWND 0x{hWnd.ToInt64():X}");
            _recovery?.HandleTaskbarCreated(hWnd, msg);
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
                TaskbarLogger.Debug($"[HOST] Ephemeral Host HWND 0x{hWnd.ToInt64():X} received WM_NCDESTROY.");
                if (_hostHwnd == hWnd)
                {
                    _hostHwnd = IntPtr.Zero;
                }
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

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
}
