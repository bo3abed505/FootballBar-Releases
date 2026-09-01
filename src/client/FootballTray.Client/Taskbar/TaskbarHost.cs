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
    private const string WindowClassName = "FootballTray_TaskbarHostClass";

    private readonly WndProc _wndProcDelegate;
    private readonly DispatcherQueue _dispatcherQueue;
    private IntPtr _hostHwnd = IntPtr.Zero;
    private IntPtr _taskbarHwnd = IntPtr.Zero;
    private DesktopWindowXamlSource? _xamlSource;
    private UIElement? _hostedContent;
    private TaskbarWatcher? _watcher;
    private ExplorerRecovery? _recovery;
    private bool _isDisposed;
    private bool _classRegistered;
    private uint _taskbarCreatedMsgId;
    private bool _isAttached;

    public bool IsAttached => _isAttached;

    public event Action? Repositioned;
    public event Action<bool>? AttachmentChanged;

    public TaskbarHost(DispatcherQueue? dispatcherQueue = null)
    {
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        _wndProcDelegate = CustomWndProc;
        _taskbarCreatedMsgId = RegisterWindowMessage(TaskbarCreatedMessage);
        TaskbarLogger.Debug($"TaskbarCreated message registered with ID: 0x{_taskbarCreatedMsgId:X}");
    }

    /// <summary>
    /// Initializes the native HWND, embeds DesktopWindowXamlSource, attaches to Shell_TrayWnd,
    /// and starts taskbar state watching and Explorer recovery.
    /// </summary>
    public async Task<bool> InitializeAsync(UIElement content)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(TaskbarHost));

        _hostedContent = content ?? throw new ArgumentNullException(nameof(content));

        TaskbarLogger.Info("Initializing TaskbarHost...");

        // 1. Create native host HWND and XAML island
        if (!CreateNativeHost(content))
        {
            TaskbarLogger.Error("Failed to create native host HWND and XAML Island.");
            return false;
        }

        // 2. Initialize Explorer Recovery
        _recovery = new ExplorerRecovery(_dispatcherQueue, async () =>
        {
            await ReconnectAsync();
        });

        // 3. Initialize Taskbar Watcher
        _watcher = new TaskbarWatcher(_dispatcherQueue);
        _watcher.TaskbarChanged += () =>
        {
            Reposition();
        };

        // 4. Attach to primary taskbar
        bool attached = await ReconnectAsync();
        return attached;
    }

    /// <summary>
    /// Connects or reconnects the host to Shell_TrayWnd and calculates layout.
    /// </summary>
    private async Task<bool> ReconnectAsync()
    {
        if (_isDisposed) return false;

        _taskbarHwnd = TaskbarInjection.FindPrimaryTaskbar();
        if (_taskbarHwnd == IntPtr.Zero)
        {
            TaskbarLogger.Warn("Primary taskbar (Shell_TrayWnd) not found. Waiting for recovery...");
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        // Calculate initial geometry
        if (!TaskbarGeometry.TryGetPrimaryTaskbarPlacement(_taskbarHwnd, out TaskbarPlacement placement))
        {
            TaskbarLogger.Warn("Could not determine primary taskbar placement geometry.");
            _isAttached = false;
            AttachmentChanged?.Invoke(false);
            return false;
        }

        // Attach host HWND to Shell_TrayWnd
        bool attached = TaskbarInjection.Attach(_hostHwnd, _taskbarHwnd, placement);
        if (attached)
        {
            _isAttached = true;
            UpdateSize(placement.Width, placement.Height);
            _watcher?.Start(_taskbarHwnd);
            AttachmentChanged?.Invoke(true);
            Repositioned?.Invoke();
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
                UpdateSize(placement.Width, placement.Height);
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

    private bool CreateNativeHost(UIElement content, int width = 140, int height = 32)
    {
        if (_hostHwnd != IntPtr.Zero) return true;

        try
        {
            IntPtr hInstance = GetModuleHandle(null);

            // Register Window Class
            WNDCLASSEX wcx = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = _wndProcDelegate,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero
            };

            ushort atom = RegisterClassEx(ref wcx);
            if (atom != 0)
            {
                _classRegistered = true;
            }

            int exStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            int style = WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

            _hostHwnd = CreateWindowEx(
                exStyle,
                WindowClassName,
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

            if (_hostHwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                TaskbarLogger.Error($"CreateWindowEx failed with Win32Error={err}");
                return false;
            }

            TaskbarLogger.Info($"Native host HWND created: 0x{_hostHwnd.ToInt64():X}");

            // Initialize DesktopWindowXamlSource
            _xamlSource = new DesktopWindowXamlSource();
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hostHwnd);
            _xamlSource.Initialize(windowId);
            _xamlSource.Content = content;

            if (_xamlSource.SiteBridge != null)
            {
                _xamlSource.SiteBridge.MoveAndResize(new RectInt32(0, 0, width, height));
                _xamlSource.SiteBridge.Show();
            }

            return true;
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Exception while creating native host window.", ex);
            return false;
        }
    }

    private void UpdateSize(int width, int height)
    {
        if (_xamlSource?.SiteBridge != null && width > 0 && height > 0)
        {
            _xamlSource.SiteBridge.MoveAndResize(new RectInt32(0, 0, width, height));
        }
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsgId && _taskbarCreatedMsgId != 0)
        {
            TaskbarLogger.Info("Host WndProc caught TaskbarCreated broadcast message.");
            _recovery?.HandleTaskbarCreated();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_SIZE:
                int width = (short)(lParam.ToInt64() & 0xFFFF);
                int height = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                UpdateSize(width, height);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1); // Suppress background repaint flicker

            case WM_DESTROY:
                TaskbarLogger.Debug("Host HWND received WM_DESTROY.");
                return IntPtr.Zero;

            case WM_NCDESTROY:
                TaskbarLogger.Debug("Host HWND received WM_NCDESTROY.");
                _hostHwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        TaskbarLogger.Info("Disposing TaskbarHost and detaching from taskbar...");

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

            if (_classRegistered)
            {
                UnregisterClass(WindowClassName, GetModuleHandle(null));
                _classRegistered = false;
            }
        }
        catch (Exception ex)
        {
            TaskbarLogger.Error("Exception during TaskbarHost disposal.", ex);
        }
    }
}
