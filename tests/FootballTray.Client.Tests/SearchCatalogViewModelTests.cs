using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class SearchCatalogViewModelTests
{
    private string _tempDir = string.Empty;
    private PreferencesService _prefService = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SearchCatalogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _prefService = new PreferencesService(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private static HttpResponseMessage CreateTeamsResponse(string jsonPayload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var start = DateTimeOffset.UtcNow;
        while (!condition() && (DateTimeOffset.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
        }
        Assert.IsTrue(condition(), $"Condition was not met within {timeoutMs}ms.");
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_Debounce_MultipleKeystrokes_InvokesApiOnce()
    {
        int apiCallCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri != null && req.RequestUri.PathAndQuery.Contains("/api/v2/catalog/teams"))
                {
                    Interlocked.Increment(ref apiCallCount);
                    return CreateTeamsResponse("""
                    {
                        "schemaVersion": "2.0",
                        "requestId": "req_1",
                        "version": 2,
                        "generatedAt": "2026-03-01T12:00:00Z",
                        "serverTime": "2026-03-01T12:00:00Z",
                        "stale": false,
                        "dataAgeSeconds": 0,
                        "data": [
                            {
                                "id": "ft_team_arsenal",
                                "name": "Arsenal",
                                "shortName": "ARS",
                                "country": "England",
                                "logo": "https://media.api-sports.io/football/teams/42.png",
                                "logoUrl": "https://media.api-sports.io/football/teams/42.png"
                            }
                        ]
                    }
                    """);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);

        // Rapidly change search query (simulating keystrokes faster than 300ms debounce)
        vm.SearchQuery = "a";
        vm.SearchQuery = "ar";
        vm.SearchQuery = "ars";
        vm.SearchQuery = "arse";
        vm.SearchQuery = "arsenal";

        // Wait for debounce period (300ms) plus execution
        await WaitForConditionAsync(() => Volatile.Read(ref apiCallCount) == 1, timeoutMs: 5000);

        Assert.AreEqual(1, apiCallCount, "Debounce mechanism should coalesce rapid typing into exactly 1 API call.");
        Assert.AreEqual(1, vm.TeamSearchResults.Count);
        Assert.AreEqual("Arsenal", vm.TeamSearchResults[0].Name);
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_SearchTeams_PopulatesSearchResults()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri != null && req.RequestUri.PathAndQuery.Contains("/api/v2/catalog/teams"))
                {
                    return CreateTeamsResponse("""
                    {
                        "schemaVersion": "2.0",
                        "requestId": "req_2",
                        "version": 2,
                        "generatedAt": "2026-03-01T12:00:00Z",
                        "serverTime": "2026-03-01T12:00:00Z",
                        "stale": false,
                        "dataAgeSeconds": 0,
                        "data": [
                            {
                                "id": "ft_team_barca",
                                "name": "FC Barcelona",
                                "shortName": "BAR",
                                "country": "Spain",
                                "logo": "https://media.api-sports.io/football/teams/529.png"
                            },
                            {
                                "id": "ft_team_madrid",
                                "name": "Real Madrid",
                                "shortName": "RMA",
                                "country": "Spain",
                                "logo": "https://media.api-sports.io/football/teams/541.png"
                            }
                        ]
                    }
                    """);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        await vm.ExecuteSearchAsync("Spain");

        Assert.AreEqual(2, vm.TeamSearchResults.Count);
        Assert.IsTrue(vm.HasTeamSearchResults);
        Assert.AreEqual("FC Barcelona", vm.TeamSearchResults[0].Name);
        Assert.AreEqual("Real Madrid", vm.TeamSearchResults[1].Name);
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_SearchTeams_EmptyQuery_ClearsResultsWithoutApiCall()
    {
        int apiCallCount = 0;
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                Interlocked.Increment(ref apiCallCount);
                return CreateTeamsResponse("""{"schemaVersion":"2.0","version":2,"data":[]}""");
            }
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        vm.SearchQuery = "   ";
        await Task.Delay(350);

        Assert.AreEqual(0, apiCallCount, "Empty query must not invoke the backend API.");
        Assert.AreEqual(0, vm.TeamSearchResults.Count);
        Assert.IsFalse(vm.IsSearching);
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_SearchCompetitions_PopulatesSearchResults()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri != null && req.RequestUri.PathAndQuery.Contains("/api/v2/catalog/competitions"))
                {
                    return CreateTeamsResponse("""
                    {
                        "schemaVersion": "2.0",
                        "requestId": "req_3",
                        "version": 2,
                        "generatedAt": "2026-03-01T12:00:00Z",
                        "serverTime": "2026-03-01T12:00:00Z",
                        "stale": false,
                        "dataAgeSeconds": 0,
                        "data": [
                            {
                                "id": "ft_comp_pl",
                                "name": "Premier League",
                                "code": "PL",
                                "country": "England",
                                "logo": "https://media.api-sports.io/football/leagues/39.png"
                            }
                        ]
                    }
                    """);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        vm.SelectedScope = CatalogSearchScope.Competitions;
        await vm.ExecuteSearchAsync("Premier");

        Assert.AreEqual(1, vm.CompetitionSearchResults.Count);
        Assert.IsTrue(vm.HasCompetitionSearchResults);
        Assert.AreEqual("Premier League", vm.CompetitionSearchResults[0].Name);
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_FollowTeam_UpdatesCollectionAndPreferences()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => CreateTeamsResponse("""
            {
                "schemaVersion": "2.0",
                "version": 2,
                "data": [
                    {
                        "id": "ft_team_chelsea",
                        "name": "Chelsea",
                        "shortName": "CHE",
                        "country": "England",
                        "logo": "https://media.api-sports.io/football/teams/49.png"
                    }
                ]
            }
            """)
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        await vm.InitializeAsync();
        await vm.ExecuteSearchAsync("Chelsea");

        Assert.AreEqual(1, vm.TeamSearchResults.Count);
        var chelsea = vm.TeamSearchResults[0];
        Assert.IsFalse(chelsea.IsFollowed);

        // Follow Chelsea
        await vm.FollowTeamAsync(chelsea);

        Assert.IsTrue(chelsea.IsFollowed);
        Assert.IsTrue(vm.FollowedTeams.Any(t => t.Id == "ft_team_chelsea"));

        // Verify persisted to disk via preferences service
        var prefs = _prefService.GetPreferences();
        Assert.IsTrue(prefs.FollowedTeams.Any(t => t.Id == "ft_team_chelsea"));
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_UnfollowTeam_RemovesFromCollectionAndPreferences()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => CreateTeamsResponse("""
            {
                "schemaVersion": "2.0",
                "version": 2,
                "data": [
                    {
                        "id": "ft_team_barca",
                        "name": "FC Barcelona",
                        "shortName": "BAR",
                        "country": "Spain"
                    }
                ]
            }
            """)
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        await vm.InitializeAsync();

        // Default preferences contain Barcelona
        Assert.IsTrue(vm.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));

        // Unfollow Barcelona
        await vm.UnfollowTeamAsync(CanonicalEntityIds.Barcelona);

        Assert.IsFalse(vm.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
        var prefs = _prefService.GetPreferences();
        Assert.IsFalse(prefs.FollowedTeams.Any(t => t.Id == CanonicalEntityIds.Barcelona));
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_FollowAndUnfollowCompetition_UpdatesState()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => CreateTeamsResponse("""
            {
                "schemaVersion": "2.0",
                "version": 2,
                "data": [
                    {
                        "id": "ft_comp_bundesliga",
                        "name": "Bundesliga",
                        "code": "BL1",
                        "country": "Germany"
                    }
                ]
            }
            """)
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        await vm.InitializeAsync();
        vm.SelectedScope = CatalogSearchScope.Competitions;
        await vm.ExecuteSearchAsync("Bundesliga");

        var bundesliga = vm.CompetitionSearchResults[0];
        Assert.IsFalse(bundesliga.IsFollowed);

        // Follow
        await vm.FollowCompetitionAsync(bundesliga);
        Assert.IsTrue(bundesliga.IsFollowed);
        Assert.IsTrue(vm.FollowedCompetitions.Any(c => c.Id == "ft_comp_bundesliga"));
        Assert.IsTrue(_prefService.GetPreferences().FollowedCompetitions.Any(c => c.Id == "ft_comp_bundesliga"));

        // Unfollow
        await vm.UnfollowCompetitionAsync("ft_comp_bundesliga");
        Assert.IsFalse(bundesliga.IsFollowed);
        Assert.IsFalse(vm.FollowedCompetitions.Any(c => c.Id == "ft_comp_bundesliga"));
        Assert.IsFalse(_prefService.GetPreferences().FollowedCompetitions.Any(c => c.Id == "ft_comp_bundesliga"));
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_SearchError_SetsStatusMessageGracefully()
    {
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Database connection failed", Encoding.UTF8, "text/plain")
            }
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        await vm.ExecuteSearchAsync("ErrorQuery");

        Assert.IsFalse(vm.IsSearching);
        Assert.IsTrue(vm.HasSearchStatusMessage);
        Assert.IsTrue(vm.SearchStatusMessage!.Contains("500") || vm.SearchStatusMessage.Contains("failed"));
        Assert.AreEqual(0, vm.TeamSearchResults.Count);
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_InitializeAsync_HydratesFollowedLists()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        await vm.InitializeAsync();

        // Default preferences contain 2 teams (Barcelona, Al Ahly) and 6 competitions
        Assert.AreEqual(2, vm.FollowedTeams.Count);
        Assert.AreEqual(6, vm.FollowedCompetitions.Count);
        Assert.IsTrue(vm.HasFollowedTeams);
        Assert.IsTrue(vm.HasFollowedCompetitions);
    }

    [TestMethod]
    public async Task SearchCatalogViewModel_ScopeSwitch_TriggersSearchForNewScope()
    {
        var requestedUrls = new List<string>();
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                if (req.RequestUri != null)
                {
                    lock (requestedUrls)
                    {
                        requestedUrls.Add(req.RequestUri.PathAndQuery);
                    }
                }
                return CreateTeamsResponse("""{"schemaVersion":"2.0","version":2,"data":[]}""");
            }
        };

        var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        var apiClient = new FootballApiClient(httpClient);

        using var vm = new SearchCatalogViewModel(apiClient, _prefService);
        vm.SearchQuery = "Madrid";
        await WaitForConditionAsync(() => { lock (requestedUrls) { return requestedUrls.Any(u => u.Contains("/api/v2/catalog/teams")); } });

        lock (requestedUrls) { requestedUrls.Clear(); }
        vm.SelectedScope = CatalogSearchScope.Competitions;
        await WaitForConditionAsync(() => { lock (requestedUrls) { return requestedUrls.Any(u => u.Contains("/api/v2/catalog/competitions")); } });
    }
}
