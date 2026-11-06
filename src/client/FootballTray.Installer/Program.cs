using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FootballTray.Installer;

internal static class Program
{
    private const string AppDisplayName = "FootballBar";
    private const string AppExecutableName = "FootballTray.exe";
    private const string LegacyAppName = "FootballTray";
    private const string UninstallExeName = "uninstall.exe";
    private const string RegistryUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FootballBar";
    private const string LegacyRegistryUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FootballTray";
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_ICONERROR = 0x00000010;

    [STAThread]
    public static int Main(string[] args)
    {
        bool isUninstall = args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(a, "-u", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase));
        bool isSilent = args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(a, "/s", StringComparison.OrdinalIgnoreCase));

        try
        {
            if (isUninstall)
            {
                return PerformUninstall(isSilent);
            }

            if (isSilent)
            {
                PerformInstallCore(createDesktopShortcut: true,
                                   createStartMenuShortcut: true,
                                   startWithWindows: false,
                                   launchImmediately: true,
                                   progress: null);
                return 0;
            }

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var wizard = new SetupWizardForm();
            Application.Run(wizard);
            return wizard.ExitCode;
        }
        catch (Exception ex)
        {
            if (!isSilent)
            {
                MessageBoxW(IntPtr.Zero, $"Setup error: {ex.Message}", $"{AppDisplayName} Setup", MB_OK | MB_ICONERROR);
            }
            return 1;
        }
    }

    public static void PerformInstallCore(
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool startWithWindows,
        bool launchImmediately,
        IProgress<(int percent, string message)>? progress)
    {
        progress?.Report((10, "Closing running instances..."));
        StopRunningProcesses();

        progress?.Report((25, "Cleaning legacy installation..."));
        CleanLegacyInstallation();

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string installDir = Path.Combine(localAppData, "Programs", AppDisplayName);
        Directory.CreateDirectory(installDir);

        string fullDestDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        progress?.Report((45, "Extracting application binaries..."));
        using (var stream = GetPayloadStream())
        {
            if (stream == null)
            {
                throw new InvalidOperationException("Installation payload archive not found inside installer package.");
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            int total = archive.Entries.Count;
            int count = 0;
            foreach (var entry in archive.Entries)
            {
                count++;
                if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry

                string fullEntryPath = Path.GetFullPath(Path.Combine(fullDestDir, entry.FullName));
                if (!fullEntryPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Blocked potentially malicious zip entry path traversal.");
                }

                string? destDir = Path.GetDirectoryName(fullEntryPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                entry.ExtractToFile(fullEntryPath, overwrite: true);

                if (total > 0 && count % 5 == 0)
                {
                    int pct = 45 + (int)((count / (float)total) * 35);
                    progress?.Report((pct, $"Extracting {entry.Name}..."));
                }
            }
        }

        // Copy uninstaller executable
        try
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (File.Exists(currentExe))
            {
                string targetUninstallExe = Path.Combine(installDir, UninstallExeName);
                if (!string.Equals(currentExe, targetUninstallExe, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(currentExe, targetUninstallExe, overwrite: true);
                }
            }
        }
        catch { }

        string targetAppExe = Path.Combine(installDir, AppExecutableName);

        // Clean up any legacy FootballBar.exe that may have been created by previous installer builds
        string brandExe = Path.Combine(installDir, $"{AppDisplayName}.exe");
        try
        {
            if (File.Exists(brandExe))
            {
                File.Delete(brandExe);
            }
        }
        catch { }

        progress?.Report((85, "Creating shortcuts and configuring system..."));

        // Shortcuts
        if (createStartMenuShortcut)
        {
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
            Directory.CreateDirectory(startMenuDir);
            string startMenuShortcut = Path.Combine(startMenuDir, $"{AppDisplayName}.lnk");
            CreateShortcut(startMenuShortcut, targetAppExe, installDir, $"{AppDisplayName} - Live Football Tracker");
        }

        if (createDesktopShortcut)
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopShortcut = Path.Combine(desktopDir, $"{AppDisplayName}.lnk");
            CreateShortcut(desktopShortcut, targetAppExe, installDir, $"{AppDisplayName} - Live Football Tracker");
        }

        // Windows Startup Run Key
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (runKey != null)
            {
                if (startWithWindows)
                {
                    runKey.SetValue(AppDisplayName, $"\"{targetAppExe}\"");
                }
                else
                {
                    runKey.DeleteValue(AppDisplayName, throwOnMissingValue: false);
                }
            }
        }
        catch { }

        // Register in Windows Add or Remove Programs (HKCU)
        string version = GetAppVersion();
        RegisterUninstall(installDir, targetAppExe, version);

        progress?.Report((100, "Setup complete!"));

        // Launch if requested
        if (launchImmediately && File.Exists(targetAppExe))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetAppExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private static void CleanLegacyInstallation()
    {
        try
        {
            // 1. Remove old shortcuts from Desktop and Start Menu
            string oldStartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs", $"{LegacyAppName}.lnk");
            if (File.Exists(oldStartMenuShortcut)) File.Delete(oldStartMenuShortcut);

            string oldDesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"{LegacyAppName}.lnk");
            if (File.Exists(oldDesktopShortcut)) File.Delete(oldDesktopShortcut);

            // 2. Remove legacy Run key
            using (var runKey = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true))
            {
                runKey?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }

            // 3. Remove legacy Uninstall registry key
            Registry.CurrentUser.DeleteSubKeyTree(LegacyRegistryUninstallKey, throwOnMissingSubKey: false);

            // 4. Remove legacy installation binaries directory: %LOCALAPPDATA%\Programs\FootballTray
            // Note: Does NOT touch %LOCALAPPDATA%\FootballTray user data/preferences!
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string legacyInstallDir = Path.Combine(localAppData, "Programs", LegacyAppName);
            if (Directory.Exists(legacyInstallDir))
            {
                Directory.Delete(legacyInstallDir, recursive: true);
            }
        }
        catch { }
    }

    private static int PerformUninstall(bool isSilent)
    {
        StopRunningProcesses();

        // 1. Remove shortcuts
        try
        {
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs");
            string sc1 = Path.Combine(startMenuDir, $"{AppDisplayName}.lnk");
            if (File.Exists(sc1)) File.Delete(sc1);
            string sc2 = Path.Combine(startMenuDir, $"{LegacyAppName}.lnk");
            if (File.Exists(sc2)) File.Delete(sc2);

            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string d1 = Path.Combine(desktopDir, $"{AppDisplayName}.lnk");
            if (File.Exists(d1)) File.Delete(d1);
            string d2 = Path.Combine(desktopDir, $"{LegacyAppName}.lnk");
            if (File.Exists(d2)) File.Delete(d2);
        }
        catch { }

        // 2. Remove Registry Run and Uninstall keys
        try
        {
            using (var runKey = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true))
            {
                runKey?.DeleteValue(AppDisplayName, throwOnMissingValue: false);
                runKey?.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree(RegistryUninstallKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(LegacyRegistryUninstallKey, throwOnMissingSubKey: false);
        }
        catch { }

        // 3. Remove installation files
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string installDir = Path.Combine(localAppData, "Programs", AppDisplayName);
        string legacyInstallDir = Path.Combine(localAppData, "Programs", LegacyAppName);

        ScheduleDirectoryRemoval(installDir);
        ScheduleDirectoryRemoval(legacyInstallDir);

        if (!isSilent)
        {
            MessageBoxW(IntPtr.Zero, $"{AppDisplayName} was successfully removed from your computer.", $"{AppDisplayName} Uninstall", MB_OK | MB_ICONINFORMATION);
        }

        return 0;
    }

    private static void ScheduleDirectoryRemoval(string directory)
    {
        if (Directory.Exists(directory))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 1 /nobreak > NUL & rmdir /s /q \"{directory}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }
    }

    private static void StopRunningProcesses()
    {
        string[] processNames = { "FootballTray", "FootballBar" };
        foreach (var name in processNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var p in processes)
                {
                    try
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(1500))
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }
        }
    }

    private static Stream? GetPayloadStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        var payloadName = names.FirstOrDefault(n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(payloadName))
        {
            return assembly.GetManifestResourceStream(payloadName);
        }

        // Development fallback: look next to installer
        var baseDir = AppContext.BaseDirectory;
        var zipFiles = Directory.GetFiles(baseDir, "*.zip");
        if (zipFiles.Length > 0)
        {
            return File.OpenRead(zipFiles[0]);
        }

        return null;
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.IconLocation = $"{targetPath},0";
            shortcut.Description = description;
            shortcut.Save();
        }
        catch { }
    }

    private static void RegisterUninstall(string installDir, string appExe, string version)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryUninstallKey);
            if (key == null) return;

            string uninstallExe = Path.Combine(installDir, UninstallExeName);
            key.SetValue("DisplayName", AppDisplayName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", AppDisplayName);
            key.SetValue("DisplayIcon", $"{appExe},0");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{uninstallExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" --uninstall --silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static string GetAppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plus = infoVer.IndexOf('+');
            return plus > 0 ? infoVer[..plus] : infoVer;
        }

        return asm.GetName().Version?.ToString(3) ?? "1.3.1";
    }
}

