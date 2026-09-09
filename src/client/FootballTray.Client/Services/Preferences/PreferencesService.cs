using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.Services.Preferences;

/// <summary>
/// Thread-safe client preferences service with atomic file persistence, corruption resilience, and schema versioning.
/// </summary>
public sealed class PreferencesService : IPreferencesService
{
    private const string FileName = "preferences.json";
    private const long MaxAllowedFileSizeBytes = 1024 * 1024; // 1 MB sanity bound

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private UserPreferences _currentPreferences;
    private bool _isLoaded;

    public event EventHandler<UserPreferences>? PreferencesChanged;

    public PreferencesService(string? customDirectory = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(customDirectory)
            ? customDirectory
            : LocalCacheService.GetDefaultCacheDirectory();

        Directory.CreateDirectory(baseDir);
        _filePath = Path.Combine(baseDir, FileName);
        _currentPreferences = DefaultPreferences.CreateDefault();
    }

    public UserPreferences GetPreferences()
    {
        return _currentPreferences;
    }

    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_isLoaded && File.Exists(_filePath))
            {
                return _currentPreferences;
            }

            _currentPreferences = await LoadFromDiskAsync(cancellationToken);
            _isLoaded = true;
            return _currentPreferences;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await SaveToDiskAsync(_currentPreferences, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetPreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var normalized = NormalizePreferences(preferences);
            await SaveToDiskAsync(normalized, cancellationToken);
            _currentPreferences = normalized;
            _isLoaded = true;
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task FollowTeamAsync(FollowedTeam team, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (string.IsNullOrWhiteSpace(team.Id)) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var teams = _currentPreferences.FollowedTeams?.ToList() ?? new List<FollowedTeam>();
            if (!teams.Any(t => string.Equals(t.Id, team.Id, StringComparison.OrdinalIgnoreCase)))
            {
                teams.Add(team);
                var updated = _currentPreferences with { FollowedTeams = teams };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task UnfollowTeamAsync(string teamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var teams = _currentPreferences.FollowedTeams?.ToList() ?? new List<FollowedTeam>();
            int removed = teams.RemoveAll(t => string.Equals(t.Id, teamId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                var updated = _currentPreferences with { FollowedTeams = teams };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task FollowCompetitionAsync(FollowedCompetition competition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if (string.IsNullOrWhiteSpace(competition.Id)) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var comps = _currentPreferences.FollowedCompetitions?.ToList() ?? new List<FollowedCompetition>();
            if (!comps.Any(c => string.Equals(c.Id, competition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                comps.Add(competition);
                var updated = _currentPreferences with { FollowedCompetitions = comps };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task UnfollowCompetitionAsync(string competitionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(competitionId)) return;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var comps = _currentPreferences.FollowedCompetitions?.ToList() ?? new List<FollowedCompetition>();
            int removed = comps.RemoveAll(c => string.Equals(c.Id, competitionId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                var updated = _currentPreferences with { FollowedCompetitions = comps };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task SetNotificationsGloballyEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var currentNotifs = _currentPreferences.Notifications ?? new NotificationPreferences();
            if (currentNotifs.GlobalEnabled != enabled)
            {
                var updatedNotifs = currentNotifs with { GlobalEnabled = enabled };
                var updated = _currentPreferences with { Notifications = updatedNotifs };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task SetNotificationTypeEnabledAsync(NotificationType type, bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var currentNotifs = _currentPreferences.Notifications ?? new NotificationPreferences();
            var types = currentNotifs.EnabledTypes?.ToList() ?? new List<NotificationType>();

            bool changed = false;
            if (enabled && !types.Contains(type))
            {
                types.Add(type);
                changed = true;
            }
            else if (!enabled && types.Contains(type))
            {
                types.Remove(type);
                changed = true;
            }

            if (changed)
            {
                var updatedNotifs = currentNotifs with { EnabledTypes = types };
                var updated = _currentPreferences with { Notifications = updatedNotifs };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    private async Task<UserPreferences> LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            var defaults = DefaultPreferences.CreateDefault();
            await SaveToDiskAsync(defaults, cancellationToken);
            return defaults;
        }

        try
        {
            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Length == 0 || fileInfo.Length > MaxAllowedFileSizeBytes)
            {
                System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Invalid preference file size ({fileInfo.Length} bytes). Resetting to defaults.");
                var defaults = DefaultPreferences.CreateDefault();
                await SaveToDiskAsync(defaults, cancellationToken);
                return defaults;
            }

            string json;
            using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                var defaults = DefaultPreferences.CreateDefault();
                await SaveToDiskAsync(defaults, cancellationToken);
                return defaults;
            }

            var loaded = JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.UserPreferences);
            if (loaded == null)
            {
                var defaults = DefaultPreferences.CreateDefault();
                await SaveToDiskAsync(defaults, cancellationToken);
                return defaults;
            }

            // Version check
            if (loaded.PreferencesVersion < 1 || loaded.PreferencesVersion > UserPreferences.CurrentSchemaVersion)
            {
                System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Unsupported preferences version ({loaded.PreferencesVersion}). Safely reverting to defaults.");
                var defaults = DefaultPreferences.CreateDefault();
                await SaveToDiskAsync(defaults, cancellationToken);
                return defaults;
            }

            return NormalizePreferences(loaded);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Error reading preferences file: {ex.Message}. Recovering with defaults.");
            var defaults = DefaultPreferences.CreateDefault();
            try
            {
                await SaveToDiskAsync(defaults, cancellationToken);
            }
            catch
            {
                // Ignored in recovery
            }
            return defaults;
        }
    }

    private async Task SaveToDiskAsync(UserPreferences preferences, CancellationToken cancellationToken)
    {
        var tempFile = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(preferences, FootballJsonSerializerContext.Default.UserPreferences);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(tempFile, _filePath, null);
            }
            else
            {
                File.Move(tempFile, _filePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Failed to persist preferences atomically: {ex.Message}");
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            throw;
        }
    }

    private static UserPreferences NormalizePreferences(UserPreferences raw)
    {
        if (raw == null) return DefaultPreferences.CreateDefault();

        // 1. Normalize Teams (case-insensitive deduplication, skip null/empty IDs)
        var seenTeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedTeams = new List<FollowedTeam>();
        if (raw.FollowedTeams != null)
        {
            foreach (var team in raw.FollowedTeams)
            {
                if (!string.IsNullOrWhiteSpace(team.Id) && seenTeamIds.Add(team.Id))
                {
                    normalizedTeams.Add(team);
                }
            }
        }

        // 2. Normalize Competitions (case-insensitive deduplication, skip null/empty IDs)
        var seenCompIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedComps = new List<FollowedCompetition>();
        if (raw.FollowedCompetitions != null)
        {
            foreach (var comp in raw.FollowedCompetitions)
            {
                if (!string.IsNullOrWhiteSpace(comp.Id) && seenCompIds.Add(comp.Id))
                {
                    normalizedComps.Add(comp);
                }
            }
        }

        // 3. Normalize Notifications
        var notifs = raw.Notifications ?? new NotificationPreferences();
        var normalizedTypes = notifs.EnabledTypes?
            .Distinct()
            .ToList() ?? new List<NotificationType>();

        return new UserPreferences
        {
            PreferencesVersion = raw.PreferencesVersion > 0 ? raw.PreferencesVersion : UserPreferences.CurrentSchemaVersion,
            FollowedTeams = normalizedTeams,
            FollowedCompetitions = normalizedComps,
            Notifications = notifs with { EnabledTypes = normalizedTypes },
            ExtensionData = raw.ExtensionData
        };
    }
}
