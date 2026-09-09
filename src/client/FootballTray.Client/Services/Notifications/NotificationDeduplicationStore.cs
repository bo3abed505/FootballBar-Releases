using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Serialization;

namespace FootballTray.Client.Services.Notifications;

public sealed class NotificationHistoryModel
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, DateTimeOffset> NotifiedKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Thread-safe, bounded, and atomic local persistence store for notification deduplication keys.
/// Ensures that alerts are not repeatedly triggered across polls, restarts, or offline recoveries.
/// </summary>
public sealed class NotificationDeduplicationStore
{
    private const string NotificationFileName = "notifications_history.json";
    private const int MaxKeyCapacity = 500;
    private static readonly TimeSpan MaxRetentionAge = TimeSpan.FromDays(7);

    private readonly string _storageDirectory;
    private readonly string _filePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _notifiedKeys = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_notifiedKeys)
            {
                return _notifiedKeys.Count;
            }
        }
    }

    public NotificationDeduplicationStore(string? storageDirectory = null)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FootballTray",
            "Cache"
        );
        _filePath = Path.Combine(_storageDirectory, NotificationFileName);
    }

    /// <summary>
    /// Checks whether a given deduplication key has already been notified.
    /// </summary>
    public bool IsAlreadyNotified(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return true;

        lock (_notifiedKeys)
        {
            return _notifiedKeys.ContainsKey(key);
        }
    }

    /// <summary>
    /// Records a deduplication key as notified in memory.
    /// </summary>
    public void MarkNotified(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (_notifiedKeys)
        {
            _notifiedKeys[key] = DateTimeOffset.UtcNow;
            PruneInMemory();
        }
    }

    /// <summary>
    /// Loads persisted notification history from disk with graceful corruption handling.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            string json;
            using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var model = JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.NotificationHistoryModel);

            if (model?.NotifiedKeys != null)
            {
                lock (_notifiedKeys)
                {
                    _notifiedKeys.Clear();
                    foreach (var kvp in model.NotifiedKeys)
                    {
                        _notifiedKeys[kvp.Key] = kvp.Value;
                    }
                    PruneInMemory();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATIONS] Failed to load deduplication history: {ex.Message}");
            // Corrupt file fails safely by resetting memory
            lock (_notifiedKeys)
            {
                _notifiedKeys.Clear();
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Atomically persists the in-memory notification history to disk using a temporary file and atomic replace.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_storageDirectory);

            NotificationHistoryModel model;
            lock (_notifiedKeys)
            {
                PruneInMemory();
                model = new NotificationHistoryModel
                {
                    SchemaVersion = 1,
                    NotifiedKeys = new Dictionary<string, DateTimeOffset>(_notifiedKeys, StringComparer.OrdinalIgnoreCase)
                };
            }

            string json = JsonSerializer.Serialize(model, FootballJsonSerializerContext.Default.NotificationHistoryModel);

            string tempFilePath = Path.Combine(_storageDirectory, $"notifications_{Guid.NewGuid():N}.tmp");

            await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(fileStream))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            if (File.Exists(_filePath))
            {
                try
                {
                    File.Replace(tempFilePath, _filePath, null);
                }
                catch
                {
                    File.Delete(_filePath);
                    File.Move(tempFilePath, _filePath);
                }
            }
            else
            {
                File.Move(tempFilePath, _filePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATIONS] Failed to save deduplication history: {ex.Message}");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private void PruneInMemory()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxRetentionAge;

        // 1. Remove expired entries
        var expiredKeys = _notifiedKeys.Where(k => k.Value < cutoff).Select(k => k.Key).ToList();
        foreach (var k in expiredKeys)
        {
            _notifiedKeys.Remove(k);
        }

        // 2. Enforce capacity limit
        if (_notifiedKeys.Count > MaxKeyCapacity)
        {
            var excess = _notifiedKeys.OrderBy(k => k.Value).Take(_notifiedKeys.Count - MaxKeyCapacity).Select(k => k.Key).ToList();
            foreach (var k in excess)
            {
                _notifiedKeys.Remove(k);
            }
        }
    }
}
