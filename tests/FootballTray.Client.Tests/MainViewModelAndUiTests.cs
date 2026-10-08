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
using FootballTray.Client.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class MainViewModelAndUiTests
{
    private string _tempDir = string.Empty;
    private PreferencesService _prefService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ft_ui_test_{Guid.NewGuid():N}");
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
    // Step 13: Preferences UI Tests (1-11)
    // ============================================================

    [TestMethod]
    public async Task Test01_UiLoads_PersistedPreferences()
    {
        // 1. Arrange persisted state with customized preferences
        await _prefService.LoadAsync();
        await _prefService.FollowTeamAsync(new FollowedTeam("team_42", "Arsenal FC", "ARS", "England"));
        await _prefService.SetNotificationTypeEnabledAsync(NotificationType.HalfTime, false);

        // 2. Initialize ViewModel with that preferences service
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // 3. Assert ViewModel accurately loaded the state
        Assert.IsTrue(vm.AvailableTeams.Any(t => t.Id == "team_42" && t.IsFollowed));
        var htCat = vm.NotificationCategories.FirstOrDefault(c => c.Type == NotificationType.HalfTime);
        Assert.IsNotNull(htCat);
        Assert.IsFalse(htCat.IsEnabled);
    }

    [TestMethod]
    public async Task Test02_DefaultPreferences_AppearCorrectly()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Boot with empty followed lists
        Assert.AreEqual(0, vm.AvailableTeams.Count);
        Assert.AreEqual(0, vm.AvailableCompetitions.Count);

        // Global and all 7 categories enabled
        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);
        Assert.AreEqual(7, vm.NotificationCategories.Count);
        Assert.IsTrue(vm.NotificationCategories.All(c => c.IsEnabled));
    }

    [TestMethod]
    public async Task Test03_FollowTeam_UpdatesUi()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Follow Barcelona
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona, "FC Barcelona");
        var barca = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
        Assert.IsTrue(barca.IsFollowed);

        // Unfollow Barcelona then follow again
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        Assert.IsFalse(barca.IsFollowed);

        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        Assert.IsTrue(barca.IsFollowed);

        // Verify persistence
        var persisted = _prefService.GetPreferences();
        Assert.IsTrue(persisted.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
    }

    [TestMethod]
    public async Task Test04_UnfollowTeam_UpdatesUi()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Follow Al Ahly
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.AlAhly, "Al Ahly SC");
        var ahly = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.AlAhly);
        Assert.IsTrue(ahly.IsFollowed);

        // Unfollow
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.AlAhly);
        Assert.IsFalse(ahly.IsFollowed);
        var persisted = _prefService.GetPreferences();
        Assert.IsFalse(persisted.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.AlAhly));
    }

    [TestMethod]
    public async Task Test05_FollowCompetition_UpdatesUi()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Follow Premier League
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.PremierLeague, "Premier League", "PL");
        var pl = vm.AvailableCompetitions.First(c => c.Id == CanonicalEntityIds.PremierLeague);
        Assert.IsTrue(pl.IsFollowed);

        // Unfollow then re-follow
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.PremierLeague);
        Assert.IsFalse(pl.IsFollowed);

        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.PremierLeague);
        Assert.IsTrue(pl.IsFollowed);

        var persisted = _prefService.GetPreferences();
        Assert.IsTrue(persisted.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague));
    }

    [TestMethod]
    public async Task Test06_UnfollowCompetition_UpdatesUi()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Follow La Liga
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.LaLiga, "La Liga", "ESP_L1");
        var laliga = vm.AvailableCompetitions.First(c => c.Id == CanonicalEntityIds.LaLiga);
        Assert.IsTrue(laliga.IsFollowed);

        // Unfollow
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.LaLiga);
        Assert.IsFalse(laliga.IsFollowed);
        var persisted = _prefService.GetPreferences();
        Assert.IsFalse(persisted.FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.LaLiga));
    }

    [TestMethod]
    public async Task Test07_GlobalNotificationToggle_UpdatesUiAndPersistence()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);

        await vm.ToggleGlobalNotificationsAsync(false);
        Assert.IsFalse(vm.IsGlobalNotificationsEnabled);
        Assert.IsFalse(_prefService.GetPreferences().Notifications.GlobalEnabled);

        await vm.ToggleGlobalNotificationsAsync(true);
        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);
        Assert.IsTrue(_prefService.GetPreferences().Notifications.GlobalEnabled);
    }

    [TestMethod]
    public async Task Test08_CategoryToggle_UpdatesUiAndPersistence()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var goalCategory = vm.NotificationCategories.First(c => c.Type == NotificationType.Goal);
        Assert.IsTrue(goalCategory.IsEnabled);

        await vm.ToggleNotificationCategoryAsync(NotificationType.Goal, false);
        Assert.IsFalse(goalCategory.IsEnabled);
        Assert.IsFalse(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.Goal));

        await vm.ToggleNotificationCategoryAsync(NotificationType.Goal, true);
        Assert.IsTrue(goalCategory.IsEnabled);
        Assert.IsTrue(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.Goal));
    }

    [TestMethod]
    public async Task Test09_MutationFailure_PreservesPreviousState()
    {
        // Use a mock/failing preferences service
        var failingService = new FailingPreferencesService(_prefService);
        using var vm = new MainViewModel(failingService);
        await vm.InitializeAsync();

        // Attempt to toggle global notifications which will fail
        await vm.ToggleGlobalNotificationsAsync(false);

        // UI state must revert to true and surface a user readable error
        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);
        Assert.IsTrue(vm.HasStatusMessage);
        Assert.IsTrue(vm.StatusMessage!.Contains("Unable to update notification settings"));
    }

    [TestMethod]
    public async Task Test10_Restart_ReloadsMutations()
    {
        // 1. Mutate state via VM 1
        using (var vm1 = new MainViewModel(_prefService))
        {
            await vm1.InitializeAsync();
            await vm1.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona, "FC Barcelona"); // follow
            await vm1.ToggleNotificationCategoryAsync(NotificationType.Goal, false);
        }

        // 2. Simulate fresh app start with VM 2 pointing to same storage
        var prefService2 = new PreferencesService(_tempDir);
        using (var vm2 = new MainViewModel(prefService2))
        {
            await vm2.InitializeAsync();
            var barca = vm2.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
            var goal = vm2.NotificationCategories.First(c => c.Type == NotificationType.Goal);

            Assert.IsTrue(barca.IsFollowed);
            Assert.IsFalse(goal.IsEnabled);
        }
    }

    [TestMethod]
    public async Task Test11_CorruptPreferenceFile_FallsBackSafely()
    {
        string filePath = Path.Combine(_tempDir, "preferences.json");
        await File.WriteAllTextAsync(filePath, "{ invalid corrupted preference json @#$ }");

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Must recover safely to default values (empty followed lists)
        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);
        Assert.AreEqual(0, vm.AvailableTeams.Count);
        Assert.AreEqual(0, vm.AvailableCompetitions.Count);
    }

    // ============================================================
    // Step 14: Match Discovery Tests (12-20)
    // ============================================================

    [TestMethod]
    public async Task Test12_AllFilter_DisplaysAllAvailableMatches()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_1", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 35),
            CreateMatch("fix_2", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague, state: "PREMATCH"),
            CreateMatch("fix_3", "team_roma", "team_lazio", "comp_seriea", state: "FT")
        };
        var data = new TrayDataDto { Matches = matches };

        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.All);

        Assert.AreEqual(3, vm.FilteredMatches.Count);
        Assert.IsTrue(vm.HasMatches);
        Assert.IsFalse(vm.IsEmpty);
    }

    [TestMethod]
    public async Task Test13_FollowingFilter_DisplaysOnlyRelevantMatches()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.PremierLeague);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_1", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga), // Followed team + comp
            CreateMatch("fix_2", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague),    // Followed comp
            CreateMatch("fix_3", "team_roma", "team_lazio", "comp_seriea")                             // Unfollowed
        };
        var data = new TrayDataDto { Matches = matches };

        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Following);

        Assert.AreEqual(2, vm.FilteredMatches.Count);
        Assert.IsTrue(vm.FilteredMatches.Any(m => m.Id == "fix_1"));
        Assert.IsTrue(vm.FilteredMatches.Any(m => m.Id == "fix_2"));
        Assert.IsFalse(vm.FilteredMatches.Any(m => m.Id == "fix_3"));
    }

    [TestMethod]
    public async Task Test14_LiveFilter_DisplaysLiveMatches()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_live1", "team_a", "team_b", "comp_1", state: "LIVE", elapsed: 40),
            CreateMatch("fix_live2", "team_c", "team_d", "comp_2", state: "HT", elapsed: 45),
            CreateMatch("fix_prem", "team_e", "team_f", "comp_3", state: "PREMATCH"),
            CreateMatch("fix_ft", "team_g", "team_h", "comp_4", state: "FT")
        };
        var data = new TrayDataDto { Matches = matches };

        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Live);

        Assert.AreEqual(2, vm.FilteredMatches.Count);
        Assert.IsTrue(vm.FilteredMatches.All(m => m.IsLive));
    }

    [TestMethod]
    public async Task Test15_UpcomingFilter_DisplaysUpcomingMatches()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_live", "team_a", "team_b", "comp_1", state: "LIVE", elapsed: 10),
            CreateMatch("fix_up1", "team_c", "team_d", "comp_2", state: "PREMATCH"),
            CreateMatch("fix_up2", "team_e", "team_f", "comp_3", state: "NS")
        };
        var data = new TrayDataDto { Matches = matches };

        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Upcoming);

        Assert.AreEqual(2, vm.FilteredMatches.Count);
        Assert.IsTrue(vm.FilteredMatches.All(m => m.IsPrematch));
    }

    [TestMethod]
    public async Task Test16_FinishedFilter_DisplaysTerminalMatches()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_live", "team_a", "team_b", "comp_1", state: "LIVE", elapsed: 90),
            CreateMatch("fix_ft1", "team_c", "team_d", "comp_2", state: "FT"),
            CreateMatch("fix_ft2", "team_e", "team_f", "comp_3", state: "AET")
        };
        var data = new TrayDataDto { Matches = matches };

        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Finished);

        Assert.AreEqual(2, vm.FilteredMatches.Count);
        Assert.IsTrue(vm.FilteredMatches.All(m => m.IsFinished));
    }

    [TestMethod]
    public async Task Test17_Filters_DoNotMutateUnderlyingTrayData()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_1", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 35),
            CreateMatch("fix_2", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague, state: "PREMATCH")
        };
        var data = new TrayDataDto { Matches = matches };

        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        // Switch filters repeatedly
        vm.SetFilter(MatchFilterType.Live);
        Assert.AreEqual(1, vm.FilteredMatches.Count);

        vm.SetFilter(MatchFilterType.Upcoming);
        Assert.AreEqual(1, vm.FilteredMatches.Count);

        vm.SetFilter(MatchFilterType.All);
        Assert.AreEqual(2, vm.FilteredMatches.Count);

        // Verify underlying list in TrayData was not modified
        Assert.AreEqual(2, data.Matches.Count);
    }

    [TestMethod]
    public async Task Test18_DeterministicOrdering_RemainsStable()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var kickoff = DateTimeOffset.UtcNow;
        var matchA = CreateMatch("fix_a", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 75, kickoff: kickoff);
        var matchB = CreateMatch("fix_b", CanonicalEntityIds.AlAhly, "team_zamalek", CanonicalEntityIds.EgyptianPremierLeague, state: "LIVE", elapsed: 30, kickoff: kickoff);

        // Provide in reverse order
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchB, matchA } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        Assert.AreEqual("fix_a", vm.FilteredMatches[0].Id);
        Assert.AreEqual("fix_b", vm.FilteredMatches[1].Id);
    }

    [TestMethod]
    public async Task Test19_RelevanceReason_IsCorrectlyDisplayed()
    {
        await _prefService.FollowCompetitionAsync(DefaultPreferences.PremierLeague);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var match = CreateMatch("fix_pl", "team_everton", "team_wolves", CanonicalEntityIds.PremierLeague, compName: "Premier League");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { match } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        var item = vm.FilteredMatches.First();
        Assert.IsTrue(item.IsRelevant);
        Assert.IsFalse(item.FollowedByTeam);
        Assert.IsTrue(item.FollowedByCompetition);
        Assert.IsTrue(item.RelevanceDisplay.Contains("Premier League"));
    }

    [TestMethod]
    public async Task Test20_MultipleRelevanceReasons_ArePreserved()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.LaLiga);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Barcelona playing in La Liga (both followed)
        var match = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_valencia", CanonicalEntityIds.LaLiga, homeName: "FC Barcelona", compName: "La Liga");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { match } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        var item = vm.FilteredMatches.First();
        Assert.IsTrue(item.IsRelevant);
        Assert.IsTrue(item.FollowedByTeam);
        Assert.IsTrue(item.FollowedByCompetition);
        Assert.IsTrue(item.RelevanceReasons.Count >= 2);
        Assert.IsTrue(item.RelevanceDisplay.Contains("FC Barcelona"));
        Assert.IsTrue(item.RelevanceDisplay.Contains("La Liga"));
    }

    // ============================================================
    // Step 15: Offline Tests (21-25)
    // ============================================================

    [TestMethod]
    public async Task Test21_CachedMatches_AppearWhileBackendUnavailable()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var cachedMatch = CreateMatch("fix_cached", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 55);
        var cachedData = new TrayDataDto { Matches = new List<TrayMatchDto> { cachedMatch } };
        await cacheService.SaveCacheAsync(WrapEnvelope(cachedData), "\"etag_1\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => throw new HttpRequestException("Backend offline")
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, preferencesService: _prefService);

        using var vm = new MainViewModel(_prefService, syncService);
        await syncService.StartAsync();
        await vm.InitializeAsync();

        // Matches should appear even though backend is offline
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_cached", vm.FilteredMatches[0].Id);
        Assert.IsTrue(vm.IsOffline);
    }

    [TestMethod]
    public async Task Test22_PreferenceChanges_WorkWhileOffline()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => throw new HttpRequestException("Backend offline")
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, preferencesService: _prefService);

        using var vm = new MainViewModel(_prefService, syncService);
        await syncService.StartAsync();
        await vm.InitializeAsync();

        // Mutate preferences while offline: follow Barcelona, then unfollow
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona, "FC Barcelona");
        var barca = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
        Assert.IsTrue(barca.IsFollowed);

        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        Assert.IsFalse(barca.IsFollowed);

        // Verify locally persisted
        var persisted = _prefService.GetPreferences();
        Assert.IsFalse(persisted.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
    }

    [TestMethod]
    public async Task Test23_OfflineState_RemainsClearlyVisible()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        vm.RefreshMatches(new TrayDataDto(), DataFreshnessState.Stale, TickerConnectionState.OfflineCached);

        Assert.IsTrue(vm.IsOffline);
        Assert.IsTrue(vm.ConnectionStatusText.Contains("Offline"));
    }

    [TestMethod]
    public async Task Test24_NoNotificationReplay_OccursAfterChangingPreferencesOffline()
    {
        var renderer = new TestNotificationRenderer();
        var store = new NotificationDeduplicationStore(_tempDir);
        var notificationService = new NotificationService(renderer: renderer, store: store);
        var cacheService = new LocalCacheService(_tempDir);

        var events = new List<TrayEventDto>
        {
            new() { Id = "evt_g1", Type = "Goal", Minute = 20, TeamId = CanonicalEntityIds.Barcelona, PlayerName = "Pedri" }
        };
        var cachedData = new TrayDataDto
        {
            Matches = new List<TrayMatchDto> { CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 20, events: events) }
        };
        await cacheService.SaveCacheAsync(WrapEnvelope(cachedData), "\"etag_1\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => throw new HttpRequestException("Backend offline")
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, notificationService, _prefService);

        using var vm = new MainViewModel(_prefService, syncService);
        await syncService.StartAsync();
        await vm.InitializeAsync();

        // Toggle preference offline
        await vm.ToggleNotificationCategoryAsync(NotificationType.Goal, false);
        await vm.ToggleNotificationCategoryAsync(NotificationType.Goal, true);

        // Must NOT emit any notification replay from cached match
        Assert.AreEqual(0, renderer.Rendered.Count);
    }

    [TestMethod]
    public async Task Test25_Reconnection_UpdatesMatchesWithoutLosingPreferences()
    {
        var cacheService = new LocalCacheService(_tempDir);
        bool isOnline = false;

        var liveMatch = CreateMatch("fix_live", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 70);
        var liveData = new TrayDataDto { Matches = new List<TrayMatchDto> { liveMatch } };

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                if (!isOnline) throw new HttpRequestException("Backend offline");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(WrapEnvelope(liveData, 2)), Encoding.UTF8, "application/json"),
                    Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag_reconnected\"") }
                };
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, preferencesService: _prefService);

        await _prefService.FollowTeamAsync(DefaultPreferences.AlAhly);

        using var vm = new MainViewModel(_prefService, syncService);
        await syncService.StartAsync();
        await vm.InitializeAsync();

        // Mutate preferences while offline
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.AlAhly); // unfollow Al Ahly

        // Reconnect
        isOnline = true;
        await syncService.PollOnceAsync();

        // Matches update and customized preferences are strictly preserved
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_live", vm.FilteredMatches[0].Id);
        Assert.IsFalse(vm.IsOffline);
        Assert.IsFalse(vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.AlAhly).IsFollowed);
    }

    // ============================================================
    // Step 16: Taskbar Integration Tests (26-30)
    // ============================================================

    [TestMethod]
    public async Task Test26_FollowingPreferenceChange_ImmediatelyRecalculatesActiveMatch()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var matchUnrelated = CreateMatch("fix_unrel", "team_x", "team_y", "comp_z", state: "LIVE", elapsed: 80);
        var matchBarca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 20);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchUnrelated, matchBarca } };
        await cacheService.SaveCacheAsync(WrapEnvelope(data), "\"etag_1\"");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.NotModified)
        };
        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        using var syncService = new SyncService(client, cacheService, preferencesService: _prefService);

        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.LaLiga);

        using var vm = new MainViewModel(_prefService, syncService);
        await syncService.StartAsync();
        await vm.InitializeAsync();

        // 1. Initial state: Barcelona is followed -> Barca match is active
        Assert.AreEqual("fix_barca", vm.ActiveMatchId);
        Assert.IsTrue(vm.FilteredMatches.First(m => m.Id == "fix_barca").IsActiveTaskbarMatch);

        // 2. Unfollow Barcelona -> Unrelated match with higher elapsed minute becomes active
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.LaLiga);

        Assert.AreEqual("fix_unrel", vm.ActiveMatchId);
        Assert.IsTrue(vm.FilteredMatches.First(m => m.Id == "fix_unrel").IsActiveTaskbarMatch);
    }

    [TestMethod]
    public async Task Test27_FollowedLiveTeam_OutranksUnrelatedLiveMatch()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.LaLiga);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matchUnrelated = CreateMatch("fix_unrel", "team_x", "team_y", "comp_z", state: "LIVE", elapsed: 89);
        var matchBarca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 15);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchUnrelated, matchBarca } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        Assert.AreEqual("fix_barca", vm.ActiveMatchId);
    }

    [TestMethod]
    public async Task Test28_Unfollow_RemovesThatRelevance()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.LaLiga);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matchBarca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga);
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchBarca } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        Assert.IsTrue(vm.FilteredMatches[0].IsRelevant);

        // Unfollow both team and comp
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.LaLiga);

        Assert.IsFalse(vm.FilteredMatches[0].IsRelevant);
    }

    [TestMethod]
    public async Task Test29_TickerFormatting_RemainsUnchangedForSelectedMatch()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matchBarca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, homeName: "FC Barcelona", awayName: "Real Madrid", state: "LIVE", elapsed: 65, homeScore: 2, awayScore: 1, homeShortName: "BAR", awayShortName: "RMA");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchBarca } };

        var formatted = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Fresh, _prefService.GetPreferences());
        Assert.AreEqual("BAR 2–1 RMA 65'", formatted);
    }

    [TestMethod]
    public async Task Test30_StaleOfflineMarker_RemainsCorrect()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matchBarca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, homeName: "FC Barcelona", awayName: "Real Madrid", state: "LIVE", elapsed: 65, homeScore: 2, awayScore: 1, homeShortName: "BAR", awayShortName: "RMA");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchBarca } };

        var formattedStale = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Stale, _prefService.GetPreferences());
        Assert.AreEqual("BAR 2–1 RMA 65' · offline", formattedStale);
    }

    [TestMethod]
    public async Task Test31_Complete_ManualInteraction_Sequence_A_Through_G()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowTeamAsync(DefaultPreferences.AlAhly);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.PremierLeague);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.LaLiga);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.EgyptianPremierLeague);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.ChampionsLeague);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.AfricaCupOfNations);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.WorldCup);

        // -------------------------------------------------------------
        // A. Main UI: Initialize VM and load default state
        // -------------------------------------------------------------
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matches = new List<TrayMatchDto>
        {
            CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, homeName: "FC Barcelona", awayName: "Real Madrid", compName: "La Liga", state: "LIVE", elapsed: 65, homeScore: 2, awayScore: 1, homeShortName: "BAR", awayShortName: "RMA"),
            CreateMatch("fix_pl", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague, homeName: "Chelsea", awayName: "Arsenal", compName: "Premier League", state: "PREMATCH", homeShortName: "CHE", awayShortName: "ARS"),
            CreateMatch("fix_unrel", "team_roma", "team_lazio", "comp_seriea", homeName: "Roma", awayName: "Lazio", compName: "Serie A", state: "FT", homeScore: 0, awayScore: 1, homeShortName: "ROM", awayShortName: "LAZ")
        };
        var data = new TrayDataDto { Matches = matches };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        Assert.AreEqual(3, vm.FilteredMatches.Count);
        Assert.AreEqual(2, vm.AvailableTeams.Count);
        Assert.AreEqual(6, vm.AvailableCompetitions.Count);
        Assert.AreEqual(7, vm.NotificationCategories.Count);

        // -------------------------------------------------------------
        // B. Followed Teams: Toggle Barcelona OFF -> Persists -> Toggle ON
        // -------------------------------------------------------------
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        var barcaTeam = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
        Assert.IsFalse(barcaTeam.IsFollowed);
        Assert.IsFalse(_prefService.GetPreferences().FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));

        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        Assert.IsTrue(barcaTeam.IsFollowed);
        Assert.IsTrue(_prefService.GetPreferences().FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));

        // -------------------------------------------------------------
        // C. Followed Competitions: Toggle Premier League OFF -> ON
        // -------------------------------------------------------------
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.PremierLeague);
        var plComp = vm.AvailableCompetitions.First(c => c.Id == CanonicalEntityIds.PremierLeague);
        Assert.IsFalse(plComp.IsFollowed);
        Assert.IsFalse(_prefService.GetPreferences().FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague));

        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.PremierLeague);
        Assert.IsTrue(plComp.IsFollowed);
        Assert.IsTrue(_prefService.GetPreferences().FollowedCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague));

        // -------------------------------------------------------------
        // D. Notifications: Toggle Global -> Toggle Category Goal
        // -------------------------------------------------------------
        await vm.ToggleGlobalNotificationsAsync(false);
        Assert.IsFalse(vm.IsGlobalNotificationsEnabled);
        Assert.IsFalse(_prefService.GetPreferences().Notifications.GlobalEnabled);

        await vm.ToggleGlobalNotificationsAsync(true);
        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);
        Assert.IsTrue(_prefService.GetPreferences().Notifications.GlobalEnabled);

        await vm.ToggleNotificationCategoryAsync(NotificationType.Goal, false);
        var goalCat = vm.NotificationCategories.First(c => c.Type == NotificationType.Goal);
        var startCat = vm.NotificationCategories.First(c => c.Type == NotificationType.MatchStarted);
        Assert.IsFalse(goalCat.IsEnabled);
        Assert.IsTrue(startCat.IsEnabled);
        Assert.IsFalse(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.Goal));
        Assert.IsTrue(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.MatchStarted));

        await vm.ToggleNotificationCategoryAsync(NotificationType.Goal, true);
        Assert.IsTrue(goalCat.IsEnabled);
        Assert.IsTrue(_prefService.GetPreferences().Notifications.IsTypeEnabled(NotificationType.Goal));

        // -------------------------------------------------------------
        // E. Match Filters: All, Following, Live, Upcoming, Finished
        // -------------------------------------------------------------
        vm.SetFilter(MatchFilterType.All);
        Assert.AreEqual(3, vm.FilteredMatches.Count);

        vm.SetFilter(MatchFilterType.Following);
        Assert.AreEqual(2, vm.FilteredMatches.Count);
        Assert.IsTrue(vm.FilteredMatches.Any(m => m.Id == "fix_barca"));
        Assert.IsTrue(vm.FilteredMatches.Any(m => m.Id == "fix_pl"));

        vm.SetFilter(MatchFilterType.Live);
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_barca", vm.FilteredMatches[0].Id);

        vm.SetFilter(MatchFilterType.Upcoming);
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_pl", vm.FilteredMatches[0].Id);

        vm.SetFilter(MatchFilterType.Finished);
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_unrel", vm.FilteredMatches[0].Id);

        // -------------------------------------------------------------
        // F. Offline: Cached matches visible, offline status, editable prefs
        // -------------------------------------------------------------
        vm.RefreshMatches(data, DataFreshnessState.Stale, TickerConnectionState.OfflineCached);
        Assert.IsTrue(vm.IsOffline);
        Assert.IsTrue(vm.ConnectionStatusText.Contains("Offline"));
        Assert.AreEqual(1, vm.FilteredMatches.Count); // Finished filter still active

        vm.SetFilter(MatchFilterType.All);
        Assert.AreEqual(3, vm.FilteredMatches.Count);

        // Mutate preferences while offline
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.AlAhly);
        Assert.IsFalse(vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.AlAhly).IsFollowed);
        Assert.IsFalse(_prefService.GetPreferences().FollowedTeams.Any(t => t.Id == CanonicalEntityIds.AlAhly));

        // -------------------------------------------------------------
        // G. Taskbar: Active match priority updates immediately
        // -------------------------------------------------------------
        Assert.AreEqual("fix_barca", vm.ActiveMatchId);
        Assert.IsTrue(vm.ActiveTickerText.Contains("BAR"));

        // Unfollow Barcelona and La Liga
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.LaLiga);

        // Taskbar priority updates immediately
        Assert.AreEqual("fix_pl", vm.ActiveMatchId);
    }

    // ============================================================
    // Strike 2: Sorting Engine & 7-Day Navigation Tests (32-37)
    // ============================================================

    [TestMethod]
    public async Task Test32_MasterSorting_AllTab_FollowsStrictHierarchy()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.PremierLeague);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // 1. Followed Team match (Barcelona - Finished)
        var barcaFinished = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_sevilla", CanonicalEntityIds.LaLiga, state: "FT");
        // 2. Followed Competition match (Premier League - Prematch)
        var plUpcoming = CreateMatch("fix_pl", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague, state: "PREMATCH");
        // 3. Other match (Serie A - Live 80')
        var otherLive = CreateMatch("fix_other", "team_roma", "team_lazio", "comp_seriea", state: "LIVE", elapsed: 80);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { otherLive, plUpcoming, barcaFinished } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.All);

        Assert.AreEqual(3, vm.FilteredMatches.Count);
        // Strict Hierarchy: Followed Teams (Top) -> Followed Competitions -> Other matches
        Assert.AreEqual("fix_barca", vm.FilteredMatches[0].Id, "Followed team must rank top regardless of match state.");
        Assert.AreEqual("fix_pl", vm.FilteredMatches[1].Id, "Followed competition must rank second.");
        Assert.AreEqual("fix_other", vm.FilteredMatches[2].Id, "Other matches must rank third.");

        // Distinct Followed vs Other collections
        Assert.AreEqual(2, vm.FollowedMatches.Count);
        Assert.AreEqual(1, vm.OtherMatches.Count);
        Assert.AreEqual("fix_other", vm.OtherMatches[0].Id);
    }

    [TestMethod]
    public async Task Test_CompetitionGroups_RendersExpandableAccordionsWithCorrectCounts()
    {
        await _prefService.FollowCompetitionAsync(DefaultPreferences.PremierLeague);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var plMatch1 = CreateMatch("fix_pl_1", "team_1", "team_2", "comp_39", compName: "Premier League", state: "PREMATCH");
        var plMatch2 = CreateMatch("fix_pl_2", "team_3", "team_4", "comp_39", compName: "Premier League", state: "PREMATCH");
        var laligaMatch = CreateMatch("fix_ll_1", "team_5", "team_6", "comp_140", compName: "La Liga", state: "PREMATCH");

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { plMatch1, plMatch2, laligaMatch } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.All);

        Assert.AreEqual(2, vm.CompetitionGroups.Count, "Should group 3 matches into 2 competition accordion groups");

        var plGroup = vm.CompetitionGroups.FirstOrDefault(g => g.CompetitionName == "Premier League");
        Assert.IsNotNull(plGroup);
        Assert.AreEqual(2, plGroup.MatchCount);
        Assert.AreEqual("2", plGroup.MatchCountText);
        Assert.IsTrue(plGroup.IsExpanded, "Followed competition accordion starts expanded by default");
        Assert.AreEqual("\uE70E", plGroup.ChevronGlyph);

        // Test accordion toggle
        plGroup.ToggleExpanded();
        Assert.IsFalse(plGroup.IsExpanded);
        Assert.AreEqual("\uE70D", plGroup.ChevronGlyph);

        var llGroup = vm.CompetitionGroups.FirstOrDefault(g => g.CompetitionName == "La Liga");
        Assert.IsNotNull(llGroup);
        Assert.AreEqual(1, llGroup.MatchCount);
        Assert.IsFalse(llGroup.IsExpanded, "Unfollowed competition accordion starts collapsed by default");
    }

    [TestMethod]
    public async Task Test33_MasterSorting_OtherMatchesSortedByPopularity()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // All 3 matches are unfollowed, but belong to different tiers of popularity
        var uclMatch = CreateMatch("fix_ucl", "team_x", "team_y", "comp_2", compName: "UEFA Champions League", state: "PREMATCH");
        var serieAMatch = CreateMatch("fix_sa", "team_a", "team_b", "comp_135", compName: "Serie A", state: "PREMATCH");
        var unknownMatch = CreateMatch("fix_unk", "team_c", "team_d", "comp_9999", compName: "Regional Cup", state: "PREMATCH");

        // Supply in reverse popularity order
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { unknownMatch, serieAMatch, uclMatch } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.All);

        Assert.AreEqual(3, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_ucl", vm.FilteredMatches[0].Id, "Champions League (tier 1 popularity) must rank first.");
        Assert.AreEqual("fix_sa", vm.FilteredMatches[1].Id, "Serie A (tier 5 popularity) must rank second.");
        Assert.AreEqual("fix_unk", vm.FilteredMatches[2].Id, "Unknown regional cup (tier 100) must rank last.");
    }

    [TestMethod]
    public async Task Test34_MasterSorting_FollowingTab_ExcludesOtherMatches()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);
        await _prefService.FollowCompetitionAsync(DefaultPreferences.PremierLeague);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var barcaMatch = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga);
        var plMatch = CreateMatch("fix_pl", "team_tot", "team_mci", CanonicalEntityIds.PremierLeague);
        var otherMatch = CreateMatch("fix_other", "team_roma", "team_lazio", "comp_seriea");

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { otherMatch, plMatch, barcaMatch } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Following);

        Assert.AreEqual(2, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_barca", vm.FilteredMatches[0].Id);
        Assert.AreEqual("fix_pl", vm.FilteredMatches[1].Id);
        Assert.AreEqual(0, vm.OtherMatches.Count, "Following tab must exclude other matches.");
        Assert.IsFalse(vm.IsOtherMatchesVisible);
    }

    [TestMethod]
    public async Task Test35_7DayCalendar_PopulatesSevenDaysWithTodaySelected()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        Assert.AreEqual(7, vm.CalendarDays.Count);
        Assert.AreEqual(DateTime.Today.AddDays(-1).Date, vm.CalendarDays[0].Date);
        Assert.AreEqual("Yesterday", vm.CalendarDays[0].RelativeLabel);
        Assert.IsFalse(vm.CalendarDays[0].IsToday);

        Assert.AreEqual(DateTime.Today.Date, vm.CalendarDays[1].Date);
        Assert.AreEqual("Today", vm.CalendarDays[1].RelativeLabel);
        Assert.IsTrue(vm.CalendarDays[1].IsToday);
        Assert.IsTrue(vm.CalendarDays[1].IsSelected);

        Assert.AreEqual(DateTime.Today.AddDays(1).Date, vm.CalendarDays[2].Date);
        Assert.AreEqual("Tomorrow", vm.CalendarDays[2].RelativeLabel);

        Assert.AreEqual(DateTime.Today.AddDays(5).Date, vm.CalendarDays[6].Date);
    }

    [TestMethod]
    public async Task Test36_7DayCalendar_DateSelection_FiltersMatches()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var today = DateTime.Today;
        var todayKickoff = new DateTimeOffset(today.Year, today.Month, today.Day, 15, 0, 0, TimeSpan.Zero);
        var tomorrowKickoff = todayKickoff.AddDays(1);

        var matchToday = CreateMatch("fix_today", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, kickoff: todayKickoff);
        var matchTomorrow = CreateMatch("fix_tomorrow", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague, kickoff: tomorrowKickoff);

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchToday, matchTomorrow } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        // Initially on Today
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_today", vm.FilteredMatches[0].Id);

        // Select Tomorrow
        vm.SelectCalendarDate(today.AddDays(1));
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreEqual("fix_tomorrow", vm.FilteredMatches[0].Id);
        Assert.IsTrue(vm.CalendarDays.First(d => d.Date == today.AddDays(1)).IsSelected);
        Assert.IsFalse(vm.CalendarDays.First(d => d.Date == today).IsSelected);
    }

    [TestMethod]
    public async Task Test36b_Strike24D_CalendarDays_7DayWindow_PreservesMatchCounts_AndTriggersSync()
    {
        var mockSync = new MockSyncService();
        using var vm = new MainViewModel(_prefService, syncService: mockSync);
        await vm.InitializeAsync();

        var today = DateTime.Today;
        // Verify 7 tabs: Yesterday through Today + 5
        Assert.AreEqual(7, vm.CalendarDays.Count);
        for (int i = 0; i < 7; i++)
        {
            Assert.AreEqual(today.AddDays(i - 1).Date, vm.CalendarDays[i].Date);
        }

        // Simulate initial payload with 1 match today
        var todayKickoff = new DateTimeOffset(today.Year, today.Month, today.Day, 15, 0, 0, TimeSpan.Zero);
        var matchToday = CreateMatch("fix_today", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, kickoff: todayKickoff);
        vm.RefreshMatches(new TrayDataDto { Matches = new List<TrayMatchDto> { matchToday } }, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        var todayItem = vm.CalendarDays.First(d => d.Date == today);
        Assert.AreEqual(1, todayItem.MatchCount);

        // Select Tomorrow (+1) -> SyncService target date must update and trigger sync
        var tomorrow = today.AddDays(1);
        vm.SelectCalendarDate(tomorrow);
        Assert.AreEqual(tomorrow.Date, mockSync.TargetDate.Date);
        Assert.IsTrue(mockSync.ImmediateSyncTriggerCount > 0);

        // Simulate returning 2 matches for tomorrow
        var tomorrowKickoff = todayKickoff.AddDays(1);
        var matchTomorrow1 = CreateMatch("fix_tom_1", "team_chelsea", "team_arsenal", CanonicalEntityIds.PremierLeague, kickoff: tomorrowKickoff);
        var matchTomorrow2 = CreateMatch("fix_tom_2", "team_liverpool", "team_everton", CanonicalEntityIds.PremierLeague, kickoff: tomorrowKickoff);
        vm.RefreshMatches(new TrayDataDto { Matches = new List<TrayMatchDto> { matchTomorrow1, matchTomorrow2 } }, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        // Tomorrow has 2 matches, and Today's match count (1) remains preserved rather than being zeroed out!
        var tomorrowItem = vm.CalendarDays.First(d => d.Date == tomorrow);
        Assert.AreEqual(2, tomorrowItem.MatchCount);
        Assert.AreEqual(1, todayItem.MatchCount);
    }

    [TestMethod]
    public async Task Test37_CollapsibleOtherMatches_ExpanderStateToggle()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Scenario 1: Followed matches exist in Live tab
        var barcaLive = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, state: "LIVE", elapsed: 20);
        var otherLive = CreateMatch("fix_other", "team_roma", "team_lazio", "comp_seriea", state: "LIVE", elapsed: 40);

        var dataWithFollowed = new TrayDataDto { Matches = new List<TrayMatchDto> { barcaLive, otherLive } };
        vm.RefreshMatches(dataWithFollowed, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Live);

        Assert.IsTrue(vm.HasFollowedMatches);
        Assert.IsTrue(vm.HasOtherMatches);
        Assert.IsTrue(vm.IsOtherMatchesVisible);
        // By default, if followed matches exist, other matches section is collapsed
        Assert.IsFalse(vm.IsOtherMatchesExpanded, "Other matches section must default to collapsed when followed matches exist.");

        // Scenario 2: No followed matches exist in Live tab
        var dataNoFollowed = new TrayDataDto { Matches = new List<TrayMatchDto> { otherLive } };
        vm.RefreshMatches(dataNoFollowed, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        vm.SetFilter(MatchFilterType.Upcoming);
        vm.SetFilter(MatchFilterType.Live); // re-trigger filter change

        Assert.IsFalse(vm.HasFollowedMatches);
        Assert.IsTrue(vm.HasOtherMatches);
        // By default, if no followed matches exist, other matches section is expanded so user sees matches
        Assert.IsTrue(vm.IsOtherMatchesExpanded, "Other matches section must default to expanded when no followed matches exist.");
    }

    [TestMethod]
    public async Task Test38_Strike21_AllTab_OtherMatchesAlwaysExpanded()
    {
        await _prefService.FollowTeamAsync(DefaultPreferences.Barcelona);

        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var barca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga);
        var other = CreateMatch("fix_other", "team_roma", "team_lazio", "comp_seriea");

        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { barca, other } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        vm.SetFilter(MatchFilterType.All);
        Assert.IsTrue(vm.HasFollowedMatches);
        Assert.IsTrue(vm.HasOtherMatches);
        Assert.IsTrue(vm.IsOtherMatchesExpanded, "All tab must always have other matches expanded so master schedule is fully visible.");
    }

    [TestMethod]
    public async Task Test39_Strike21_CalendarVisibility_OnlyOnAllAndFollowing()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        vm.SetFilter(MatchFilterType.All);
        Assert.IsTrue(vm.IsCalendarVisible, "Calendar must be visible on All tab.");

        vm.SetFilter(MatchFilterType.Following);
        Assert.IsTrue(vm.IsCalendarVisible, "Calendar must be visible on Following tab.");

        vm.SetFilter(MatchFilterType.Live);
        Assert.IsFalse(vm.IsCalendarVisible, "Calendar must be hidden on Live tab.");

        vm.SetFilter(MatchFilterType.Upcoming);
        Assert.IsFalse(vm.IsCalendarVisible, "Calendar must be hidden on Upcoming tab.");

        vm.SetFilter(MatchFilterType.Finished);
        Assert.IsFalse(vm.IsCalendarVisible, "Calendar must be hidden on Finished tab.");
    }

    [TestMethod]
    public void Test40_DaemonLifecycleManager_PortSelfHealing_DoesNotThrow()
    {
        var pids = FootballTray.Client.Services.Daemon.DaemonLifecycleManager.FindPidsListeningOnPort(65534);
        Assert.IsNotNull(pids);

        FootballTray.Client.Services.Daemon.DaemonLifecycleManager.FreePortIfOccupied(65534);
    }

    [TestMethod]
    public async Task Test41_Strike3_UniversalPinning_PreservesManualPinAcrossFilterChanges()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var match1 = CreateMatch("m_1", "team_1", "team_2", "comp_1", state: "LIVE", elapsed: 35);
        var match2 = CreateMatch("m_2", "team_3", "team_4", "comp_2", state: "PREMATCH");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { match1, match2 } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        // Pin match2 (which is prematch and unfollowed)
        vm.PinMatch("m_2");

        Assert.AreEqual("m_2", vm.ManualPinnedMatchId);
        Assert.AreEqual("m_2", vm.ActiveMatchId);
        Assert.IsFalse(vm.IsExplicitlyUnpinned);
        Assert.IsTrue(vm.ActiveTickerText.Contains("Pinned"), "ActiveTickerText must indicate pinned match.");

        var item = vm.FilteredMatches.FirstOrDefault(m => m.Id == "m_2");
        Assert.IsNotNull(item);
        Assert.IsTrue(item.IsActiveTaskbarMatch);
        Assert.AreEqual("\uE840", item.PinGlyph);

        // Switch to Live filter (m_2 is prematch, so it won't be in Live filtered list)
        vm.SetFilter(MatchFilterType.Live);
        Assert.AreEqual("m_2", vm.ManualPinnedMatchId, "ManualPinnedMatchId must be preserved across filter change.");
        Assert.AreEqual("m_2", vm.ActiveMatchId, "ActiveMatchId must remain the pinned match.");
        Assert.IsTrue(vm.ActiveTickerText.Contains("Pinned"));

        // Switch back to All filter
        vm.SetFilter(MatchFilterType.All);
        Assert.AreEqual("m_2", vm.ManualPinnedMatchId);
        Assert.AreEqual("m_2", vm.ActiveMatchId);
        item = vm.FilteredMatches.FirstOrDefault(m => m.Id == "m_2");
        Assert.IsNotNull(item);
        Assert.IsTrue(item.IsActiveTaskbarMatch);
    }

    [TestMethod]
    public async Task Test42_Strike3_UniversalPinning_ExplicitUnpin_PreservesUnpinnedAcrossFilterChanges()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var match1 = CreateMatch("m_1", "team_1", "team_2", "comp_1", state: "LIVE", elapsed: 40);
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { match1 } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        Assert.AreEqual("m_1", vm.ActiveMatchId, "ActiveMatchId should initially default to active match.");

        // Unpin
        vm.UnpinMatch("m_1");

        Assert.IsNull(vm.ManualPinnedMatchId);
        Assert.IsNull(vm.ActiveMatchId);
        Assert.IsTrue(vm.IsExplicitlyUnpinned);
        Assert.AreEqual("📌 Auto-Rotate Ticker (Carousel Mode)", vm.ActiveTickerText);

        // Switch filter - must not auto-repin m_1
        vm.SetFilter(MatchFilterType.Live);
        Assert.IsNull(vm.ActiveMatchId);
        Assert.IsTrue(vm.IsExplicitlyUnpinned);
        Assert.AreEqual("📌 Auto-Rotate Ticker (Carousel Mode)", vm.ActiveTickerText);
    }

    [TestMethod]
    public async Task Test43_Strike3_FollowStarToggles_UpdatesUserPreferencesAndMatchViewModel()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var match1 = CreateMatch("m_1", "team_roma", "team_lazio", "comp_seriea", homeName: "AS Roma", awayName: "SS Lazio", compName: "Serie A");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { match1 } };
        vm.RefreshMatches(data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        var item = vm.FilteredMatches.First(m => m.Id == "m_1");
        Assert.IsFalse(item.IsHomeTeamFollowed);
        Assert.IsFalse(item.IsAwayTeamFollowed);
        Assert.IsFalse(item.IsCompetitionFollowed);

        // Toggle Home Team follow
        await vm.ToggleTeamFollowAsync("team_roma", "AS Roma", "ROM");
        item = vm.FilteredMatches.First(m => m.Id == "m_1");
        Assert.IsTrue(item.IsHomeTeamFollowed);
        Assert.AreEqual("\uE735", item.HomeStarGlyph);

        // Toggle Competition follow
        await vm.ToggleCompetitionFollowAsync("comp_seriea", "Serie A", "SA");
        item = vm.FilteredMatches.First(m => m.Id == "m_1");
        Assert.IsTrue(item.IsCompetitionFollowed);
        Assert.AreEqual("\uE735", item.CompetitionStarGlyph);

        // Toggle Home Team unfollow
        await vm.ToggleTeamFollowAsync("team_roma", "AS Roma", "ROM");
        item = vm.FilteredMatches.First(m => m.Id == "m_1");
        Assert.IsFalse(item.IsHomeTeamFollowed);
        Assert.AreEqual("\uE734", item.HomeStarGlyph);
    }

    [TestMethod]
    public void Test44_Strike3_MatchCenter_HeaderIncidentsAndTimelineSplit_GracefulFallback()
    {
        var vm = new MatchCenterViewModel();

        // 1. Verify graceful handling of null / empty events
        var emptyDetail = new MatchDetailDto
        {
            Id = "m_empty",
            HomeTeam = new TeamSummaryDto { Id = "h1", Name = "Home FC" },
            AwayTeam = new TeamSummaryDto { Id = "a1", Name = "Away FC" },
            Events = null!
        };

        vm.Load(emptyDetail);
        Assert.AreEqual(0, vm.HomeIncidents.Count);
        Assert.AreEqual(0, vm.AwayIncidents.Count);
        Assert.AreEqual(0, vm.TimelineItems.Count);

        // 2. Populate with goals, red cards, substitutions, and neutral events
        var richDetail = new MatchDetailDto
        {
            Id = "m_rich",
            HomeTeam = new TeamSummaryDto { Id = "h1", Name = "Barcelona", ShortName = "BAR" },
            AwayTeam = new TeamSummaryDto { Id = "a1", Name = "Real Madrid", ShortName = "RMA" },
            Score = new ScoreDto { Home = 2, Away = 1 },
            Events = new List<TrayEventDto>
            {
                new() { Type = "PeriodStart", Minute = 1, Description = "First Half begins" },
                new() { Type = "Goal", Minute = 14, PlayerName = "Messi", TeamId = "h1" },
                new() { Type = "Goal", Minute = 42, PlayerName = "Benzema", TeamId = "a1" },
                new() { Type = "HalfTime", Minute = 45, Description = "Half Time" },
                new() { Type = "Goal", Minute = 89, PlayerName = "Messi", TeamId = "h1" },
                new() { Type = "RedCard", Minute = 90, ExtraMinute = 2, PlayerName = "Ramos", TeamId = "a1" }
            }
        };

        vm.Load(richDetail);

        // Header incidents
        Assert.AreEqual(1, vm.HomeIncidents.Count);
        Assert.AreEqual("Messi 14', 89'", vm.HomeIncidents[0].Text);
        Assert.AreEqual("⚽", vm.HomeIncidents[0].Icon);

        Assert.AreEqual(2, vm.AwayIncidents.Count);
        Assert.IsTrue(vm.AwayIncidents.Any(i => i.Text.Contains("Benzema 42'") && i.Icon == "⚽"));
        Assert.IsTrue(vm.AwayIncidents.Any(i => i.Text.Contains("Ramos 90+2'") && i.Icon == "🟥"));

        // Timeline items split (Reverse chronological: newest on top)
        Assert.AreEqual(6, vm.TimelineItems.Count);

        var firstHalfStart = vm.TimelineItems[5];
        Assert.IsTrue(firstHalfStart.IsNeutral);
        Assert.IsFalse(firstHalfStart.IsHomeTeam);
        Assert.IsFalse(firstHalfStart.IsAwayTeam);

        var messiGoal1 = vm.TimelineItems[4];
        Assert.IsTrue(messiGoal1.IsHomeTeam);
        Assert.IsFalse(messiGoal1.IsAwayTeam);
        Assert.AreEqual("Messi", messiGoal1.EventTitle);

        var benzemaGoal = vm.TimelineItems[3];
        Assert.IsTrue(benzemaGoal.IsAwayTeam);
        Assert.IsFalse(benzemaGoal.IsHomeTeam);
        Assert.AreEqual("Benzema", benzemaGoal.EventTitle);

        var halfTime = vm.TimelineItems[2];
        Assert.IsTrue(halfTime.IsNeutral);

        var ramosRed = vm.TimelineItems[0];
        Assert.IsTrue(ramosRed.IsAwayTeam);
        Assert.AreEqual("90+2'", ramosRed.MinuteDisplay);
    }

    [TestMethod]
    public void Test45_Strike31_MatchCenter_HandlesPrefixMismatchAndPlayerExtraction()
    {
        var vm = new MatchCenterViewModel();

        var detail = new MatchDetailDto
        {
            Id = "m_mismatch",
            HomeTeam = new TeamSummaryDto
            {
                Id = "ft_team_0e3a479b1836",
                LegacyId = "team_40",
                Name = "Liverpool FC",
                ShortName = "LIV"
            },
            AwayTeam = new TeamSummaryDto
            {
                Id = "ft_team_b82cf711d9a2",
                LegacyId = "team_50",
                Name = "Manchester City",
                ShortName = "MCI"
            },
            Score = new ScoreDto { Home = 2, Away = 1 },
            Events = new List<TrayEventDto>
            {
                // Raw numeric string "40" without "team_" prefix vs LegacyId "team_40"
                new()
                {
                    Type = "Goal",
                    Minute = 9,
                    TeamId = "40",
                    PlayerName = "M. Salah",
                    AssistPlayerName = "C. Gakpo",
                    Description = "[Goal] Normal Goal"
                },
                // "team_50" matching AwayTeam.LegacyId
                new()
                {
                    Type = "Goal",
                    Minute = 33,
                    TeamId = "team_50",
                    PlayerName = "E. Haaland",
                    Description = "[Goal] Normal Goal"
                },
                // Raw numeric string "40" matching HomeTeam
                new()
                {
                    Type = "Goal",
                    Minute = 67,
                    TeamId = "40",
                    PlayerName = "M. Salah",
                    Description = "[Goal] Penalty"
                },
                // PeriodEnd neutral
                new()
                {
                    Type = "PeriodEnd",
                    Minute = 90,
                    ExtraMinute = 4,
                    Description = "Full Time"
                }
            }
        };

        vm.Load(detail);

        // Header Incidents
        Assert.AreEqual(1, vm.HomeIncidents.Count, "HomeIncidents must find the 2 Salah goals grouped");
        Assert.AreEqual("M. Salah 9', 67'", vm.HomeIncidents[0].Text);
        Assert.AreEqual("⚽", vm.HomeIncidents[0].Icon);

        Assert.AreEqual(1, vm.AwayIncidents.Count, "AwayIncidents must find Haaland goal");
        Assert.AreEqual("E. Haaland 33'", vm.AwayIncidents[0].Text);

        // Timeline items (Reverse chronological: newest on top)
        Assert.AreEqual(4, vm.TimelineItems.Count);

        var ft = vm.TimelineItems[0];
        Assert.IsTrue(ft.IsNeutral);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, ft.NeutralVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Collapsed, ft.NonNeutralVisibility);

        var haalandGoal = vm.TimelineItems[2];
        Assert.IsTrue(haalandGoal.IsAwayTeam, "Must identify 'team_50' as AwayTeam");
        Assert.IsFalse(haalandGoal.IsHomeTeam);
        Assert.AreEqual("E. Haaland", haalandGoal.EventTitle);
        Assert.AreEqual("Normal Goal", haalandGoal.EventSubtitle, "EventSubtitle must strip [Goal] prefix");

        var salahGoal1 = vm.TimelineItems[3];
        Assert.IsTrue(salahGoal1.IsHomeTeam, "Must identify '40' as HomeTeam despite ft_team_ canonical ID");
        Assert.IsFalse(salahGoal1.IsAwayTeam);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, salahGoal1.HomeVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Collapsed, salahGoal1.AwayVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, salahGoal1.NonNeutralVisibility);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Collapsed, salahGoal1.NeutralVisibility);
        Assert.AreEqual("M. Salah", salahGoal1.EventTitle, "EventTitle must be player name");
        Assert.AreEqual("Assist: C. Gakpo", salahGoal1.EventSubtitle, "EventSubtitle must show assist");
    }

    [TestMethod]
    public void Test46_Strike4_CalendarDateChange_UpdatesSyncServiceTargetDateAndTriggersImmediateSync()
    {
        var mockSync = new MockSyncService();
        var vm = new MainViewModel(_prefService, syncService: mockSync);

        var targetDate = DateTime.Today.AddDays(2);

        Assert.AreEqual(0, mockSync.ImmediateSyncTriggerCount);
        Assert.AreEqual(DateTime.Today.Date, mockSync.TargetDate.Date);

        // Act: SelectCalendarDate
        vm.SelectCalendarDate(targetDate);

        // Assert: sync service TargetDate was updated and TriggerImmediateSync was called
        Assert.AreEqual(targetDate.Date, mockSync.TargetDate.Date);
        Assert.AreEqual(1, mockSync.ImmediateSyncTriggerCount);
        Assert.AreEqual(targetDate.Date, vm.SelectedCalendarDate.Date);

        // Act: Direct SelectedCalendarDate property setter
        var previousDay = DateTime.Today.AddDays(-1);
        vm.SelectedCalendarDate = previousDay;

        // Assert: sync service updated again and triggered sync
        Assert.AreEqual(previousDay.Date, mockSync.TargetDate.Date);
        Assert.AreEqual(2, mockSync.ImmediateSyncTriggerCount);
    }

    // ============================================================
    // Helpers & Test Doubles
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
        string? awayShortName = null,
        List<TrayEventDto>? events = null)
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
                ShortName = homeShortName ?? (homeName.Length <= 3 ? homeName.ToUpperInvariant() : homeName[..3].ToUpperInvariant())
            },
            AwayTeam = new TeamSummaryDto
            {
                Id = awayId,
                Name = awayName,
                ShortName = awayShortName ?? (awayName.Length <= 3 ? awayName.ToUpperInvariant() : awayName[..3].ToUpperInvariant())
            },
            Competition = new CompetitionSummaryDto { Id = compId, Name = compName, Code = compName },
            Score = new ScoreDto { Home = homeScore, Away = awayScore, Display = $"{homeScore} - {awayScore}" },
            RecentEvents = events ?? new List<TrayEventDto>()
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

    private sealed class FailingPreferencesService : IPreferencesService
    {
        private readonly IPreferencesService _inner;

        public event EventHandler<UserPreferences>? PreferencesChanged
        {
            add => _inner.PreferencesChanged += value;
            remove => _inner.PreferencesChanged -= value;
        }

        public FailingPreferencesService(IPreferencesService inner)
        {
            _inner = inner;
        }

        public UserPreferences GetPreferences() => _inner.GetPreferences();

        public Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);

        public Task SaveAsync(CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task FollowTeamAsync(FollowedTeam team, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task UnfollowTeamAsync(string teamId, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task FollowCompetitionAsync(FollowedCompetition competition, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task UnfollowCompetitionAsync(string competitionId, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task SetNotificationsGloballyEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task SetNotificationTypeEnabledAsync(NotificationType type, bool enabled, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task SetAudioEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task SetAudioVolumeAsync(double volume, CancellationToken cancellationToken = default) => throw new IOException("Disk error");

        public Task SetPreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken = default) => throw new IOException("Disk error");
    }

    private sealed class MockSyncService : ISyncService
    {
#pragma warning disable CS0067
        public event EventHandler<TrayUpdateEventArgs>? TrayUpdated;
#pragma warning restore CS0067
        public TrayDataDto? CurrentTrayData { get; set; }
        public DataFreshnessState FreshnessState { get; set; } = DataFreshnessState.Fresh;
        public TickerConnectionState CurrentState { get; set; } = TickerConnectionState.OnlineFresh;
        public DateTimeOffset? LastSuccessfulServerResponseTime { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastRefreshAttempt { get; set; } = DateTimeOffset.UtcNow;
        public string? LastKnownETag { get; set; }
        public DateTimeOffset? LastValidatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTime TargetDate { get; set; } = DateTime.Today;
        public int ImmediateSyncTriggerCount { get; private set; }

        public void TriggerImmediateSync()
        {
            ImmediateSyncTriggerCount++;
        }

        public Task<DataFreshnessState> PollOnceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(FreshnessState);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RefreshTickerOnPreferenceChange() { }

        public void Dispose() { }
    }
}
