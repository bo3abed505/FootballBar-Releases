using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FootballTray.Client;

/// <summary>
/// The application window. This hosts a Frame that displays pages.
/// Intercepts window closing and minimizing to hide to the system tray instead of taskbar/exiting.
/// </summary>
public sealed partial class MainWindow : Window
{
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private readonly SubclassProc _subclassProc;

    public bool IsClosingPermitted { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);
        AppWindow.Title = "FootballBar";

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclassProc = WindowSubclass;
        SetWindowSubclass(hWnd, _subclassProc, 1, IntPtr.Zero);

        AppWindow.Closing += (sender, args) =>
        {
            if (!IsClosingPermitted)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    public void Navigate(Type sourcePageType, object? parameter = null)
    {
        RootFrame.Navigate(sourcePageType, parameter);
    }

    private IntPtr WindowSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == 0x0112 && (wParam.ToInt32() & 0xFFF0) == 0xF020)
        {
            ShowWindow(hWnd, 0);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
