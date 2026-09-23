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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class ClientV2MigrationTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ClientV2MigrationTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task FootballApiClient_GetTrayAsync_CallsV2Endpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                capturedRequest = req;
                var envelope = new ApiResponseEnvelope<TrayDataDto>
                {
                    SchemaVersion = "2.0",
                    RequestId = "req_v2_1",
                    Version = 1,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Data = new TrayDataDto
                    {
                        Matches = new List<TrayMatchDto>()
                    }
                };
                var json = JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new FootballApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        var result = await client.GetTrayAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual("/api/v2/tray", capturedRequest.RequestUri?.AbsolutePath);
    }

    [TestMethod]
    public async Task FootballApiClient_GetMatchDetailsAsync_CallsV2Endpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new MockHttpMessageHandler
        {
            ResponseFactory = req =>
            {
                capturedRequest = req;
                var envelope = new ApiResponseEnvelope<MatchDetailDto>
                {
                    SchemaVersion = "2.0",
                    RequestId = "req_v2_match",
                    Version = 1,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    ServerTime = DateTimeOffset.UtcNow,
                    Data = new MatchDetailDto
                    {
                        Id = "ft_fix_test_123",
                        LegacyId = "fix_test_123",
                        State = "LIVE",
                        HomeTeam = new TeamSummaryDto { Id = "ft_team_barca", LegacyId = "team_529", Name = "FC Barcelona" },
                        AwayTeam = new TeamSummaryDto { Id = "ft_team_madrid", LegacyId = "team_541", Name = "Real Madrid" },
                        Trust = new V2TrustMetadataDto
                        {
                            FreshnessState = "FRESH",
                            DataAgeSeconds = 4,
                            Provider = "api-football"
                        }
                    }
                };
                var json = JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        using var client = new FootballApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        var result = await client.GetMatchDetailsAsync("ft_fix_test_123");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(capturedRequest);
        Assert.AreEqual("/api/v2/match/ft_fix_test_123", capturedRequest.RequestUri?.AbsolutePath);
        Assert.AreEqual("ft_fix_test_123", result.Envelope?.Data?.Id);
        Assert.AreEqual("fix_test_123", result.Envelope?.Data?.LegacyId);
        Assert.AreEqual("FRESH", result.Envelope?.Data?.Trust?.FreshnessState);
        Assert.AreEqual(4, result.Envelope?.Data?.Trust?.DataAgeSeconds);
    }

    [TestMethod]
    public void ApiContractDtos_DeserializesV2Envelope_WithTrustAndLegacyIds()
    {
        var json = """
        {
            "schemaVersion": "2.0",
            "requestId": "req_canon_1",
            "version": 1,
            "generatedAt": "2026-09-23T00:00:00Z",
            "serverTime": "2026-09-23T00:00:01Z",
            "stale": false,
            "dataAgeSeconds": 3,
            "data": {
                "matches": [
                    {
                        "id": "ft_fix_01M33DKNF0123456789ABCDEF0",
                        "legacyId": "fix_99901",
                        "competition": {
                            "id": "ft_comp_01M33DKNC8CKGMJ9Z7898AH8HA",
                            "legacyId": "comp_140",
                            "name": "La Liga",
                            "code": "ESP_L1"
                        },
                        "homeTeam": {
                            "id": "ft_team_01M33DKNCTXK0YKJNJDDC15GS8",
                            "legacyId": "team_529",
                            "name": "FC Barcelona",
                            "shortName": "BAR"
                        },
                        "awayTeam": {
                            "id": "ft_team_01M33DKNCP7HVGD8FVFYW5YNAK",
                            "legacyId": "team_1029",
                            "name": "Al Ahly SC",
                            "shortName": "AHL"
                        },
                        "state": "LIVE",
                        "elapsedMinute": 42,
                        "extraMinute": 0,
                        "score": {
                            "home": 2,
                            "away": 1,
                            "display": "2 - 1"
                        },
                        "kickoffTime": "2026-09-23T20:00:00Z",
                        "recentEvents": [],
                        "trust": {
                            "freshnessState": "FRESH",
                            "sourceObservedAt": "2026-09-23T00:00:00Z",
                            "dataAgeSeconds": 2,
                            "provider": "api-football"
                        }
                    }
                ]
            }
        }
        """;

        var envelope = JsonSerializer.Deserialize(json, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);

        Assert.IsNotNull(envelope);
        Assert.AreEqual("2.0", envelope.SchemaVersion);
        Assert.IsNotNull(envelope.Data);
        Assert.AreEqual(1, envelope.Data.Matches.Count);

        var match = envelope.Data.Matches[0];
        Assert.AreEqual("ft_fix_01M33DKNF0123456789ABCDEF0", match.Id);
        Assert.AreEqual("fix_99901", match.LegacyId);
        Assert.AreEqual(CanonicalEntityIds.LaLiga, match.Competition?.Id);
        Assert.AreEqual("comp_140", match.Competition?.LegacyId);
        Assert.AreEqual(CanonicalEntityIds.Barcelona, match.HomeTeam?.Id);
        Assert.AreEqual("team_529", match.HomeTeam?.LegacyId);
        Assert.AreEqual(CanonicalEntityIds.AlAhly, match.AwayTeam?.Id);
        Assert.AreEqual("team_1029", match.AwayTeam?.LegacyId);
        Assert.IsNotNull(match.Trust);
        Assert.AreEqual("FRESH", match.Trust.FreshnessState);
        Assert.AreEqual(2, match.Trust.DataAgeSeconds);
        Assert.AreEqual("api-football", match.Trust.Provider);
    }

    [TestMethod]
    public async Task PreferencesService_MigratesV1Preferences_ToCanonicalV2_AndCreatesBackup()
    {
        var prefPath = Path.Combine(_tempDir, "preferences.json");
        var backupPath = Path.Combine(_tempDir, "preferences.json.bak");

        var v1Json = """
        {
            "preferencesVersion": 1,
            "followedTeams": [
                { "id": "team_529", "name": "FC Barcelona", "shortName": "BAR", "country": "Spain" },
                { "id": "team_1029", "name": "Al Ahly SC", "shortName": "AHL", "country": "Egypt" }
            ],
            "followedCompetitions": [
                { "id": "comp_39", "name": "Premier League", "code": "PL", "country": "England" },
                { "id": "comp_140", "name": "La Liga", "code": "ESP_L1", "country": "Spain" }
            ],
            "notifications": {
                "globalEnabled": true,
                "enabledTypes": [ 0, 1 ]
            }
        }
        """;
        await File.WriteAllTextAsync(prefPath, v1Json);

        var prefService = new PreferencesService(_tempDir);
        var loaded = await prefService.LoadAsync();

        // 1. Assert schema migration
        Assert.AreEqual(2, loaded.PreferencesVersion);

        // 2. Assert translated canonical IDs
        Assert.AreEqual(2, loaded.FollowedTeams.Count);
        Assert.AreEqual(CanonicalEntityIds.Barcelona, loaded.FollowedTeams[0].Id);
        Assert.AreEqual(CanonicalEntityIds.AlAhly, loaded.FollowedTeams[1].Id);

        Assert.AreEqual(2, loaded.FollowedCompetitions.Count);
        Assert.AreEqual(CanonicalEntityIds.PremierLeague, loaded.FollowedCompetitions[0].Id);
        Assert.AreEqual(CanonicalEntityIds.LaLiga, loaded.FollowedCompetitions[1].Id);

        // 3. Assert backup file created with original content
        Assert.IsTrue(File.Exists(backupPath), "Backup file preferences.json.bak must exist");
        var backupContent = await File.ReadAllTextAsync(backupPath);
        Assert.IsTrue(backupContent.Contains("team_529"));
        Assert.IsTrue(backupContent.Contains("comp_39"));

        // 4. Assert migration persisted to primary preferences.json
        var persistedJson = await File.ReadAllTextAsync(prefPath);
        Assert.IsTrue(persistedJson.Contains(CanonicalEntityIds.Barcelona));
        Assert.IsTrue(persistedJson.Contains(CanonicalEntityIds.PremierLeague));
    }

    [TestMethod]
    public async Task PreferencesService_PreservesUnknownLegacyIds_WithProvisionalMarker()
    {
        var prefPath = Path.Combine(_tempDir, "preferences.json");
        var v1Json = """
        {
            "preferencesVersion": 1,
            "followedTeams": [
                { "id": "team_9999", "name": "Custom Team 9999", "shortName": "CT", "country": "Nowhere" }
            ],
            "followedCompetitions": [
                { "id": "comp_8888", "name": "Custom Cup 8888", "code": "CC", "country": "World" }
            ]
        }
        """;
        await File.WriteAllTextAsync(prefPath, v1Json);

        var prefService = new PreferencesService(_tempDir);
        var loaded = await prefService.LoadAsync();

        Assert.AreEqual(2, loaded.PreferencesVersion);
        Assert.AreEqual("provisional:team_9999", loaded.FollowedTeams[0].Id);
        Assert.AreEqual("provisional:comp_8888", loaded.FollowedCompetitions[0].Id);

        // Lookup sets should match both with and without provisional: prefix
        var teamSet = loaded.GetFollowedTeamIdSet();
        Assert.IsTrue(teamSet.Contains("provisional:team_9999"));
        Assert.IsTrue(teamSet.Contains("team_9999"));

        var compSet = loaded.GetFollowedCompetitionIdSet();
        Assert.IsTrue(compSet.Contains("provisional:comp_8888"));
        Assert.IsTrue(compSet.Contains("comp_8888"));
    }

    [TestMethod]
    public async Task PreferencesService_MigrationIsIdempotent()
    {
        var prefService = new PreferencesService(_tempDir);
        var initial = await prefService.LoadAsync();
        Assert.AreEqual(2, initial.PreferencesVersion);

        // Modify followed team to a canonical ID
        var updated = initial with
        {
            FollowedTeams = new List<FollowedTeam>
            {
                new(CanonicalEntityIds.Barcelona, "FC Barcelona", "BAR", "Spain")
            }
        };
        await prefService.SetPreferencesAsync(updated);

        // Recreate service instance and reload
        var reloadedService = new PreferencesService(_tempDir);
        var reloaded = await reloadedService.LoadAsync();

        Assert.AreEqual(2, reloaded.PreferencesVersion);
        Assert.AreEqual(1, reloaded.FollowedTeams.Count);
        Assert.AreEqual(CanonicalEntityIds.Barcelona, reloaded.FollowedTeams[0].Id);
    }

    [TestMethod]
    public async Task PreferencesService_RecoversFromBackup_WhenPrimaryIsCorrupted()
    {
        var prefPath = Path.Combine(_tempDir, "preferences.json");
        var backupPath = Path.Combine(_tempDir, "preferences.json.bak");

        // Valid backup with canonical preferences
        var backupJson = $$"""
        {
            "preferencesVersion": 2,
            "followedTeams": [
                { "id": "{{CanonicalEntityIds.Barcelona}}", "name": "FC Barcelona", "shortName": "BAR", "country": "Spain" }
            ],
            "followedCompetitions": [
                { "id": "{{CanonicalEntityIds.PremierLeague}}", "name": "Premier League", "code": "PL", "country": "England" }
            ]
        }
        """;
        await File.WriteAllTextAsync(backupPath, backupJson);

        // Corrupted primary file
        await File.WriteAllTextAsync(prefPath, "{ MALFORMED JSON !!!@#$%^");

        var prefService = new PreferencesService(_tempDir);
        var loaded = await prefService.LoadAsync();

        Assert.AreEqual(2, loaded.PreferencesVersion);
        Assert.AreEqual(1, loaded.FollowedTeams.Count);
        Assert.AreEqual(CanonicalEntityIds.Barcelona, loaded.FollowedTeams[0].Id);
        Assert.AreEqual(CanonicalEntityIds.PremierLeague, loaded.FollowedCompetitions[0].Id);

        // Primary should have been restored atomically
        var restoredContent = await File.ReadAllTextAsync(prefPath);
        Assert.IsTrue(restoredContent.Contains(CanonicalEntityIds.Barcelona));
    }

    [TestMethod]
    public void FixtureRelevanceEngine_MatchesProvisionalLegacyIds_AgainstV2CanonicalMatch()
    {
        // User has a provisional legacy follow
        var preferences = new UserPreferences
        {
            PreferencesVersion = 2,
            FollowedTeams = new List<FollowedTeam>
            {
                new("provisional:team_9999", "Custom FC", "CST", "Nowhere")
            },
            FollowedCompetitions = new List<FollowedCompetition>
            {
                new("provisional:comp_8888", "Custom League", "CL", "Nowhere")
            }
        };

        // Incoming v2 match has canonical IDs but includes legacyId
        var v2Match = new TrayMatchDto
        {
            Id = "ft_fix_canonical_1",
            LegacyId = "fix_legacy_1",
            HomeTeam = new TeamSummaryDto
            {
                Id = "ft_team_canonical_9999",
                LegacyId = "team_9999",
                Name = "Custom FC"
            },
            AwayTeam = new TeamSummaryDto
            {
                Id = "ft_team_canonical_1234",
                LegacyId = "team_1234",
                Name = "Other FC"
            },
            Competition = new CompetitionSummaryDto
            {
                Id = "ft_comp_canonical_8888",
                LegacyId = "comp_8888",
                Name = "Custom League"
            },
            State = "LIVE",
            ElapsedMinute = 30
        };

        var relevance = FixtureRelevanceEngine.EvaluateRelevance(v2Match, preferences);

        // Mandatory stipulation verification:
        Assert.IsTrue(relevance.IsRelevant, "Match must be relevant via LegacyId matching");
        Assert.IsTrue(relevance.FollowedByTeam, "FollowedByTeam must be true for provisional match");
        Assert.IsTrue(relevance.FollowedByCompetition, "FollowedByCompetition must be true for provisional match");
        Assert.IsTrue(relevance.Reasons.Any(r => r.Contains("Custom FC")));
        Assert.IsTrue(relevance.Reasons.Any(r => r.Contains("Custom League")));
    }

    [TestMethod]
    public async Task NotificationEngine_EstablishesBaseline_OnV1Upgrade_WithoutFalseAlerts()
    {
        var historyPath = Path.Combine(_tempDir, "notifications_history.json");

        // Simulate pre-existing v1 deduplication file
        var v1History = """
        {
            "schemaVersion": 1,
            "notifiedKeys": {
                "match:fix_old_1:started": "2026-09-20T12:00:00Z"
            }
        }
        """;
        await File.WriteAllTextAsync(historyPath, v1History);

        var dedupStore = new NotificationDeduplicationStore(_tempDir);
        await dedupStore.LoadAsync();

        Assert.IsTrue(dedupStore.NeedsV2Baseline, "Upgrading from SchemaVersion 1 must trigger NeedsV2Baseline");

        var engine = new NotificationEngine(dedupStore);

        // Simulate first polling cycle after client upgrade with live canonical v2 matches
        var previousSnapshot = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "ft_fix_v2_live_1",
                    LegacyId = "fix_live_1",
                    State = "LIVE",
                    ElapsedMinute = 15,
                    HomeTeam = new TeamSummaryDto { Id = CanonicalEntityIds.Barcelona, Name = "FC Barcelona" },
                    AwayTeam = new TeamSummaryDto { Id = "ft_team_other", Name = "Other FC" },
                    Score = new ScoreDto { Home = 1, Away = 0 }
                }
            }
        };

        var currentSnapshot = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "ft_fix_v2_live_1",
                    LegacyId = "fix_live_1",
                    State = "LIVE",
                    ElapsedMinute = 16,
                    HomeTeam = new TeamSummaryDto { Id = CanonicalEntityIds.Barcelona, Name = "FC Barcelona" },
                    AwayTeam = new TeamSummaryDto { Id = "ft_team_other", Name = "Other FC" },
                    Score = new ScoreDto { Home = 1, Away = 0 }
                }
            }
        };

        // When evaluating updates with NeedsV2Baseline = true:
        var candidates = engine.EvaluateUpdates(previousSnapshot, currentSnapshot);

        // Should NOT emit false alerts for ongoing matches
        Assert.AreEqual(0, candidates.Count, "Upgrade baseline must establish without emitting alerts");
        Assert.IsFalse(dedupStore.NeedsV2Baseline, "NeedsV2Baseline should be cleared after first snapshot baseline");
        Assert.IsTrue(dedupStore.IsAlreadyNotified("match:ft_fix_v2_live_1:started"), "Match start must be recorded in baseline");

        // Subsequent new goal event on the same match SHOULD emit an alert normally
        var subsequentSnapshot = new TrayDataDto
        {
            Matches = new List<TrayMatchDto>
            {
                new()
                {
                    Id = "ft_fix_v2_live_1",
                    LegacyId = "fix_live_1",
                    State = "LIVE",
                    ElapsedMinute = 25,
                    HomeTeam = new TeamSummaryDto { Id = CanonicalEntityIds.Barcelona, Name = "FC Barcelona" },
                    AwayTeam = new TeamSummaryDto { Id = "ft_team_other", Name = "Other FC" },
                    Score = new ScoreDto { Home = 2, Away = 0 },
                    RecentEvents = new List<TrayEventDto>
                    {
                        new()
                        {
                            Id = "evt_goal_99",
                            Type = "Goal",
                            Minute = 25,
                            PlayerName = "Pedri",
                            TeamId = CanonicalEntityIds.Barcelona
                        }
                    }
                }
            }
        };

        var goalCandidates = engine.EvaluateUpdates(currentSnapshot, subsequentSnapshot);
        Assert.AreEqual(1, goalCandidates.Count, "New goal event after baseline must trigger a notification candidate");
        Assert.AreEqual(NotificationType.Goal, goalCandidates[0].Type);
        Assert.AreEqual("Pedri", goalCandidates[0].EventPlayerName);
    }
}
