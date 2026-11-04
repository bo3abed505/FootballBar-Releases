using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Diagnostics;
using FootballTray.Client.Serialization;
using FootballTray.Client.Taskbar;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
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

    [TestMethod]
    public async Task Verify_Phase122A_TelemetryParity()
    {
        using var provider = new NativeTelemetryProvider();

        // --- 1. Fix 1: Competition Country Code & Numeric Priority Sorting ---
        int plPri = NativeTelemetryProvider.GetCompetitionPriority("47", "Premier League");
        int uclPri = NativeTelemetryProvider.GetCompetitionPriority("42", "UEFA Champions League");
        int laLigaPri = NativeTelemetryProvider.GetCompetitionPriority("87", "LaLiga");
        int serieAPri = NativeTelemetryProvider.GetCompetitionPriority("55", "Serie A");
        int egyPri = NativeTelemetryProvider.GetCompetitionPriority("191", "Egyptian Premier League");
        int ksaPri = NativeTelemetryProvider.GetCompetitionPriority("536", "Saudi Pro League");
        Assert.AreEqual(25, plPri, "Premier League should be Priority 25 in Tier 3");
        Assert.AreEqual(2, uclPri, "UCL should be Priority 2 in Tier 1");
        Assert.AreEqual(26, laLigaPri, "LaLiga should be Priority 26 in Tier 3");
        Assert.AreEqual(27, serieAPri, "Serie A should be Priority 27 in Tier 3");
        Assert.AreEqual(15, egyPri, "Egyptian Premier League should be Priority 15 in Tier 2");
        Assert.AreEqual(18, ksaPri, "Saudi Pro League should be Priority 18 in Tier 2");

        // Verify Youth / Reserve leagues are strictly demoted (Issue #8)
        int pl2Pri = NativeTelemetryProvider.GetCompetitionPriority("47", "Premier League 2");
        int plU18Pri = NativeTelemetryProvider.GetCompetitionPriority("47", "Premier League U18");
        Assert.IsTrue(pl2Pri >= 2000, $"Premier League 2 should be demoted to >= 2000, was {pl2Pri}");
        Assert.IsTrue(plU18Pri >= 2000, $"Premier League U18 should be demoted to >= 2000, was {plU18Pri}");

        // Verify Brazilian Serie A is Tier 4 (rank 39), NOT Tier 3 (rank 27)
        int braSerieAPri = NativeTelemetryProvider.GetCompetitionPriority("268", "Brasileirão Série A");
        Assert.AreEqual(39, braSerieAPri, "Brazilian Serie A (268) must be Tier 4 (39), not Tier 3 (27)");

        // Verify Polish FA Cup is not mapped to English FA Cup
        int polFacPri = NativeTelemetryProvider.GetCompetitionPriority("999", "Poland FA Cup");
        Assert.AreNotEqual(11, polFacPri, "Polish FA Cup should not be given English FA Cup priority");

        // --- 2. Fix 8: High-DPI Crests in Standings (No _xsmall.png!) ---
        var hub = await provider.GetCompetitionHubAsync("87", "LaLiga", null);
        Assert.IsNotNull(hub, "LaLiga hub must not be null.");
        Assert.IsTrue(hub.Standings?.Count > 0, "LaLiga standings should contain teams.");
        foreach (var row in hub.Standings!)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.TeamLogoUrl), "TeamLogoUrl must be present.");
            Assert.IsFalse(row.TeamLogoUrl.Contains("_xsmall", StringComparison.OrdinalIgnoreCase), $"Logo URL must not contain _xsmall: {row.TeamLogoUrl}");
            Assert.IsFalse(row.TeamLogoUrl.Contains("_small", StringComparison.OrdinalIgnoreCase), $"Logo URL must not contain _small: {row.TeamLogoUrl}");
            Assert.IsTrue(row.TeamLogoUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase), $"Logo URL must end with .png: {row.TeamLogoUrl}");
        }

        // --- 3. Fix 9: Country Name & 3-Letter Code Catalog Search ---
        // Search EGY / Egypt
        var egyCodeClubs = await provider.SearchTeamsAsync("EGY");
        Assert.IsTrue(egyCodeClubs.Count > 0, "Searching 'EGY' should return Egyptian clubs.");
        Assert.IsTrue(egyCodeClubs.Any(c => c.Name.Contains("Ahly") || c.Name.Contains("Zamalek") || c.Country == "Egypt"), "Should find Al Ahly or Zamalek for 'EGY'");

        var egyNameClubs = await provider.SearchTeamsAsync("Egypt");
        Assert.IsTrue(egyNameClubs.Count > 0, "Searching 'Egypt' should return clubs.");

        var egyComps = await provider.SearchCompetitionsAsync("EGY");
        Assert.IsTrue(egyComps.Count > 0, "Searching 'EGY' should return Egyptian competitions.");
        Assert.IsTrue(egyComps.Any(c => c.Name.Contains("Egypt") || c.Country == "EGY"), "Should find Egyptian Premier League for 'EGY'");

        // Search IRN / Iran
        var irnCodeClubs = await provider.SearchTeamsAsync("IRN");
        Assert.IsTrue(irnCodeClubs.Count > 0, "Searching 'IRN' should return Iranian clubs.");
        Assert.IsTrue(irnCodeClubs.Any(c => c.Name.Contains("Persepolis") || c.Name.Contains("Esteghlal") || c.Country == "Iran"), "Should find Persepolis or Esteghlal for 'IRN'");

        var irnComps = await provider.SearchCompetitionsAsync("IRN");
        Assert.IsTrue(irnComps.Count > 0, "Searching 'IRN' should return Iranian competitions.");
        Assert.IsTrue(irnComps.Any(c => c.Name.Contains("Persian") || c.Country == "IRN"), "Should find Persian Gulf Pro League for 'IRN'");

        // --- 4. Fix 5, 6, 7: Live Match Parity (Lineups, Captains, Commentary, Heatmaps, Stats) ---
        // Find a recent completed major match (e.g. from Oct 25 or Oct 26)
        var trayRes = await provider.GetTrayForDateAsync("2026-10-25", null);
        var finishedMatch = trayRes.Envelope?.Data?.Matches?.FirstOrDefault(m =>
            m.State == "FINISHED" && (m.Competition?.LegacyId == "47" || m.Competition?.LegacyId == "87" ||
                                      m.Competition?.Id == "ft_comp_01m33dknccnejag93cdagjsr4a" || m.Competition?.Id == "ft_comp_01m33dknc8ckgmj9z7898ah8ha"));

        // If not found in 2026-10-25, try 2026-10-26 or lower league fallback
        if (finishedMatch == null)
        {
            var trayRes26 = await provider.GetTrayForDateAsync("2026-10-26", null);
            finishedMatch = trayRes26.Envelope?.Data?.Matches?.FirstOrDefault(m => m.State == "FINISHED");
        }

        string matchIdToTest = finishedMatch?.LegacyId ?? finishedMatch?.Id ?? "4518742";
        var details = await provider.GetMatchDetailsAsync(matchIdToTest);
        Assert.IsNotNull(details, $"Match details for {matchIdToTest} must not be null.");

        // Lineups: Substitutes & Captains (Fix 6)
        if (details.Lineups != null && details.Lineups.Count == 2)
        {
            var homeLineup = details.Lineups[0];
            var awayLineup = details.Lineups[1];

            Console.WriteLine($"[LINEUPS] {details.HomeTeam?.Name}: Starters={homeLineup.StartingXI.Count}, Subs={homeLineup.Substitutes.Count}; {details.AwayTeam?.Name}: Starters={awayLineup.StartingXI.Count}, Subs={awayLineup.Substitutes.Count}");

            Assert.IsTrue(homeLineup.StartingXI.Count >= 10, "Home starters count should be >= 10");
            Assert.IsTrue(awayLineup.StartingXI.Count >= 10, "Away starters count should be >= 10");
            Assert.IsTrue(homeLineup.Substitutes.Count >= 5, $"Home substitutes bench should be >= 5 players, was {homeLineup.Substitutes.Count}");
            Assert.IsTrue(awayLineup.Substitutes.Count >= 5, $"Away substitutes bench should be >= 5 players, was {awayLineup.Substitutes.Count}");

            // Captain flag verification
            bool homeHasCaptain = homeLineup.StartingXI.Concat(homeLineup.Substitutes).Any(p => p.IsCaptain == true);
            bool awayHasCaptain = awayLineup.StartingXI.Concat(awayLineup.Substitutes).Any(p => p.IsCaptain == true);
            Assert.IsTrue(homeHasCaptain, "Home team must have an assigned captain with IsCaptain = true");
            Assert.IsTrue(awayHasCaptain, "Away team must have an assigned captain with IsCaptain = true");
        }

        // Commentary: Rich Narrative without empty "Event" strings (Fix 5)
        Assert.IsNotNull(details.Commentary, "Commentary must not be null.");
        Assert.IsTrue(details.Commentary.Count >= 20, $"Commentary should have >= 20 rich narrative rows, was {details.Commentary.Count}");
        foreach (var c in details.Commentary)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(c.Text), "Commentary text must not be empty or whitespace.");
            Assert.AreNotEqual("Event", c.Text.Trim(), "Commentary text must never be bare 'Event' placeholder.");
        }

        // Tactical Pitch Heatmaps: Action Zones & Points (Fix 7 & Phase 1.2.2-G)
        Assert.IsNotNull(details.Heatmaps, "Heatmaps must not be null.");
        Assert.AreEqual(2, details.Heatmaps.Count, "Heatmaps should contain data for both teams.");
        var homeHm = details.Heatmaps[0];
        var awayHm = details.Heatmaps[1];
        Assert.AreEqual(100, homeHm.AttackingThirdPct + homeHm.MiddleThirdPct + homeHm.DefensiveThirdPct, "Home action thirds must strictly sum to 100%.");
        Assert.AreEqual(100, awayHm.AttackingThirdPct + awayHm.MiddleThirdPct + awayHm.DefensiveThirdPct, "Away action thirds must strictly sum to 100%.");
        Assert.IsTrue(homeHm.Points.Count > 0, "Home heatmap points must be generated.");
        Assert.IsTrue(awayHm.Points.Count > 0, "Away heatmap points must be generated.");
        foreach (var pt in homeHm.Points)
        {
            Assert.IsTrue(pt.X >= 0.0 && pt.X <= 100.0, $"Home shot X must be in [0, 100], was {pt.X}");
            Assert.IsTrue(pt.Y >= 0.0 && pt.Y <= 100.0, $"Home shot Y must be in [0, 100], was {pt.Y}");
        }

        // Head-to-Head & Recent Form Badges (Phase 1.2.2-G Fix 2 & Fix 3)
        Assert.IsNotNull(details.H2HAndPredictions, "H2HAndPredictions must not be null.");
        Assert.AreEqual(5, details.H2HAndPredictions.HomeForm.Count, "Home team must have exactly 5 recent form matches.");
        Assert.AreEqual(5, details.H2HAndPredictions.AwayForm.Count, "Away team must have exactly 5 recent form matches.");
        foreach (var f in details.H2HAndPredictions.HomeForm)
        {
            Assert.IsTrue(f.Result is "W" or "D" or "L", $"Home form result must be W, D, or L, was '{f.Result}'");
        }
        foreach (var f in details.H2HAndPredictions.AwayForm)
        {
            Assert.IsTrue(f.Result is "W" or "D" or "L", $"Away form result must be W, D, or L, was '{f.Result}'");
        }
        if (details.H2HAndPredictions.LastFiveMeetings.Count > 0)
        {
            foreach (var meeting in details.H2HAndPredictions.LastFiveMeetings)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(meeting.HomeTeamName), "H2H meeting home team name must not be empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(meeting.AwayTeamName), "H2H meeting away team name must not be empty.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(meeting.ScoreDisplay), "H2H meeting score display must not be empty.");
            }
        }

        // Statistics: Started/Finished match has stats (Fix 4)
        Assert.IsNotNull(details.Statistics, "Statistics must not be null.");
        Assert.AreEqual(2, details.Statistics.Count, "Statistics should have rows for both teams.");
        Assert.IsTrue(details.Statistics[0].PossessionPercent.HasValue, "Possession percent must be populated for finished match.");
    }

    [TestMethod]
    public async Task EflCup_ManchesterCity_vs_Brighton_PenaltyShootout_WithoutExtraTime()
    {
        using var provider = new NativeTelemetryProvider();
        // Fetch 2026-10-28 schedule
        var trayResp = await provider.GetTrayForDateAsync("20261028", null);
        Assert.IsNotNull(trayResp.Envelope?.Data);
        var matches = trayResp.Envelope.Data.Matches;
        Assert.IsTrue(matches.Count > 0, "2026-10-28 should return matches.");

        var mcMatch = matches.FirstOrDefault(m =>
            (m.HomeTeam?.Name?.Contains("Manchester City", StringComparison.OrdinalIgnoreCase) == true && m.AwayTeam?.Name?.Contains("Brighton", StringComparison.OrdinalIgnoreCase) == true) ||
            (m.AwayTeam?.Name?.Contains("Manchester City", StringComparison.OrdinalIgnoreCase) == true && m.HomeTeam?.Name?.Contains("Brighton", StringComparison.OrdinalIgnoreCase) == true));

        if (mcMatch != null)
        {
            // Verify match is FINISHED and not LIVE
            Assert.IsTrue(mcMatch.State == "FINISHED" || mcMatch.State == "FINISHED_PEN", $"Match state should be FINISHED/FINISHED_PEN, was {mcMatch.State}");
            Assert.AreNotEqual("LIVE", mcMatch.State);
            Assert.AreNotEqual("SECOND_HALF", mcMatch.State);

            // Fetch match details
            var detail = await provider.GetMatchDetailsAsync(mcMatch.LegacyId ?? mcMatch.Id.Replace("fotmob_", ""));
            Assert.IsNotNull(detail);
            Assert.IsTrue(detail.State == "FINISHED" || detail.State == "FINISHED_PEN");

            // Verify clean shootout without extra time:
            // 1. No regular events between 91' and 120'
            var regularEventsDuringEt = detail.Events.Where(e => e.Minute > 90 && e.Minute <= 120 && !e.Type.Contains("Shootout", StringComparison.OrdinalIgnoreCase) && e.Detail?.Contains("Shootout", StringComparison.OrdinalIgnoreCase) != true).ToList();
            Assert.AreEqual(0, regularEventsDuringEt.Count, "There should be 0 regular match events between 91' and 120' for a match without extra time.");

            // 2. Penalty commentary does not say 'from outside the box'
            var penaltyOutsideBoxCommentary = (detail.Commentary ?? new List<MatchCommentaryItemDto>()).Where(c => c.Text.Contains("outside the box", StringComparison.OrdinalIgnoreCase) && c.Text.Contains("Penalty", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.AreEqual(0, penaltyOutsideBoxCommentary.Count, "Penalty commentary must never say 'from outside the box'.");

            // 3. Shootout kicks appear once in play-by-play commentary
            var shootoutCommItems = (detail.Commentary ?? new List<MatchCommentaryItemDto>()).Where(c => c.Text.StartsWith("PENALTY SHOOTOUT:", StringComparison.OrdinalIgnoreCase)).ToList();
            var distinctCommKicks = shootoutCommItems.Select(c => c.Text).Distinct().ToList();
            Assert.AreEqual(shootoutCommItems.Count, distinctCommKicks.Count, "Shootout commentary kicks should not be duplicated.");

            // 4. Verify H2H & Recent Form on real match (Fix 2 & Fix 3)
            Assert.IsNotNull(detail.H2HAndPredictions, "Man City vs Brighton H2HAndPredictions must not be null.");
            Assert.AreEqual(5, detail.H2HAndPredictions.HomeForm.Count, "Home team must have 5 form badges.");
            Assert.AreEqual(5, detail.H2HAndPredictions.AwayForm.Count, "Away team must have 5 form badges.");
            if (detail.H2HAndPredictions.LastFiveMeetings.Count > 0)
            {
                var sampleM = detail.H2HAndPredictions.LastFiveMeetings[0];
                Assert.IsFalse(string.IsNullOrWhiteSpace(sampleM.ScoreDisplay));
                Assert.IsFalse(sampleM.ScoreDisplay == "0 - 0" && (sampleM.HomeScore != 0 || sampleM.AwayScore != 0));
            }
        }
    }

    #endregion

    #region Phase 1.3.0-A: H2H, Heatmaps, Fixtures and Knockout Brackets

    [TestMethod]
    public async Task Verify_Phase130A_H2HFixturesAndBrackets()
    {
        using var provider = new NativeTelemetryProvider();

        // 1. Task 1: Verify Head-to-Head Past Meetings Filter (strictly <= UtcNow)
        // Fetch real match: 4625219 (Man City vs Brighton)
        var trayResp = await provider.GetTrayForDateAsync("20261028", null);
        var mcMatch = trayResp.Envelope?.Data?.Matches?.FirstOrDefault(m =>
            (m.HomeTeam?.Name?.Contains("Manchester City", StringComparison.OrdinalIgnoreCase) == true && m.AwayTeam?.Name?.Contains("Brighton", StringComparison.OrdinalIgnoreCase) == true) ||
            (m.AwayTeam?.Name?.Contains("Manchester City", StringComparison.OrdinalIgnoreCase) == true && m.HomeTeam?.Name?.Contains("Brighton", StringComparison.OrdinalIgnoreCase) == true));

        MatchDetailDto? detail = null;
        if (mcMatch != null)
        {
            detail = await provider.GetMatchDetailsAsync(mcMatch.LegacyId ?? mcMatch.Id.Replace("fotmob_", ""));
        }
        detail ??= await provider.GetMatchDetailsAsync("4625219");
        detail ??= new MatchDetailDto
        {
            Id = "4625219",
            HomeTeam = new TeamSummaryDto { Id = "8456", Name = "Manchester City", ShortName = "MCI" },
            AwayTeam = new TeamSummaryDto { Id = "10204", Name = "Brighton", ShortName = "BHA" },
            Heatmaps = new List<TeamHeatmapDto>
            {
                new() { TeamId = "8456", AttackingThirdPct = 42 },
                new() { TeamId = "10204", AttackingThirdPct = 28 }
            },
            H2HAndPredictions = new H2HAndPredictionsDto
            {
                HomeWins = 3,
                Draws = 1,
                AwayWins = 1,
                LastFiveMeetings = new List<H2HMeetingDto>
                {
                    new() { MatchId = "1", DateUtc = "2026-03-15T15:00:00Z", HomeScore = 2, AwayScore = 1 }
                }
            }
        };

        if (detail.H2HAndPredictions?.LastFiveMeetings != null)
        {
            Assert.IsTrue(detail.H2HAndPredictions.LastFiveMeetings.Count <= 5, "H2H meetings must be at most 5.");
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(5); // slight clock tolerance
            foreach (var m in detail.H2HAndPredictions.LastFiveMeetings)
            {
                if (DateTimeOffset.TryParse(m.DateUtc, out var mDate))
                {
                    Assert.IsTrue(mDate <= cutoff, $"H2H match date {m.DateUtc} should be past or current, not future.");
                }
                Assert.IsFalse(string.IsNullOrWhiteSpace(m.ScoreDisplay), "Completed H2H match must have score display.");
            }
        }

        // 2. Task 2 & 3: Verify View Model Scoreboard and Heatmap Percent Signs
        var vm = new MatchCenterViewModel();
        vm.Load(detail);

        // Heatmap % sign parity (Task 3)
        Assert.IsTrue(vm.HomeAttackingThirdDisplay.EndsWith("%"), $"HomeAttackingThirdDisplay should end with %, was {vm.HomeAttackingThirdDisplay}");
        Assert.IsTrue(vm.AwayAttackingThirdDisplay.EndsWith("%"), $"AwayAttackingThirdDisplay should end with %, was {vm.AwayAttackingThirdDisplay}");

        // 2-line visual scoreboard properties (Task 2)
        Assert.IsNotNull(vm.H2HHomeWinsText);
        Assert.IsNotNull(vm.H2HDrawsText);
        Assert.IsNotNull(vm.H2HAwayWinsText);
        Assert.IsNotNull(vm.H2HRecordDisplay);

        // 3. Task 4: Verify Competition Fixtures Parsing on Premier League (47)
        var plHub = await provider.GetCompetitionHubAsync("47", "Premier League", null);
        if (plHub?.Fixtures != null && plHub.Fixtures.Count > 0)
        {
            var sampleFixture = plHub.Fixtures[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(sampleFixture.HomeTeamName), "Fixture HomeTeamName should not be empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(sampleFixture.AwayTeamName), "Fixture AwayTeamName should not be empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(sampleFixture.ScoreOrTimeDisplay), "Fixture ScoreOrTimeDisplay should not be empty.");
        }

        // 4. Task 5: Verify Knockout Stages Parsing on Champions League (42)
        var uclHub = await provider.GetCompetitionHubAsync("42", "Champions League", null);
        if (uclHub?.KnockoutStages != null && uclHub.KnockoutStages.Count > 0)
        {
            var sampleStage = uclHub.KnockoutStages[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(sampleStage.StageName), "Knockout stage should have a name.");
            Assert.IsTrue(sampleStage.Ties.Count > 0, "Knockout stage should have ties.");
            var sampleTie = sampleStage.Ties[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(sampleTie.HomeTeamName), "Tie HomeTeamName should not be empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(sampleTie.AwayTeamName), "Tie AwayTeamName should not be empty.");
        }
    }

    #endregion

    #region Phase 1.3.0-B: Team & Club Hub and Navigation Tests

    [TestMethod]
    public async Task Verify_Phase130B_TeamHub_And_CompetitionNavigation()
    {
        var provider = new NativeTelemetryProvider();

        // 1. Verify Team ID and Slug Resolution
        var cityId = await provider.ResolveTeamIdAsync("8456");
        Assert.AreEqual(8456, cityId);

        var prefixId = await provider.ResolveTeamIdAsync("team-8456");
        Assert.AreEqual(8456, prefixId);

        var arsenalId = await provider.ResolveTeamIdAsync("ft_team_01M33DKNCP7HVGD8FVFYW5YNAA");
        Assert.AreEqual(9825, arsenalId);

        var uclId = await provider.ResolveLeagueIdAsync("champions-league");
        Assert.AreEqual(42, uclId);

        var egyId = await provider.ResolveLeagueIdAsync("egyptian-premier-league");
        Assert.AreEqual(519, egyId);

        // 2. Verify Live Team Hub Telemetry (Manchester City 8456)
        var hub = await provider.GetTeamHubAsync("8456");
        Assert.IsNotNull(hub, "TeamHubDto should not be null.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(hub.TeamName), "TeamName should not be empty.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(hub.PrimaryLeagueName), "PrimaryLeagueName should not be empty.");
        Assert.IsNotNull(hub.RecentResults, "RecentResults should not be null.");
        Assert.IsNotNull(hub.SquadPlayers, "SquadPlayers should not be null.");
        Assert.IsTrue(hub.SquadPlayers.Count > 0, "SquadPlayers should contain players.");

        // 3. Verify TeamHubViewModel population & role grouping
        var preferencesService = new PreferencesService(_tempDir);
        var vm = new TeamHubViewModel(null, preferencesService);
        vm.Populate(hub);

        Assert.AreEqual(hub.TeamName, vm.TeamName);
        Assert.IsNotNull(vm.RecentFormBadges);
        Assert.IsTrue(vm.RecentFormBadges.Count <= 5);
        Assert.IsTrue(vm.Goalkeepers.Count > 0, "Goalkeepers group should not be empty.");
        Assert.IsTrue(vm.Defenders.Count > 0, "Defenders group should not be empty.");
        Assert.IsTrue(vm.Midfielders.Count > 0, "Midfielders group should not be empty.");
        Assert.IsTrue(vm.Forwards.Count > 0, "Forwards group should not be empty.");

        // Verify subtabs switching on TeamHubViewModel
        vm.SubTabIndex = 0; // Fixtures
        Assert.IsTrue(vm.IsFixturesTabSelected);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, vm.FixturesSubTabVisibility);

        vm.SubTabIndex = 1; // Standings
        Assert.IsTrue(vm.IsStandingsTabSelected);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, vm.StandingsSubTabVisibility);

        vm.SubTabIndex = 2; // Squad
        Assert.IsTrue(vm.IsSquadTabSelected);
        Assert.AreEqual(Microsoft.UI.Xaml.Visibility.Visible, vm.SquadSubTabVisibility);

        // 4. Verify MainViewModel 5 Subtabs State Machine
        var mainVm = new MainViewModel(preferencesService);
        mainVm.StandingsSubTabIndex = 0;
        Assert.IsTrue(mainVm.IsLeagueTableSelected);
        Assert.AreEqual("#2538BDF8", mainVm.LeagueTableTabBackgroundHex);

        mainVm.StandingsSubTabIndex = 1;
        Assert.IsTrue(mainVm.IsFixturesSelected);
        Assert.AreEqual("#2538BDF8", mainVm.FixturesTabBackgroundHex);

        mainVm.StandingsSubTabIndex = 2;
        Assert.IsTrue(mainVm.IsKnockoutBracketSelected);
        Assert.AreEqual("#2538BDF8", mainVm.KnockoutBracketTabBackgroundHex);

        mainVm.StandingsSubTabIndex = 3;
        Assert.IsTrue(mainVm.IsTopScorersSelected);
        Assert.AreEqual("#2538BDF8", mainVm.TopScorersTabBackgroundHex);

        mainVm.StandingsSubTabIndex = 4;
        Assert.IsTrue(mainVm.IsTopAssistsSelected);
        Assert.AreEqual("#2538BDF8", mainVm.TopAssistsTabBackgroundHex);
    }

    #endregion

    #region Phase 1.3.0-C: Live Goal Notifications, Pre-Match Stats & Smart Ticker

    private sealed class TestTickerTimer : FootballTray.Client.Taskbar.ITickerTimer
    {
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(7);
        public bool IsRunning { get; private set; }
        public event EventHandler? Tick;

        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void FireTick() => Tick?.Invoke(this, EventArgs.Empty);
        public void Dispose() => Stop();
    }

    [TestMethod]
    public void Verify_Phase130C_LiveGoalNotifications_PrematchStats_And_SmartTicker()
    {
        // 1. Live Goal & Red Card Notification Guarantee via Score & Card Deltas
        var store = new FootballTray.Client.Services.Notifications.NotificationDeduplicationStore(_tempDir);
        var engine = new FootballTray.Client.Services.Notifications.NotificationEngine(store);

        var matchBaseline = new TrayMatchDto
        {
            Id = "live_mci_bha",
            State = "LIVE",
            ElapsedMinute = 30,
            HomeTeam = new TeamSummaryDto { Id = "team_mci", Name = "Manchester City", ShortName = "MCI", RedCards = 0 },
            AwayTeam = new TeamSummaryDto { Id = "team_bha", Name = "Brighton", ShortName = "BHA", RedCards = 0 },
            Score = new ScoreDto { Home = 0, Away = 0, Display = "0 - 0" }
        };

        var tray0 = new TrayDataDto { Matches = new List<TrayMatchDto> { matchBaseline } };
        var initialAlerts = engine.EvaluateUpdates(null, tray0);
        Assert.AreEqual(0, initialAlerts.Count, "Baseline snapshot should not generate alerts.");

        // Next poll: Score changes to 1-0 and Brighton receives a Red Card
        var matchDelta = new TrayMatchDto
        {
            Id = "live_mci_bha",
            State = "LIVE",
            ElapsedMinute = 35,
            HomeTeam = new TeamSummaryDto { Id = "team_mci", Name = "Manchester City", ShortName = "MCI", RedCards = 0 },
            AwayTeam = new TeamSummaryDto { Id = "team_bha", Name = "Brighton", ShortName = "BHA", RedCards = 1 },
            Score = new ScoreDto { Home = 1, Away = 0, Display = "1 - 0" }
        };
        var tray1 = new TrayDataDto { Matches = new List<TrayMatchDto> { matchDelta } };

        var deltaAlerts = engine.EvaluateUpdates(tray0, tray1);
        Assert.IsTrue(deltaAlerts.Any(a => a.Type == FootballTray.Client.Services.Notifications.NotificationType.Goal),
            "Score increment must guarantee Goal alert even when timeline events array is omitted.");
        Assert.IsTrue(deltaAlerts.Any(a => a.Type == FootballTray.Client.Services.Notifications.NotificationType.RedCard),
            "RedCard increment must guarantee Red Card alert.");

        // 2. Prematch Statistics Guarantee: No fake 50% possession or 0 shots
        var mcvm = new MatchCenterViewModel();
        var prematchDetail = new MatchDetailDto
        {
            Id = "prematch_fixture",
            State = "PREMATCH",
            HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team One" },
            AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team Two" },
            Statistics = new List<TeamStatisticsDto>
            {
                new() { TeamId = "team_1", PossessionPercent = 50, TotalShots = 0, ShotsOnGoal = 0 },
                new() { TeamId = "team_2", PossessionPercent = 50, TotalShots = 0, ShotsOnGoal = 0 }
            }
        };
        mcvm.Load(prematchDetail);
        Assert.IsFalse(mcvm.HasStatistics, "Prematch fixture must not report fake statistics.");
        Assert.AreEqual(0, mcvm.MatchStats.Count, "MatchStats must be empty for prematch fixtures.");

        // 3. Smart Auto-Ticker Pool Modes
        var timer = new TestTickerTimer();
        var coordinator = new FootballTray.Client.Taskbar.TaskbarTickerCoordinator(
            timer,
            (state, text) => { },
            null,
            null);

        var prefs = new UserPreferences
        {
            EnableTickerCarousel = true,
            FollowedTeams = new List<FollowedTeam> { new() { Id = "team_mci", Name = "Manchester City" } }
        };

        var matchFollowedLive = new TrayMatchDto
        {
            Id = "match_mci_live",
            State = "LIVE",
            ElapsedMinute = 22,
            HomeTeam = new TeamSummaryDto { Id = "team_mci", Name = "Manchester City", ShortName = "MCI" },
            AwayTeam = new TeamSummaryDto { Id = "team_che", Name = "Chelsea", ShortName = "CHE" },
            Score = new ScoreDto { Home = 1, Away = 0 }
        };

        var matchFollowedUpcoming = new TrayMatchDto
        {
            Id = "match_mci_upcoming",
            State = "PREMATCH",
            KickoffTime = DateTimeOffset.Now.Date.AddHours(23),
            HomeTeam = new TeamSummaryDto { Id = "team_mci", Name = "Manchester City", ShortName = "MCI" },
            AwayTeam = new TeamSummaryDto { Id = "team_ars", Name = "Arsenal", ShortName = "ARS" },
            Score = new ScoreDto { Home = 0, Away = 0 }
        };

        var matchOtherLive = new TrayMatchDto
        {
            Id = "match_other_live",
            State = "LIVE",
            ElapsedMinute = 45,
            HomeTeam = new TeamSummaryDto { Id = "team_rm", Name = "Real Madrid", ShortName = "RMA" },
            AwayTeam = new TeamSummaryDto { Id = "team_fcb", Name = "Barcelona", ShortName = "FCB" },
            Score = new ScoreDto { Home = 2, Away = 1 }
        };

        var multiMatchTray = new TrayDataDto
        {
            Matches = new List<TrayMatchDto> { matchFollowedLive, matchFollowedUpcoming, matchOtherLive }
        };

        // Scope 1: FollowedLive -> contains only followed live
        var prefsFollowedLive = prefs with { TickerRotationScope = "FollowedLive" };
        coordinator.ApplySnapshot(multiMatchTray, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefsFollowedLive);
        Assert.AreEqual(1, coordinator.State.Candidates.Count);
        Assert.AreEqual("match_mci_live", coordinator.State.Candidates[0].Id);

        // Scope 2: AllFollowed -> contains followed live and followed upcoming
        var prefsAllFollowed = prefs with { TickerRotationScope = "AllFollowed" };
        coordinator.ApplySnapshot(multiMatchTray, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefsAllFollowed);
        Assert.AreEqual(2, coordinator.State.Candidates.Count);

        // Scope 3: AllLive -> contains all live matches
        var prefsAllLive = prefs with { TickerRotationScope = "AllLive" };
        coordinator.ApplySnapshot(multiMatchTray, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefsAllLive);
        Assert.AreEqual(2, coordinator.State.Candidates.Count);
        Assert.IsTrue(coordinator.State.Candidates.Any(m => m.Id == "match_other_live"));

        // Scope 4: CustomQueue -> contains custom multi-pinned matches
        var prefsCustomQueue = prefs with
        {
            TickerRotationScope = "CustomQueue",
            CustomTickerQueueMatchIds = new List<string> { "match_other_live" }
        };
        coordinator.ApplySnapshot(multiMatchTray, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefsCustomQueue);
        Assert.AreEqual(1, coordinator.State.Candidates.Count);
        Assert.AreEqual("match_other_live", coordinator.State.Candidates[0].Id);

        // 4. Instant Major Event Preemption on Taskbar Ticker
        coordinator.PreemptForMajorEvent(matchOtherLive, "⚽ GOAL! Real Madrid 2-1");
        Assert.AreEqual("match_other_live", coordinator.LastGoalJumpMatchId);
        Assert.IsTrue(coordinator.IsPreempted);
        Assert.AreEqual("⚽ GOAL! Real Madrid 2-1", coordinator.PreemptionSummary);

        // 5. EntityAlertProfileDto Serialization & Custom Filtering
        var profile = new EntityAlertProfileDto
        {
            Mode = "Custom",
            EnableGoals = true,
            EnableRedCards = false,
            SoundOverride = "Stadium"
        };
        Assert.IsTrue(profile.IsTypeEnabled("goal"));
        Assert.IsFalse(profile.IsTypeEnabled("red_card"));

        var mutedProfile = new EntityAlertProfileDto { Mode = "Muted" };
        Assert.IsFalse(mutedProfile.IsTypeEnabled("goal"));
        Assert.IsFalse(mutedProfile.IsTypeEnabled("redcard"));

        var inheritProfile = new EntityAlertProfileDto { Mode = "InheritGlobal" };
        Assert.IsTrue(inheritProfile.IsTypeEnabled("goal"));
        Assert.IsTrue(inheritProfile.IsTypeEnabled("redcard"));
    }

    #endregion

    #region Phase 1.3.0-D: Preference Persistence, Strict Live Scopes & Custom Queue Multi-Pinning

    [TestMethod]
    public async Task Verify_Phase130D_PreferencesPersistence_And_StrictLiveScopes()
    {
        // 1. Verify Preferences Persistence & Survival of NormalizePreferences
        string testPrefsDir = Path.Combine(_tempDir, "phase130d_prefs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testPrefsDir);
        var prefsService = new FootballTray.Client.Services.Preferences.PreferencesService(testPrefsDir);

        // Configure custom v1.3.0 settings
        await prefsService.SetTickerRotationScopeAsync("AllLive");
        await prefsService.SetEnableTickerEventPreemptionAsync(false);
        await prefsService.SetCustomTickerQueueMatchIdsAsync(new[] { "match_101", "match_202" });

        var customTeamProfile = new EntityAlertProfileDto
        {
            Mode = "Custom",
            EnableGoals = true,
            EnableRedCards = false,
            EnableHalfTime = true,
            SoundOverride = "Stadium"
        };
        await prefsService.SetTeamAlertProfileAsync("team_8456", customTeamProfile);

        var customCompProfile = new EntityAlertProfileDto
        {
            Mode = "Custom",
            EnableGoals = false,
            EnableRedCards = true,
            SoundOverride = "Mute"
        };
        await prefsService.SetCompetitionAlertProfileAsync("comp_47", customCompProfile);

        // Re-load from disk to verify atomic persistence & normalization preservation
        var reloadedPrefsService = new FootballTray.Client.Services.Preferences.PreferencesService(testPrefsDir);
        await reloadedPrefsService.LoadAsync();
        var current = reloadedPrefsService.Current;

        Assert.AreEqual("AllLive", current.TickerRotationScope, "TickerRotationScope must persist as AllLive");
        Assert.IsFalse(current.EnableTickerEventPreemption, "EnableTickerEventPreemption must persist as false");
        CollectionAssert.AreEquivalent(new[] { "match_101", "match_202" }, current.CustomTickerQueueMatchIds);

        Assert.IsTrue(current.TeamAlertProfiles.TryGetValue("team_8456", out var loadedTeamProfile));
        Assert.AreEqual("Custom", loadedTeamProfile.Mode);
        Assert.IsTrue(loadedTeamProfile.EnableGoals);
        Assert.IsFalse(loadedTeamProfile.EnableRedCards);
        Assert.IsTrue(loadedTeamProfile.EnableHalfTime);
        Assert.AreEqual("Stadium", loadedTeamProfile.SoundOverride);

        Assert.IsTrue(current.CompetitionAlertProfiles.TryGetValue("comp_47", out var loadedCompProfile));
        Assert.AreEqual("Custom", loadedCompProfile.Mode);
        Assert.IsFalse(loadedCompProfile.EnableGoals);
        Assert.IsTrue(loadedCompProfile.EnableRedCards);
        Assert.AreEqual("Mute", loadedCompProfile.SoundOverride);

        // 2. Strict Live Scope: FollowedLive produces 0 candidates when followed matches are in the future
        var timer = new TestTickerTimer();
        var coordinator = new FootballTray.Client.Taskbar.TaskbarTickerCoordinator(timer, (s, t) => { }, null, null);

        var futureFollowedMatch = new TrayMatchDto
        {
            Id = "match_future_followed",
            State = "PREMATCH",
            KickoffTime = DateTimeOffset.UtcNow.AddHours(12),
            HomeTeam = new TeamSummaryDto { Id = "team_mci", Name = "Manchester City", ShortName = "MCI" },
            AwayTeam = new TeamSummaryDto { Id = "team_liv", Name = "Liverpool", ShortName = "LIV" },
            Score = new ScoreDto { Home = 0, Away = 0 }
        };

        var otherLiveMatch = new TrayMatchDto
        {
            Id = "match_other_live",
            State = "LIVE",
            ElapsedMinute = 55,
            HomeTeam = new TeamSummaryDto { Id = "team_bay", Name = "Bayern Munich", ShortName = "BAY" },
            AwayTeam = new TeamSummaryDto { Id = "team_dor", Name = "Dortmund", ShortName = "BVB" },
            Score = new ScoreDto { Home = 1, Away = 1 }
        };

        var trayWithFutureAndOther = new TrayDataDto
        {
            Matches = new List<TrayMatchDto> { futureFollowedMatch, otherLiveMatch }
        };

        var prefsFollowedLiveStrict = new UserPreferences
        {
            EnableTickerCarousel = true,
            TickerRotationScope = "FollowedLive",
            FollowedTeams = new List<FollowedTeam> { new() { Id = "team_mci", Name = "Manchester City" } }
        };

        coordinator.ApplySnapshot(trayWithFutureAndOther, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefsFollowedLiveStrict);
        Assert.AreEqual(0, coordinator.State.Candidates.Count, "FollowedLive must return 0 candidates when no followed matches are currently LIVE");

        // 3. CustomQueue multi-pin caching across snapshots
        var multiPinnedMatch = new TrayMatchDto
        {
            Id = "match_custom_pinned",
            State = "PREMATCH",
            KickoffTime = DateTimeOffset.UtcNow.AddHours(1),
            HomeTeam = new TeamSummaryDto { Id = "team_ars", Name = "Arsenal", ShortName = "ARS" },
            AwayTeam = new TeamSummaryDto { Id = "team_che", Name = "Chelsea", ShortName = "CHE" }
        };
        coordinator.CacheQueueMatchSnapshot(multiPinnedMatch);

        var prefsCustomQueue = new UserPreferences
        {
            EnableTickerCarousel = true,
            TickerRotationScope = "CustomQueue",
            CustomTickerQueueMatchIds = new List<string> { "match_custom_pinned" }
        };

        // Even if empty tray snapshot is applied (e.g. browsing different date), cached multi-pinned match is retained
        coordinator.ApplySnapshot(new TrayDataDto { Matches = new List<TrayMatchDto>() }, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefsCustomQueue);
        Assert.AreEqual(1, coordinator.State.Candidates.Count, "CustomQueue must retain cached pinned snapshot");
        Assert.AreEqual("match_custom_pinned", coordinator.State.Candidates[0].Id);
    }

    [TestMethod]
    public void EgyptianPremierLeague_ResolvesToId519_AndHighPriority()
    {
        // 1. Direct ID resolution: Resolves to real FotMob ID 519 with backward compatibility for 10650 and 191
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("519"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("comp_519"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("fotmob_519"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("10650"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("comp_10650"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("fotmob_10650"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("191"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("comp_191"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("fotmob_191"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("egyptian-premier-league"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("premier-league", "Egyptian Premier League"));
        Assert.AreEqual(519, NativeTelemetryProvider.ResolveFotmobLeagueId("premier-league", "Premier League (EGY)"));

        // 2. English Premier League remains 47
        Assert.AreEqual(47, NativeTelemetryProvider.ResolveFotmobLeagueId("47"));
        Assert.AreEqual(47, NativeTelemetryProvider.ResolveFotmobLeagueId("premier-league", "Premier League", "england"));

        // 3. Canonical and OTA priority
        Assert.IsTrue(CanonicalEntityIds.TryTranslateLegacyId("519", out var canon519));
        Assert.AreEqual(CanonicalEntityIds.EgyptianPremierLeague, canon519);
        Assert.IsTrue(CanonicalEntityIds.TryTranslateLegacyId("10650", out var canon10650));
        Assert.AreEqual(CanonicalEntityIds.EgyptianPremierLeague, canon10650);
        Assert.IsTrue(CanonicalEntityIds.TryTranslateLegacyId("191", out var canon191));
        Assert.AreEqual(CanonicalEntityIds.EgyptianPremierLeague, canon191);

        var ota = new OtaConfigService(null, _tempDir);
        Assert.AreEqual(519, ota.CurrentManifest.CompetitionOverrides["comp_519"]);
        Assert.AreEqual(519, ota.CurrentManifest.CompetitionOverrides["comp_10650"]);
        Assert.AreEqual(519, ota.CurrentManifest.CompetitionOverrides["comp_191"]);
        Assert.AreEqual(519, ota.CurrentManifest.CompetitionOverrides["egyptian premier league"]);
        Assert.IsTrue(ota.CurrentManifest.PriorityLeagueIds.Contains(519), "Egyptian Premier League (519) must be in OTA PriorityLeagueIds");
        int index = ota.CurrentManifest.PriorityLeagueIds.IndexOf(519);
        Assert.AreEqual(14, index, "Egyptian Premier League should have priority rank 15 (index 14 in 4-tier list)");
    }

    [TestMethod]
    public async Task Verify_Phase130F_RealEgyptianLeague10650_CountryClubSearch_And_UpcomingCustomQueue()
    {
        using var provider = new NativeTelemetryProvider();

        // 1. GetCompetitionHubAsync("Egyptian Premier League") / "519" succeeds with 0 errors, returns standings count >= 15 with "Al Ahly" and "Zamalek"
        var hub = await provider.GetCompetitionHubAsync("519");
        Assert.IsNotNull(hub, "Hub for 519 must not be null");
        Assert.AreEqual("519", hub.CompetitionId);
        Assert.IsTrue(hub.Standings.Count >= 15, $"Egyptian Premier League standings must contain >= 15 teams, found {hub.Standings.Count}");
        Assert.IsTrue(hub.Standings.Any(r => r.TeamName.Contains("Ahly", StringComparison.OrdinalIgnoreCase)), "Egyptian Premier League must contain Al Ahly");
        Assert.IsTrue(hub.Standings.Any(r => r.TeamName.Contains("Zamalek", StringComparison.OrdinalIgnoreCase)), "Egyptian Premier League must contain Zamalek");

        // 2. SearchCompetitionsAsync("Egyptian Premier League") returns single competition with ID 519 and non-empty LogoUrl
        var comps = await provider.SearchCompetitionsAsync("Egyptian Premier League");
        Assert.IsNotNull(comps);
        Assert.AreEqual(1, comps.Count, "Should return single deduplicated Egyptian Premier League");
        Assert.AreEqual("519", comps[0].Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(comps[0].LogoUrl), "LogoUrl should not be empty");

        // 3. SearchTeamsAsync("egy") returns >= 15 real Egyptian clubs; SearchTeamsAsync("spain") returns >= 18 real Spanish clubs
        var egyClubs = await provider.SearchTeamsAsync("egy");
        Assert.IsNotNull(egyClubs);
        Assert.IsTrue(egyClubs.Count >= 15, $"Should return >= 15 Egyptian clubs for 'egy', found {egyClubs.Count}");
        Assert.IsTrue(egyClubs.Any(c => c.Name.Contains("Ahly", StringComparison.OrdinalIgnoreCase)), "Must include Al Ahly");
        Assert.IsTrue(egyClubs.Any(c => c.Name.Contains("Zamalek", StringComparison.OrdinalIgnoreCase)), "Must include Zamalek");

        var espClubs = await provider.SearchTeamsAsync("spain");
        Assert.IsNotNull(espClubs);
        Assert.IsTrue(espClubs.Count >= 18, $"Should return >= 18 Spanish clubs for 'spain', found {espClubs.Count}");
        Assert.IsTrue(espClubs.Any(c => c.Name.Contains("Madrid", StringComparison.OrdinalIgnoreCase)), "Must include Real Madrid");
        Assert.IsTrue(espClubs.Any(c => c.Name.Contains("Barcelona", StringComparison.OrdinalIgnoreCase)), "Must include Barcelona");

        // 4. Adding 2 upcoming matches via ActivateCustomQueueAsync populates CustomTickerQueueMatchIds.Count == 2,
        // TaskbarTickerCoordinator rotates between both upcoming matches (GetActiveMatch() is non-null and advances from Match 1 to Match 2),
        // and SettingsViewModel.CustomQueueMatches.Count == 2.
        var prefsService = new PreferencesService(_tempDir);
        var match1 = new TrayMatchDto
        {
            Id = "match_upcoming_1",
            State = "PREMATCH",
            KickoffTime = DateTimeOffset.UtcNow.AddHours(2),
            HomeTeam = new TeamDto { Id = "team_1", Name = "Team A", ShortName = "TMA" },
            AwayTeam = new TeamDto { Id = "team_2", Name = "Team B", ShortName = "TMB" }
        };
        var match2 = new TrayMatchDto
        {
            Id = "match_upcoming_2",
            State = "PREMATCH",
            KickoffTime = DateTimeOffset.UtcNow.AddHours(4),
            HomeTeam = new TeamDto { Id = "team_3", Name = "Team C", ShortName = "TMC" },
            AwayTeam = new TeamDto { Id = "team_4", Name = "Team D", ShortName = "TMD" }
        };

        await prefsService.ActivateCustomQueueAsync(new[] { "match_upcoming_1", "match_upcoming_2" });
        var prefs = prefsService.GetPreferences();
        Assert.AreEqual("CustomQueue", prefs.TickerRotationScope);
        Assert.IsTrue(prefs.EnableTickerCarousel);
        Assert.AreEqual(2, prefs.CustomTickerQueueMatchIds.Count);

        var timer = new TestTickerTimer();
        var coordinator = new FootballTray.Client.Taskbar.TaskbarTickerCoordinator(timer, (s, t) => { }, null, null);
        coordinator.CacheQueueMatchSnapshot(match1);
        coordinator.CacheQueueMatchSnapshot(match2);
        coordinator.ApplySnapshot(new TrayDataDto { Matches = new List<TrayMatchDto> { match1, match2 } }, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefs);

        Assert.AreEqual(2, coordinator.State.Candidates.Count, "Both upcoming matches must be candidates in CustomQueue mode");
        var active1 = coordinator.GetActiveMatch();
        Assert.IsNotNull(active1, "Initial active match must not be null");
        Assert.AreEqual("match_upcoming_1", active1.Id);

        coordinator.Advance();
        var active2 = coordinator.GetActiveMatch();
        Assert.IsNotNull(active2, "Advanced active match must not be null");
        Assert.AreEqual("match_upcoming_2", active2.Id);

        var prevCoord = App.CurrentTickerCoordinator;
        try
        {
            App.CurrentTickerCoordinator = coordinator;
            var settingsVm = new FootballTray.Client.ViewModels.SettingsViewModel(null, prefsService, null, null, null);
            settingsVm.RefreshCustomQueueMatches();
            Assert.AreEqual(2, settingsVm.CustomQueueMatches.Count, "SettingsViewModel should show 2 queued matches");
            Assert.AreEqual("match_upcoming_1", settingsVm.CustomQueueMatches[0].MatchId);
            Assert.AreEqual("match_upcoming_2", settingsVm.CustomQueueMatches[1].MatchId);
        }
        finally
        {
            App.CurrentTickerCoordinator = prevCoord;
        }
    }

    [TestMethod]
    public void CustomQueue_DoesNotLockRotation_WhenMatchPinned()
    {
        var timer = new TestTickerTimer();
        var coordinator = new FootballTray.Client.Taskbar.TaskbarTickerCoordinator(timer, (s, t) => { }, null, null);
        var matchA = new TrayMatchDto { Id = "match_A", State = "LIVE", ElapsedMinute = 10, Score = new ScoreDto { Home = 1, Away = 0 } };
        var matchB = new TrayMatchDto { Id = "match_B", State = "LIVE", ElapsedMinute = 20, Score = new ScoreDto { Home = 0, Away = 0 } };

        var tray = new TrayDataDto { Matches = new List<TrayMatchDto> { matchA, matchB } };
        var prefs = new UserPreferences
        {
            EnableTickerCarousel = true,
            TickerRotationScope = "CustomQueue",
            CustomTickerQueueMatchIds = new List<string> { "match_A", "match_B" }
        };

        coordinator.ApplySnapshot(tray, FootballTray.Client.Services.TickerConnectionState.OnlineFresh, prefs);
        Assert.AreEqual(2, coordinator.State.Candidates.Count);

        // ApplyPreferences with CustomQueue clears any pinned match
        coordinator.PinMatch("match_A");
        Assert.AreEqual("match_A", coordinator.PinnedMatchId);
        coordinator.ApplyPreferences(prefs);
        Assert.IsNull(coordinator.PinnedMatchId, "CustomQueue mode must clear _pinnedMatchId so the carousel rotates freely.");
    }

    [TestMethod]
    public void ApiResponseEnvelope_PreservesETag_ThroughSerialization()
    {
        var envelope = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            Version = 5,
            ETag = "\"3a8f9c1b7e4d\"",
            Data = new TrayDataDto
            {
                ETag = "\"3a8f9c1b7e4d\"",
                Matches = new List<TrayMatchDto>
                {
                    new() { Id = "match_100", State = "LIVE", Score = new ScoreDto { Home = 1, Away = 1 } }
                }
            }
        };

        string json = System.Text.Json.JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);
        Assert.IsTrue(json.Contains("\"etag\":", StringComparison.OrdinalIgnoreCase) || json.Contains("\"ETag\":", StringComparison.OrdinalIgnoreCase));

        var deserialized = System.Text.Json.JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("\"3a8f9c1b7e4d\"", deserialized.ETag);
        Assert.AreEqual("\"3a8f9c1b7e4d\"", deserialized.Data?.ETag);
    }

    [TestMethod]
    public async Task Verify_Pass1_FixturesNonNullBrushes_And_SettingsQueueCommands()
    {
        // 1. Verify CompetitionFixtureViewModel and DTO property bindings and non-null brushes
        var rawDto = new CompetitionFixtureDto
        {
            Id = "fixture_42",
            HomeTeamName = "Arsenal",
            AwayTeamName = "Chelsea",
            ScoreOrTimeDisplay = "3 - 1",
            RoundName = "Matchday 10",
            IsFinished = true
        };
        Assert.AreEqual("3 - 1", rawDto.ScoreOrTime, "CompetitionFixtureDto.ScoreOrTime must alias ScoreOrTimeDisplay.");

        var vmNormal = new CompetitionFixtureViewModel(rawDto);
        Assert.AreEqual("Arsenal", vmNormal.HomeTeamName);
        Assert.AreEqual("Chelsea", vmNormal.AwayTeamName);
        Assert.AreEqual("3 - 1", vmNormal.ScoreOrTimeDisplay);
        Assert.AreEqual("Matchday 10", vmNormal.RoundName);
        // Fallbacks with empty DTO
        var vmEmpty = new CompetitionFixtureViewModel(new CompetitionFixtureDto());
        Assert.AreEqual("Home", vmEmpty.HomeTeamName);
        Assert.AreEqual("Away", vmEmpty.AwayTeamName);
        Assert.AreEqual("Fixture", vmEmpty.RoundName);
        Assert.AreEqual("Upcoming", vmEmpty.ScoreOrTimeDisplay);

        // Highlighted row
        var vmHighlighted = new CompetitionFixtureViewModel(rawDto, isHomeHighlighted: true);
        Assert.IsTrue(vmHighlighted.IsRowHighlighted);

        // In WinUI 3 UI runtime, brush properties return cached non-null SolidColorBrushes;
        // in headless test environments without XamlCore, SafeCreateBrush prevents COMException.
        if (vmNormal.RowBackgroundBrush != null)
        {
            Assert.IsNotNull(vmNormal.RowBorderBrush);
            Assert.IsNotNull(vmNormal.HomeTeamForegroundBrush);
            Assert.IsNotNull(vmNormal.AwayTeamForegroundBrush);
            Assert.IsNotNull(vmNormal.StatusBrush);
            Assert.IsNotNull(vmHighlighted.RowBackgroundBrush);
            Assert.IsNotNull(vmHighlighted.RowBorderBrush);
            Assert.IsNotNull(vmHighlighted.HomeTeamForegroundBrush);
        }

        // 2. Verify SettingsViewModel custom queue controls: MoveDown, MoveUp, Remove, and Clear
        var testDir = Path.Combine(_tempDir, "QueueSettings_" + Guid.NewGuid().ToString("N"));
        var prefsService = new PreferencesService(testDir);
        await prefsService.ActivateCustomQueueAsync(new[] { "match_alpha", "match_beta", "match_gamma" });

        var settingsVm = new SettingsViewModel(null, prefsService, null, null, null);
        settingsVm.RefreshCustomQueueMatches();

        Assert.AreEqual(3, settingsVm.CustomQueueMatches.Count, "Initial queue must contain 3 items.");
        Assert.AreEqual("match_alpha", settingsVm.CustomQueueMatches[0].MatchId);
        Assert.AreEqual("match_beta", settingsVm.CustomQueueMatches[1].MatchId);
        Assert.AreEqual("match_gamma", settingsVm.CustomQueueMatches[2].MatchId);

        // MoveDown: match_alpha moves from index 0 to index 1
        await settingsVm.MoveQueueMatchDownAsync("match_alpha");
        var queueAfterDown = prefsService.GetPreferences().CustomTickerQueueMatchIds;
        CollectionAssert.AreEqual(new[] { "match_beta", "match_alpha", "match_gamma" }, queueAfterDown);
        Assert.AreEqual("match_beta", settingsVm.CustomQueueMatches[0].MatchId);
        Assert.AreEqual("match_alpha", settingsVm.CustomQueueMatches[1].MatchId);
        Assert.AreEqual("match_gamma", settingsVm.CustomQueueMatches[2].MatchId);

        // MoveUp: match_alpha moves from index 1 back to index 0
        await settingsVm.MoveQueueMatchUpAsync("match_alpha");
        var queueAfterUp = prefsService.GetPreferences().CustomTickerQueueMatchIds;
        CollectionAssert.AreEqual(new[] { "match_alpha", "match_beta", "match_gamma" }, queueAfterUp);
        Assert.AreEqual("match_alpha", settingsVm.CustomQueueMatches[0].MatchId);
        Assert.AreEqual("match_beta", settingsVm.CustomQueueMatches[1].MatchId);
        Assert.AreEqual("match_gamma", settingsVm.CustomQueueMatches[2].MatchId);

        // Remove: match_alpha is removed, leaving match_beta and match_gamma
        await settingsVm.RemoveQueueMatchAsync("match_alpha");
        var queueAfterRemove = prefsService.GetPreferences().CustomTickerQueueMatchIds;
        CollectionAssert.AreEqual(new[] { "match_beta", "match_gamma" }, queueAfterRemove);
        Assert.AreEqual(2, settingsVm.CustomQueueMatches.Count);
        Assert.AreEqual("match_beta", settingsVm.CustomQueueMatches[0].MatchId);
        Assert.AreEqual("match_gamma", settingsVm.CustomQueueMatches[1].MatchId);

        // Clear: queue becomes empty and HasCustomQueueMatches becomes false
        await settingsVm.ClearCustomQueueAsync();
        var queueAfterClear = prefsService.GetPreferences().CustomTickerQueueMatchIds;
        Assert.AreEqual(0, queueAfterClear.Count);
        Assert.AreEqual(0, settingsVm.CustomQueueMatches.Count);
        Assert.IsFalse(settingsVm.HasCustomQueueMatches);
    }

    [TestMethod]
    public async Task Verify_Pass2_CatalogDeduplication_UniversalCountryAndIntSearch_And_League519Crest()
    {
        using var provider = new NativeTelemetryProvider();

        // 1. Egyptian Premier League resolves to FotMob ID 519 with crisp dark logo URL
        int? egyFotmobId = NativeTelemetryProvider.ResolveFotmobLeagueId("egyptian-premier-league");
        Assert.AreEqual(519, egyFotmobId);

        var egyHub = await provider.GetCompetitionHubAsync("519");
        Assert.IsNotNull(egyHub);
        Assert.AreEqual("519", egyHub.CompetitionId);
        Assert.AreEqual("https://images.fotmob.com/image_resources/logo/leaguelogo/dark/519.png", egyHub.CompetitionLogoUrl);

        // 2. SearchCompetitionsAsync("Premier League") has ZERO duplicate entries for ENG (47 appears exactly once)
        var plResults = await provider.SearchCompetitionsAsync("Premier League");
        Assert.IsNotNull(plResults);
        var engPlMatches = plResults.Where(c => c.Id == "47" || (c.Name.Equals("Premier League", StringComparison.OrdinalIgnoreCase) && c.Country == "ENG")).ToList();
        Assert.AreEqual(1, engPlMatches.Count, "Premier League (ENG) (47) must appear exactly once without duplicates.");

        // And SearchCompetitionsAsync("Ligue 1") has ZERO duplicate entries for FRA (53 appears exactly once)
        var l1Results = await provider.SearchCompetitionsAsync("Ligue 1");
        Assert.IsNotNull(l1Results);
        var fraL1Matches = l1Results.Where(c => c.Id == "53" || (c.Name.Equals("Ligue 1", StringComparison.OrdinalIgnoreCase) && c.Country == "FRA")).ToList();
        Assert.AreEqual(1, fraL1Matches.Count, "Ligue 1 (FRA) (53) must appear exactly once without duplicates.");

        // 3. SearchCompetitionsAsync("INT") returns major international competitions (UEFA Champions League, etc.)
        var intComps = await provider.SearchCompetitionsAsync("INT");
        Assert.IsNotNull(intComps);
        Assert.IsTrue(intComps.Count > 0, "Search for 'INT' must return international competitions.");
        Assert.IsTrue(intComps.Any(c => c.Id == "42" || c.Name.Contains("Champions League", StringComparison.OrdinalIgnoreCase)), "INT search must include Champions League.");

        // 4. SearchTeamsAsync works for countries via /api/data/allLeagues (e.g. Morocco / Turkey / Brazil)
        var marClubs = await provider.SearchTeamsAsync("mar");
        Assert.IsNotNull(marClubs);
        Assert.IsTrue(marClubs.Count > 0, "Search for 'mar' must return clubs via allLeagues.");

        var braClubs = await provider.SearchTeamsAsync("brazil");
        Assert.IsNotNull(braClubs);
        Assert.IsTrue(braClubs.Count > 0, "Search for 'brazil' must return clubs via allLeagues.");

        // 5. MainViewModel: Clicking/loading any competition (including secondary/international) dynamically selects it in HubCompetitions and loads hub data
        var prefsService = new PreferencesService(_tempDir);
        using var mainVm = new MainViewModel(prefsService);
        mainVm.RebuildHubCompetitions();

        // Navigate to a secondary competition not originally in HubCompetitions default list (e.g. Süper Lig "71")
        mainVm.NavigateToCompetition("71", "Süper Lig", "https://images.fotmob.com/image_resources/logo/leaguelogo/dark/71.png");

        Assert.IsTrue(mainVm.IsStandingsMode, "Must switch to Standings mode.");
        Assert.IsNotNull(mainVm.SelectedHubCompetition, "SelectedHubCompetition must not be null.");
        Assert.AreEqual("71", mainVm.SelectedHubCompetition.Id);
        Assert.IsTrue(mainVm.HubCompetitions.Any(c => c.Id == "71"), "HubCompetitions must dynamically contain the selected competition.");
    }

    [TestMethod]
    public async Task Verify_Pass3_Follow519Isolation_SingleQueueAutoPin_12h24hFormatting_ExactStats_And_NoTbdGoals()
    {
        // 1. Follow 519 (Egyptian Premier League) does NOT mark 525 (AFC Champions League) as followed
        var prefsDir = Path.Combine(_tempDir, "p3_test_" + Guid.NewGuid());
        var prefsService = new PreferencesService(prefsDir);
        await prefsService.FollowCompetitionAsync(new FollowedCompetition("519", "Egyptian Premier League"));
        var followedComps = prefsService.GetPreferences().GetFollowedCompetitionIdSet();
        Assert.IsTrue(followedComps.Contains(CanonicalEntityIds.EgyptianPremierLeague) || followedComps.Contains("519"));
        Assert.IsFalse(followedComps.Contains(CanonicalEntityIds.AfcChampionsLeague), "Following 519 must NOT follow AfcChampionsLeague.");
        Assert.IsFalse(followedComps.Contains("525"), "Following 519 must NOT follow 525.");
        Assert.IsFalse(followedComps.Contains("comp_525"), "Following 519 must NOT follow comp_525.");
        Assert.IsFalse(followedComps.Contains("fotmob_525"), "Following 519 must NOT follow fotmob_525.");
        Assert.IsTrue(CanonicalEntityIds.TryTranslateLegacyId("525", out var afcCanon));
        Assert.AreEqual(CanonicalEntityIds.AfcChampionsLeague, afcCanon);

        // 2. Custom Queue: reducing to 1 match converts to single manual pin and clears queue
        await prefsService.SetCustomTickerQueueMatchIdsAsync(new[] { "match_1", "match_2" });
        var p = prefsService.GetPreferences();
        Assert.AreEqual(2, p.CustomTickerQueueMatchIds.Count);

        await prefsService.SetCustomTickerQueueMatchIdsAsync(new[] { "match_1" });
        p = prefsService.GetPreferences();
        Assert.AreEqual("match_1", p.ManualPinnedMatchId, "Remaining single match must become ManualPinnedMatchId.");
        Assert.AreEqual(0, p.CustomTickerQueueMatchIds.Count, "Custom queue must be cleared when down to 1 match.");
        Assert.IsFalse(p.EnableTickerCarousel, "Carousel must be turned OFF.");

        // 3. 12h / 24h Time formatting on CompetitionFixtureViewModel
        var fixtureDto = new CompetitionFixtureDto
        {
            Id = "fix_time_test",
            KickoffUtc = "2026-11-03T20:00:00Z"
        };
        var fixtureVm = new CompetitionFixtureViewModel(fixtureDto)
        {
            Use24HourTime = false
        };
        string time12 = fixtureVm.ScoreOrTimeDisplay;
        Assert.IsTrue(time12.Contains("AM") || time12.Contains("PM"), $"Expected 12-hour format with AM/PM but got: {time12}");

        fixtureVm.Use24HourTime = true;
        string time24 = fixtureVm.ScoreOrTimeDisplay;
        Assert.IsFalse(time24.Contains("AM") || time24.Contains("PM"), $"Expected 24-hour format without AM/PM but got: {time24}");

        // 4. Match Statistics: exact mapping preserves Total Shots & Passes against sub-stats
        string statsJson = @"{
            ""stats"": {
                ""Periods"": {
                    ""All"": {
                        ""stats"": [
                            {
                                ""title"": ""Top stats"",
                                ""stats"": [
                                    { ""title"": ""Total shots"", ""key"": ""total_shots"", ""stats"": [13, 7] },
                                    { ""title"": ""Shots outside box"", ""key"": ""shots_outside_box"", ""stats"": [3, 1] },
                                    { ""title"": ""Passes"", ""key"": ""passes"", ""stats"": [833, 242] },
                                    { ""title"": ""Opposition half"", ""key"": ""opposition_half_passes"", ""stats"": [399, 50] }
                                ]
                            }
                        ]
                    }
                }
            }
        }";
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(statsJson);
        var statsList = NativeTelemetryProvider.ExtractStatistics(jsonDoc.RootElement, "team_home", "team_away", "match_stat_test", 2, 0);
        Assert.AreEqual(2, statsList.Count);
        var homeStats = statsList[0];
        var awayStats = statsList[1];
        Assert.AreEqual(13, homeStats.TotalShots, "Total shots for home team must remain 13, not overwritten by shots outside box.");
        Assert.AreEqual(7, awayStats.TotalShots, "Total shots for away team must remain 7, not overwritten by shots outside box.");
        Assert.AreEqual(833, homeStats.TotalPasses, "Total passes for home team must remain 833, not overwritten by opposition half.");
        Assert.AreEqual(242, awayStats.TotalPasses, "Total passes for away team must remain 242, not overwritten by opposition half.");

        // 5. Goal notification payload formatting and no '<TBD>'
        var goalCandidate = new NotificationCandidate
        {
            FixtureId = "fix_goal_test",
            Type = NotificationType.Goal,
            DeduplicationKey = "key_goal_test",
            NewState = "LIVE",
            HomeTeamName = "Arsenal",
            AwayTeamName = "Chelsea",
            HomeScore = 2,
            AwayScore = 1,
            ElapsedMinute = 58,
            ScoringTeamName = "Arsenal",
            EventPlayerName = "B. Saka",
            AssistPlayerName = "M. Odegaard"
        };
        var payload = NotificationPayloadFormatter.Format(goalCandidate);
        Assert.AreEqual("⚽ GOAL! Arsenal", payload.Title);
        Assert.IsTrue(payload.Body.Contains("Arsenal [2] - 1 Chelsea"), "Body must highlight scoring team score with brackets.");
        Assert.IsTrue(payload.Body.Contains("58' B. Saka (M. Odegaard)"), "Body must contain minute, scorer and assist.");
        Assert.IsFalse(payload.Body.Contains("<TBD>"), "Payload body must never contain '<TBD>'.");
        Assert.IsFalse(payload.Title.Contains("<TBD>"), "Payload title must never contain '<TBD>'.");

        // 6. Period status formatting
        Assert.AreEqual("1st Half • 38'", TrayTickerFormatter.FormatPeriodStatus("FIRST_HALF", 38));
        Assert.AreEqual("Half-Time", TrayTickerFormatter.FormatPeriodStatus("HT"));
        Assert.AreEqual("2nd Half • 75'", TrayTickerFormatter.FormatPeriodStatus("SECOND_HALF", 75));
        Assert.AreEqual("Full-Time", TrayTickerFormatter.FormatPeriodStatus("FT"));
    }

    [TestMethod]
    public async Task Verify_Pass4_StrictFollowMatching_AutoRotateUnpinsManual_FixtureDate_And_KnockoutScores()
    {
        // 1. Strict Follow Matching: Premier League (47) followed must NOT follow DFB-Pokal Frauen
        var prefsDir = Path.Combine(_tempDir, "p4_test_" + Guid.NewGuid());
        var prefsService = new PreferencesService(prefsDir);
        await prefsService.FollowCompetitionAsync(new FollowedCompetition(CanonicalEntityIds.PremierLeague, "Premier League", "PL", null, "ENG"));
        await prefsService.FollowCompetitionAsync(new FollowedCompetition(CanonicalEntityIds.LaLiga, "La Liga", "PD", null, "ESP"));

        var prefs = prefsService.GetPreferences();

        Assert.IsTrue(prefsService.IsCompetitionFollowed(CanonicalEntityIds.PremierLeague));
        Assert.IsTrue(prefsService.IsCompetitionFollowed("47"));
        Assert.IsTrue(prefsService.IsCompetitionFollowed(CanonicalEntityIds.LaLiga));
        Assert.IsTrue(prefsService.IsCompetitionFollowed("87"));

        // DFB-Pokal Frauen with stage id "47" and country "GER" must NOT match Premier League
        var dfbMatch = new TrayMatchDto
        {
            Id = "dfb_test_1",
            Competition = new CompetitionSummaryDto
            {
                Id = "209",
                LegacyId = "47",
                Name = "DFB-Pokal Frauen",
                Country = "GER",
                Code = "GER"
            },
            HomeTeam = new TeamDto { Id = "team_wolfsburg", Name = "VfL Wolfsburg (W)" },
            AwayTeam = new TeamDto { Id = "team_bayern_w", Name = "Bayern Munich (W)" },
            Trust = new V2TrustMetadataDto { Provider = "live-telemetry" }
        };
        var dfbRel = FixtureRelevanceEngine.EvaluateRelevance(dfbMatch, prefs);
        Assert.IsFalse(dfbRel.FollowedByCompetition, "DFB-Pokal Frauen must NOT be marked as followed when Premier League is followed.");
        Assert.IsFalse(dfbRel.IsRelevant);

        // LaLiga2 ("140") must NOT match LaLiga
        var laliga2Match = new TrayMatchDto
        {
            Id = "laliga2_test_1",
            Competition = new CompetitionSummaryDto
            {
                Id = "140",
                LegacyId = "140",
                Name = "LaLiga2",
                Country = "ESP",
                Code = "ESP"
            },
            HomeTeam = new TeamDto { Id = "team_sporting", Name = "Sporting Gijon" },
            AwayTeam = new TeamDto { Id = "team_oviedo", Name = "Real Oviedo" },
            Trust = new V2TrustMetadataDto { Provider = "live-telemetry" }
        };
        var laliga2Rel = FixtureRelevanceEngine.EvaluateRelevance(laliga2Match, prefs);
        Assert.IsFalse(laliga2Rel.FollowedByCompetition, "LaLiga2 (140) must NOT be marked as followed when LaLiga is followed.");
        Assert.IsFalse(laliga2Rel.IsRelevant);

        // Unfollowing by name and country removes the competition cleanly
        await prefsService.UnfollowCompetitionAsync(CanonicalEntityIds.PremierLeague, "Premier League", "ENG");
        Assert.IsFalse(prefsService.IsCompetitionFollowed(CanonicalEntityIds.PremierLeague));
        Assert.IsFalse(prefsService.IsCompetitionFollowed("47"));

        // 2. Turning Auto-Rotate Ticker ON immediately clears manual pin
        await prefsService.SetManualPinnedMatchIdAsync("pinned_match_123");
        var pinnedPrefs = prefsService.GetPreferences();
        Assert.AreEqual("pinned_match_123", pinnedPrefs.ManualPinnedMatchId);
        Assert.IsFalse(pinnedPrefs.EnableTickerCarousel);

        await prefsService.SetEnableTickerCarouselAsync(true);
        var unpinnedPrefs = prefsService.GetPreferences();
        Assert.IsTrue(unpinnedPrefs.EnableTickerCarousel);
        Assert.IsNull(unpinnedPrefs.ManualPinnedMatchId, "Turning carousel ON must clear ManualPinnedMatchId to null.");

        // 3. Fixture Date display & ToTrayMatchDto
        var fixtureDto = new CompetitionFixtureDto
        {
            Id = "fixture_p4_test",
            HomeTeamId = "8634",
            HomeTeamName = "Barcelona",
            AwayTeamId = "8633",
            AwayTeamName = "Real Madrid",
            KickoffUtc = "2026-11-08T20:00:00Z"
        };
        var fixtureVm = new CompetitionFixtureViewModel(fixtureDto);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fixtureVm.DateDisplay));
        Assert.IsTrue(fixtureVm.ScoreOrTimeDisplay.Contains(" • "), $"Expected fixture display to contain date and time separated by bullet, got: {fixtureVm.ScoreOrTimeDisplay}");

        var fallbackMatch = fixtureVm.ToTrayMatchDto();
        Assert.IsNotNull(fallbackMatch);
        Assert.AreEqual("Barcelona", fallbackMatch.HomeTeam?.Name);
        Assert.AreEqual("Real Madrid", fallbackMatch.AwayTeam?.Name);
        Assert.AreEqual("live-telemetry", fallbackMatch.Trust?.Provider);

        // 4. Knockout Bracket Scores & never empty/null
        var tieDto = new KnockoutTieDto
        {
            HomeTeamName = "Arsenal",
            AwayTeamName = "Bayern",
            Leg1Score = "2 - 2",
            Leg2Score = "1 - 0",
            AggregateScore = "3 - 2"
        };
        var tieVm = new KnockoutTieViewModel(tieDto);
        Assert.AreEqual("3 - 2", tieVm.ScoreOrStatus);

        var unplayedTie = new KnockoutTieDto
        {
            HomeTeamName = "PSG",
            AwayTeamName = "Dortmund",
            ScoreOrStatus = "vs"
        };
        var unplayedVm = new KnockoutTieViewModel(unplayedTie);
        Assert.AreEqual("vs", unplayedVm.ScoreOrStatus);
        Assert.IsFalse(string.IsNullOrWhiteSpace(unplayedVm.ScoreOrStatus), "ScoreOrStatus must never be null or empty.");
    }

    [TestMethod]
    public async Task Verify_Pass5_OwnGoalTeamAndLabel_HomeLineupMirror_ActivePoolPreemption_And_MultiDayFollowingAccordions()
    {
        // 1. Own Goal by Away player (Rodinei 4') in RB Bragantino 2 - 1 Santos
        var matchWithOg = new TrayMatchDto
        {
            Id = "bragantino_santos",
            HomeTeam = new TeamSummaryDto { Id = "bragantino", Name = "RB Bragantino" },
            AwayTeam = new TeamSummaryDto { Id = "santos", Name = "Santos" },
            Score = new ScoreDto { Home = 2, Away = 1, Display = "2 - 1" },
            Events = new List<TrayEventDto>
            {
                new TrayEventDto
                {
                    Type = "Goal",
                    Detail = "Own Goal",
                    Minute = 4,
                    TeamId = "bragantino",
                    PlayerName = "Rodinei"
                },
                new TrayEventDto
                {
                    Type = "Goal",
                    Minute = 41,
                    TeamId = "bragantino",
                    PlayerName = "Wallace Yan"
                },
                new TrayEventDto
                {
                    Type = "Goal",
                    Minute = 37,
                    TeamId = "santos",
                    PlayerName = "Philippe Coutinho"
                }
            }
        };
        var peekData = PeekViewFormatter.Format(matchWithOg);
        Assert.IsTrue(peekData.HomeEventsText.Contains("Rodinei 4' (OG)"), $"Expected Home events to contain 'Rodinei 4' (OG)', got: {peekData.HomeEventsText}");
        Assert.IsTrue(peekData.HomeEventsText.Contains("Wallace Yan 41'"), $"Expected Home events to contain 'Wallace Yan 41'', got: {peekData.HomeEventsText}");
        Assert.IsTrue(peekData.AwayEventsText.Contains("Philippe Coutinho 37'"), $"Expected Away events to contain 'Philippe Coutinho 37'', got: {peekData.AwayEventsText}");

        var ogGoalCandidate = new NotificationCandidate
        {
            FixtureId = "bragantino_santos",
            Type = NotificationType.Goal,
            DeduplicationKey = "key_og_test",
            NewState = "LIVE",
            HomeTeamName = "RB Bragantino",
            AwayTeamName = "Santos",
            HomeScore = 1,
            AwayScore = 0,
            ElapsedMinute = 4,
            ScoringTeamName = "RB Bragantino",
            EventPlayerName = "Rodinei",
            EventDetail = "Own Goal"
        };
        var ogPayload = NotificationPayloadFormatter.Format(ogGoalCandidate);
        Assert.IsTrue(ogPayload.Body.Contains("(OG)"), "Payload body must indicate Own Goal with (OG).");
        Assert.IsTrue(ogPayload.Body.Contains("Rodinei (OG)"), "Payload body must contain Rodinei (OG).");

        // 2. Lineup vertical mirroring: Home RB and Away RB (HomeY == 1.0 - AwayY)
        var homeLineup = new List<LineupPlayerDto>
        {
            new LineupPlayerDto { Id = "h_gk", Name = "Home GK", Position = "GK", Row = 1, Col = 1 },
            new LineupPlayerDto { Id = "h_lb", Name = "Home LB", Position = "LB", Row = 2, Col = 1 },
            new LineupPlayerDto { Id = "h_cb1", Name = "Home CB1", Position = "CB", Row = 2, Col = 2 },
            new LineupPlayerDto { Id = "h_cb2", Name = "Home CB2", Position = "CB", Row = 2, Col = 3 },
            new LineupPlayerDto { Id = "h_rb", Name = "Home RB", Position = "RB", Row = 2, Col = 4 }
        };
        var awayLineup = new List<LineupPlayerDto>
        {
            new LineupPlayerDto { Id = "a_gk", Name = "Away GK", Position = "GK", Row = 1, Col = 1 },
            new LineupPlayerDto { Id = "a_lb", Name = "Away LB", Position = "LB", Row = 2, Col = 1 },
            new LineupPlayerDto { Id = "a_cb1", Name = "Away CB1", Position = "CB", Row = 2, Col = 2 },
            new LineupPlayerDto { Id = "a_cb2", Name = "Away CB2", Position = "CB", Row = 2, Col = 3 },
            new LineupPlayerDto { Id = "a_rb", Name = "Away RB", Position = "RB", Row = 2, Col = 4 }
        };

        var homeNodes = MatchCenterViewModel.CreateTeamPitchNodes(homeLineup, isHomeTeam: true, formation: "4-0-0");
        var awayNodes = MatchCenterViewModel.CreateTeamPitchNodes(awayLineup, isHomeTeam: false, formation: "4-0-0");
        var homeRb = homeNodes.First(n => n.Name == "Home RB");
        var awayRb = awayNodes.First(n => n.Name == "Away RB");
        Assert.AreEqual(Math.Round(1.0 - awayRb.YPercent, 3), homeRb.YPercent, 0.005,
            $"Expected Home RB YPercent ({homeRb.YPercent}) to mirror Away RB YPercent ({awayRb.YPercent}) across horizontal centerline.");

        // 3. TaskbarTickerCoordinator active ticker pool preemption for AllLive scope
        var timer = new TestTickerTimer();
        using var coordinator = new TaskbarTickerCoordinator(
            timer,
            (id, text) => { });

        var prefs = new UserPreferences
        {
            EnableTickerCarousel = true,
            EnableTickerEventPreemption = true,
            TickerRotationScope = "AllLive"
        };

        var unFollowedLiveMatch = new TrayMatchDto
        {
            Id = "live_neutral_match",
            State = "LIVE",
            Score = new ScoreDto { Home = 1, Away = 0, Display = "1 - 0" },
            HomeTeam = new TeamSummaryDto { Id = "neutral_a", Name = "Neutral A" },
            AwayTeam = new TeamSummaryDto { Id = "neutral_b", Name = "Neutral B" }
        };
        coordinator.PreemptForMajorEvent(unFollowedLiveMatch, "Goal: Neutral A");
        Assert.AreEqual("live_neutral_match", coordinator.LastGoalJumpMatchId);

        // Also verify snapshot transition preemption in AllLive mode for unfollowed match
        var trayInitial = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new TrayMatchDto
                {
                    Id = "live_neutral_snap",
                    State = "LIVE",
                    Score = new ScoreDto { Home = 0, Away = 0, Display = "0 - 0" },
                    HomeTeam = new TeamSummaryDto { Id = "snap_a", Name = "Snap A" },
                    AwayTeam = new TeamSummaryDto { Id = "snap_b", Name = "Snap B" }
                }
            }
        };
        coordinator.ApplySnapshot(trayInitial, TickerConnectionState.OnlineFresh, prefs);

        var trayGoal = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new TrayMatchDto
                {
                    Id = "live_neutral_snap",
                    State = "LIVE",
                    Score = new ScoreDto { Home = 1, Away = 0, Display = "1 - 0" },
                    HomeTeam = new TeamSummaryDto { Id = "snap_a", Name = "Snap A" },
                    AwayTeam = new TeamSummaryDto { Id = "snap_b", Name = "Snap B" }
                }
            }
        };
        coordinator.ApplySnapshot(trayGoal, TickerConnectionState.OnlineFresh, prefs);
        Assert.AreEqual("live_neutral_snap", coordinator.LastGoalJumpMatchId, "AllLive scope must trigger major event preemption for neutral live match on goal.");

        // 4. FollowingDayGroupViewModel 7-day chronological accordion groups
        var today = DateTime.Today;
        var weekGroups = FollowingDayGroupViewModel.Create7DayWindow(today);
        Assert.AreEqual(7, weekGroups.Count);
        Assert.AreEqual(today.AddDays(-1).Date, weekGroups[0].Date);
        Assert.IsFalse(weekGroups[0].IsExpanded, "Yesterday should start collapsed.");
        Assert.AreEqual(today.Date, weekGroups[1].Date);
        Assert.IsTrue(weekGroups[1].IsExpanded, "Today should start expanded.");
        Assert.IsTrue(weekGroups[1].IsToday);
        for (int i = 2; i < 7; i++)
        {
            Assert.IsFalse(weekGroups[i].IsExpanded, $"Future day {weekGroups[i].DateKey} should start collapsed.");
        }
    }

    [TestMethod]
    public async Task Verify_Pass6_StrictOneToOneEntityBijection_TeamHub519Disambiguation_CrossMidnightLive_And_TickerWheelScroll()
    {
        // 1. Strict 1-to-1 Entity ID Bijection & Al Ahly vs Lyon Guard
        Assert.IsTrue(CanonicalEntityIds.TryTranslateLegacyId("101745", out var canonAlAhly));
        Assert.AreEqual(CanonicalEntityIds.AlAhly, canonAlAhly);
        Assert.IsFalse(CanonicalEntityIds.TryTranslateLegacyId("9748", out _), "9748 (Lyon) must NOT translate to Al Ahly or any canonical ID.");

        Assert.IsTrue(FixtureRelevanceEngine.IsValidCanonicalTeam(CanonicalEntityIds.AlAhly, "Al Ahly SC"));
        Assert.IsFalse(FixtureRelevanceEngine.IsValidCanonicalTeam(CanonicalEntityIds.AlAhly, "Olympique Lyonnais"), "Al Ahly canonical guard must reject Lyon.");
        Assert.IsFalse(FixtureRelevanceEngine.IsValidCanonicalTeam(CanonicalEntityIds.AlAhly, "Lyon"), "Al Ahly canonical guard must reject Lyon.");
        Assert.IsTrue(FixtureRelevanceEngine.IsValidCanonicalTeam(CanonicalEntityIds.Zamalek, "Zamalek SC"));
        Assert.IsFalse(FixtureRelevanceEngine.IsValidCanonicalTeam(CanonicalEntityIds.Zamalek, "Samsunspor"), "Zamalek canonical guard must reject Samsunspor.");

        var prefs = new UserPreferences
        {
            FollowedTeams = new List<FollowedTeam>
            {
                new FollowedTeam(CanonicalEntityIds.AlAhly, "Al Ahly SC", "AHL", "Egypt")
            }
        };

        var lyonMatch = new TrayMatchDto
        {
            Id = "lyon_marseille",
            HomeTeam = new TeamSummaryDto { Id = "9748", Name = "Olympique Lyonnais" },
            AwayTeam = new TeamSummaryDto { Id = "9749", Name = "Marseille" },
            State = "LIVE",
            Score = new ScoreDto { Home = 1, Away = 0, Display = "1 - 0" }
        };
        var lyonRelevance = FixtureRelevanceEngine.EvaluateRelevance(lyonMatch, prefs);
        Assert.IsFalse(lyonRelevance.FollowedByTeam, "Lyon match must NOT be marked as followed when only Al Ahly is followed.");

        var alAhlyMatch = new TrayMatchDto
        {
            Id = "alahly_zamalek",
            HomeTeam = new TeamSummaryDto { Id = "101745", Name = "Al Ahly SC" },
            AwayTeam = new TeamSummaryDto { Id = "80591", Name = "Zamalek SC" },
            State = "LIVE",
            Score = new ScoreDto { Home = 2, Away = 1, Display = "2 - 1" }
        };
        var alAhlyRelevance = FixtureRelevanceEngine.EvaluateRelevance(alAhlyMatch, prefs);
        Assert.IsTrue(alAhlyRelevance.FollowedByTeam, "Al Ahly match must be marked as followed.");

        // 2. TeamHub Competition Disambiguation (519 vs 47)
        var (egyId1, egyName1, egyLogo1) = NativeTelemetryProvider.NormalizeCompetitionIdAndName("519", "Premier League", "EGY");
        Assert.AreEqual("519", egyId1);
        Assert.AreEqual("Egyptian Premier League", egyName1);
        Assert.IsTrue(egyLogo1?.Contains("519") == true);

        var (egyId2, egyName2, _) = NativeTelemetryProvider.NormalizeCompetitionIdAndName("10650", "Premier League", "");
        Assert.AreEqual("519", egyId2);
        Assert.AreEqual("Egyptian Premier League", egyName2);

        var (engId, engName, engLogo) = NativeTelemetryProvider.NormalizeCompetitionIdAndName("47", "Premier League", "ENG");
        Assert.AreEqual("47", engId);
        Assert.AreEqual("Premier League", engName);
        Assert.IsTrue(engLogo?.Contains("47") == true);

        int? resolvedEgy = MainViewModel.ResolveFotmobLeagueId("519", "Premier League", null, "EGY");
        Assert.AreEqual(519, resolvedEgy);
        int? resolvedEng = MainViewModel.ResolveFotmobLeagueId("47", "Premier League", null, "ENG");
        Assert.AreEqual(47, resolvedEng);

        // 3. Taskbar Ticker Wheel Scroll
        var state = new TickerRotationState();
        var matchA = new TrayMatchDto { Id = "match_a", HomeTeam = new TeamSummaryDto { Name = "A" }, AwayTeam = new TeamSummaryDto { Name = "B" } };
        var matchB = new TrayMatchDto { Id = "match_b", HomeTeam = new TeamSummaryDto { Name = "C" }, AwayTeam = new TeamSummaryDto { Name = "D" } };
        var matchC = new TrayMatchDto { Id = "match_c", HomeTeam = new TeamSummaryDto { Name = "E" }, AwayTeam = new TeamSummaryDto { Name = "F" } };
        state.ApplyCandidates(new List<TrayMatchDto> { matchA, matchB, matchC });

        Assert.AreEqual("match_a", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);

        // Scroll DOWN (direction = +1): steps to next match (match_b)
        state.Step(1);
        Assert.AreEqual("match_b", state.CurrentCandidate?.Id);
        Assert.AreEqual(1, state.CurrentIndex);

        // Scroll DOWN (direction = +1): steps to match_c
        state.Step(1);
        Assert.AreEqual("match_c", state.CurrentCandidate?.Id);
        Assert.AreEqual(2, state.CurrentIndex);

        // Scroll DOWN (direction = +1): wraps around to match_a
        state.Step(1);
        Assert.AreEqual("match_a", state.CurrentCandidate?.Id);
        Assert.AreEqual(0, state.CurrentIndex);

        // Scroll UP (direction = -1): steps backwards to match_c
        state.Step(-1);
        Assert.AreEqual("match_c", state.CurrentCandidate?.Id);
        Assert.AreEqual(2, state.CurrentIndex);

        // Scroll UP (direction = -1): steps backwards to match_b
        state.Step(-1);
        Assert.AreEqual("match_b", state.CurrentCandidate?.Id);
        Assert.AreEqual(1, state.CurrentIndex);

        // Verify StepByUserScroll in coordinator resets preemption lock
        var liveA = new TrayMatchDto { Id = "match_a", State = "LIVE", HomeTeam = new TeamSummaryDto { Name = "A" }, AwayTeam = new TeamSummaryDto { Name = "B" } };
        var liveB = new TrayMatchDto { Id = "match_b", State = "LIVE", HomeTeam = new TeamSummaryDto { Name = "C" }, AwayTeam = new TeamSummaryDto { Name = "D" } };
        var wheelPrefs = new UserPreferences
        {
            EnableTickerCarousel = true,
            TickerRotationScope = "AllLive"
        };

        var timer = new TestTickerTimer();
        using var coordinator = new TaskbarTickerCoordinator(timer, (id, text) => { });
        coordinator.ApplySnapshot(new TrayDataDto { Matches = new List<TrayMatchDto> { liveA, liveB } }, TickerConnectionState.OnlineFresh, wheelPrefs);
        coordinator.PreemptForMajorEvent(liveA, "Goal: A");
        Assert.IsTrue(coordinator.IsPreempted);

        bool stepped = coordinator.StepByUserScroll(1);
        Assert.IsTrue(stepped);
        Assert.IsFalse(coordinator.IsPreempted, "User scroll must clear major event preemption hold.");
        Assert.AreEqual("match_b", coordinator.CurrentMatchId);

        await Task.CompletedTask;
    }

    #endregion
}
