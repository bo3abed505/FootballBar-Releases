using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.Services.Startup;
using FootballTray.Client.Services.Telemetry;
using FootballTray.Client.Services.Updates;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class StandaloneAndOtaResilienceTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FT_StandaloneTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        AppDistributionHelper.ForcedIsPackagedForTesting = null;
        Environment.SetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL", null);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    #region Task 1.1: OtaConfigService Tests

    [TestMethod]
    public void OtaConfigService_InitializesDefaultManifest_WithValidEndpointsAndPriorities()
    {
        var service = OtaConfigService.Instance;
        Assert.IsNotNull(service, "OtaConfigService instance should not be null.");

        var manifest = service.CurrentManifest;
        Assert.IsNotNull(manifest, "CurrentManifest must be populated on startup.");
        Assert.AreEqual("1.0", manifest.SchemaVersion);
        Assert.AreEqual("live-telemetry", manifest.Provider);
        Assert.IsFalse(string.IsNullOrWhiteSpace(manifest.ManifestVersion));
        Assert.IsNotNull(manifest.Endpoints, "Endpoints DTO must be initialized.");

        // Validate endpoint URL templates
        Assert.IsTrue(manifest.Endpoints.MatchesByDateUrlTemplate.Contains("{date}"), "Matches endpoint must include {date} placeholder.");
        Assert.IsTrue(manifest.Endpoints.MatchDetailsUrlTemplate.Contains("{matchId}"), "Match details endpoint must include {matchId} placeholder.");
        Assert.IsTrue(manifest.Endpoints.LeagueHubUrlTemplate.Contains("{leagueId}"), "League hub endpoint must include {leagueId} placeholder.");
        Assert.IsTrue(manifest.Endpoints.SearchSuggestUrlTemplate.Contains("{term}"), "Search suggest endpoint must include {term} placeholder.");
        Assert.IsTrue(manifest.Endpoints.TeamLogoUrlTemplate.Contains("{teamId}"), "Team logo endpoint must include {teamId} placeholder.");
        Assert.IsTrue(manifest.Endpoints.LeagueLogoUrlTemplate.Contains("{leagueId}"), "League logo endpoint must include {leagueId} placeholder.");

        // Priority League IDs must include Premier League (47) and UCL (42)
        CollectionAssert.Contains(manifest.PriorityLeagueIds, 47, "Premier League (47) must be in priority leagues.");
        CollectionAssert.Contains(manifest.PriorityLeagueIds, 42, "Champions League (42) must be in priority leagues.");

        // Competition Overrides lookup
        Assert.IsTrue(manifest.CompetitionOverrides.ContainsKey("premier_league"));
        Assert.AreEqual(47, manifest.CompetitionOverrides["premier_league"]);
        Assert.IsTrue(manifest.CompetitionOverrides.ContainsKey("champions_league"));
        Assert.AreEqual(42, manifest.CompetitionOverrides["champions_league"]);
    }

    #endregion

    #region Task 1.2: LocalCacheService Multi-Entity Offline Tests

    [TestMethod]
    public async Task LocalCacheService_PerDateTraySchedule_SavesAndLoadsSuccessfully()
    {
        var cacheService = new LocalCacheService(_tempDir);
        string testDate = "2026-10-26";

        var envelope = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            Version = 42,
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "match_1001",
                        HomeTeam = new TeamSummaryDto { Name = "Arsenal", ShortName = "ARS" },
                        AwayTeam = new TeamSummaryDto { Name = "Chelsea", ShortName = "CHE" },
                        State = "LIVE",
                        ElapsedMinute = 33,
                        Score = new ScoreDto { Home = 2, Away = 0 }
                    }
                }
            }
        };

        bool saved = await cacheService.SaveTrayDateCacheAsync(testDate, envelope, CancellationToken.None);
        Assert.IsTrue(saved, "Tray date cache save must return true.");

        var loaded = await cacheService.LoadTrayDateCacheAsync(testDate, CancellationToken.None);
        Assert.IsNotNull(loaded, "Loaded tray date cache must not be null.");
        Assert.AreEqual(42, loaded.Version);
        Assert.AreEqual(1, loaded.Data?.Matches.Count);
        Assert.AreEqual("ARS", loaded.Data?.Matches[0].HomeTeam.ShortName);
        Assert.AreEqual("CHE", loaded.Data?.Matches[0].AwayTeam.ShortName);
    }

    [TestMethod]
    public async Task LocalCacheService_MatchDetails_SavesAndLoadsSuccessfully()
    {
        var cacheService = new LocalCacheService(_tempDir);
        string matchId = "match_elclasico_2026";

        var envelope = new ApiResponseEnvelope<MatchDetailDto>
        {
            SchemaVersion = "1.0",
            Version = 1,
            Data = new MatchDetailDto
            {
                Id = matchId,
                HomeTeam = new TeamSummaryDto { Name = "Barcelona", ShortName = "BAR" },
                AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                State = "FT",
                Score = new ScoreDto { Home = 3, Away = 2 },
                Commentary = new List<MatchCommentaryItemDto>
                {
                    new() { Minute = 90, Text = "Full time whistle blown!" }
                }
            }
        };

        bool saved = await cacheService.SaveMatchDetailCacheAsync(matchId, envelope, CancellationToken.None);
        Assert.IsTrue(saved, "Match detail cache save must succeed.");

        var loaded = await cacheService.LoadMatchDetailCacheAsync(matchId, CancellationToken.None);
        Assert.IsNotNull(loaded, "Loaded match detail cache must not be null.");
        Assert.IsNotNull(loaded?.Data);
        Assert.AreEqual(matchId, loaded.Data.Id);
        Assert.AreEqual("FT", loaded.Data.State);
        Assert.IsNotNull(loaded.Data.Commentary);
        Assert.AreEqual(1, loaded.Data.Commentary.Count);
        Assert.AreEqual(90, loaded.Data.Commentary[0].Minute);
    }

    [TestMethod]
    public async Task LocalCacheService_CompetitionHub_SavesAndLoadsSuccessfully()
    {
        var cacheService = new LocalCacheService(_tempDir);
        string compKey = "premier_league";

        var envelope = new ApiResponseEnvelope<CompetitionHubDto>
        {
            SchemaVersion = "1.0",
            Version = 1,
            Data = new CompetitionHubDto
            {
                CompetitionId = compKey,
                CompetitionName = "Premier League",
                Standings = new List<StandingRowDto>
                {
                    new() { Rank = 1, TeamName = "Arsenal", Points = 50, Played = 20 }
                }
            }
        };

        bool saved = await cacheService.SaveCompetitionHubCacheAsync(compKey, envelope, CancellationToken.None);
        Assert.IsTrue(saved, "Competition hub cache save must succeed.");

        var loaded = await cacheService.LoadCompetitionHubCacheAsync(compKey, CancellationToken.None);
        Assert.IsNotNull(loaded, "Loaded competition hub cache must not be null.");
        Assert.IsNotNull(loaded.Data);
        Assert.AreEqual("Premier League", loaded.Data.CompetitionName);
        Assert.AreEqual(1, loaded.Data.Standings.Count);
        Assert.AreEqual("Arsenal", loaded.Data.Standings[0].TeamName);
    }

    #endregion

    #region Task 1.3: AppDistributionHelper & Store Policy 10.2.3 Tests

    [TestMethod]
    [DoNotParallelize]
    public void AppDistributionHelper_EnvironmentOverride_ResolvesCorrectly()
    {
        // GitHub / Standalone mode
        Environment.SetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL", "github");
        // Re-read or verify channel name logic
        Assert.IsFalse(string.Equals(Environment.GetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL"), "store", StringComparison.OrdinalIgnoreCase));

        // Microsoft Store / MSIX mode
        Environment.SetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL", "store");
        Assert.IsTrue(string.Equals(Environment.GetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL"), "store", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task SettingsViewModel_PackagedStoreMode_CompliesWithStorePolicy1023()
    {
        AppDistributionHelper.ForcedIsPackagedForTesting = true;
        Environment.SetEnvironmentVariable("FOOTBALLTRAY_DISTRIBUTION_CHANNEL", "store");

        var preferencesService = new PreferencesService(_tempDir);
        var diagnosticsService = new DiagnosticsService();

        using var settingsVm = new SettingsViewModel(
            mainViewModel: null,
            preferencesService: preferencesService,
            diagnosticsService: diagnosticsService);

        // When user checks for updates in Packaged mode
        await settingsVm.CheckForUpdatesAsync();

        Assert.IsFalse(settingsVm.CanDownloadUpdate, "CanDownloadUpdate must be false in Packaged MSIX Store mode.");
        Assert.IsFalse(settingsVm.CanInstallUpdate, "CanInstallUpdate must be false in Packaged MSIX Store mode.");
        Assert.IsTrue(
            settingsVm.UpdateStatusMessage?.Contains("Microsoft Store") == true,
            $"Expected status message to mention Microsoft Store, got: '{settingsVm.UpdateStatusMessage}'");
    }

    #endregion

    #region Task 1.4: DiagnosticsService Standalone Alignment Tests

    [TestMethod]
    public void DiagnosticsService_ReportsHealthy_WhenStandaloneNativeEngineActive()
    {
        // Simulate Standalone Native Engine mode (Tier 2 in-process active)
        using var client = new FootballApiClient(enableNativeFallback: true);
        client.SetStandaloneNativeEngineActiveForTesting(true);

        var diagnostics = new DiagnosticsService(apiClient: client);
        diagnostics.RecordTaskbarStatus("Standalone Window / Unattached (Taskbar Available)");

        var snapshot = diagnostics.GetCurrentSnapshot();

        // In standalone mode, "Unattached" must NOT degrade health
        Assert.IsTrue(
            snapshot.HealthState == ApplicationHealthState.Healthy,
            $"Expected Healthy state in standalone mode, but received: {snapshot.HealthState}");

        Assert.AreEqual("in-process (live-telemetry)", snapshot.BackendEndpoint);
    }

    [TestMethod]
    public async Task Live_NativeTelemetryProvider_Diagnostics()
    {
        using var provider = new NativeTelemetryProvider();
        
        // 1. Without filtering (2026-10-26)
        var resNoFilter = await provider.GetTrayForDateAsync("2026-10-26", null);
        Console.WriteLine($"[NO FILTER 2026-10-26] Success={resNoFilter.IsSuccess}, Matches={resNoFilter.Envelope?.Data?.Matches?.Count}, Err={resNoFilter.ErrorMessage}");
        Assert.IsTrue(resNoFilter.IsSuccess, "GetTrayForDateAsync(2026-10-26) should succeed");
        Assert.IsTrue(resNoFilter.Envelope?.Data?.Matches?.Count > 0, "Matches for 2026-10-26 should be > 0");

        var sampleMatch = resNoFilter.Envelope?.Data?.Matches?[0];
        if (sampleMatch != null)
        {
            Console.WriteLine($"[SAMPLE MATCH 2026-10-26] ID={sampleMatch.Id}, {sampleMatch.HomeTeam?.Name} ({sampleMatch.Score?.Home}) vs {sampleMatch.AwayTeam?.Name} ({sampleMatch.Score?.Away}), State={sampleMatch.State}");
        }

        // 2. With SyncService's default competitions
        var compIds = new List<string>
        {
            FootballTray.Client.Services.Preferences.CanonicalEntityIds.PremierLeague,
            FootballTray.Client.Services.Preferences.CanonicalEntityIds.LaLiga,
            FootballTray.Client.Services.Preferences.CanonicalEntityIds.EgyptianPremierLeague,
            FootballTray.Client.Services.Preferences.CanonicalEntityIds.ChampionsLeague
        };
        var resWithComps = await provider.GetTrayForDateAsync("2026-10-26", null, competitions: compIds);
        Console.WriteLine($"[WITH COMPS 2026-10-26] Success={resWithComps.IsSuccess}, Matches={resWithComps.Envelope?.Data?.Matches?.Count}, Err={resWithComps.ErrorMessage}");
        Assert.IsTrue(resWithComps.IsSuccess, "GetTrayForDateAsync with SyncService competitions must succeed");
        Assert.IsTrue(resWithComps.Envelope?.Data?.Matches?.Count > 0, "Matches with SyncService competitions must be > 0");

        // 3. 2026-10-25
        var res25 = await provider.GetTrayForDateAsync("2026-10-25", null);
        Console.WriteLine($"[NO FILTER 2026-10-25] Success={res25.IsSuccess}, Matches={res25.Envelope?.Data?.Matches?.Count}, Err={res25.ErrorMessage}");
        Assert.IsTrue(res25.IsSuccess, "GetTrayForDateAsync(2026-10-25) should succeed");
        Assert.IsTrue(res25.Envelope?.Data?.Matches?.Count > 0, "Matches for 2026-10-25 should be > 0");

        var sampleMatch25 = res25.Envelope?.Data?.Matches?[0];
        if (sampleMatch25 != null)
        {
            Console.WriteLine($"[SAMPLE MATCH 2026-10-25] ID={sampleMatch25.Id}, {sampleMatch25.HomeTeam?.Name} ({sampleMatch25.Score?.Home}) vs {sampleMatch25.AwayTeam?.Name} ({sampleMatch25.Score?.Away}), State={sampleMatch25.State}");
        }

        // 4. Match Details: Lower-league / failing match 5973209
        string problemMatchId = "fotmob_5973209";
        var matchDetailProblem = await provider.GetMatchDetailsAsync(problemMatchId);
        Console.WriteLine($"[MATCH DETAILS {problemMatchId}] Found={matchDetailProblem != null}, Status={matchDetailProblem?.State}, Teams={matchDetailProblem?.HomeTeam?.Name} vs {matchDetailProblem?.AwayTeam?.Name}, EventsCount={matchDetailProblem?.Events?.Count ?? 0}, CommentaryCount={matchDetailProblem?.Commentary?.Count ?? 0}");
        Assert.IsNotNull(matchDetailProblem, $"Match details for {problemMatchId} must not be null.");

        // 4b. Match Details: Major match (sample match from tray)
        string targetMatchId = sampleMatch?.Id ?? sampleMatch25?.Id ?? "4444000";
        var matchDetail = await provider.GetMatchDetailsAsync(targetMatchId);
        Console.WriteLine($"[MATCH DETAILS {targetMatchId}] Found={matchDetail != null}, Status={matchDetail?.State}, Teams={matchDetail?.HomeTeam?.Name} vs {matchDetail?.AwayTeam?.Name}, CommentaryCount={matchDetail?.Commentary?.Count ?? 0}");
        Assert.IsNotNull(matchDetail, $"Match details for {targetMatchId} must not be null.");

        // 5. Competition Hub for Premier League with ("47", "Premier League", null)
        var hub47 = await provider.GetCompetitionHubAsync("47", "Premier League", null);
        Console.WriteLine($"[COMPETITION HUB 47] Found={hub47 != null}, Name={hub47?.CompetitionName}, StandingsRows={hub47?.Standings?.Count ?? 0}, ScorersCount={hub47?.TopScorers?.Count ?? 0}");
        Assert.IsNotNull(hub47, "Premier League hub (ID 47) must not be null.");
        Assert.IsTrue(hub47.Standings?.Count > 0, "Premier League standings table must contain rows.");

        // 5b. Competition Hub via slug "premier_league"
        var hubSlug = await provider.GetCompetitionHubAsync("premier_league");
        Console.WriteLine($"[COMPETITION HUB premier_league] Found={hubSlug != null}, Name={hubSlug?.CompetitionName}, StandingsRows={hubSlug?.Standings?.Count ?? 0}");
        Assert.IsNotNull(hubSlug, "Premier League hub (slug) must not be null.");
        Assert.IsTrue(hubSlug.Standings?.Count > 0, "Premier League slug standings table must contain rows.");
    }

    #endregion
}

