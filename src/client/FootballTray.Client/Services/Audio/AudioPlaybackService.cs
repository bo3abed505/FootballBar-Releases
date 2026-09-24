using System;
using System.IO;
using System.Media;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.Services.Audio;

/// <summary>
/// Lightweight service responsible for playing notification alert sounds.
/// Uses System.Media.SoundPlayer for non-blocking WAV playback, resolving absolute
/// physical file paths from AppContext.BaseDirectory per WinUI 3 requirements,
/// with graceful fallback to SystemSounds.Asterisk upon failure.
/// </summary>
public class AudioPlaybackService : IAudioPlaybackService
{
    private static AudioPlaybackService? _instance;
    public static AudioPlaybackService Instance => _instance ??= new AudioPlaybackService();

    private readonly string _audioDirectory;

    public AudioPlaybackService(string? customAudioDirectory = null)
    {
        _audioDirectory = customAudioDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");
    }

    public string AudioDirectory => _audioDirectory;

    public void PlaySound(NotificationType type)
    {
        switch (type)
        {
            case NotificationType.MatchStarted:
                PlayWhistle();
                break;
            case NotificationType.Goal:
                PlayGoalSound();
                break;
            default:
                // Graceful generic alert for other notification types if needed
                PlayFallbackSound();
                break;
        }
    }

    public void PlayWhistle()
    {
        PlayAudioFile("whistle.wav");
    }

    public void PlayGoalSound()
    {
        PlayAudioFile("goal.wav");
    }

    private void PlayAudioFile(string fileName)
    {
        string filePath = Path.Combine(_audioDirectory, fileName);

        try
        {
            if (File.Exists(filePath))
            {
                using var player = new SoundPlayer(filePath);
                player.Play();
                AppLogger.Debug("Audio", "SoundPlayed", $"Played alert audio: {fileName}");
                return;
            }

            AppLogger.Debug("Audio", "SoundFileNotFound", $"Audio file not found at '{filePath}', falling back to SystemSounds.");
            PlayFallbackSound();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("Audio", "PlaybackError", $"Audio playback failed for '{filePath}': {ex.Message}");
            PlayFallbackSound();
        }
    }

    private static void PlayFallbackSound()
    {
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
