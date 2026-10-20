using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Client.Diagnostics;
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

    public UserPreferences Current => _currentPreferences;

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
            AppLogger.IsLoggingEnabled = _currentPreferences.IsLoggingEnabled;
            AppLogger.IsDebugLoggingEnabled = _currentPreferences.IsDebugLoggingEnabled;
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
        return _currentPreferences;
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
            AppLogger.IsLoggingEnabled = _currentPreferences.IsLoggingEnabled;
            AppLogger.IsDebugLoggingEnabled = _currentPreferences.IsDebugLoggingEnabled;
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task SetActiveProviderAsync(string provider, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var updated = _currentPreferences with { ActiveProvider = provider };
            var normalized = NormalizePreferences(updated);
            await SaveToDiskAsync(normalized, cancellationToken);
            _currentPreferences = normalized;
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
                (!string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(team.Name) && string.Equals(t.Name.Trim(), team.Name.Trim(), StringComparison.OrdinalIgnoreCase)) ||
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
                (!string.IsNullOrWhiteSpace(t.Name) && string.Equals(t.Name.Trim(), teamId.Trim(), StringComparison.OrdinalIgnoreCase)) ||
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

    public Task RemoveFollowedTeam(string teamId, CancellationToken cancellationToken = default) => UnfollowTeamAsync(teamId, cancellationToken);

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
                (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(competition.Name) && string.Equals(c.Name.Trim(), competition.Name.Trim(), StringComparison.OrdinalIgnoreCase)) ||
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
                (!string.IsNullOrWhiteSpace(c.Name) && string.Equals(c.Name.Trim(), competitionId.Trim(), StringComparison.OrdinalIgnoreCase)) ||
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

    public Task RemoveFollowedCompetition(string competitionId, CancellationToken cancellationToken = default) => UnfollowCompetitionAsync(competitionId, cancellationToken);

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

    public async Task SetSoundModeAsync(NotificationSoundMode mode, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.SoundMode != mode)
            {
                var updated = _currentPreferences with { SoundMode = mode };
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

    public async Task SetAudioEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var targetMode = enabled ? NotificationSoundMode.Custom : NotificationSoundMode.Off;
            if (_currentPreferences.SoundMode != targetMode)
            {
                var updated = _currentPreferences with { SoundMode = targetMode };
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

    public async Task SetAudioVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        double clamped = Math.Clamp(volume, 0.0, 1.0);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (Math.Abs(_currentPreferences.AudioVolume - clamped) > 0.001)
            {
                var updated = _currentPreferences with { AudioVolume = clamped };
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

    public async Task SetTickerVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.IsTickerVisible != visible)
            {
                var updated = _currentPreferences with { IsTickerVisible = visible };
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

    public async Task SetTickerOffsetXAsync(int offset, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.TickerOffsetX != offset)
            {
                var updated = _currentPreferences with { TickerOffsetX = offset };
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

    public async Task SetTargetScreenDeviceNameAsync(string? deviceName, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.TargetScreenDeviceName != deviceName)
            {
                var updated = _currentPreferences with { TargetScreenDeviceName = deviceName };
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

    public async Task SetEnableTickerCarouselAsync(bool enable, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.EnableTickerCarousel != enable)
            {
                var updated = _currentPreferences with { EnableTickerCarousel = enable };
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

    public async Task SetUse24HourFormatAsync(bool use24Hour, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.Use24HourFormat != use24Hour)
            {
                var updated = _currentPreferences with { Use24HourFormat = use24Hour };
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

    public async Task SetRunAtStartupAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.IsRunAtStartupEnabled != enabled)
            {
                var updated = _currentPreferences with { IsRunAtStartupEnabled = enabled };
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

    public async Task SetLoggingEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.IsLoggingEnabled != enabled)
            {
                var updated = _currentPreferences with { IsLoggingEnabled = enabled };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
                AppLogger.IsLoggingEnabled = enabled;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task SetDebugLoggingEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.IsDebugLoggingEnabled != enabled)
            {
                var updated = _currentPreferences with { IsDebugLoggingEnabled = enabled };
                var normalized = NormalizePreferences(updated);
                await SaveToDiskAsync(normalized, cancellationToken);
                _currentPreferences = normalized;
                _isLoaded = true;
                AppLogger.IsDebugLoggingEnabled = enabled;
            }
        }
        finally
        {
            _lock.Release();
        }

        PreferencesChanged?.Invoke(this, _currentPreferences);
    }

    public async Task SetKickoffReminderEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.IsKickoffReminderEnabled != enabled)
            {
                var updated = _currentPreferences with { IsKickoffReminderEnabled = enabled };
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

    public async Task SetKickoffReminderOffsetMinutesAsync(int minutes, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_currentPreferences.KickoffReminderOffsetMinutes != minutes)
            {
                var updated = _currentPreferences with { KickoffReminderOffsetMinutes = minutes };
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
        bool primaryExists = File.Exists(_filePath);

        // Missing preferences file: initialize with strict empty collections ([]), preventing silent ghost re-injection
        if (!primaryExists)
        {
            var missingDefaults = DefaultPreferences.CreateDefault();
            try
            {
                if (File.Exists(_backupFilePath)) File.Delete(_backupFilePath);
                await SaveToDiskAsync(missingDefaults, cancellationToken);
            }
            catch { }
            return missingDefaults;
        }

        // Empty or 0-byte preferences file: initialize with strict empty collections ([])
        try
        {
            var fileInfo = new FileInfo(_filePath);
            if (fileInfo.Length == 0)
            {
                var emptyDefaults = DefaultPreferences.CreateDefault();
                if (File.Exists(_backupFilePath)) File.Delete(_backupFilePath);
                await SaveToDiskAsync(emptyDefaults, cancellationToken);
                return emptyDefaults;
            }
        }
        catch { }

        UserPreferences? primary = await TryReadPreferencesFileAsync(_filePath, cancellationToken);

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

            var normalizedPrimary = NormalizePreferences(primary);
            if (primary.FollowedTeams?.Count != normalizedPrimary.FollowedTeams.Count ||
                primary.FollowedCompetitions?.Count != normalizedPrimary.FollowedCompetitions.Count ||
                !normalizedPrimary.FollowedTeams.SequenceEqual(primary.FollowedTeams ?? Enumerable.Empty<FollowedTeam>()) ||
                !normalizedPrimary.FollowedCompetitions.SequenceEqual(primary.FollowedCompetitions ?? Enumerable.Empty<FollowedCompetition>()))
            {
                await SaveToDiskAsync(normalizedPrimary, cancellationToken);
            }
            return normalizedPrimary;
        }

        // If primary exists and is whitespace-only, treat as empty preferences
        try
        {
            string rawContent = await File.ReadAllTextAsync(_filePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                var emptyDefaults = DefaultPreferences.CreateDefault();
                if (File.Exists(_backupFilePath)) File.Delete(_backupFilePath);
                await SaveToDiskAsync(emptyDefaults, cancellationToken);
                return emptyDefaults;
            }
        }
        catch { }

        // Primary is corrupted (non-empty unparseable JSON) — attempt recovery from backup
        if (File.Exists(_backupFilePath))
        {
            System.Diagnostics.Debug.WriteLine("[PREFERENCES] Primary file corrupted. Attempting recovery from backup.");
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
            ActiveProvider = "fotmob",
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

        // 1. Normalize Teams (deduplicate by Id, canonical Id, and Name ignoring case)
        var normalizedTeams = new List<FollowedTeam>();
        var seenTeamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTeamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (raw.FollowedTeams != null)
        {
            foreach (var team in raw.FollowedTeams)
            {
                if (team == null || string.IsNullOrWhiteSpace(team.Id)) continue;
                if (IsMockEntity(team.Id, team.Name)) continue;

                string id = team.Id.Trim();
                string canonicalId = CanonicalEntityIds.TryTranslateLegacyId(id, out var cId) ? cId : id;
                string name = (team.Name ?? string.Empty).Trim();

                if (seenTeamIds.Contains(id) || seenTeamIds.Contains(canonicalId))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name) && seenTeamNames.Contains(name))
                {
                    continue;
                }

                seenTeamIds.Add(id);
                seenTeamIds.Add(canonicalId);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    seenTeamNames.Add(name);
                }

                normalizedTeams.Add(team);
            }
        }

        // 2. Normalize Competitions (deduplicate by Id, canonical Id, and Name ignoring case)
        var normalizedComps = new List<FollowedCompetition>();
        var seenCompIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCompNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (raw.FollowedCompetitions != null)
        {
            foreach (var comp in raw.FollowedCompetitions)
            {
                if (comp == null || string.IsNullOrWhiteSpace(comp.Id)) continue;
                if (IsMockEntity(comp.Id, comp.Name, comp.Code)) continue;

                string id = comp.Id.Trim();
                string canonicalId = CanonicalEntityIds.TryTranslateLegacyId(id, out var cId) ? cId : id;
                string name = (comp.Name ?? string.Empty).Trim();

                if (seenCompIds.Contains(id) || seenCompIds.Contains(canonicalId))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name) && seenCompNames.Contains(name))
                {
                    continue;
                }

                seenCompIds.Add(id);
                seenCompIds.Add(canonicalId);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    seenCompNames.Add(name);
                }

                normalizedComps.Add(comp);
            }
        }

        // 3. Normalize Notifications
        var notifs = raw.Notifications ?? new NotificationPreferences();
        var normalizedTypes = notifs.EnabledTypes?
            .Distinct()
            .ToList() ?? new List<NotificationType>();

        // 4. Normalize Audio
        double normalizedVolume = Math.Clamp(raw.AudioVolume, 0.0, 1.0);

        return new UserPreferences
        {
            PreferencesVersion = raw.PreferencesVersion > 0 ? raw.PreferencesVersion : UserPreferences.CurrentSchemaVersion,
            ActiveProvider = "fotmob",
            FollowedTeams = normalizedTeams,
            FollowedCompetitions = normalizedComps,
            Notifications = notifs with { EnabledTypes = normalizedTypes },
            SoundMode = raw.SoundMode,
            AudioVolume = normalizedVolume,
            IsTickerVisible = raw.IsTickerVisible,
            TickerOffsetX = raw.TickerOffsetX,
            TargetScreenDeviceName = raw.TargetScreenDeviceName,
            EnableTickerCarousel = raw.EnableTickerCarousel,
            Use24HourFormat = raw.Use24HourFormat,
            IsRunAtStartupEnabled = raw.IsRunAtStartupEnabled,
            IsLoggingEnabled = raw.IsLoggingEnabled,
            IsDebugLoggingEnabled = raw.IsDebugLoggingEnabled,
            IsKickoffReminderEnabled = raw.IsKickoffReminderEnabled,
            KickoffReminderOffsetMinutes = raw.KickoffReminderOffsetMinutes >= 0 ? raw.KickoffReminderOffsetMinutes : 15,
            ExtensionData = raw.ExtensionData
        };
    }

    private static bool IsMockEntity(string? id, string? name, string? code = null)
    {
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)) return false;
        var n = (name ?? string.Empty).ToLowerInvariant();
        var i = (id ?? string.Empty).ToLowerInvariant();
        var c = (code ?? string.Empty).ToLowerInvariant();
        return n.Contains("(dev mock)") ||
               n.Contains("mock") ||
               i.Contains("mock") ||
               c.Contains("mock") ||
               i == "comp_laliga" ||
               i == "comp_egypt_pl" ||
               i == "comp_international" ||
               i == "team_barcelona" ||
               i == "team_realmadrid" ||
               i == "team_alahly" ||
               i == "team_egypt" ||
               i == "team_spain";
    }
}
