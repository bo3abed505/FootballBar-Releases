using System;
using System.IO;
using System.Text;

namespace FootballTray.Client.Diagnostics;

/// <summary>
/// Bounded, rotating local file log sink.
/// Appends UTF-8 logs to %LOCALAPPDATA%\FootballTray\logs\footballtray.log.
/// Rotates when file exceeds max size, retaining up to configured max archives.
/// Any disk I/O failure is silently suppressed to guarantee zero application crashes.
/// </summary>
public class FileLogSink
{
    public const long DefaultMaxSizeBytes = 5 * 1024 * 1024; // 5 MB
    public const int DefaultMaxRetainedFiles = 3;

    private readonly string _logFilePath;
    private readonly string _logDirectory;
    private readonly long _maxSizeBytes;
    private readonly int _maxRetainedFiles;
    private readonly object _lock = new();

    public string LogFilePath => _logFilePath;

    public FileLogSink(string? customLogPath = null, long maxSizeBytes = DefaultMaxSizeBytes, int maxRetainedFiles = DefaultMaxRetainedFiles)
    {
        _maxSizeBytes = maxSizeBytes;
        _maxRetainedFiles = maxRetainedFiles;

        if (!string.IsNullOrWhiteSpace(customLogPath))
        {
            _logFilePath = customLogPath;
            _logDirectory = Path.GetDirectoryName(customLogPath) ?? Path.GetTempPath();
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logDirectory = Path.Combine(localAppData, "FootballTray", "logs");
            _logFilePath = Path.Combine(_logDirectory, "footballtray.log");
        }
    }

    public void Write(DiagnosticLogEntry entry)
    {
        try
        {
            string line = entry.ToString();
            WriteRawLine(line);
        }
        catch
        {
            // Defensive: logging must never crash application
        }
    }

    public void WriteRawLine(string line)
    {
        try
        {
            lock (_lock)
            {
                EnsureDirectoryExists();
                CheckAndRotate();

                using var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Defensive: logging must never crash application
        }
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }
    }

    private void CheckAndRotate()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length < _maxSizeBytes) return;

            // Rotate existing files: footballtray.2.log -> footballtray.3.log, etc.
            string baseDir = Path.GetDirectoryName(_logFilePath)!;
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(_logFilePath);
            string ext = Path.GetExtension(_logFilePath);

            for (int i = _maxRetainedFiles; i >= 1; i--)
            {
                string currentTarget = Path.Combine(baseDir, $"{fileNameWithoutExt}.{i}{ext}");
                if (i == _maxRetainedFiles)
                {
                    if (File.Exists(currentTarget))
                    {
                        File.Delete(currentTarget);
                    }
                }
                else
                {
                    string nextTarget = Path.Combine(baseDir, $"{fileNameWithoutExt}.{i + 1}{ext}");
                    if (File.Exists(currentTarget))
                    {
                        if (File.Exists(nextTarget)) File.Delete(nextTarget);
                        File.Move(currentTarget, nextTarget);
                    }
                }
            }

            // Move active log to .1.log
            string firstRotated = Path.Combine(baseDir, $"{fileNameWithoutExt}.1{ext}");
            if (File.Exists(firstRotated)) File.Delete(firstRotated);
            File.Move(_logFilePath, firstRotated);
        }
        catch
        {
            // Rotation error suppressed to guarantee app safety
        }
    }
}
