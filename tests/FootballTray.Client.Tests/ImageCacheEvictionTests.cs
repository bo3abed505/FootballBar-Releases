using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FootballTray.Client.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FootballTray.Client.Tests;

[TestClass]
public class ImageCacheEvictionTests
{
    private string _tempCacheDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), $"FootballTray_EvictionTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempCacheDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempCacheDir))
        {
            try
            {
                Directory.Delete(_tempCacheDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [TestMethod]
    public void CacheDirectory_AppliesNotContentIndexedAttribute()
    {
        var service = new ImageCacheService(customCacheDirectory: _tempCacheDir);

        var di = new DirectoryInfo(_tempCacheDir);
        Assert.IsTrue(
            di.Attributes.HasFlag(FileAttributes.NotContentIndexed),
            "Cache directory must have FileAttributes.NotContentIndexed set to prevent OS search indexer bloat.");
    }

    [TestMethod]
    public void EvictStaleEntries_DeletesFilesOlderThan30Days_AndKeepsRecentFiles()
    {
        var service = new ImageCacheService(customCacheDirectory: _tempCacheDir);

        string oldFile1 = Path.Combine(_tempCacheDir, "old_logo1.png");
        string oldFile2 = Path.Combine(_tempCacheDir, "old_logo2.png");
        string freshFile1 = Path.Combine(_tempCacheDir, "fresh_logo1.png");
        string freshFile2 = Path.Combine(_tempCacheDir, "fresh_logo2.png");

        File.WriteAllText(oldFile1, "old_1");
        File.WriteAllText(oldFile2, "old_2");
        File.WriteAllText(freshFile1, "fresh_1");
        File.WriteAllText(freshFile2, "fresh_2");

        // Set modification times
        File.SetLastWriteTimeUtc(oldFile1, DateTime.UtcNow.AddDays(-35));
        File.SetLastWriteTimeUtc(oldFile2, DateTime.UtcNow.AddDays(-31));
        File.SetLastWriteTimeUtc(freshFile1, DateTime.UtcNow.AddDays(-5));
        File.SetLastWriteTimeUtc(freshFile2, DateTime.UtcNow);

        int deletedCount = service.EvictStaleEntries();

        Assert.AreEqual(2, deletedCount, "EvictStaleEntries should report 2 files deleted.");
        Assert.IsFalse(File.Exists(oldFile1), "oldFile1 should have been deleted.");
        Assert.IsFalse(File.Exists(oldFile2), "oldFile2 should have been deleted.");
        Assert.IsTrue(File.Exists(freshFile1), "freshFile1 should still exist.");
        Assert.IsTrue(File.Exists(freshFile2), "freshFile2 should still exist.");
    }

    [TestMethod]
    public void EvictStaleEntries_DeletesOrphanedTempFilesOlderThan1Hour()
    {
        var service = new ImageCacheService(customCacheDirectory: _tempCacheDir);

        string staleTmp = Path.Combine(_tempCacheDir, "orphan.tmp");
        string freshTmp = Path.Combine(_tempCacheDir, "inflight.tmp");

        File.WriteAllText(staleTmp, "stale_temp_bytes");
        File.WriteAllText(freshTmp, "fresh_temp_bytes");

        File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(freshTmp, DateTime.UtcNow.AddMinutes(-5));

        int deletedCount = service.EvictStaleEntries();

        Assert.IsTrue(deletedCount >= 1, "At least 1 orphaned tmp file should be deleted.");
        Assert.IsFalse(File.Exists(staleTmp), "Stale tmp file (> 1 hour) must be deleted.");
        Assert.IsTrue(File.Exists(freshTmp), "Fresh tmp file (< 1 hour) should not be deleted.");
    }

    [TestMethod]
    public async Task EvictStaleEntriesAsync_RespectsCustomMaxAge()
    {
        var service = new ImageCacheService(customCacheDirectory: _tempCacheDir);

        string tenDaysOld = Path.Combine(_tempCacheDir, "logo_10d.png");
        string threeDaysOld = Path.Combine(_tempCacheDir, "logo_3d.png");

        File.WriteAllText(tenDaysOld, "10_days_old");
        File.WriteAllText(threeDaysOld, "3_days_old");

        File.SetLastWriteTimeUtc(tenDaysOld, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(threeDaysOld, DateTime.UtcNow.AddDays(-3));

        // Evict with 7 days threshold
        int deleted = await service.EvictStaleEntriesAsync(TimeSpan.FromDays(7));

        Assert.AreEqual(1, deleted);
        Assert.IsFalse(File.Exists(tenDaysOld));
        Assert.IsTrue(File.Exists(threeDaysOld));
    }
}
