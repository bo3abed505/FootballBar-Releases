using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;

namespace FootballTray.Client.Services.Daemon;

/// <summary>
/// Manages the lifecycle of the local Node.js Fastify backend engine.
/// Ensures the background service is running before client sync begins,
/// and cleanly terminates spawned child processes upon application exit.
/// </summary>
public sealed class DaemonLifecycleManager : IDisposable
{
    private readonly string _baseUrl;
    private readonly string? _customBackendDirectory;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private Process? _daemonProcess;
    private bool _ownsProcess;
    private bool _isDisposed;

    public bool IsProcessOwned => _ownsProcess;
    public int? ProcessId => _daemonProcess?.Id;
    public bool IsProcessRunning => _daemonProcess != null && !_daemonProcess.HasExited;

    public DaemonLifecycleManager(
        string? baseUrl = null,
        string? customBackendDirectory = null,
        HttpClient? httpClient = null)
    {
        _baseUrl = (baseUrl ?? FootballApiClient.GetDefaultBaseUrl()).TrimEnd('/');
        _customBackendDirectory = customBackendDirectory;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2),
            };
            _disposeClient = true;
        }
    }

    /// <summary>
    /// Checks if the backend health endpoint responds with HTTP 200 OK.
    /// </summary>
    public async Task<bool> IsDaemonHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var healthUrl = $"{_baseUrl}/health";
            using var response = await _httpClient.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the absolute path to the backend directory containing dist/server.js.
    /// </summary>
    public string? ResolveBackendDirectory()
    {
        // 1. Explicitly configured custom directory
        if (!string.IsNullOrWhiteSpace(_customBackendDirectory) && Directory.Exists(_customBackendDirectory))
        {
            if (File.Exists(Path.Combine(_customBackendDirectory, "dist", "server.js")))
            {
                return Path.GetFullPath(_customBackendDirectory);
            }
        }

        // 2. Environment variable override
        string? envDir = Environment.GetEnvironmentVariable("FOOTBALLTRAY_BACKEND_DIR");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
        {
            if (File.Exists(Path.Combine(envDir, "dist", "server.js")))
            {
                return Path.GetFullPath(envDir);
            }
        }

        // 3. Application base directory relative (packaged deployment layout)
        string baseDir = AppContext.BaseDirectory;
        string packagedPath = Path.Combine(baseDir, "backend");
        if (Directory.Exists(packagedPath) && File.Exists(Path.Combine(packagedPath, "dist", "server.js")))
        {
            return Path.GetFullPath(packagedPath);
        }

        // 4. Upward traversal from AppContext.BaseDirectory to find solution root backend/
        try
        {
            var dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "backend");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "dist", "server.js")))
                {
                    return Path.GetFullPath(candidate);
                }
                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Daemon", "TraversalFailed", $"Error during upward backend directory traversal: {ex.Message}", ex: ex);
        }

        return null;
    }

    /// <summary>
    /// Ensures the backend daemon is running. If not already active, launches node dist/server.js
    /// in the background without creating a console window.
    /// </summary>
    public async Task<bool> StartDaemonAsync(string activeProvider = "api_football", CancellationToken cancellationToken = default)
    {
        // 1. Pre-flight check: Is the backend already running?
        if (await IsDaemonHealthyAsync(cancellationToken))
        {
            AppLogger.Info("Daemon", "AlreadyRunning", $"Backend daemon is already responding at {_baseUrl}.");
            _ownsProcess = false;
            return true;
        }

        // 2. Pre-launch check: If port is occupied by an unresponsive/zombie process, terminate it to prevent EADDRINUSE
        int targetPort = 4000;
        if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri) && baseUri.Port > 0)
        {
            targetPort = baseUri.Port;
        }

        FreePortIfOccupied(targetPort);

        // 3. Locate backend directory
        string? backendDir = ResolveBackendDirectory();
        if (string.IsNullOrWhiteSpace(backendDir))
        {
            AppLogger.Warn("Daemon", "DirectoryNotFound", "Could not locate backend directory with compiled dist/server.js.");
            return false;
        }

        string absoluteBackendDir = Path.GetFullPath(backendDir);
        string serverJsPath = Path.Combine(absoluteBackendDir, "dist", "server.js");
        if (!File.Exists(serverJsPath))
        {
            AppLogger.Warn("Daemon", "EntryNotFound", $"Backend entry point not found at '{serverJsPath}'. Ensure 'npm run build' has been executed.");
            return false;
        }

        // Locate .env file in backend directory or parent directories
        string? envFilePath = null;
        string defaultEnvPath = Path.Combine(absoluteBackendDir, ".env");
        if (File.Exists(defaultEnvPath))
        {
            envFilePath = defaultEnvPath;
        }
        else
        {
            var searchDir = Directory.GetParent(absoluteBackendDir);
            while (searchDir != null)
            {
                var cand = Path.Combine(searchDir.FullName, ".env");
                if (File.Exists(cand))
                {
                    envFilePath = cand;
                    break;
                }
                var candBackend = Path.Combine(searchDir.FullName, "backend", ".env");
                if (File.Exists(candBackend))
                {
                    envFilePath = candBackend;
                    break;
                }
                searchDir = searchDir.Parent;
            }
        }

        // 3. Configure hidden background process
        string nodeExe = "node.exe";
        if (File.Exists(@"C:\Program Files\nodejs\node.exe"))
        {
            nodeExe = @"C:\Program Files\nodejs\node.exe";
        }

        string arguments = !string.IsNullOrEmpty(envFilePath)
            ? $"--env-file=\"{envFilePath}\" \"{serverJsPath}\""
            : $"\"{serverJsPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = arguments,
            WorkingDirectory = absoluteBackendDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Explicitly enforce production environment to eliminate mock fallback
        startInfo.EnvironmentVariables["NODE_ENV"] = "production";
        startInfo.EnvironmentVariables["ACTIVE_PROVIDER"] = activeProvider;

        if (!string.IsNullOrEmpty(envFilePath))
        {
            startInfo.EnvironmentVariables["DOTENV_CONFIG_PATH"] = envFilePath;
            try
            {
                foreach (var line in File.ReadAllLines(envFilePath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
                    int eqIndex = trimmed.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        string key = trimmed.Substring(0, eqIndex).Trim();
                        string val = trimmed.Substring(eqIndex + 1).Trim().Trim('"', '\'');
                        if (!string.IsNullOrEmpty(key))
                        {
                            startInfo.EnvironmentVariables[key] = val;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Daemon", "EnvParseFailed", $"Could not parse .env file at '{envFilePath}': {ex.Message}");
            }

            // Strictly enforce production environment to prevent downgrade by local .env settings
            startInfo.EnvironmentVariables["NODE_ENV"] = "production";
            startInfo.EnvironmentVariables["ACTIVE_PROVIDER"] = activeProvider;
        }

        try
        {
            AppLogger.Info("Daemon", "Launching", $"Spawning Node.js backend daemon from '{backendDir}'...");
            _daemonProcess = new Process { StartInfo = startInfo };

            // Discard/drain standard streams to prevent pipe buffer deadlock
            _daemonProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    AppLogger.Debug("Daemon", "StdOut", e.Data);
                }
            };
            _daemonProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    AppLogger.Warn("Daemon", "StdErr", e.Data);
                }
            };

            bool started = _daemonProcess.Start();
            if (!started)
            {
                AppLogger.Error("Daemon", "StartFailed", "Process.Start() returned false when launching node.");
                return false;
            }

            _daemonProcess.BeginOutputReadLine();
            _daemonProcess.BeginErrorReadLine();
            _ownsProcess = true;

            AppLogger.Info("Daemon", "Started", $"Node.js backend daemon started with PID {_daemonProcess.Id}.");

            // 4. Poll health endpoint until online (up to 8 seconds)
            var sw = Stopwatch.StartNew();
            var maxWait = TimeSpan.FromSeconds(8);

            while (sw.Elapsed < maxWait)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    StopDaemon();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (_daemonProcess.HasExited)
                {
                    AppLogger.Error(
                        "Daemon",
                        "PrematureExit",
                        $"Daemon process (PID {_daemonProcess.Id}) exited prematurely with exit code {_daemonProcess.ExitCode}.");
                    _ownsProcess = false;
                    return false;
                }

                if (await IsDaemonHealthyAsync(cancellationToken))
                {
                    AppLogger.Info("Daemon", "Ready", $"Backend daemon is online and healthy after {sw.ElapsedMilliseconds}ms.");
                    return true;
                }

                await Task.Delay(250, cancellationToken);
            }

            AppLogger.Warn("Daemon", "StartupTimeout", $"Backend daemon did not report healthy within {maxWait.TotalSeconds}s.");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Daemon", "LaunchException", $"Failed to launch backend daemon: {ex.Message}", ex: ex);
            StopDaemon();
            return false;
        }
    }

    /// <summary>
    /// Cleanly terminates the spawned Node.js backend process tree.
    /// </summary>
    public void StopDaemon()
    {
        if (!_ownsProcess || _daemonProcess == null)
        {
            return;
        }

        try
        {
            if (!_daemonProcess.HasExited)
            {
                int pid = _daemonProcess.Id;
                AppLogger.Info("Daemon", "Stopping", $"Terminating spawned backend daemon (PID {pid})...");
                _daemonProcess.Kill(entireProcessTree: true);
                _daemonProcess.WaitForExit(3000);
                AppLogger.Info("Daemon", "Stopped", $"Backend daemon (PID {pid}) terminated.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Daemon", "StopFailed", $"Exception while stopping daemon: {ex.Message}", ex: ex);
        }
        finally
        {
            _daemonProcess.Dispose();
            _daemonProcess = null;
            _ownsProcess = false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopDaemon();

        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Checks if the target port is held by an unresponsive process and terminates it
    /// so the new daemon can bind without encountering EADDRINUSE.
    /// </summary>
    public static void FreePortIfOccupied(int port)
    {
        try
        {
            var pids = FindPidsListeningOnPort(port);
            foreach (var pid in pids)
            {
                if (pid > 0 && pid != Process.GetCurrentProcess().Id)
                {
                    AppLogger.Warn("Daemon", "OrphanedProcessDetected", $"Port {port} is occupied by unresponsive process (PID {pid}). Terminating orphan process...");
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                        AppLogger.Info("Daemon", "OrphanTerminated", $"Successfully terminated orphan process PID {pid} on port {port}.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("Daemon", "KillOrphanFailed", $"Could not kill process PID {pid}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Daemon", "PortCheckFailed", $"Error checking port {port}: {ex.Message}");
        }
    }

    /// <summary>
    /// Locates the PIDs of any process currently listening on the specified TCP port.
    /// </summary>
    public static System.Collections.Generic.List<int> FindPidsListeningOnPort(int port)
    {
        var result = new System.Collections.Generic.List<int>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null) return result;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);

            string targetPortStr = $":{port}";
            using var reader = new StringReader(output);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!trimmed.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && parts[1].EndsWith(targetPortStr, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(parts[parts.Length - 1], out int pid) && !result.Contains(pid))
                    {
                        result.Add(pid);
                    }
                }
            }
        }
        catch
        {
            // Fallback silent
        }

        return result;
    }
}
