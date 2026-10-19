using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Serialization;
using FootballTray.Client.Services;
using FootballTray.Client.Services.Preferences;
using FootballTray.Client.ViewModels;
using FootballTray.Core.Dto;

#pragma warning disable MSTEST0037

namespace FootballTray.Client.Tests;

[TestClass]
public class Phase8ClientConsumptionTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTrayPhase8Test_" + Guid.NewGuid().ToString("N"));
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

    private static ApiResponseEnvelope<TrayDataDto> CreateTrayEnvelope(string matchId, int homeScore = 0, int awayScore = 0)
    {
        return new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "2.0",
            RequestId = "req_phase8_test",
            Version = 2,
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
                        Id = matchId,
                        Competition = new CompetitionSummaryDto { Id = "comp_ucl", Name = "Champions League", Code = "CL" },
                        HomeTeam = new TeamSummaryDto { Id = "team_rma", Name = "Real Madrid", ShortName = "RMA" },
                        AwayTeam = new TeamSummaryDto { Id = "team_bay", Name = "Bayern Munich", ShortName = "BAY" },
                        State = "LIVE",
                        ElapsedMinute = 45,
                        KickoffTime = DateTimeOffset.UtcNow.AddMinutes(-45),
                        Score = new ScoreDto { Home = homeScore, Away = awayScore }
                    }
                }
            }
        };
    }

    [TestMethod]
    public async Task Test01_GetLiveTrayAsync_Handles_200_And_304_With_Etag()
    {
        var envelope = CreateTrayEnvelope("fix_live_01");
        string expectedEtag = "W/\"live-17000000-abcdef12-345678\"";

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                if (req.Headers.Contains("If-None-Match"))
                {
                    var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
                    notMod.Headers.TryAddWithoutValidation("ETag", expectedEtag);
                    return notMod;
                }

                var json = JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                resp.Headers.TryAddWithoutValidation("ETag", expectedEtag);
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);

        // 1. Cold poll: No ETag sent
        var res1 = await client.GetLiveTrayAsync();
        Assert.IsTrue(res1.IsSuccess);
        Assert.IsFalse(res1.IsNotModified);
        Assert.IsNotNull(res1.Envelope);
        Assert.AreEqual(expectedEtag, res1.ETag);
        Assert.IsFalse(mockHandler.LastRequest!.Headers.Contains("If-None-Match"));

        // 2. Subsequent poll: ETag sent in If-None-Match
        var res2 = await client.GetLiveTrayAsync(ifNoneMatch: res1.ETag);
        Assert.IsTrue(res2.IsSuccess);
        Assert.IsTrue(res2.IsNotModified);
        Assert.IsNull(res2.Envelope, "HTTP 304 response must not deserialize payload body.");
        Assert.AreEqual(expectedEtag, res2.ETag);
        Assert.IsTrue(mockHandler.LastRequest!.Headers.Contains("If-None-Match"));
    }

    [TestMethod]
    public async Task Test02_GetLiveTrayAsync_Propagates_CachedFallback_And_Quota_Headers()
    {
        var envelope = CreateTrayEnvelope("fix_live_02");

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                var json = JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                resp.Headers.TryAddWithoutValidation("ETag", "W/\"live-fallback\"");
                resp.Headers.TryAddWithoutValidation("X-Cached-Fallback", "true");
                resp.Headers.TryAddWithoutValidation("X-Quota-Exhausted", "true");
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);

        var res = await client.GetLiveTrayAsync();
        Assert.IsTrue(res.IsSuccess);
        Assert.IsTrue(res.CachedFallback);
        Assert.IsTrue(res.QuotaExhausted);
        Assert.IsTrue(res.Envelope!.CachedFallback);
        Assert.IsTrue(res.Envelope!.QuotaExhausted);
    }

    [TestMethod]
    public async Task Test03_GetMatchDetailsAsync_Supports_Etag_304()
    {
        var detailEnvelope = new ApiResponseEnvelope<MatchDetailDto>
        {
            SchemaVersion = "2.0",
            Version = 2,
            Data = new MatchDetailDto
            {
                Id = "ft_fix_detail_01",
                State = "LIVE",
                ElapsedMinute = 60,
                Score = new ScoreDto { Home = 1, Away = 0 }
            }
        };
        string detailEtag = "W/\"match-ft_fix_detail_01-17000-5\"";

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (req) =>
            {
                if (req.Headers.Contains("If-None-Match"))
                {
                    var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
                    notMod.Headers.TryAddWithoutValidation("ETag", detailEtag);
                    return notMod;
                }

                var json = JsonSerializer.Serialize(detailEnvelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeMatchDetailDto);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                resp.Headers.TryAddWithoutValidation("ETag", detailEtag);
                return resp;
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);

        // 1. Initial match details fetch
        var res1 = await client.GetMatchDetailsAsync("ft_fix_detail_01");
        Assert.IsTrue(res1.IsSuccess);
        Assert.IsFalse(res1.IsNotModified);
        Assert.AreEqual("ft_fix_detail_01", res1.Envelope!.Data!.Id);
        Assert.AreEqual(detailEtag, res1.ETag);

        // 2. Re-poll with ETag
        var res2 = await client.GetMatchDetailsAsync("ft_fix_detail_01", ifNoneMatch: res1.ETag);
        Assert.IsTrue(res2.IsSuccess);
        Assert.IsTrue(res2.IsNotModified);
        Assert.IsNull(res2.Envelope);
        Assert.AreEqual(detailEtag, res2.ETag);
    }

    [TestMethod]
    public void Test04_MatchItemViewModel_UpdateFromSource_AntiFlicker_OnlyFiresChangedProperties()
    {
        var dtoA = new TrayMatchDto
        {
            Id = "ft_match_100",
            HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team A", ShortName = "TMA" },
            AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team B", ShortName = "TMB" },
            Competition = new CompetitionSummaryDto { Id = "comp_1", Name = "League", Code = "LG" },
            State = "LIVE",
            ElapsedMinute = 40,
            Score = new ScoreDto { Home = 1, Away = 0 }
        };

        var vmA = MatchItemViewModel.FromDto(dtoA, new FixtureRelevanceResult(), false);

        var changedProps = new List<string>();
        vmA.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null) changedProps.Add(e.PropertyName);
        };

        // Mutate only AwayScore (0 -> 1) in source DTO
        var dtoB = new TrayMatchDto
        {
            Id = "ft_match_100",
            HomeTeam = new TeamSummaryDto { Id = "team_1", Name = "Team A", ShortName = "TMA" },
            AwayTeam = new TeamSummaryDto { Id = "team_2", Name = "Team B", ShortName = "TMB" },
            Competition = new CompetitionSummaryDto { Id = "comp_1", Name = "League", Code = "LG" },
            State = "LIVE", // Unchanged
            ElapsedMinute = 40, // Unchanged
            Score = new ScoreDto { Home = 1, Away = 1 } // AwayScore changed!
        };
        var vmB = MatchItemViewModel.FromDto(dtoB, new FixtureRelevanceResult(), false);

        bool updated = vmA.UpdateFromSource(vmB);
        Assert.IsTrue(updated);

        // AwayScore and ScoreDisplay must have fired PropertyChanged
        Assert.IsTrue(changedProps.Contains(nameof(MatchItemViewModel.AwayScore)));
        Assert.IsTrue(changedProps.Contains(nameof(MatchItemViewModel.ScoreDisplay)));

        // HomeScore, State, ElapsedMinute must NOT have fired PropertyChanged!
        Assert.IsFalse(changedProps.Contains(nameof(MatchItemViewModel.HomeScore)), "Unchanged HomeScore must NOT fire PropertyChanged!");
        Assert.IsFalse(changedProps.Contains(nameof(MatchItemViewModel.State)), "Unchanged State must NOT fire PropertyChanged!");
        Assert.IsFalse(changedProps.Contains(nameof(MatchItemViewModel.ElapsedMinute)), "Unchanged ElapsedMinute must NOT fire PropertyChanged!");

        // Calling UpdateFromSource again with identical state must fire zero events
        changedProps.Clear();
        bool updatedAgain = vmA.UpdateFromSource(vmB);
        Assert.IsFalse(updatedAgain);
        Assert.AreEqual(0, changedProps.Count, "Identical update must produce zero PropertyChanged notifications.");
    }

    [TestMethod]
    public async Task Test05_MainViewModel_RefreshMatches_304_ShortCircuits_CollectionChurn()
    {
        var prefService = new PreferencesService(_tempDir);
        var envelope = CreateTrayEnvelope("ft_match_304", homeScore: 0, awayScore: 0);

        var mockHandler = new MockHttpMessageHandler
        {
            ResponseFactory = (_) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(envelope, FootballJsonSerializerContext.Default.ApiResponseEnvelopeTrayDataDto), Encoding.UTF8, "application/json")
            }
        };

        using var httpClient = new HttpClient(mockHandler) { BaseAddress = new Uri("http://localhost:4000") };
        using var client = new FootballApiClient(httpClient);
        var cacheService = new LocalCacheService(_tempDir);
        using var syncService = new SyncService(client, cacheService, preferencesService: prefService);

        using var vm = new MainViewModel(prefService, syncService);
        await vm.InitializeAsync();

        // 1. Initial load
        vm.RefreshMatches(envelope.Data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        var originalInstance = vm.FilteredMatches[0];

        // 2. Poll cycle receives 304 Not Modified
        vm.RefreshMatches(null, DataFreshnessState.NotModified, TickerConnectionState.OnlineFresh);

        // FilteredMatches must not have been touched
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreSame(originalInstance, vm.FilteredMatches[0], "304 Not Modified must retain identical viewmodel instance.");
    }

    [TestMethod]
    public async Task Test06_MainViewModel_InPlace_ScoreUpdate_PreservesContainers()
    {
        var prefService = new PreferencesService(_tempDir);
        var envelope1 = CreateTrayEnvelope("ft_match_flicker", homeScore: 1, awayScore: 0);

        using var vm = new MainViewModel(prefService);
        await vm.InitializeAsync();

        // 1. Match is 1 - 0
        vm.RefreshMatches(envelope1.Data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        var itemInstance = vm.FilteredMatches[0];
        Assert.AreEqual(1, itemInstance.HomeScore);
        Assert.AreEqual(0, itemInstance.AwayScore);

        // 2. Goal scored: Match is 2 - 0
        var envelope2 = CreateTrayEnvelope("ft_match_flicker", homeScore: 2, awayScore: 0);
        vm.RefreshMatches(envelope2.Data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);

        // Collection count is 1, and the EXACT same object instance was updated in-place (No .Clear() called!)
        Assert.AreEqual(1, vm.FilteredMatches.Count);
        Assert.AreSame(itemInstance, vm.FilteredMatches[0], "ReconcileMatchCollection must update existing viewmodel in-place without rebuilding container!");
        Assert.AreEqual(2, itemInstance.HomeScore);
        Assert.AreEqual(0, itemInstance.AwayScore);
    }

    [TestMethod]
    public async Task Test07_MainViewModel_OfflineCached_PreservesMatchesAndSetsBadge()
    {
        var prefService = new PreferencesService(_tempDir);
        var envelope = CreateTrayEnvelope("ft_match_offline", homeScore: 1, awayScore: 1);

        using var vm = new MainViewModel(prefService);
        await vm.InitializeAsync();

        // 1. Online state
        vm.RefreshMatches(envelope.Data, DataFreshnessState.Fresh, TickerConnectionState.OnlineFresh);
        Assert.IsFalse(vm.IsOffline);
        Assert.AreEqual("Online (Live)", vm.ConnectionStatusText);
        Assert.AreEqual(1, vm.FilteredMatches.Count);

        // 2. Backend goes offline / returns cached fallback
        vm.RefreshMatches(envelope.Data, DataFreshnessState.Stale, TickerConnectionState.OfflineCached);

        // UI must show Offline badge but preserve all match cards
        Assert.IsTrue(vm.IsOffline);
        Assert.AreEqual("Offline (Cached Data)", vm.ConnectionStatusText);
        Assert.AreEqual(1, vm.FilteredMatches.Count, "Offline state must never wipe or blank out cached match cards!");
        Assert.AreEqual(1, vm.FilteredMatches[0].HomeScore);
        Assert.AreEqual(1, vm.FilteredMatches[0].AwayScore);
    }
}