/// <summary>
/// Compact Dark-Mode Setup Wizard Dialog (Style B).
/// Dimensions: 480x360, Background: #0F172A, Card: #1E293B, Accent: #0284C7.
/// </summary>
internal sealed class SetupWizardForm : Form
{
    private readonly CheckBox _chkDesktop;
    private readonly CheckBox _chkStartMenu;
    private readonly CheckBox _chkStartup;
    private readonly CheckBox _chkLaunch;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblStatus;
    private readonly Button _btnInstall;
    private readonly Button _btnCancel;
    private bool _isInstalling;

    public int ExitCode { get; private set; } = 0;

    public SetupWizardForm()
    {
        string version = Program.GetAppVersion();

        Text = $"FootballBar Setup";
        ClientSize = new Size(480, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 23, 42); // #0F172A
        ForeColor = Color.FromArgb(248, 250, 252); // #F8FAFC
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);

        // Header Title
        var lblTitle = new Label
        {
            Text = $"FootballBar v{version} Setup",
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(248, 250, 252),
            Location = new Point(24, 18),
            AutoSize = true
        };
        Controls.Add(lblTitle);

        // Header Subtitle
        var lblSubtitle = new Label
        {
            Text = "Live Football Scores & Tactical Telemetry on Your Taskbar",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(148, 163, 184), // #94A3B8
            Location = new Point(26, 46),
            AutoSize = true
        };
        Controls.Add(lblSubtitle);

