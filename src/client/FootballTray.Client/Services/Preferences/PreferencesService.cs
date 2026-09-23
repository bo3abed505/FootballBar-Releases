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
    private const string BackupFileName = "preferences.json.bak";
    private const long MaxAllowedFileSizeBytes = 1024 * 1024; // 1 MB sanity bound

    private readonly string _filePath;
    private readonly string _backupFilePath;
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
        _backupFilePath = Path.Combine(baseDir, BackupFileName);
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
            string canonicalTargetId = CanonicalEntityIds.TryTranslateLegacyId(team.Id, out var canon) ? canon : team.Id;

            bool alreadyFollowed = teams.Any(t =>
                string.Equals(t.Id, team.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Id, canonicalTargetId, StringComparison.OrdinalIgnoreCase) ||
                (CanonicalEntityIds.TryTranslateLegacyId(t.Id, out var existingCanon) && string.Equals(existingCanon, canonicalTargetId, StringComparison.OrdinalIgnoreCase)));

            if (!alreadyFollowed)
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
            string canonicalTargetId = CanonicalEntityIds.TryTranslateLegacyId(teamId, out var canon) ? canon : teamId;

            int removed = teams.RemoveAll(t =>
                string.Equals(t.Id, teamId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Id, canonicalTargetId, StringComparison.OrdinalIgnoreCase) ||
                (CanonicalEntityIds.TryTranslateLegacyId(t.Id, out var existingCanon) && string.Equals(existingCanon, canonicalTargetId, StringComparison.OrdinalIgnoreCase)));

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
            string canonicalTargetId = CanonicalEntityIds.TryTranslateLegacyId(competition.Id, out var canon) ? canon : competition.Id;

            bool alreadyFollowed = comps.Any(c =>
                string.Equals(c.Id, competition.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Id, canonicalTargetId, StringComparison.OrdinalIgnoreCase) ||
                (CanonicalEntityIds.TryTranslateLegacyId(c.Id, out var existingCanon) && string.Equals(existingCanon, canonicalTargetId, StringComparison.OrdinalIgnoreCase)));

            if (!alreadyFollowed)
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
            string canonicalTargetId = CanonicalEntityIds.TryTranslateLegacyId(competitionId, out var canon) ? canon : competitionId;

            int removed = comps.RemoveAll(c =>
                string.Equals(c.Id, competitionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Id, canonicalTargetId, StringComparison.OrdinalIgnoreCase) ||
                (CanonicalEntityIds.TryTranslateLegacyId(c.Id, out var existingCanon) && string.Equals(existingCanon, canonicalTargetId, StringComparison.OrdinalIgnoreCase)));

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
        UserPreferences? primary = null;
        bool primaryExists = File.Exists(_filePath);

        if (primaryExists)
        {
            primary = await TryReadPreferencesFileAsync(_filePath, cancellationToken);
        }

        if (primary != null)
        {
            // 1. Unsupported future version check (e.g. 999) - immediately revert to defaults!
            if (primary.PreferencesVersion > UserPreferences.CurrentSchemaVersion)
            {
                System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Unsupported preferences version ({primary.PreferencesVersion}). Safely reverting to defaults.");
                var futureDefaults = DefaultPreferences.CreateDefault();
                await SaveToDiskAsync(futureDefaults, cancellationToken);
                return futureDefaults;
            }

            // 2. Migration check: if version < 2 or legacy IDs present from v1
            if (NeedsMigration(primary))
            {
                // Create backup before persisting migration
                try
                {
                    File.Copy(_filePath, _backupFilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Failed to create backup before migration: {ex.Message}");
                }

                var migrated = MigrateToV2(primary);
                var normalized = NormalizePreferences(migrated);
                await SaveToDiskAsync(normalized, cancellationToken);
                return normalized;
            }

            return NormalizePreferences(primary);
        }

        // Primary is missing, corrupted, or unreadable — attempt recovery from backup
        if (File.Exists(_backupFilePath))
        {
            System.Diagnostics.Debug.WriteLine("[PREFERENCES] Primary file invalid or missing. Attempting recovery from backup.");
            var backup = await TryReadPreferencesFileAsync(_backupFilePath, cancellationToken);
            if (backup != null)
            {
                if (NeedsMigration(backup))
                {
                    backup = MigrateToV2(backup);
                }
                var normalized = NormalizePreferences(backup);
                try
                {
                    await SaveToDiskAsync(normalized, cancellationToken);
                }
                catch { }
                return normalized;
            }
        }

        // Neither primary nor backup available or valid — safely initialize defaults
        var defaults = DefaultPreferences.CreateDefault();
        try
        {
            await SaveToDiskAsync(defaults, cancellationToken);
        }
        catch { }
        return defaults;
    }

    private static async Task<UserPreferences?> TryReadPreferencesFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length == 0 || fileInfo.Length > MaxAllowedFileSizeBytes)
            {
                return null;
            }

            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.UserPreferences);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PREFERENCES] Error reading file {path}: {ex.Message}");
            return null;
        }
    }

    private static bool NeedsMigration(UserPreferences prefs)
    {
        if (prefs == null) return false;
        if (prefs.PreferencesVersion > UserPreferences.CurrentSchemaVersion) return false;
        if (prefs.PreferencesVersion < 2) return true;

        if (prefs.FollowedTeams != null && prefs.FollowedTeams.Any(t =>
            !string.IsNullOrWhiteSpace(t.Id) &&
            CanonicalEntityIds.LegacyToCanonicalMap.ContainsKey(t.Id)))
        {
            return true;
        }

        if (prefs.FollowedCompetitions != null && prefs.FollowedCompetitions.Any(c =>
            !string.IsNullOrWhiteSpace(c.Id) &&
            CanonicalEntityIds.LegacyToCanonicalMap.ContainsKey(c.Id)))
        {
            return true;
        }

        return false;
    }

    private static UserPreferences MigrateToV2(UserPreferences raw)
    {
        var migratedTeams = new List<FollowedTeam>();
        if (raw.FollowedTeams != null)
        {
            foreach (var team in raw.FollowedTeams)
            {
                if (string.IsNullOrWhiteSpace(team.Id)) continue;
                string newId = team.Id.Trim();
                if (CanonicalEntityIds.TryTranslateLegacyId(newId, out var canonicalId))
                {
                    newId = canonicalId;
                }
                else if (raw.PreferencesVersion < 2 &&
                         !newId.StartsWith("ft_", StringComparison.OrdinalIgnoreCase) &&
                         !newId.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
                {
                    newId = $"provisional:{newId}";
                }
                migratedTeams.Add(team with { Id = newId });
            }
        }

        var migratedComps = new List<FollowedCompetition>();
        if (raw.FollowedCompetitions != null)
        {
            foreach (var comp in raw.FollowedCompetitions)
            {
                if (string.IsNullOrWhiteSpace(comp.Id)) continue;
                string newId = comp.Id.Trim();
                if (CanonicalEntityIds.TryTranslateLegacyId(newId, out var canonicalId))
                {
                    newId = canonicalId;
                }
                else if (raw.PreferencesVersion < 2 &&
                         !newId.StartsWith("ft_", StringComparison.OrdinalIgnoreCase) &&
                         !newId.StartsWith("provisional:", StringComparison.OrdinalIgnoreCase))
                {
                    newId = $"provisional:{newId}";
                }
                migratedComps.Add(comp with { Id = newId });
            }
        }

        return raw with
        {
            PreferencesVersion = UserPreferences.CurrentSchemaVersion,
            FollowedTeams = migratedTeams,
            FollowedCompetitions = migratedComps
        };
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
