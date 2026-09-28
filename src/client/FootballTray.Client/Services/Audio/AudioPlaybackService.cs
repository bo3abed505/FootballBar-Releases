using System;
using System.Collections.Concurrent;
using System.IO;
using System.Media;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;

namespace FootballTray.Client.Services.Audio;

/// <summary>
/// Service responsible for playing notification alert sounds.
/// Uses System.Media.SoundPlayer for bulletproof WAV audio playback without WinRT capability restrictions,
/// resolving absolute physical file paths from AppContext.BaseDirectory,
/// with graceful fallback to SystemSounds.Asterisk upon failure.
/// Respects NotificationSoundMode (Custom, SystemDefault, Off).
/// </summary>
public class AudioPlaybackService : IAudioPlaybackService
{
    private static AudioPlaybackService? _instance;
    public static AudioPlaybackService Instance
    {
        get => _instance ??= new AudioPlaybackService();
        internal set => _instance = value;
    }

    private readonly IPreferencesService _preferencesService;
    private readonly string _audioDirectory;
    private readonly ConcurrentDictionary<string, SoundPlayer> _soundPlayers = new(StringComparer.OrdinalIgnoreCase);

    public AudioPlaybackService(
        IPreferencesService? preferencesService = null,
        string? customAudioDirectory = null)
    {
        _preferencesService = preferencesService ?? new PreferencesService();
        _audioDirectory = customAudioDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");
    }

    public string AudioDirectory => _audioDirectory;

    public void PlaySound(NotificationType type)
    {
        var soundMode = _preferencesService.Current.SoundMode;
        if (soundMode == NotificationSoundMode.Off)
        {
            return;
        }

        if (soundMode == NotificationSoundMode.SystemDefault)
        {
            PlayFallbackSound();
            return;
        }

        switch (type)
        {
            case NotificationType.MatchStarted:
            case NotificationType.MatchResumed:
                PlayAudioFile("RefereeWhistle_ShortBurst.wav");
                break;
            case NotificationType.HalfTime:
                PlayAudioFile("RefereeWhistle_DoubleBurst.wav");
                break;
            case NotificationType.MatchFinished:
                PlayAudioFile("RefereeWhistle_GameOver.wav");
                break;
            case NotificationType.Goal:
                PlayAudioFile("GoalNet_Celebration.wav");
                break;
            case NotificationType.RedCard:
            case NotificationType.PenaltyAwarded:
                PlayAudioFile("RefereeWhistle_LongBurst.wav");
                break;
            default:
                // Graceful generic alert for other notification types if needed
                PlayFallbackSound();
                break;
        }
    }

    public void PlayWhistle()
    {
        var soundMode = _preferencesService.Current.SoundMode;
        if (soundMode == NotificationSoundMode.Off)
        {
            return;
        }

        if (soundMode == NotificationSoundMode.SystemDefault)
        {
            PlayFallbackSound();
            return;
        }

        PlayAudioFile("RefereeWhistle_ShortBurst.wav");
    }

    public void PlayGoalSound()
    {
        var soundMode = _preferencesService.Current.SoundMode;
        if (soundMode == NotificationSoundMode.Off)
        {
            return;
        }

        if (soundMode == NotificationSoundMode.SystemDefault)
        {
            PlayFallbackSound();
            return;
        }

        PlayAudioFile("GoalNet_Celebration.wav");
    }

    public void PlayTestSound()
    {
        var soundMode = _preferencesService.Current.SoundMode;
        if (soundMode == NotificationSoundMode.SystemDefault)
        {
            PlayFallbackSound();
            return;
        }

        // For Custom mode (or when verifying audio hardware even if set to Off), play the goal sound
        PlayAudioFile("GoalNet_Celebration.wav", ignoreOffMode: true);
    }

    private void PlayAudioFile(string fileName, string? fallbackFileName = null, bool ignoreOffMode = false)
    {
        var soundMode = _preferencesService.Current.SoundMode;
        if (!ignoreOffMode && soundMode == NotificationSoundMode.Off)
        {
            return;
        }

        if (!ignoreOffMode && soundMode == NotificationSoundMode.SystemDefault)
        {
            PlayFallbackSound();
            return;
        }

        string selectedFile = fileName;
        string filePath = Path.Combine(_audioDirectory, fileName);
        if (!File.Exists(filePath) && fallbackFileName != null)
        {
            string fallbackPath = Path.Combine(_audioDirectory, fallbackFileName);
            if (File.Exists(fallbackPath))
            {
                filePath = fallbackPath;
                selectedFile = fallbackFileName;
            }
        }

        if (!File.Exists(filePath))
        {
            AppLogger.Debug("Audio", "FileNotFound", $"Audio file '{filePath}' not found; falling back to system default.");
            PlayFallbackSound();
            return;
        }

        try
        {
            var player = _soundPlayers.GetOrAdd(filePath, p => new SoundPlayer(p));
            player.Play();
            AppLogger.Debug("Audio", "SoundPlayed", $"Played alert audio via SoundPlayer: {selectedFile}");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("Audio", "PlaybackError", $"Audio playback failed for '{selectedFile}': {ex.Message}");
            PlayFallbackSound();
        }
    }

    private void PlayFallbackSound()
    {
        if (_preferencesService.Current.SoundMode == NotificationSoundMode.Off)
        {
            return;
        }

        try
        {
            SystemSounds.Asterisk.Play();
        }
        catch
        {
            // Best effort; ignore headless/soundless system errors
        }
    }
}