        // Options Card Panel (#1E293B)
        var pnlCard = new Panel
        {
            Location = new Point(24, 74),
            Size = new Size(432, 150),
            BackColor = Color.FromArgb(30, 41, 59), // #1E293B
        };

        _chkDesktop = new CheckBox
        {
            Text = "Create Desktop shortcut",
            Checked = true,
            Location = new Point(16, 14),
            AutoSize = true,
            ForeColor = Color.FromArgb(248, 250, 252),
            FlatStyle = FlatStyle.Flat
        };
        pnlCard.Controls.Add(_chkDesktop);

        _chkStartMenu = new CheckBox
        {
            Text = "Create Start Menu shortcut",
            Checked = true,
            Location = new Point(16, 44),
            AutoSize = true,
            ForeColor = Color.FromArgb(248, 250, 252),
            FlatStyle = FlatStyle.Flat
        };
        pnlCard.Controls.Add(_chkStartMenu);

        _chkStartup = new CheckBox
        {
            Text = "Start FootballBar automatically when Windows starts",
            Checked = true,
            Location = new Point(16, 74),
            AutoSize = true,
            ForeColor = Color.FromArgb(248, 250, 252),
            FlatStyle = FlatStyle.Flat
        };
        pnlCard.Controls.Add(_chkStartup);

        _chkLaunch = new CheckBox
        {
            Text = "Launch FootballBar when setup finishes",
            Checked = true,
            Location = new Point(16, 104),
            AutoSize = true,
            ForeColor = Color.FromArgb(248, 250, 252),
            FlatStyle = FlatStyle.Flat
        };
        pnlCard.Controls.Add(_chkLaunch);

        Controls.Add(pnlCard);

        // Progress Bar
        _progressBar = new ProgressBar
        {
            Location = new Point(24, 238),
            Size = new Size(432, 6),
            Style = ProgressBarStyle.Continuous,
            Value = 0,
            Maximum = 100
        };
        Controls.Add(_progressBar);

        // Status Label
        _lblStatus = new Label
        {
            Text = "Ready to install FootballBar.",
            Location = new Point(24, 250),
            Size = new Size(432, 20),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.25f, FontStyle.Regular)
        };
        Controls.Add(_lblStatus);

        // Bottom Action Buttons
        _btnInstall = new Button
        {
            Text = "Install",
            Location = new Point(252, 280),
            Size = new Size(104, 34),
            BackColor = Color.FromArgb(2, 132, 199), // #0284C7
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnInstall.FlatAppearance.BorderSize = 0;
        _btnInstall.Click += OnInstallClick;
        Controls.Add(_btnInstall);

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(366, 280),
            Size = new Size(90, 34),
            BackColor = Color.FromArgb(51, 65, 85), // #334155
            ForeColor = Color.FromArgb(248, 250, 252),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _btnCancel.FlatAppearance.BorderSize = 0;
        _btnCancel.Click += (s, e) => Close();
        Controls.Add(_btnCancel);
    }

    private async void OnInstallClick(object? sender, EventArgs e)
    {
        if (_isInstalling) return;
        _isInstalling = true;

        _btnInstall.Enabled = false;
        _btnCancel.Enabled = false;
        _chkDesktop.Enabled = false;
        _chkStartMenu.Enabled = false;
        _chkStartup.Enabled = false;
        _chkLaunch.Enabled = false;

        bool desktop = _chkDesktop.Checked;
        bool startMenu = _chkStartMenu.Checked;
        bool startup = _chkStartup.Checked;
        bool launch = _chkLaunch.Checked;

        var progress = new Progress<(int percent, string message)>(update =>
        {
            _progressBar.Value = Math.Clamp(update.percent, 0, 100);
            _lblStatus.Text = update.message;
        });

        try
        {
            await Task.Run(() =>
            {
                Program.PerformInstallCore(
                    createDesktopShortcut: desktop,
                    createStartMenuShortcut: startMenu,
                    startWithWindows: startup,
                    launchImmediately: launch,
                    progress: progress);
            });

            _progressBar.Value = 100;
            _lblStatus.Text = "Installation completed successfully!";
            _btnCancel.Text = "Close";
            _btnCancel.Enabled = true;
            _btnInstall.Visible = false;

            if (launch)
            {
                await Task.Delay(500);
                DialogResult = DialogResult.OK;
                Close();
                Application.Exit();
            }
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"Error: {ex.Message}";
            _progressBar.Value = 0;
            _btnInstall.Enabled = true;
            _btnCancel.Enabled = true;
            _isInstalling = false;
            ExitCode = 1;
            MessageBox.Show(this, $"Installation failed: {ex.Message}", "FootballBar Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
