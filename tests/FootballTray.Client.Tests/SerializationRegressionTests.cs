using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Notifications;
using FootballTray.Client.Services.Preferences;
using FootballTray.Core.Dto;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class SerializationRegressionTests
{
    private string _tempDirectory = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"FootballTray_SerializationTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [TestMethod]
    public async Task Test01_TrayApiEnvelope_Deserialization_WorksWithSourceGeneration()
    {
        // Realistic payload matching production /api/tray contract
        const string trayJson = """
        {
          "schemaVersion": "1.0",
          "requestId": "req_1788911613468_000002",
          "version": 1,
          "generatedAt": "2026-09-08T23:53:33.468Z",
          "serverTime": "2026-09-08T23:53:33.468Z",
          "stale": false,
          "dataAgeSeconds": 0,
          "data": {
            "matches": [
              {
                "id": "fix_1570392",
                "competition": {
                  "id": "comp_140",
                  "name": "La Liga",
                  "code": "LA"
                },
                "homeTeam": {
                  "id": "team_548",
                  "name": "Real Sociedad",
                  "shortName": "REA",
                  "logoUrl": "https://media.api-sports.io/football/teams/548.png"
                },
                "awayTeam": {
                  "id": "team_538",
                  "name": "Celta Vigo",
                  "shortName": "CEL",
                  "logoUrl": "https://media.api-sports.io/football/teams/538.png"
                },
                "state": "FINISHED",
                "elapsedMinute": 90,
                "extraMinute": 3,
                "score": {
                  "home": 0,
                  "away": 0,
                  "display": "0 - 0"
                },
                "kickoffTime": "2026-09-03T19:00:00.000Z",
                "recentEvents": [
                  {
                    "id": "evt_fix_1570392_15",
                    "type": "Substitution",
                    "minute": 88,
                    "extraMinute": 0,
                    "teamId": "team_548",
                    "playerName": "Benat Turrientes",
                    "description": "[subst] Substitution 5"
                  }
                ]
              }
            ]
          }
        }
        """;

        // 1. Test direct source generator deserialization
        var envelope = JsonSerializer.Deserialize(trayJson, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);

        Assert.IsNotNull(envelope, "Deserialized envelope should not be null.");
        Assert.AreEqual("1.0", envelope.SchemaVersion);
        Assert.AreEqual("req_1788911613468_000002", envelope.RequestId);
        Assert.AreEqual(1, envelope.Version);
        Assert.IsFalse(envelope.Stale);
        Assert.IsNotNull(envelope.Data);
        Assert.AreEqual(1, envelope.Data.Matches.Count);

        var match = envelope.Data.Matches[0];
        Assert.AreEqual("fix_1570392", match.Id);
        Assert.AreEqual("La Liga", match.Competition?.Name);
        Assert.AreEqual("Real Sociedad", match.HomeTeam.Name);
        Assert.AreEqual("Celta Vigo", match.AwayTeam.Name);
        Assert.AreEqual("FINISHED", match.State);
        Assert.AreEqual(90, match.ElapsedMinute);
        Assert.AreEqual(3, match.ExtraMinute);
        Assert.AreEqual("0 - 0", match.Score.Display);
        Assert.AreEqual(1, match.RecentEvents.Count);
        Assert.AreEqual("Benat Turrientes", match.RecentEvents[0].PlayerName);

        Assert.IsTrue(TrayPayloadValidator.IsValid(envelope, out var validationError), $"Payload should pass validator: {validationError}");

        // 2. Test FootballApiClient deserialization path via mock handler
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(trayJson, Encoding.UTF8, "application/json")
            }
        };

        using var client = new FootballApiClient(new HttpClient(mockHandler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        var apiResult = await client.GetTrayAsync();

        Assert.IsTrue(apiResult.IsSuccess, $"ApiClient should succeed: {apiResult.ErrorMessage}");
        Assert.IsNotNull(apiResult.Envelope);
        Assert.AreEqual(1, apiResult.Envelope.Data?.Matches.Count);
    }

    [TestMethod]
    public async Task Test02_MatchDetailEnvelope_Deserialization_WorksWithSourceGeneration()
    {
        // Realistic payload matching production /api/match/:id contract
        const string matchDetailJson = """
        {
          "schemaVersion": "1.0",
          "requestId": "req_1788911643689_000005",
          "version": 1,
          "generatedAt": "2026-09-08T23:54:03.689Z",
          "serverTime": "2026-09-08T23:54:03.689Z",
          "stale": false,
          "dataAgeSeconds": 0,
          "data": {
            "id": "fix_1570392",
            "competition": {
              "id": "comp_140",
              "name": "La Liga",
              "code": "LA"
            },
            "homeTeam": {
              "id": "team_548",
              "name": "Real Sociedad",
              "shortName": "REA",
              "logoUrl": "https://media.api-sports.io/football/teams/548.png"
            },
            "awayTeam": {
              "id": "team_538",
              "name": "Celta Vigo",
              "shortName": "CEL",
              "logoUrl": "https://media.api-sports.io/football/teams/538.png"
            },
            "state": "FINISHED",
            "elapsedMinute": 90,
            "extraMinute": 3,
            "score": {
              "home": 0,
              "away": 0,
              "halfTime": {
                "home": 0,
                "away": 0
              },
              "display": "0 - 0"
            },
            "venue": "Anoeta",
            "referee": "V. Garcia",
            "kickoffTime": "2026-09-03T19:00:00.000Z",
            "events": [
              {
                "id": "evt_fix_1570392_1",
                "type": "YellowCard",
                "minute": 22,
                "extraMinute": 0,
                "teamId": "team_548",
                "playerName": "Yangel Herrera",
                "description": "[Card] Yellow Card"
              }
            ]
          }
        }
        """;

        // 1. Test direct source generator deserialization
        var envelope = JsonSerializer.Deserialize(matchDetailJson, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);

        Assert.IsNotNull(envelope, "Match detail envelope should not be null.");
        Assert.IsNotNull(envelope.Data);
        Assert.AreEqual("fix_1570392", envelope.Data.Id);
        Assert.AreEqual("La Liga", envelope.Data.Competition?.Name);
        Assert.AreEqual("Real Sociedad", envelope.Data.HomeTeam.Name);
        Assert.AreEqual("Celta Vigo", envelope.Data.AwayTeam.Name);
        Assert.AreEqual("FINISHED", envelope.Data.State);
        Assert.AreEqual("Anoeta", envelope.Data.Venue);
        Assert.AreEqual("V. Garcia", envelope.Data.Referee);
        Assert.AreEqual(1, envelope.Data.Events.Count);
        Assert.AreEqual("Yangel Herrera", envelope.Data.Events[0].PlayerName);

        // 2. Test FootballApiClient deserialization path via mock handler
        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(matchDetailJson, Encoding.UTF8, "application/json")
            }
        };

        using var client = new FootballApiClient(new HttpClient(mockHandler) { BaseAddress = new Uri("http://127.0.0.1:4000") });
        var apiResult = await client.GetMatchDetailsAsync("fix_1570392");

        Assert.IsTrue(apiResult.IsSuccess, $"GetMatchDetailsAsync should succeed: {apiResult.ErrorMessage}");
        Assert.IsNotNull(apiResult.Envelope?.Data);
        Assert.AreEqual("fix_1570392", apiResult.Envelope.Data.Id);
    }

    [TestMethod]
    public async Task Test03_Preferences_LoadSaveRoundTrip_WorksWithSourceGeneration()
    {
        var preferencesService = new PreferencesService(_tempDirectory);
        var initial = await preferencesService.LoadAsync();

        Assert.IsNotNull(initial);
        Assert.AreEqual(UserPreferences.CurrentSchemaVersion, initial.PreferencesVersion);

        // Follow a team and save
        var barca = new FollowedTeam("team_529", "FC Barcelona", "BAR", "Spain", "https://media.api-sports.io/football/teams/529.png");
        var alahly = new FollowedTeam("team_1029", "Al Ahly SC", "AHL", "Egypt", "https://media.api-sports.io/football/teams/1029.png");
        await preferencesService.FollowTeamAsync(barca);
        await preferencesService.FollowTeamAsync(alahly);

        // Verify file written to disk
        string prefsFilePath = Path.Combine(_tempDirectory, "preferences.json");
        Assert.IsTrue(File.Exists(prefsFilePath), "preferences.json should exist on disk.");

        string rawJson = await File.ReadAllTextAsync(prefsFilePath);
        Assert.IsTrue(rawJson.Contains("team_529"), "Saved JSON should contain followed team ID.");
        Assert.IsTrue(rawJson.Contains("team_1029"), "Saved JSON should contain second followed team ID.");

        // Unfollow team (this was the exact runtime crash reported in smoke test)
        await preferencesService.UnfollowTeamAsync("team_1029");

        // Reload from disk using a fresh PreferencesService instance
        var freshService = new PreferencesService(_tempDirectory);
        var reloaded = await freshService.LoadAsync();

        Assert.IsNotNull(reloaded);
        Assert.AreEqual(1, reloaded.FollowedTeams.Count);
        Assert.AreEqual("team_529", reloaded.FollowedTeams[0].Id);
        Assert.AreEqual("FC Barcelona", reloaded.FollowedTeams[0].Name);

        // Verify existing disk format compatibility (numeric enabledTypes enum array)
        const string legacyDiskJson = """
        {
          "preferencesVersion": 1,
          "followedTeams": [
            {
              "id": "team_529",
              "name": "FC Barcelona",
              "shortName": "BAR",
              "country": "Spain",
              "logoUrl": "https://media.api-sports.io/football/teams/529.png"
            }
          ],
          "followedCompetitions": [
            {
              "id": "comp_39",
              "name": "Premier League",
              "code": "PL",
              "country": "England",
              "logoUrl": "https://media.api-sports.io/football/leagues/39.png"
            }
          ],
          "notifications": {
            "globalEnabled": true,
            "enabledTypes": [0, 1, 2, 3, 4, 5, 6]
          }
        }
        """;

        var legacyLoaded = JsonSerializer.Deserialize(legacyDiskJson, FootballJsonSerializerContext.Default.UserPreferences);
        Assert.IsNotNull(legacyLoaded);
        Assert.AreEqual(1, legacyLoaded.FollowedTeams.Count);
        Assert.AreEqual(1, legacyLoaded.FollowedCompetitions.Count);
        Assert.AreEqual(7, legacyLoaded.Notifications.EnabledTypes.Count);
        Assert.IsTrue(legacyLoaded.Notifications.IsTypeEnabled(NotificationType.Goal));
    }

    [TestMethod]
    public async Task Test04_LocalCache_LoadSaveRoundTrip_WorksWithSourceGeneration()
    {
        var cacheService = new LocalCacheService(_tempDirectory);

        var sampleEnvelope = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_cache_test",
            Version = 5,
            GeneratedAt = DateTimeOffset.UtcNow,
            ServerTime = DateTimeOffset.UtcNow,
            Stale = false,
            DataAgeSeconds = 0,
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "fix_cache_1",
                        HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team One", ShortName = "T1" },
                        AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team Two", ShortName = "T2" },
                        State = "LIVE",
                        ElapsedMinute = 45,
                        Score = new ScoreDto { Home = 1, Away = 0, Display = "1 - 0" }
                    }
                }
            }
        };

        const string testEtag = "\"cache-test-etag-456\"";
        bool saved = await cacheService.SaveCacheAsync(sampleEnvelope, testEtag);
        Assert.IsTrue(saved, "SaveCacheAsync should return true.");

        string cacheFilePath = Path.Combine(_tempDirectory, "tray.json");
        Assert.IsTrue(File.Exists(cacheFilePath), "tray.json should exist on disk.");

        // Read back via a fresh LocalCacheService instance
        var freshCacheService = new LocalCacheService(_tempDirectory);
        var loaded = await freshCacheService.LoadCacheAsync();

        Assert.IsNotNull(loaded, "LoadCacheAsync should return populated payload.");
        Assert.AreEqual(testEtag, loaded.ETag);
        Assert.IsNotNull(loaded.Envelope?.Data);
        Assert.AreEqual(1, loaded.Envelope.Data.Matches.Count);
        Assert.AreEqual("fix_cache_1", loaded.Envelope.Data.Matches[0].Id);
        Assert.AreEqual("Team One", loaded.Envelope.Data.Matches[0].HomeTeam.Name);
        Assert.AreEqual("LIVE", loaded.Envelope.Data.Matches[0].State);
    }

    [TestMethod]
    public async Task Test05_NotificationDeduplicationStore_PersistenceRoundTrip_WorksWithSourceGeneration()
    {
        var store = new NotificationDeduplicationStore(_tempDirectory);
        await store.LoadAsync();

        string eventKey1 = "match:fix_1570392:event:evt_1";
        string eventKey2 = "match:fix_1570392:event:evt_2";

        Assert.IsFalse(store.IsAlreadyNotified(eventKey1), "New key should not be already notified.");
        store.MarkNotified(eventKey1);
        Assert.IsTrue(store.IsAlreadyNotified(eventKey1), "Key should now be detected as notified.");

        store.MarkNotified(eventKey2);
        await store.SaveAsync();

        string historyFilePath = Path.Combine(_tempDirectory, "notifications_history.json");
        Assert.IsTrue(File.Exists(historyFilePath), "notifications_history.json should exist on disk.");

        // Reload via fresh store instance
        var freshStore = new NotificationDeduplicationStore(_tempDirectory);
        await freshStore.LoadAsync();

        Assert.IsTrue(freshStore.IsAlreadyNotified(eventKey1), "Reloaded store should recognize persisted key 1.");
        Assert.IsTrue(freshStore.IsAlreadyNotified(eventKey2), "Reloaded store should recognize persisted key 2.");
        Assert.IsFalse(freshStore.IsAlreadyNotified("match:fix_1570392:event:evt_3"), "Unsaved key should not be notified.");
    }

    [TestMethod]
    public async Task Test06_UserPreferences_PreservesUnknownExtensionData_AcrossModifyAndSave()
    {
        string prefPath = Path.Combine(_tempDirectory, "preferences.json");
        const string initialJsonWithUnknowns = """
        {
          "preferencesVersion": 1,
          "followedTeams": [
            { "id": "team_529", "name": "FC Barcelona" }
          ],
          "followedCompetitions": [],
          "notifications": { "globalEnabled": true, "enabledTypes": [0, 1] },
          "futureGlobalSetting": "dark_mode",
          "customTelemetryId": 98765
        }
        """;
        await File.WriteAllTextAsync(prefPath, initialJsonWithUnknowns);

        var prefService = new PreferencesService(_tempDirectory);
        var loaded = await prefService.LoadAsync();

        Assert.IsNotNull(loaded, "Preferences should load successfully.");
        Assert.IsNotNull(loaded.ExtensionData, "ExtensionData dictionary should be populated with unknown properties.");
        Assert.IsTrue(loaded.ExtensionData.ContainsKey("futureGlobalSetting"), "futureGlobalSetting should be captured.");
        Assert.AreEqual("dark_mode", loaded.ExtensionData["futureGlobalSetting"].GetString());
        Assert.IsTrue(loaded.ExtensionData.ContainsKey("customTelemetryId"), "customTelemetryId should be captured.");
        Assert.AreEqual(98765, loaded.ExtensionData["customTelemetryId"].GetInt32());

        // Perform a modification (e.g. follow a new team)
        await prefService.FollowTeamAsync(new FollowedTeam("team_548", "Real Sociedad"));

        // Verify the persisted file still contains the unknown properties
        string savedJson = await File.ReadAllTextAsync(prefPath);
        using var doc = JsonDocument.Parse(savedJson);
        var root = doc.RootElement;

        Assert.IsTrue(root.TryGetProperty("futureGlobalSetting", out var futureProp), "futureGlobalSetting must survive save cycle.");
        Assert.AreEqual("dark_mode", futureProp.GetString());

        Assert.IsTrue(root.TryGetProperty("customTelemetryId", out var telemProp), "customTelemetryId must survive save cycle.");
        Assert.AreEqual(98765, telemProp.GetInt32());

        // Also verify the team was indeed added
        Assert.IsTrue(root.TryGetProperty("followedTeams", out var teamsProp));
        Assert.AreEqual(2, teamsProp.GetArrayLength());
    }
}
