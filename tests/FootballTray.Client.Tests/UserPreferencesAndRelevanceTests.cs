using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FootballTray.Core.Dto;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class UserPreferencesAndRelevanceTests
{
    private string _tempDir = string.Empty;
    private PreferencesService _prefService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ft_pref_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _prefService = new PreferencesService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    // ============================================================
    // Step 14: Preference Persistence Tests (1-20)
    // ============================================================

    [TestMethod]
    public async Task Test01_DefaultPreferences_LoadCorrectly()
    {
        var prefs = await _prefService.LoadAsync();

        Assert.IsNotNull(prefs);
        Assert.AreEqual(UserPreferences.CurrentSchemaVersion, prefs.PreferencesVersion);
        Assert.AreEqual(2, prefs.FollowedTeams.Count);
        Assert.IsTrue(prefs.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
        Assert.IsTrue(prefs.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.AlAhly));

        Assert.AreEqual(6, prefs.FollowedCompetitions.Count);
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague));
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.LaLiga));
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.EgyptianPremierLeague));
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.ChampionsLeague));
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.AfricaCupOfNations));
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.WorldCup));

        Assert.IsTrue(prefs.Notifications.GlobalEnabled);
        Assert.AreEqual(Enum.GetValues<NotificationType>().Length, prefs.Notifications.EnabledTypes.Count);
    }

    [TestMethod]
    public async Task Test02_Preferences_PersistAcrossServiceInstances()
    {
        await _prefService.LoadAsync();
        await _prefService.FollowTeamAsync(new FollowedTeam("team_42", "Arsenal FC", "ARS", "England"));

        // Create new service instance pointing to same directory
        var newService = new PreferencesService(_tempDir);
        var loaded = await newService.LoadAsync();

        Assert.AreEqual(3, loaded.FollowedTeams.Count);
        Assert.IsTrue(loaded.FollowedTeams.Any(t => t.Id == "team_42"));
    }

    [TestMethod]
    public async Task Test03_TeamFollow_AddsTeamSuccessfully()
    {
        await _prefService.LoadAsync();
        var team = new FollowedTeam("team_40", "Liverpool FC", "LIV", "England");
        await _prefService.FollowTeamAsync(team);

        var prefs = _prefService.GetPreferences();
        Assert.IsTrue(prefs.FollowedTeams.Any(t => t.Id == "team_40"));
    }

    [TestMethod]
    public async Task Test04_TeamUnfollow_RemovesTeamSuccessfully()
    {
        await _prefService.LoadAsync();
        await _prefService.UnfollowTeamAsync(CanonicalEntityIds.Barcelona);

        var prefs = _prefService.GetPreferences();
        Assert.IsFalse(prefs.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
        Assert.AreEqual(1, prefs.FollowedTeams.Count);
    }

    [TestMethod]
    public async Task Test05_CompetitionFollow_AddsCompetitionSuccessfully()
    {
        await _prefService.LoadAsync();
        var comp = new FollowedCompetition("comp_78", "Bundesliga", "GER_L1", "Germany");
        await _prefService.FollowCompetitionAsync(comp);

        var prefs = _prefService.GetPreferences();
        Assert.IsTrue(prefs.FollowedCompetitions.Any(c => c.Id == "comp_78"));
    }

    [TestMethod]
    public async Task Test06_CompetitionUnfollow_RemovesCompetitionSuccessfully()
    {
        await _prefService.LoadAsync();
        await _prefService.UnfollowCompetitionAsync(CanonicalEntityIds.PremierLeague);

        var prefs = _prefService.GetPreferences();
        Assert.IsFalse(prefs.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague));
    }

    [TestMethod]
    public async Task Test07_DuplicateFollow_IsIdempotent()
    {
        await _prefService.LoadAsync();
        var team = new FollowedTeam(CanonicalEntityIds.Barcelona, "FC Barcelona duplicate", "BAR", "Spain");
        await _prefService.FollowTeamAsync(team);

        var prefs = _prefService.GetPreferences();
        Assert.AreEqual(2, prefs.FollowedTeams.Count);
    }

    [TestMethod]
    public async Task Test08_DuplicateUnfollow_IsSafe()
    {
        await _prefService.LoadAsync();
        await _prefService.UnfollowTeamAsync("team_nonexistent");
        await _prefService.UnfollowTeamAsync("team_nonexistent");

        var prefs = _prefService.GetPreferences();
        Assert.AreEqual(2, prefs.FollowedTeams.Count);
    }

    [TestMethod]
    public async Task Test09_GlobalNotificationsDisable_Works()
    {
        await _prefService.LoadAsync();
        await _prefService.SetNotificationsGloballyEnabledAsync(false);

        var prefs = _prefService.GetPreferences();
        Assert.IsFalse(prefs.Notifications.GlobalEnabled);
        Assert.IsFalse(prefs.Notifications.IsTypeEnabled(NotificationType.Goal));
    }

    [TestMethod]
    public async Task Test10_IndividualNotificationTypeDisable_Works()
    {
        await _prefService.LoadAsync();
        await _prefService.SetNotificationTypeEnabledAsync(NotificationType.Goal, false);

        var prefs = _prefService.GetPreferences();
        Assert.IsTrue(prefs.Notifications.GlobalEnabled);
        Assert.IsFalse(prefs.Notifications.IsTypeEnabled(NotificationType.Goal));
        Assert.IsTrue(prefs.Notifications.IsTypeEnabled(NotificationType.MatchStarted));
    }

    [TestMethod]
    public async Task Test11_ReEnable_NotificationType_Works()
    {
        await _prefService.LoadAsync();
        await _prefService.SetNotificationTypeEnabledAsync(NotificationType.Goal, false);
        Assert.IsFalse(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.Goal));

        await _prefService.SetNotificationTypeEnabledAsync(NotificationType.Goal, true);
        Assert.IsTrue(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.Goal));
    }

    [TestMethod]
    public async Task Test12_CorruptPreferences_RecoverToDefaults()
    {
        string filePath = Path.Combine(_tempDir, "preferences.json");
        await File.WriteAllTextAsync(filePath, "{ malformed JSON text ::: @#$ }");

        var loaded = await _prefService.LoadAsync();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(UserPreferences.CurrentSchemaVersion, loaded.PreferencesVersion);
        Assert.AreEqual(2, loaded.FollowedTeams.Count);
        Assert.AreEqual(6, loaded.FollowedCompetitions.Count);
    }

    [TestMethod]
    public async Task Test13_EmptyPreferencesFile_RecoversSafely()
    {
        string filePath = Path.Combine(_tempDir, "preferences.json");
        await File.WriteAllTextAsync(filePath, "   ");

        var loaded = await _prefService.LoadAsync();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(2, loaded.FollowedTeams.Count);
    }

    [TestMethod]
    public async Task Test14_UnknownAdditiveProperties_AreTolerated()
    {
        string filePath = Path.Combine(_tempDir, "preferences.json");
        string jsonWithFutureProps = $$"""
        {
          "preferencesVersion": 2,
          "followedTeams": [
            { "id": "{{CanonicalEntityIds.Barcelona}}", "name": "FC Barcelona", "futureCustomProp": 12345 }
          ],
          "followedCompetitions": [],
          "notifications": { "globalEnabled": true, "enabledTypes": [0, 1] },
          "futureGlobalSetting": "awesome"
        }
        """;
        await File.WriteAllTextAsync(filePath, jsonWithFutureProps);

        var loaded = await _prefService.LoadAsync();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.FollowedTeams.Count);
        Assert.AreEqual(CanonicalEntityIds.Barcelona, loaded.FollowedTeams[0].Id);
    }

    [TestMethod]
    public async Task Test15_InvalidNullEmptyIds_AreFilteredSafely()
    {
        var raw = new UserPreferences
        {
            PreferencesVersion = 1,
            FollowedTeams = new List<FollowedTeam>
            {
                new("", "Empty"),
                new(null!, "Null"),
                new("team_valid", "Valid")
            }
        };

        await _prefService.SetPreferencesAsync(raw);
        var loaded = _prefService.GetPreferences();

        Assert.AreEqual(1, loaded.FollowedTeams.Count);
        Assert.AreEqual("team_valid", loaded.FollowedTeams[0].Id);
    }

    [TestMethod]
    public async Task Test16_DuplicateIds_AreNormalized()
    {
        var raw = new UserPreferences
        {
            PreferencesVersion = 1,
            FollowedTeams = new List<FollowedTeam>
            {
                new("team_529", "FC Barcelona 1"),
                new("TEAM_529", "FC Barcelona 2"),
                new("team_529", "FC Barcelona 3")
            }
        };

        await _prefService.SetPreferencesAsync(raw);
        var loaded = _prefService.GetPreferences();

        Assert.AreEqual(1, loaded.FollowedTeams.Count);
        Assert.AreEqual("team_529", loaded.FollowedTeams[0].Id);
    }

    [TestMethod]
    public async Task Test17_PreferenceVersion_IsWrittenAndRead()
    {
        var prefs = await _prefService.LoadAsync();
        Assert.AreEqual(UserPreferences.CurrentSchemaVersion, prefs.PreferencesVersion);

        string filePath = Path.Combine(_tempDir, "preferences.json");
        string content = await File.ReadAllTextAsync(filePath);
        Assert.IsTrue(content.Contains($"\"preferencesVersion\": {UserPreferences.CurrentSchemaVersion}"));
    }

    [TestMethod]
    public async Task Test18_UnsupportedFutureVersion_FailsSafelyToDefaults()
    {
        string filePath = Path.Combine(_tempDir, "preferences.json");
        string jsonWithFutureVersion = """
        {
          "preferencesVersion": 999,
          "followedTeams": [
            { "id": "team_future", "name": "Future Team" }
          ],
          "followedCompetitions": []
        }
        """;
        await File.WriteAllTextAsync(filePath, jsonWithFutureVersion);

        var loaded = await _prefService.LoadAsync();

        // Safely reverted to default schema version 1 and standard defaults
        Assert.AreEqual(UserPreferences.CurrentSchemaVersion, loaded.PreferencesVersion);
        Assert.AreEqual(2, loaded.FollowedTeams.Count);
        Assert.IsTrue(loaded.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
    }

    [TestMethod]
    public async Task Test19_AtomicWrite_LeavesValidState()
    {
        await _prefService.LoadAsync();
        await _prefService.FollowTeamAsync(new FollowedTeam("team_49", "Chelsea FC"));

        string filePath = Path.Combine(_tempDir, "preferences.json");
        Assert.IsTrue(File.Exists(filePath));

        // Ensure no leftover temp files
        var tempFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.AreEqual(0, tempFiles.Length);
    }

    [TestMethod]
    public async Task Test20_Restart_PreservesPreferences()
    {
        var service1 = new PreferencesService(_tempDir);
        await service1.LoadAsync();
        await service1.UnfollowTeamAsync(CanonicalEntityIds.Barcelona);
        await service1.SetNotificationTypeEnabledAsync(NotificationType.HalfTime, false);

        // Simulate app restart
        var service2 = new PreferencesService(_tempDir);
        var loaded = await service2.LoadAsync();

        Assert.IsFalse(loaded.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
        Assert.IsFalse(loaded.Notifications.IsTypeEnabled(NotificationType.HalfTime));
        Assert.IsTrue(loaded.Notifications.IsTypeEnabled(NotificationType.Goal));
    }

    // ============================================================
    // Step 15: Relevance Tests (21-31)
    // ============================================================

    [TestMethod]
    public void Test21_FollowedHomeTeam_IsRelevant()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var match = CreateMatch("fix_1", CanonicalEntityIds.Barcelona, "team_other", "comp_other", homeName: "FC Barcelona");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsTrue(result.IsRelevant);
        Assert.IsTrue(result.FollowedByTeam);
        Assert.IsFalse(result.FollowedByCompetition);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("FC Barcelona")));
    }

    [TestMethod]
    public void Test22_FollowedAwayTeam_IsRelevant()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var match = CreateMatch("fix_2", "team_other", CanonicalEntityIds.AlAhly, "comp_other", "Other FC", "Al Ahly SC");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsTrue(result.IsRelevant);
        Assert.IsTrue(result.FollowedByTeam);
        Assert.IsFalse(result.FollowedByCompetition);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("Al Ahly SC")));
    }

    [TestMethod]
    public void Test23_FollowedCompetition_IsRelevant()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var match = CreateMatch("fix_3", "team_newcastle", "team_brighton", CanonicalEntityIds.PremierLeague, "Newcastle", "Brighton", "Premier League");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsTrue(result.IsRelevant);
        Assert.IsFalse(result.FollowedByTeam);
        Assert.IsTrue(result.FollowedByCompetition);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("Premier League")));
    }

    [TestMethod]
    public void Test24_UnfollowedTeamAndCompetition_IsIrrelevant()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var match = CreateMatch("fix_4", "team_inter", "team_milan", "comp_seriea", "Inter", "Milan", "Serie A");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsFalse(result.IsRelevant);
        Assert.IsFalse(result.FollowedByTeam);
        Assert.IsFalse(result.FollowedByCompetition);
        Assert.AreEqual(0, result.Reasons.Count);
    }

    [TestMethod]
    public void Test25_TeamAndCompetitionMatch_SingleRelevantFixtureWithMultipleReasons()
    {
        var prefs = DefaultPreferences.CreateDefault();
        // Barcelona match in La Liga (both team and competition are followed)
        var match = CreateMatch("fix_5", CanonicalEntityIds.Barcelona, "team_valencia", CanonicalEntityIds.LaLiga, "FC Barcelona", "Valencia CF", "La Liga");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsTrue(result.IsRelevant);
        Assert.IsTrue(result.FollowedByTeam);
        Assert.IsTrue(result.FollowedByCompetition);
        Assert.IsTrue(result.Reasons.Count >= 2);
    }

    [TestMethod]
    public void Test26_StableIdMatching_WorksEvenWhenDisplayNamesDiffer()
    {
        var prefs = DefaultPreferences.CreateDefault();
        // ID matches 'team_529' but localized display name is 'Barca Custom Name'
        var match = CreateMatch("fix_6", CanonicalEntityIds.Barcelona, "team_unknown", "comp_unknown", "Barca Custom Name", "Other");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsTrue(result.IsRelevant);
        Assert.IsTrue(result.FollowedByTeam);
    }

    [TestMethod]
    public void Test27_UnrelatedDisplayNameSimilarity_DoesNotCreateRelevance()
    {
        var prefs = DefaultPreferences.CreateDefault();
        // Name contains 'Barcelona' (e.g. Barcelona SC in Ecuador) but ID is different
        var match = CreateMatch("fix_7", "team_barcelona_ecuador", "team_liga_quito", "comp_ecuador", "Barcelona SC Guayaquil", "Liga de Quito");

        var result = FixtureRelevanceEngine.EvaluateRelevance(match, prefs);

        Assert.IsFalse(result.IsRelevant);
    }

    [TestMethod]
    public void Test28_MultipleRelevantFixtures_RemainDeterministicallyOrdered()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var matchA = CreateMatch("fix_a", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 75);
        var matchB = CreateMatch("fix_b", CanonicalEntityIds.AlAhly, "team_zamalek", CanonicalEntityIds.EgyptianPremierLeague, state: "LIVE", elapsed: 30);

        var list = new List<TrayMatchDto> { matchB, matchA };
        var prioritized = FixtureRelevanceEngine.PrioritizeMatches(list, prefs);

        // Both are followed live teams; higher elapsed minute (75') ranks first
        Assert.AreEqual("fix_a", prioritized[0].Id);
        Assert.AreEqual("fix_b", prioritized[1].Id);
    }

    [TestMethod]
    public void Test29_LiveFollowedTeamFixture_OutranksUpcomingFollowedTeamFixture()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var upcomingBarca = CreateMatch("fix_barca_up", CanonicalEntityIds.Barcelona, "team_sevilla", CanonicalEntityIds.LaLiga, state: "PREMATCH");
        var liveAhly = CreateMatch("fix_ahly_live", CanonicalEntityIds.AlAhly, "team_pyramids", CanonicalEntityIds.EgyptianPremierLeague, state: "LIVE", elapsed: 15);

        var list = new List<TrayMatchDto> { upcomingBarca, liveAhly };
        var prioritized = FixtureRelevanceEngine.PrioritizeMatches(list, prefs);

        Assert.AreEqual("fix_ahly_live", prioritized[0].Id);
        Assert.AreEqual("fix_barca_up", prioritized[1].Id);
    }

    [TestMethod]
    public void Test30_FollowedTeamLiveFixture_OutranksFollowedCompetitionLiveFixture()
    {
        var prefs = DefaultPreferences.CreateDefault();
        // Match 1: Followed competition (Premier League) but unfollowed teams
        var plLive = CreateMatch("fix_pl_live", "team_42", "team_49", CanonicalEntityIds.PremierLeague, state: "LIVE", elapsed: 85);
        // Match 2: Followed team (Barcelona in La Liga)
        var barcaLive = CreateMatch("fix_barca_live", CanonicalEntityIds.Barcelona, "team_valencia", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 10);

        var list = new List<TrayMatchDto> { plLive, barcaLive };
        var prioritized = FixtureRelevanceEngine.PrioritizeMatches(list, prefs);

        // Followed team match (Tier 1) outranks competition-only match (Tier 3)
        Assert.AreEqual("fix_barca_live", prioritized[0].Id);
        Assert.AreEqual("fix_pl_live", prioritized[1].Id);
    }

    [TestMethod]
    public void Test31_DeterministicTieBreaking_Works()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var kickoff = DateTimeOffset.UtcNow;
        var match1 = CreateMatch("fix_z", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 50, kickoff: kickoff);
        var match2 = CreateMatch("fix_a", CanonicalEntityIds.AlAhly, "team_zamalek", CanonicalEntityIds.EgyptianPremierLeague, state: "LIVE", elapsed: 50, kickoff: kickoff);

        var list = new List<TrayMatchDto> { match1, match2 };
        var prioritized = FixtureRelevanceEngine.PrioritizeMatches(list, prefs);

        // Same tier, same elapsed minute, same kickoff: tie broken deterministically by ID string ordinal ("fix_a" < "fix_z")
        Assert.AreEqual("fix_a", prioritized[0].Id);
        Assert.AreEqual("fix_z", prioritized[1].Id);
    }

    // ============================================================
    // Step 16: Notification Filtering Tests (32-41)
    // ============================================================

    [TestMethod]
    public async Task Test32_FollowedTeamGoal_NotificationEmitted()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);
        var prefs = DefaultPreferences.CreateDefault();

        var prev = CreateTrayData("fix_barca", "LIVE", 10, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        var goalEvent = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_1", Type = "Goal", Minute = 15, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "L. Yamal" }
        };
        var curr = CreateTrayData("fix_barca", "LIVE", 15, 1, 0, goalEvent, CanonicalEntityIds.Barcelona, "team_rma");

        var alerts = await service.ProcessTrayUpdateAsync(prev, curr, prefs);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(1, renderer.Rendered.Count);
        Assert.AreEqual("15' L. Yamal", renderer.Rendered[0].Body);
    }

    [TestMethod]
    public async Task Test33_UnfollowedTeamGoal_NotificationSuppressed()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);
        var prefs = DefaultPreferences.CreateDefault();

        // Match in unfollowed competition with unfollowed teams
        var prev = CreateTrayData("fix_seriea", "LIVE", 10, 0, 0, null, "team_roma", "team_lazio", "comp_seriea");
        var goalEvent = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_roma", Type = "Goal", Minute = 12, TeamId = "team_roma", PlayerName = "P. Dybala" }
        };
        var curr = CreateTrayData("fix_seriea", "LIVE", 12, 1, 0, goalEvent, "team_roma", "team_lazio", "comp_seriea");

        var alerts = await service.ProcessTrayUpdateAsync(prev, curr, prefs);

        Assert.AreEqual(0, alerts.Count);
        Assert.AreEqual(0, renderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test34_FollowedCompetitionGoal_NotificationEmitted()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);
        var prefs = DefaultPreferences.CreateDefault();

        // Premier League match (followed competition) with unfollowed teams
        var prev = CreateTrayData("fix_pl_1", "LIVE", 20, 0, 0, null, "team_tottenham", "team_chelsea", CanonicalEntityIds.PremierLeague);
        var goalEvent = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_tot", Type = "Goal", Minute = 22, TeamId = "team_tottenham", PlayerName = "Son" }
        };
        var curr = CreateTrayData("fix_pl_1", "LIVE", 22, 1, 0, goalEvent, "team_tottenham", "team_chelsea", CanonicalEntityIds.PremierLeague);

        var alerts = await service.ProcessTrayUpdateAsync(prev, curr, prefs);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(1, renderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test35_GlobalNotificationsDisabled_ZeroNotificationsEmitted()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);
        var prefs = DefaultPreferences.CreateDefault() with
        {
            Notifications = new NotificationPreferences { GlobalEnabled = false }
        };

        var prev = CreateTrayData("fix_barca", "LIVE", 10, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        var goalEvent = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_barca", Type = "Goal", Minute = 15, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Pedri" }
        };
        var curr = CreateTrayData("fix_barca", "LIVE", 15, 1, 0, goalEvent, CanonicalEntityIds.Barcelona, "team_rma");

        var alerts = await service.ProcessTrayUpdateAsync(prev, curr, prefs);

        Assert.AreEqual(0, alerts.Count);
        Assert.AreEqual(0, renderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test36_GoalCategoryDisabled_GoalSuppressed()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);
        var prefs = DefaultPreferences.CreateDefault() with
        {
            Notifications = new NotificationPreferences
            {
                GlobalEnabled = true,
                EnabledTypes = new List<NotificationType> { NotificationType.MatchStarted, NotificationType.MatchFinished }
            }
        };

        var prev = CreateTrayData("fix_barca", "LIVE", 10, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        var goalEvent = new List<TrayEventDto>
        {
            new() { Id = "evt_goal_barca2", Type = "Goal", Minute = 25, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Lewandowski" }
        };
        var curr = CreateTrayData("fix_barca", "LIVE", 25, 1, 0, goalEvent, CanonicalEntityIds.Barcelona, "team_rma");

        var alerts = await service.ProcessTrayUpdateAsync(prev, curr, prefs);

        Assert.AreEqual(0, alerts.Count);
    }

    [TestMethod]
    public async Task Test37_MatchStarted_RemainsEnabled_WhenGoalIsDisabled()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);
        var prefs = DefaultPreferences.CreateDefault() with
        {
            Notifications = new NotificationPreferences
            {
                GlobalEnabled = true,
                EnabledTypes = new List<NotificationType> { NotificationType.MatchStarted }
            }
        };

        var prev = CreateTrayData("fix_barca", "PREMATCH", 0, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        var curr = CreateTrayData("fix_barca", "LIVE", 1, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");

        var alerts = await service.ProcessTrayUpdateAsync(prev, curr, prefs);

        Assert.AreEqual(1, alerts.Count);
        Assert.AreEqual(NotificationType.MatchStarted, alerts[0].Type);
    }

    [TestMethod]
    public async Task Test38_ReEnablingGoal_DoesNotReplayHistoricalGoals()
    {
        var renderer = new TestNotificationRenderer();
        var service = new NotificationService(renderer: renderer);

        // 1. Goal occurred while Goal notifications were disabled
        var disabledPrefs = DefaultPreferences.CreateDefault() with
        {
            Notifications = new NotificationPreferences
            {
                GlobalEnabled = true,
                EnabledTypes = new List<NotificationType> { NotificationType.MatchStarted }
            }
        };

        var prev = CreateTrayData("fix_barca", "LIVE", 10, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        var goalEvents = new List<TrayEventDto>
        {
            new() { Id = "evt_past_goal", Type = "Goal", Minute = 15, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Pedri" }
        };
        var curr1 = CreateTrayData("fix_barca", "LIVE", 15, 1, 0, goalEvents, CanonicalEntityIds.Barcelona, "team_rma");

        await service.ProcessTrayUpdateAsync(prev, curr1, disabledPrefs);
        Assert.AreEqual(0, renderer.Rendered.Count);

        // 2. User re-enables Goal notifications
        var enabledPrefs = DefaultPreferences.CreateDefault();

        // Poll again with same data
        var alerts2 = await service.ProcessTrayUpdateAsync(curr1, curr1, enabledPrefs);

        // Must NOT replay the past goal
        Assert.AreEqual(0, alerts2.Count);
        Assert.AreEqual(0, renderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test39_ExistingDeduplication_SurvivesPreferenceChanges()
    {
        var renderer = new TestNotificationRenderer();
        var store = new NotificationDeduplicationStore(_tempDir);
        var service = new NotificationService(renderer: renderer, store: store);

        var prefs1 = DefaultPreferences.CreateDefault();
        var prev = CreateTrayData("fix_barca", "LIVE", 10, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        var goalEvent = new List<TrayEventDto>
        {
            new() { Id = "evt_g1", Type = "Goal", Minute = 15, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Gavi" }
        };
        var curr = CreateTrayData("fix_barca", "LIVE", 15, 1, 0, goalEvent, CanonicalEntityIds.Barcelona, "team_rma");

        await service.ProcessTrayUpdateAsync(prev, curr, prefs1);
        Assert.AreEqual(1, renderer.Rendered.Count);

        // Change preferences to unfollow Barcelona and follow again
        await _prefService.UnfollowTeamAsync(CanonicalEntityIds.Barcelona);
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);

        // Process same goal event again
        var alerts2 = await service.ProcessTrayUpdateAsync(prev, curr, _prefService.GetPreferences());

        // Deduplication history strictly prevents replay
        Assert.AreEqual(0, alerts2.Count);
    }

    [TestMethod]
    public async Task Test40_OfflineCachedData_NeverCreatesNotifications()
    {
        var renderer = new TestNotificationRenderer();
        var notificationService = new NotificationService(renderer: renderer);
        var cacheService = new LocalCacheService(_tempDir);

        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_cached_1", Type = "Goal", Minute = 35, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Lewandowski" }
        };
        var cachedData = CreateTrayData("fix_barca", "LIVE", 35, 1, 0, events, CanonicalEntityIds.Barcelona, "team_rma");
        await cacheService.SaveCacheAsync(WrapEnvelope(cachedData), "\"etag_123\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => throw new HttpRequestException("Backend offline")
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, notificationService, _prefService);

        // Start offline
        await syncService.StartAsync();

        // Zero notifications generated from cache load
        Assert.AreEqual(0, renderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test41_NewConfirmedEventAfterOffline_NotifiesWhenRelevant()
    {
        var renderer = new TestNotificationRenderer();
        var store = new NotificationDeduplicationStore(_tempDir);
        var notificationService = new NotificationService(renderer: renderer, store: store);
        var cacheService = new LocalCacheService(_tempDir);
        await _prefService.LoadAsync();

        var initialData = CreateTrayData("fix_barca", "LIVE", 10, 0, 0, null, CanonicalEntityIds.Barcelona, "team_rma");
        await cacheService.SaveCacheAsync(WrapEnvelope(initialData), "\"etag_0\"");

        bool isOnline = false;
        var newEvents = new List<TrayEventDto>
        {
            new() { Id = "evt_fresh_1", Type = "Goal", Minute = 88, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Raphinha" }
        };
        var freshData = CreateTrayData("fix_barca", "LIVE", 88, 1, 0, newEvents, CanonicalEntityIds.Barcelona, "team_rma");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                if (!isOnline) throw new HttpRequestException("Backend offline");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(WrapEnvelope(freshData, 2)), Encoding.UTF8, "application/json"),
                    Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_recovered\"") }
                };
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, notificationService, _prefService);

        await syncService.StartAsync();
        Assert.AreEqual(0, renderer.Rendered.Count);

        // Network recovers
        isOnline = true;
        await syncService.PollOnceAsync();

        Assert.AreEqual(1, renderer.Rendered.Count);
        Assert.AreEqual("88' Raphinha", renderer.Rendered[0].Body);
    }

    // ============================================================
    // Step 17: Taskbar Prioritization Tests (42-47)
    // ============================================================

    [TestMethod]
    public void Test42_FollowedLiveTeamMatch_IsSelectedOverUnrelatedLiveMatch()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var unrelatedLive = CreateMatch("fix_unrel", "team_x", "team_y", "comp_z", state: "LIVE", elapsed: 80);
        var followedLive = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 20);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { unrelatedLive, followedLive } };
        var active = TrayTickerFormatter.GetActiveMatch(data, prefs);

        Assert.IsNotNull(active);
        Assert.AreEqual("fix_barca", active.Id);
    }

    [TestMethod]
    public void Test43_FollowedUpcomingMatch_IsSelectedOverUnrelatedUpcomingMatch()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var now = DateTimeOffset.UtcNow;
        var unrelatedUpcoming = CreateMatch("fix_unrel", "team_x", "team_y", "comp_z", state: "PREMATCH", kickoff: now.AddHours(1));
        var followedUpcoming = CreateMatch("fix_ahly", CanonicalEntityIds.AlAhly, "team_zamalek", CanonicalEntityIds.EgyptianPremierLeague, state: "PREMATCH", kickoff: now.AddHours(2));

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { unrelatedUpcoming, followedUpcoming } };
        var active = TrayTickerFormatter.GetActiveMatch(data, prefs);

        Assert.IsNotNull(active);
        Assert.AreEqual("fix_ahly", active.Id);
    }

    [TestMethod]
    public void Test44_FollowedCompetitionMatch_OutranksUnrelatedMatch()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var unrelatedMatch = CreateMatch("fix_unrel", "team_a", "team_b", "comp_unrelated", state: "LIVE", elapsed: 50);
        var plMatch = CreateMatch("fix_pl", "team_wolves", "team_everton", CanonicalEntityIds.PremierLeague, state: "LIVE", elapsed: 30);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { unrelatedMatch, plMatch } };
        var active = TrayTickerFormatter.GetActiveMatch(data, prefs);

        Assert.IsNotNull(active);
        Assert.AreEqual("fix_pl", active.Id);
    }

    [TestMethod]
    public void Test45_NoFollowedMatches_PreservesDeterministicDefaultOrdering()
    {
        var prefs = new UserPreferences
        {
            FollowedTeams = new List<FollowedTeam>(),
            FollowedCompetitions = new List<FollowedCompetition>()
        };

        var live1 = CreateMatch("fix_live_1", "team_1", "team_2", "comp_1", state: "LIVE", elapsed: 25);
        var live2 = CreateMatch("fix_live_2", "team_3", "team_4", "comp_2", state: "LIVE", elapsed: 60);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { live1, live2 } };
        var active = TrayTickerFormatter.GetActiveMatch(data, prefs);

        Assert.IsNotNull(active);
        // Higher elapsed minute live match ranks first
        Assert.AreEqual("fix_live_2", active.Id);
    }

    [TestMethod]
    public void Test46_TwoEquallyRelevantMatches_ResolveDeterministically()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var now = DateTimeOffset.UtcNow;
        var barca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 45, kickoff: now);
        var ahly = CreateMatch("fix_ahly", CanonicalEntityIds.AlAhly, "team_zamalek", CanonicalEntityIds.EgyptianPremierLeague, state: "LIVE", elapsed: 45, kickoff: now);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { barca, ahly } };
        var active = TrayTickerFormatter.GetActiveMatch(data, prefs);

        Assert.IsNotNull(active);
        // Tie-breaker by Id ordinal: "fix_ahly" < "fix_barca"
        Assert.AreEqual("fix_ahly", active.Id);
    }

    [TestMethod]
    public void Test47_StaleFollowedMatch_FormatsCleanlyWithOfflineBadge()
    {
        var prefs = DefaultPreferences.CreateDefault();
        var match = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, homeName: "FC Barcelona", awayName: "Real Madrid", state: "LIVE", elapsed: 65, homeScore: 2, awayScore: 1, homeShortName: "BAR", awayShortName: "RMA");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { match } };

        var formattedFresh = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Fresh, prefs);
        Assert.AreEqual("BAR 2–1 RMA 65'", formattedFresh);

        var formattedStale = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Stale, prefs);
        Assert.AreEqual("BAR 2–1 RMA 65' · offline", formattedStale);

        var formattedOnlineStale = TrayTickerFormatter.FormatTicker(data, TickerConnectionState.OnlineStale, prefs);
        Assert.AreEqual("BAR 2–1 RMA 65' · cached", formattedOnlineStale);

        var formattedOffline = TrayTickerFormatter.FormatTicker(data, TickerConnectionState.OfflineCached, prefs);
        Assert.AreEqual("BAR 2–1 RMA 65' · offline", formattedOffline);
    }

    // ============================================================
    // WP7 Audit Specific Tests (A, B, C, D, E)
    // ============================================================

    [TestMethod]
    public void TestA_CanonicalBarcelonaFixture_IsRelevant_WithDefaultPreferences()
    {
        var defaultPrefs = DefaultPreferences.CreateDefault();

        // 1. Production API-Football string format ('team_529')
        var prodMatch = CreateMatch("fix_barca_prod", "team_529", "team_541", "comp_140", homeName: "FC Barcelona", awayName: "Real Madrid");
        var prodResult = FixtureRelevanceEngine.EvaluateRelevance(prodMatch, defaultPrefs);
        Assert.IsTrue(prodResult.IsRelevant);
        Assert.IsTrue(prodResult.FollowedByTeam);
        Assert.IsTrue(prodResult.FollowedByCompetition);

        // 2. Numeric string format ('529')
        var numericMatch = CreateMatch("fix_barca_num", "529", "541", "140", homeName: "FC Barcelona", awayName: "Real Madrid");
        var numResult = FixtureRelevanceEngine.EvaluateRelevance(numericMatch, defaultPrefs);
        Assert.IsTrue(numResult.IsRelevant);
        Assert.IsTrue(numResult.FollowedByTeam);

        // 3. Mock alias format ('team_barcelona')
        var mockMatch = CreateMatch("fix_barca_mock", "team_barcelona", "team_realmadrid", "comp_laliga", homeName: "FC Barcelona", awayName: "Real Madrid");
        var mockResult = FixtureRelevanceEngine.EvaluateRelevance(mockMatch, defaultPrefs);
        Assert.IsTrue(mockResult.IsRelevant);
        Assert.IsTrue(mockResult.FollowedByTeam);
    }

    [TestMethod]
    public void TestB_CanonicalAlAhlyFixture_IsRelevant_WithDefaultPreferences()
    {
        var defaultPrefs = DefaultPreferences.CreateDefault();

        // 1. Production API-Football string format ('team_1029')
        var prodMatch = CreateMatch("fix_ahly_prod", "team_other", "team_1029", "comp_233", homeName: "Zamalek", awayName: "Al Ahly SC");
        var prodResult = FixtureRelevanceEngine.EvaluateRelevance(prodMatch, defaultPrefs);
        Assert.IsTrue(prodResult.IsRelevant);
        Assert.IsTrue(prodResult.FollowedByTeam);
        Assert.IsTrue(prodResult.FollowedByCompetition);

        // 2. Numeric string format ('1029')
        var numericMatch = CreateMatch("fix_ahly_num", "9999", "1029", "233", homeName: "Zamalek", awayName: "Al Ahly SC");
        var numResult = FixtureRelevanceEngine.EvaluateRelevance(numericMatch, defaultPrefs);
        Assert.IsTrue(numResult.IsRelevant);
        Assert.IsTrue(numResult.FollowedByTeam);

        // 3. Mock alias format ('team_alahly')
        var mockMatch = CreateMatch("fix_ahly_mock", "team_zamalek", "team_alahly", "comp_egypt_pl", homeName: "Zamalek", awayName: "Al Ahly SC");
        var mockResult = FixtureRelevanceEngine.EvaluateRelevance(mockMatch, defaultPrefs);
        Assert.IsTrue(mockResult.IsRelevant);
        Assert.IsTrue(mockResult.FollowedByTeam);
    }

    [TestMethod]
    public void TestC_CanonicalPremierLeagueFixture_IsRelevant_WithDefaultPreferences()
    {
        var defaultPrefs = DefaultPreferences.CreateDefault();

        // 1. Production API-Football string format ('comp_39')
        var prodMatch = CreateMatch("fix_pl_prod", "team_42", "team_49", "comp_39", homeName: "Arsenal", awayName: "Chelsea", compName: "Premier League");
        var prodResult = FixtureRelevanceEngine.EvaluateRelevance(prodMatch, defaultPrefs);
        Assert.IsTrue(prodResult.IsRelevant);
        Assert.IsFalse(prodResult.FollowedByTeam);
        Assert.IsTrue(prodResult.FollowedByCompetition);

        // 2. Numeric string format ('39')
        var numMatch = CreateMatch("fix_pl_num", "team_42", "team_49", "39", homeName: "Arsenal", awayName: "Chelsea", compName: "Premier League");
        var numResult = FixtureRelevanceEngine.EvaluateRelevance(numMatch, defaultPrefs);
        Assert.IsTrue(numResult.IsRelevant);
        Assert.IsTrue(numResult.FollowedByCompetition);

        // 3. Mock alias format ('comp_pl')
        var mockMatch = CreateMatch("fix_pl_mock", "team_42", "team_49", "comp_pl", homeName: "Arsenal", awayName: "Chelsea", compName: "Premier League");
        var mockResult = FixtureRelevanceEngine.EvaluateRelevance(mockMatch, defaultPrefs);
        Assert.IsTrue(mockResult.IsRelevant);
        Assert.IsTrue(mockResult.FollowedByCompetition);
    }

    [TestMethod]
    public void TestD_DefaultPreferences_MatchCanonicalProviderConfiguration()
    {
        var defaults = DefaultPreferences.CreateDefault();

        // Teams match canonical provider IDs
        Assert.AreEqual(CanonicalEntityIds.Barcelona, defaults.FollowedTeams[0].Id);
        Assert.AreEqual("FC Barcelona", defaults.FollowedTeams[0].Name);

        Assert.AreEqual(CanonicalEntityIds.AlAhly, defaults.FollowedTeams[1].Id);
        Assert.AreEqual("Al Ahly SC", defaults.FollowedTeams[1].Name);

        // Competitions match TARGET_COMPETITION_IDS (39, 140, 233, 2, 6, 1)
        var compMap = defaults.FollowedCompetitions.ToDictionary(c => c.Id, c => c.Name);
        Assert.IsTrue(compMap.ContainsKey(CanonicalEntityIds.PremierLeague));
        Assert.AreEqual("Premier League", compMap[CanonicalEntityIds.PremierLeague]);

        Assert.IsTrue(compMap.ContainsKey(CanonicalEntityIds.LaLiga));
        Assert.AreEqual("La Liga", compMap[CanonicalEntityIds.LaLiga]);

        Assert.IsTrue(compMap.ContainsKey(CanonicalEntityIds.EgyptianPremierLeague));
        Assert.AreEqual("Egyptian Premier League", compMap[CanonicalEntityIds.EgyptianPremierLeague]);

        Assert.IsTrue(compMap.ContainsKey(CanonicalEntityIds.ChampionsLeague));
        Assert.AreEqual("UEFA Champions League", compMap[CanonicalEntityIds.ChampionsLeague]);

        Assert.IsTrue(compMap.ContainsKey(CanonicalEntityIds.AfricaCupOfNations));
        Assert.AreEqual("CAF Africa Cup of Nations", compMap[CanonicalEntityIds.AfricaCupOfNations]);

        Assert.IsTrue(compMap.ContainsKey(CanonicalEntityIds.WorldCup));
        Assert.AreEqual("FIFA World Cup", compMap[CanonicalEntityIds.WorldCup]);
    }

    [TestMethod]
    public void TestE_NotificationType_AllDefaultEnabledTypes_AreValidEnumMembers()
    {
        var defaults = DefaultPreferences.CreateDefault();
        var enabledTypes = defaults.Notifications.EnabledTypes;

        Assert.IsNotNull(enabledTypes);
        Assert.AreEqual(Enum.GetValues<NotificationType>().Length, enabledTypes.Count);

        // Ensure every enabled type is a valid defined enum member of NotificationType
        foreach (var type in enabledTypes)
        {
            Assert.IsTrue(Enum.IsDefined(typeof(NotificationType), type), $"Type {type} is not a valid defined NotificationType enum member.");
        }

        // Specifically verify exact canonical categories
        Assert.IsTrue(enabledTypes.Contains(NotificationType.MatchStarted));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.Goal));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.HalfTime));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.MatchFinished));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.MatchPostponed));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.MatchCancelled));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.MatchResumed));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.RedCard));
        Assert.IsTrue(enabledTypes.Contains(NotificationType.PenaltyAwarded));
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static TrayMatchDto CreateMatch(
        string id,
        string homeId,
        string awayId,
        string compId,
        string homeName = "Home Team",
        string awayName = "Away Team",
        string compName = "Competition",
        string state = "PREMATCH",
        int elapsed = 0,
        int homeScore = 0,
        int awayScore = 0,
        DateTimeOffset? kickoff = null,
        string? homeShortName = null,
        string? awayShortName = null)
    {
        return new TrayMatchDto
        {
            Id = id,
            State = state,
            ElapsedMinute = elapsed,
            ExtraMinute = 0,
            KickoffTime = kickoff ?? DateTimeOffset.UtcNow,
            HomeTeam = new TeamSummaryDto
            {
                Id = homeId,
                Name = homeName,
                ShortName = homeShortName ?? (homeName.Length <= 3 ? homeName.ToUpperInvariant() : homeName.Substring(0, 3).ToUpperInvariant())
            },
            AwayTeam = new TeamSummaryDto
            {
                Id = awayId,
                Name = awayName,
                ShortName = awayShortName ?? (awayName.Length <= 3 ? awayName.ToUpperInvariant() : awayName.Substring(0, 3).ToUpperInvariant())
            },
            Competition = new CompetitionSummaryDto { Id = compId, Name = compName, Code = compName },
            Score = new ScoreDto { Home = homeScore, Away = awayScore, Display = $"{homeScore} - {awayScore}" },
            RecentEvents = new List<TrayEventDto>()
        };
    }

    private static TrayDataDto CreateTrayData(
        string fixtureId,
        string state,
        int elapsed,
        int homeScore,
        int awayScore,
        List<TrayEventDto>? events = null,
        string homeTeamId = CanonicalEntityIds.Barcelona,
        string awayTeamId = "team_rma",
        string compId = CanonicalEntityIds.LaLiga)
    {
        return new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = fixtureId,
                    State = state,
                    ElapsedMinute = elapsed,
                    ExtraMinute = 0,
                    KickoffTime = DateTimeOffset.UtcNow,
                    HomeTeam = new TeamSummaryDto { Id = homeTeamId, Name = "FC Barcelona", ShortName = "BAR" },
                    AwayTeam = new TeamSummaryDto { Id = awayTeamId, Name = "Real Madrid", ShortName = "RMA" },
                    Competition = new CompetitionSummaryDto { Id = compId, Name = "La Liga", Code = "ESP_L1" },
                    Score = new ScoreDto { Home = homeScore, Away = awayScore, Display = $"{homeScore} - {awayScore}" },
                    RecentEvents = events ?? new List<TrayEventDto>()
                }
            }
        };
    }

    private static ApiResponseEnvelope<TrayDataDto> WrapEnvelope(TrayDataDto data, int version = 1)
    {
        return new ApiResponseEnvelope<TrayDataDto>
        {
            Version = version,
            SchemaVersion = "1.0",
            ServerTime = DateTimeOffset.UtcNow,
            GeneratedAt = DateTimeOffset.UtcNow,
            Data = data,
            Error = null
        };
    }
}
