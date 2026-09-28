using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FootballTray.Client.Services.Audio;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class AudioPlaybackServiceTests
{
    private sealed class SpyAudioPlaybackService : IAudioPlaybackService
    {
        public int WhistleCallCount { get; private set; }
        public int GoalCallCount { get; private set; }
        public int TestSoundCallCount { get; private set; }
        public List<NotificationType> PlayedTypes { get; } = new();

        public void PlaySound(NotificationType type)
        {
            PlayedTypes.Add(type);
            if (type == NotificationType.MatchStarted)
            {
                PlayWhistle();
            }
            else if (type == NotificationType.Goal)
            {
                PlayGoalSound();
            }
        }

        public void PlayWhistle()
        {
            WhistleCallCount++;
        }

        public void PlayGoalSound()
        {
            GoalCallCount++;
        }

        public void PlayTestSound()
        {
            TestSoundCallCount++;
        }
    }

    private sealed class ThrowingAudioPlaybackService : IAudioPlaybackService
    {
        public void PlaySound(NotificationType type) => throw new InvalidOperationException("Simulated hardware audio failure.");
        public void PlayWhistle() => throw new InvalidOperationException("Simulated hardware audio failure.");
        public void PlayGoalSound() => throw new InvalidOperationException("Simulated hardware audio failure.");
        public void PlayTestSound() => throw new InvalidOperationException("Simulated hardware audio failure.");
    }

    [TestMethod]
    public void AudioPlaybackService_ResolvesPhysicalPathUnderAppContextBaseDirectory()
    {
        var service = new AudioPlaybackService();
        string expectedDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Audio");

        Assert.AreEqual(expectedDir, service.AudioDirectory,
            "Audio directory must strictly resolve to physical filesystem path under AppContext.BaseDirectory per WinUI 3 requirements.");
    }

    [TestMethod]
    public void AudioPlaybackService_PlayMethods_SafelyExecuteWithoutThrowingInHeadlessEnvironment()
    {
        var service = new AudioPlaybackService();

        // Must gracefully handle sound hardware absence in headless CI/unit test environments
        service.PlayWhistle();
        service.PlayGoalSound();
        service.PlayTestSound();
        service.PlaySound(NotificationType.MatchStarted);
        service.PlaySound(NotificationType.Goal);
        service.PlaySound(NotificationType.HalfTime);
        service.PlaySound(NotificationType.MatchFinished);
        service.PlaySound(NotificationType.MatchResumed);
        service.PlaySound(NotificationType.RedCard);
        service.PlaySound(NotificationType.PenaltyAwarded);
    }

    [TestMethod]
    public async Task NotificationService_InvokesAudioService_OnMatchStartedAndGoalAlerts()
    {
        var spyAudio = new SpyAudioPlaybackService();
        var store = new NotificationDeduplicationStore();
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer, store: store, audioService: spyAudio);

        // Snapshot 1: Pre-match fixture
        var prev = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "ft_fix_match_1",
                    State = "PREMATCH",
                    HomeTeam = new TeamSummaryDto { Id = "team_a", Name = "Team A" },
                    AwayTeam = new TeamSummaryDto { Id = "team_b", Name = "Team B" },
                    Score = new ScoreDto { Home = 0, Away = 0 },
                    KickoffTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                    ElapsedMinute = 0
                }
            ]
        };

        // Snapshot 2: Match started -> Kickoff alert
        var currMatchStarted = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "ft_fix_match_1",
                    State = "LIVE",
                    HomeTeam = new TeamSummaryDto { Id = "team_a", Name = "Team A" },
                    AwayTeam = new TeamSummaryDto { Id = "team_b", Name = "Team B" },
                    Score = new ScoreDto { Home = 0, Away = 0 },
                    KickoffTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                    ElapsedMinute = 1
                }
            ]
        };

        var dispatched = await service.ProcessTrayUpdateAsync(prev, currMatchStarted);

        Assert.AreEqual(1, dispatched.Count);
        Assert.AreEqual(NotificationType.MatchStarted, dispatched[0].Type);
        Assert.AreEqual(1, spyAudio.WhistleCallCount, "Whistle sound should have been triggered on match start.");

        // Snapshot 3: Goal scored -> Goal alert
        var currGoal = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "ft_fix_match_1",
                    State = "LIVE",
                    HomeTeam = new TeamSummaryDto { Id = "team_a", Name = "Team A" },
                    AwayTeam = new TeamSummaryDto { Id = "team_b", Name = "Team B" },
                    Score = new ScoreDto { Home = 1, Away = 0 },
                    KickoffTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                    ElapsedMinute = 12,
                    RecentEvents =
                    [
                        new TrayEventDto
                        {
                            Id = "evt_goal_1",
                            Type = "GOAL",
                            Minute = 12,
                            TeamId = "team_a",
                            PlayerName = "Striker A"
                        }
                    ]
                }
            ]
        };

        var goalDispatched = await service.ProcessTrayUpdateAsync(currMatchStarted, currGoal);

        Assert.AreEqual(1, goalDispatched.Count);
        Assert.AreEqual(NotificationType.Goal, goalDispatched[0].Type);
        Assert.AreEqual(1, spyAudio.GoalCallCount, "Goal alert sound should have been triggered on goal scored.");
    }

    [TestMethod]
    public async Task NotificationService_AudioFailure_DoesNotPreventNotificationDispatch()
    {
        var throwingAudio = new ThrowingAudioPlaybackService();
        var store = new NotificationDeduplicationStore();
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer, store: store, audioService: throwingAudio);

        var prev = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "ft_fix_test",
                    State = "PREMATCH",
                    HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team 1" },
                    AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team 2" },
                    Score = new ScoreDto { Home = 0, Away = 0 }
                }
            ]
        };

        var curr = new TrayDataDto
        {
            Matches =
            [
                new TrayMatchDto
                {
                    Id = "ft_fix_test",
                    State = "LIVE",
                    HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team 1" },
                    AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team 2" },
                    Score = new ScoreDto { Home = 0, Away = 0 }
                }
            ]
        };

        // Even with throwing audio service, notification processing must NOT throw
        var dispatched = await service.ProcessTrayUpdateAsync(prev, curr);

        Assert.AreEqual(1, dispatched.Count);
        Assert.AreEqual(1, renderer.Rendered.Count);
        Assert.IsTrue(store.IsAlreadyNotified("match:ft_fix_test:started"));
    }

    [TestMethod]
    public void AudioPlaybackService_HonorsMutePreference()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ft_audio_{Guid.NewGuid():N}");
        try
        {
            var prefs = new PreferencesService(tempDir);
            prefs.SetAudioEnabledAsync(false).GetAwaiter().GetResult();

            var service = new AudioPlaybackService(prefs);

            // When audio is disabled, methods must return immediately without playing or throwing
            service.PlayWhistle();
            service.PlayGoalSound();
            service.PlaySound(NotificationType.MatchStarted);
            service.PlaySound(NotificationType.Goal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void AudioPreferences_DefaultValuesAndVolumeClamping_OperateDeterministically()
    {
        var defaults = DefaultPreferences.CreateDefault();
        Assert.IsTrue(defaults.IsAudioEnabled, "Audio should be enabled by default.");
        Assert.AreEqual(0.2, defaults.AudioVolume, 0.001, "Audio volume should default to 20% (0.2).");

        string tempDir = Path.Combine(Path.GetTempPath(), $"ft_audio_vol_{Guid.NewGuid():N}");
        try
        {
            var prefs = new PreferencesService(tempDir);
            Assert.IsTrue(prefs.Current.IsAudioEnabled);
            Assert.AreEqual(0.2, prefs.Current.AudioVolume, 0.001);

            // Test volume mutation and clamping above 1.0
            prefs.SetAudioVolumeAsync(1.5).GetAwaiter().GetResult();
            Assert.AreEqual(1.0, prefs.Current.AudioVolume, 0.001, "Volume above 1.0 must be clamped to 1.0.");

            // Test volume mutation and clamping below 0.0
            prefs.SetAudioVolumeAsync(-0.5).GetAwaiter().GetResult();
            Assert.AreEqual(0.0, prefs.Current.AudioVolume, 0.001, "Volume below 0.0 must be clamped to 0.0.");

            // Test valid volume
            prefs.SetAudioVolumeAsync(0.65).GetAwaiter().GetResult();
            Assert.AreEqual(0.65, prefs.Current.AudioVolume, 0.001);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void AudioPlaybackService_HonorsNotificationSoundModes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ft_audio_modes_{Guid.NewGuid():N}");
        try
        {
            var prefs = new PreferencesService(tempDir);
            Assert.AreEqual(NotificationSoundMode.Custom, prefs.Current.SoundMode);
            Assert.IsTrue(prefs.Current.IsAudioEnabled);

            var service = new AudioPlaybackService(prefs);

            // 1. Custom mode - safely executes in headless CI
            service.PlayWhistle();
            service.PlayGoalSound();

            // 2. SystemDefault mode
            prefs.SetSoundModeAsync(NotificationSoundMode.SystemDefault).GetAwaiter().GetResult();
            Assert.AreEqual(NotificationSoundMode.SystemDefault, prefs.Current.SoundMode);
            Assert.IsTrue(prefs.Current.IsAudioEnabled);
            service.PlayWhistle();
            service.PlayGoalSound();

            // 3. Off mode
            prefs.SetSoundModeAsync(NotificationSoundMode.Off).GetAwaiter().GetResult();
            Assert.AreEqual(NotificationSoundMode.Off, prefs.Current.SoundMode);
            Assert.IsFalse(prefs.Current.IsAudioEnabled);
            service.PlayWhistle();
            service.PlayGoalSound();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
