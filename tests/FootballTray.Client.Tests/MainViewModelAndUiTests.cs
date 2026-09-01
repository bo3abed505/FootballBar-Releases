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

        // Standard teams
        Assert.IsTrue(vm.AvailableTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona && t.IsFollowed));
        Assert.IsTrue(vm.AvailableTeams.Any(t => t.Id == CanonicalEntityIds.AlAhly && t.IsFollowed));

        // Standard 6 competitions
        Assert.AreEqual(6, vm.AvailableCompetitions.Count);
        Assert.IsTrue(vm.AvailableCompetitions.All(c => c.IsFollowed));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.LaLiga));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.EgyptianPremierLeague));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.ChampionsLeague));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.AfricaCupOfNations));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.WorldCup));

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

        // Unfollow Barcelona then follow again
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        var barca = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
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

        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.AlAhly);
        var ahly = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.AlAhly);

        Assert.IsFalse(ahly.IsFollowed);
        var persisted = _prefService.GetPreferences();
        Assert.IsFalse(persisted.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.AlAhly));
    }

    [TestMethod]
    public async Task Test05_FollowCompetition_UpdatesUi()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        // Unfollow PL then re-follow
        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.PremierLeague);
        var pl = vm.AvailableCompetitions.First(c => c.Id == CanonicalEntityIds.PremierLeague);
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

        await vm.ToggleCompetitionFollowAsync(CanonicalEntityIds.LaLiga);
        var laliga = vm.AvailableCompetitions.First(c => c.Id == CanonicalEntityIds.LaLiga);

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
            await vm1.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona); // unfollow
            await vm1.ToggleNotificationCategoryAsync(NotificationType.Goal, false);
        }

        // 2. Simulate fresh app start with VM 2 pointing to same storage
        var prefService2 = new PreferencesService(_tempDir);
        using (var vm2 = new MainViewModel(prefService2))
        {
            await vm2.InitializeAsync();
            var barca = vm2.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
            var goal = vm2.NotificationCategories.First(c => c.Type == NotificationType.Goal);

            Assert.IsFalse(barca.IsFollowed);
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

        // Must recover safely to default values
        Assert.IsTrue(vm.IsGlobalNotificationsEnabled);
        Assert.IsTrue(vm.AvailableTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona && t.IsFollowed));
        Assert.IsTrue(vm.AvailableCompetitions.Any(c => c.Id == CanonicalEntityIds.PremierLeague && c.IsFollowed));
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

        // Mutate preferences while offline
        await vm.ToggleTeamFollowAsync(CanonicalEntityIds.Barcelona);
        var barca = vm.AvailableTeams.First(t => t.Id == CanonicalEntityIds.Barcelona);
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
        Assert.AreEqual("⚽ BAR 2–1 RMA 65'", formatted);
    }

    [TestMethod]
    public async Task Test30_StaleOfflineMarker_RemainsCorrect()
    {
        using var vm = new MainViewModel(_prefService);
        await vm.InitializeAsync();

        var matchBarca = CreateMatch("fix_barca", CanonicalEntityIds.Barcelona, "team_rma", CanonicalEntityIds.LaLiga, homeName: "FC Barcelona", awayName: "Real Madrid", state: "LIVE", elapsed: 65, homeScore: 2, awayScore: 1, homeShortName: "BAR", awayShortName: "RMA");
        var data = new TrayDataDto { Matches = new List<TrayMatchDto> { matchBarca } };

        var formattedStale = TrayTickerFormatter.FormatTicker(data, DataFreshnessState.Stale, _prefService.GetPreferences());
        Assert.AreEqual("⚽ BAR 2–1 RMA 65' · offline", formattedStale);
    }

    [TestMethod]
    public async Task Test31_Complete_ManualInteraction_Sequence_A_Through_G()
    {
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

        public Task SetPreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken = default) => throw new IOException("Disk error");
    }
}
