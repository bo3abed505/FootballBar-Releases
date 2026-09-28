using FootballTray.Client.Services.Notifications;

namespace FootballTray.Client.Services.Audio;

/// <summary>
/// Service abstraction for playing alert audio on notification events.
/// </summary>
public interface IAudioPlaybackService
{
    /// <summary>
    /// Plays the appropriate sound for a notification type.
    /// </summary>
    void PlaySound(NotificationType type);

    /// <summary>
    /// Plays the whistle sound for match kick-off / start.
    /// </summary>
    void PlayWhistle();

    /// <summary>
    /// Plays the goal alert sound for goal events.
    /// </summary>
    void PlayGoalSound();

    /// <summary>
    /// Plays a test alert sound to verify audio output.
    /// </summary>
    void PlayTestSound();
}
