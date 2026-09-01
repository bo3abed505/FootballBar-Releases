using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FootballTray.Client.Services;
using FootballTray.Core.Dto;

namespace FootballTray.Client.Tests;

[TestClass]
public class LocalCacheServiceTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FootballTrayTestCache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public async Task SaveAndLoadCache_RoundTripsSuccessfully()
    {
        var cacheService = new LocalCacheService(_tempDir);

        var envelope = new ApiResponseEnvelope<TrayDataDto>
        {
            SchemaVersion = "1.0",
            RequestId = "req_test_123",
            Version = 3,
            GeneratedAt = DateTimeOffset.UtcNow,
            ServerTime = DateTimeOffset.UtcNow,
            Data = new TrayDataDto
            {
                Matches = new List<TrayMatchDto>
                {
                    new()
                    {
                        Id = "fix_elclasico",
                        HomeTeam = new TeamSummaryDto { Name = "FC Barcelona", ShortName = "BAR" },
                        AwayTeam = new TeamSummaryDto { Name = "Real Madrid", ShortName = "RMA" },
                        State = "LIVE",
                        ElapsedMinute = 72,
                        Score = new ScoreDto { Home = 1, Away = 1 },
                    }
                }
            }
        };

        var saved = await cacheService.SaveCacheAsync(envelope, "\"etag_12345\"");
        Assert.IsTrue(saved);
        Assert.IsTrue(File.Exists(cacheService.CacheFilePath));

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNotNull(loaded);
        Assert.IsNotNull(loaded.Envelope);
        Assert.AreEqual(3, loaded.Envelope.Version);
        Assert.AreEqual("\"etag_12345\"", loaded.ETag);
        Assert.AreEqual(1, loaded.Envelope.Data?.Matches.Count);
        Assert.AreEqual("BAR", loaded.Envelope.Data?.Matches[0].HomeTeam.ShortName);
    }

    [TestMethod]
    public async Task LoadCache_MissingFile_ReturnsNull()
    {
        var cacheService = new LocalCacheService(_tempDir);
        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task LoadCache_CorruptFile_ReturnsNullWithoutThrowing()
    {
        var cacheService = new LocalCacheService(_tempDir);
        await File.WriteAllTextAsync(cacheService.CacheFilePath, "{ corrupt json content !!! not valid");

        var loaded = await cacheService.LoadCacheAsync();
        Assert.IsNull(loaded);
    }
}
