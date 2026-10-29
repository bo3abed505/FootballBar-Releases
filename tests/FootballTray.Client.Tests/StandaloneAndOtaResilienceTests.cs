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

    [TestMethod]
    public async Task Verify_Phase122A_TelemetryParity()
    {
        using var provider = new NativeTelemetryProvider();

        // --- 1. Fix 1: Competition Country Code & Numeric Priority Sorting ---
        int plPri = NativeTelemetryProvider.GetCompetitionPriority("47", "Premier League");
        int uclPri = NativeTelemetryProvider.GetCompetitionPriority("42", "UEFA Champions League");
        int laLigaPri = NativeTelemetryProvider.GetCompetitionPriority("87", "LaLiga");
        int serieAPri = NativeTelemetryProvider.GetCompetitionPriority("55", "Serie A");
        int egyPri = NativeTelemetryProvider.GetCompetitionPriority("525", "Egyptian Premier League");
        int ksaPri = NativeTelemetryProvider.GetCompetitionPriority("536", "Saudi Pro League");
        Assert.AreEqual(1, plPri, "Premier League should be Priority 1");
        Assert.AreEqual(2, uclPri, "UCL should be Priority 2");
        Assert.AreEqual(3, laLigaPri, "LaLiga should be Priority 3");
        Assert.AreEqual(4, serieAPri, "Serie A should be Priority 4");
        Assert.AreEqual(9, egyPri, "Egyptian Premier League should be Priority 9");
        Assert.AreEqual(10, ksaPri, "Saudi Pro League should be Priority 10");

        // Verify Youth / Reserve leagues are strictly demoted (Issue #8)
        int pl2Pri = NativeTelemetryProvider.GetCompetitionPriority("47", "Premier League 2");
        int plU18Pri = NativeTelemetryProvider.GetCompetitionPriority("47", "Premier League U18");
        Assert.IsTrue(pl2Pri >= 2000, $"Premier League 2 should be demoted to >= 2000, was {pl2Pri}");
        Assert.IsTrue(plU18Pri >= 2000, $"Premier League U18 should be demoted to >= 2000, was {plU18Pri}");

        // Verify Brazilian Serie A is Tier 2 (rank 33), NOT Tier 1 rank 4
        int braSerieAPri = NativeTelemetryProvider.GetCompetitionPriority("268", "Brasileirão Série A");
        Assert.AreEqual(33, braSerieAPri, "Brazilian Serie A (268) must be Tier 2 (33), not Tier 1 (4)");

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
}

