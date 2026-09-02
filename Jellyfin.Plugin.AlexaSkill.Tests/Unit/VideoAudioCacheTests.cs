using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for the VideoAudioCache — file-based MP4 cache service.
/// </summary>
[Collection("Plugin")]
public class VideoAudioCacheTests : PluginTestBase, IDisposable
{
    private readonly string _tempDir;
    private readonly VideoAudioCache _cache;
    private readonly ILogger<VideoAudioCache> _logger;

    public VideoAudioCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "va-cache-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(_tempDir);

        _logger = LoggerFactory.Create(b => { }).CreateLogger<VideoAudioCache>();
        _cache = new VideoAudioCache(appPaths.Object, _logger);

        // Ensure plugin instance exists for config access in EvictIfNeeded
        EnsurePluginInstance(
            new PluginConfiguration(),
            LoggerFactory.Create(b => { }),
            cfg => { },
            "va-cache-test");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Temp dir cleanup best-effort
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetCacheFilePath_ContainsItemIdAndTicks()
    {
        string path = _cache.GetCacheFilePath("abc123", 637500000000000000);

        Assert.Contains("abc123", Path.GetFileName(path));
        Assert.Contains("637500000000000000", Path.GetFileName(path));
        Assert.EndsWith(".mp4", path);
        Assert.Contains("alexaskill-video-audio", path);
    }

    [Fact]
    public void GetCacheFilePath_DifferentTicks_DifferentPaths()
    {
        string path1 = _cache.GetCacheFilePath("item1", 100);
        string path2 = _cache.GetCacheFilePath("item1", 200);

        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public async Task GetCachedFile_NoFile_ReturnsNull()
    {
        FileInfo? result = await _cache.GetCachedFile("nonexistent", 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedFile_FileExists_ReturnsFileInfo()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write enough data to exceed the minimum valid file size (10 KB)
        await File.WriteAllTextAsync(path, new string('x', 12 * 1024));

        FileInfo? result = await _cache.GetCachedFile("item1", 12345);

        Assert.NotNull(result);
        Assert.Equal(path, result!.FullName);
        Assert.True(result.Length > 0);
    }

    [Fact]
    public async Task GetCachedFile_EmptyFile_ReturnsNull()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, string.Empty);

        FileInfo? result = await _cache.GetCachedFile("item1", 12345);

        Assert.Null(result);
    }

    /// <summary>
    /// Verify that a corrupt stub file (36 bytes) is treated as a cache miss
    /// by GetCachedFile, and deleted by DeleteStubIfPresent.
    /// </summary>
    [Fact]
    public async Task GetCachedFile_StubFile_36Bytes_ReturnsNullWithoutDeleting()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Exactly 36 bytes — real production case: empty ftyp+moov boxes
        await File.WriteAllTextAsync(path, new string('x', 36));
        Assert.True(File.Exists(path));

        // GetCachedFile returns null but does NOT delete (may be actively written)
        FileInfo? result = await _cache.GetCachedFile("item1", 12345);
        Assert.Null(result);
        Assert.True(File.Exists(path), "GetCachedFile should not delete stubs");

        // DeleteStubIfPresent (called inside the lock) does the cleanup
        _cache.DeleteStubIfPresent("item1", 12345);
        Assert.False(File.Exists(path), "DeleteStubIfPresent should delete the stub");
    }

    /// <summary>
    /// Verify that a small file (just under 10 KB threshold) is treated as
    /// a cache miss but not deleted by GetCachedFile.
    /// </summary>
    [Fact]
    public async Task GetCachedFile_BoundarySize_ReturnsNullUnderThreshold()
    {
        // File just under 10 KB — should be treated as miss but NOT deleted
        string path = _cache.GetCacheFilePath("small-item", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, new string('x', 10 * 1024 - 1));

        FileInfo? result = await _cache.GetCachedFile("small-item", 12345);

        Assert.Null(result);
        Assert.True(File.Exists(path), "GetCachedFile should not delete files");
    }

    /// <summary>
    /// Verify that a file exactly at the 10 KB threshold is a valid cache hit.
    /// </summary>
    [Fact]
    public async Task GetCachedFile_ExactlyMinSize_IsValidHit()
    {
        string path = _cache.GetCacheFilePath("exact-item", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, new string('x', 10 * 1024));

        FileInfo? result = await _cache.GetCachedFile("exact-item", 12345);

        Assert.NotNull(result);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task GetCachedFile_WrongTicks_ReturnsNull()
    {
        string path = _cache.GetCacheFilePath("item1", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "content");

        FileInfo? result = await _cache.GetCachedFile("item1", 99999);

        Assert.Null(result);
    }

    [Fact]
    public async Task EvictIfNeeded_UnderLimit_DoesNothing()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 2048;

        // Create a small file
        string path = _cache.GetCacheFilePath("item1", 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "small");

        await _cache.EvictIfNeeded();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task EvictIfNeeded_OverLimit_DeletesOldestFiles()
    {
        // Set limit to 1 MB so only one small file survives
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        // Create two files with different access times
        // Make the first file large enough that deleting it brings us under limit
        string path1 = _cache.GetCacheFilePath("old-item", 1);
        string path2 = _cache.GetCacheFilePath("new-item", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);

        // Write 1.5 MB for old file (will be evicted)
        await File.WriteAllTextAsync(path1, new string('x', 1536 * 1024));
        File.SetLastAccessTimeUtc(path1, DateTime.UtcNow.AddHours(-2));

        // Write 100 KB for new file (will survive)
        await File.WriteAllTextAsync(path2, new string('y', 100 * 1024));
        File.SetLastAccessTimeUtc(path2, DateTime.UtcNow);

        await _cache.EvictIfNeeded();

        // Oldest file should be deleted, newest should survive
        Assert.False(File.Exists(path1), "Oldest file should have been evicted");
        Assert.True(File.Exists(path2), "Newest file should survive eviction");
    }

    /// <summary>
    /// A file served recently (in-memory RecordAccess via a cache hit) survives eviction even
    /// when its filesystem atime is older than a cold file's -- the in-memory recency overrides
    /// the stale atime that relatime/noatime mounts would otherwise freeze (JF-320 part 2).
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_RecentlyServedFile_SurvivesDespiteStaleAtime()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        string servedPath = _cache.GetCacheFilePath("served-item", 1);
        string coldPath = _cache.GetCacheFilePath("cold-item", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(servedPath)!);

        await File.WriteAllTextAsync(servedPath, new string('x', 100 * 1024));
        await File.WriteAllTextAsync(coldPath, new string('y', 1536 * 1024));

        // Serve the smaller file -> RecordAccess marks it recently used in memory.
        await _cache.GetCachedFile("served-item", 1);

        // Make the SERVED file's atime OLDER than the cold file's -- without the in-memory
        // recency, atime-based eviction would evict the served file first.
        File.SetLastAccessTimeUtc(servedPath, DateTime.UtcNow.AddHours(-5));
        File.SetLastAccessTimeUtc(coldPath, DateTime.UtcNow.AddHours(-1));

        await _cache.EvictIfNeeded();

        Assert.True(File.Exists(servedPath), "Recently-served file should survive (in-memory recency overrides stale atime)");
        Assert.False(File.Exists(coldPath), "Cold file should be evicted");
    }

    // --- JF-428: eviction floor + in-use pinning ---

    /// <summary>
    /// JF-428: a pre-encode headroom larger than the configured cap must floor the eviction
    /// target at half the cap, never zero. The old clamp-to-0 targeted zero and wiped EVERY
    /// entry, including the HLS directory of the audiobook currently being encoded/streamed.
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_HeadroomExceedsCap_FloorsAtHalfCap_NotZero()
    {
        // Cap 1MB; entries 0.7MB total; headroom 2MB (> cap).
        // Floor = 0.5MB: the oldest 0.2MB entry is evicted (0.7 > 0.5), the newest 0.5MB survives.
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        string oldestPath = _cache.GetCacheFilePath("old-item", 1);
        string newestPath = _cache.GetCacheFilePath("new-item", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(oldestPath)!);

        await File.WriteAllTextAsync(oldestPath, new string('x', 200 * 1024));
        File.SetLastAccessTimeUtc(oldestPath, DateTime.UtcNow.AddHours(-2));
        await File.WriteAllTextAsync(newestPath, new string('y', 512 * 1024));
        File.SetLastAccessTimeUtc(newestPath, DateTime.UtcNow);

        await _cache.EnsureDiskBudgetBeforeEncodeAsync(2L * 1024 * 1024);

        Assert.False(File.Exists(oldestPath), "Oldest entry is still evictable pressure (0.7MB total > 0.5MB floor)");
        Assert.True(File.Exists(newestPath), "Newest entry must SURVIVE: the floor is half the cap, not zero");
    }

    /// <summary>
    /// JF-428: an entry pinned as in-use (its directory is being encoded and streamed)
    /// is never evicted, even when it is the oldest over-limit entry.
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_PinnedEntry_NeverEvictedEvenWhenOldest()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        string pinnedPath = _cache.GetCacheFilePath("in-use-item", 1);
        string coldPath = _cache.GetCacheFilePath("cold-item", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(pinnedPath)!);

        await File.WriteAllTextAsync(pinnedPath, new string('x', 1536 * 1024));
        File.SetLastAccessTimeUtc(pinnedPath, DateTime.UtcNow.AddHours(-2));
        await File.WriteAllTextAsync(coldPath, new string('y', 100 * 1024));
        File.SetLastAccessTimeUtc(coldPath, DateTime.UtcNow);

        _cache.Pin(pinnedPath);
        try
        {
            await _cache.EvictIfNeeded();

            Assert.True(File.Exists(pinnedPath), "Pinned (in-use) entry must never be evicted mid-encode/mid-stream");
            Assert.False(File.Exists(coldPath), "Cold unpinned entry should still be evicted");
        }
        finally
        {
            _cache.Unpin(pinnedPath);
        }
    }

    /// <summary>
    /// JF-428: when a pinned entry alone exceeds the target, the sweep stops instead of
    /// deleting everything else trying to reach an unreachable target.
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_SinglePinnedOverLimit_DeletesNothing()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        string pinnedPath = _cache.GetCacheFilePath("in-use-item", 1);
        Directory.CreateDirectory(Path.GetDirectoryName(pinnedPath)!);
        await File.WriteAllTextAsync(pinnedPath, new string('x', 1536 * 1024));
        File.SetLastAccessTimeUtc(pinnedPath, DateTime.UtcNow.AddHours(-2));

        _cache.Pin(pinnedPath);
        try
        {
            await _cache.EvictIfNeeded();

            Assert.True(File.Exists(pinnedPath), "Nothing is deleted when the only entry is pinned");
        }
        finally
        {
            _cache.Unpin(pinnedPath);
        }
    }

    /// <summary>
    /// JF-428: pins are refcounted. A retrying encode can re-pin the same path while
    /// the previous attempt's delayed exit-poll release (up to 500ms) is still pending;
    /// that late release must not expose the new encode's entry to eviction.
    /// </summary>
    [Fact]
    public async Task Pin_IsRefcounted_LateReleaseDoesNotExposeRePinnedEntry()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        string path = _cache.GetCacheFilePath("in-use-item", 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, new string('x', 1536 * 1024));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddHours(-2));

        _cache.Pin(path);      // encode A
        _cache.Pin(path);      // retrying encode B, same path
        _cache.Unpin(path);    // A's delayed exit-poll release
        try
        {
            await _cache.EvictIfNeeded();
            Assert.True(File.Exists(path), "B's pin must still protect the entry after A's release");

            _cache.Unpin(path);    // B finishes
            await _cache.EvictIfNeeded();
            Assert.False(File.Exists(path), "Entry is evictable once the last pin is released");
        }
        finally
        {
            _cache.Unpin(path);    // cleanup if an assert failed; no-op when unpinned
        }
    }

    // --- review 2026-09-02: permission-denied entries must not wedge the sweep ---

    /// <summary>
    /// Review fix (2026-09-02): a cache entry that cannot be deleted (access denied,
    /// e.g. a root-owned subdir left by a podman cp incident) must be skipped with a
    /// warning while the remaining entries still evict. Before the fix the
    /// UnauthorizedAccessException from Directory.Delete escaped EvictIfNeededCore onto
    /// the Alexa play path and, with the cache left over cap, failed every subsequent
    /// encode until restart.
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_UndeletableEntry_IsSkippedAndOthersStillEvicted()
    {
        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        if (!PermissionBitsDenyDelete())
        {
            return; // privileges bypass permission bits (root): no denial to simulate
        }

        string stuckDir = _cache.GetHlsDirectoryPath("aaaaaaaa-1111-1111-1111-111111111111", 1);
        string freshDir = _cache.GetHlsDirectoryPath("bbbbbbbb-2222-2222-2222-222222222222", 2);
        Directory.CreateDirectory(stuckDir);
        Directory.CreateDirectory(freshDir);
        await File.WriteAllTextAsync(Path.Combine(stuckDir, "stream.m3u8"), "#EXTM3U\n");
        await File.WriteAllBytesAsync(Path.Combine(stuckDir, "seg_000.ts"), new byte[1200 * 1024]);
        await File.WriteAllTextAsync(Path.Combine(freshDir, "stream.m3u8"), "#EXTM3U\n");
        await File.WriteAllBytesAsync(Path.Combine(freshDir, "seg_000.ts"), new byte[100 * 1024]);

        // Entry recency is max(dir atime, contained files' atimes): age BOTH sides so the
        // undeletable dir is deterministically the oldest (first eviction candidate).
        File.SetLastAccessTimeUtc(stuckDir, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(Path.Combine(stuckDir, "stream.m3u8"), DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(Path.Combine(stuckDir, "seg_000.ts"), DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(freshDir, DateTime.UtcNow);
        File.SetLastAccessTimeUtc(Path.Combine(freshDir, "stream.m3u8"), DateTime.UtcNow);
        File.SetLastAccessTimeUtc(Path.Combine(freshDir, "seg_000.ts"), DateTime.UtcNow);

        // Owner loses write (mode 555 on Linux): enumeration still works, recursive
        // delete is denied.
        File.SetAttributes(stuckDir, FileAttributes.ReadOnly);
        try
        {
            await _cache.EvictIfNeeded();

            Assert.True(Directory.Exists(stuckDir), "The undeletable entry must be skipped, not crash the sweep");
            Assert.True(File.Exists(Path.Combine(stuckDir, "seg_000.ts")), "The undeletable entry's contents must be untouched");
            Assert.False(Directory.Exists(freshDir), "Other entries must still evict after the undeletable one is skipped");
        }
        finally
        {
            File.SetAttributes(stuckDir, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// Review fix (2026-09-02): an HLS directory whose contents cannot be enumerated
    /// (search permission only) is skipped during the scan while sibling entries are
    /// still collected and evicted. Before the fix the UnauthorizedAccessException from
    /// GetFiles escaped EvictIfNeededCore.
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_UnreadableHlsDir_ScanSkipsItAndStillEvictsOthers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // the mode-bit setup below needs chmod; BCL attributes cannot drop read
        }

        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        string lockedDir = _cache.GetHlsDirectoryPath("cccccccc-3333-3333-3333-333333333333", 1);
        string freshDir = _cache.GetHlsDirectoryPath("dddddddd-4444-4444-4444-444444444444", 2);
        Directory.CreateDirectory(lockedDir);
        Directory.CreateDirectory(freshDir);
        await File.WriteAllTextAsync(Path.Combine(lockedDir, "stream.m3u8"), "#EXTM3U\n");
        await File.WriteAllBytesAsync(Path.Combine(lockedDir, "seg_000.ts"), new byte[1200 * 1024]);
        await File.WriteAllTextAsync(Path.Combine(freshDir, "stream.m3u8"), "#EXTM3U\n");
        await File.WriteAllBytesAsync(Path.Combine(freshDir, "seg_000.ts"), new byte[1200 * 1024]);
        File.SetLastAccessTimeUtc(freshDir, DateTime.UtcNow);
        File.SetLastAccessTimeUtc(Path.Combine(freshDir, "seg_000.ts"), DateTime.UtcNow);

        // Owner keeps search (x) but loses read (r): the playlist stat still works, the
        // GetFiles enumeration inside is denied.
        Chmod(lockedDir, "111");
        try
        {
            try
            {
                _ = Directory.GetFiles(lockedDir);
                return; // privileges bypass mode bits (root): no denial to simulate
            }
            catch (UnauthorizedAccessException)
            {
                // expected under a normal (non-root) user
            }

            await _cache.EvictIfNeeded();

            Assert.True(Directory.Exists(lockedDir), "The unreadable entry must be skipped, not crash the scan");
            Assert.True(File.Exists(Path.Combine(lockedDir, "seg_000.ts")), "The unreadable entry's contents must be untouched");
            Assert.False(Directory.Exists(freshDir), "The readable sibling entry must still be evicted");
        }
        finally
        {
            Chmod(lockedDir, "755");
        }
    }

    /// <summary>
    /// Review fix (2026-09-02): a cache directory the process cannot read must not
    /// throw out of the sweep (the throw lands on the Alexa play path and leaks the
    /// pre-encode pin); the scan logs and returns without evicting.
    /// </summary>
    [Fact]
    public async Task EvictIfNeeded_UnreadableCacheDir_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // the mode-bit setup below needs chmod; BCL attributes cannot drop read
        }

        Plugin.Instance!.Configuration.VideoAudioCacheSizeMB = 1;

        Directory.CreateDirectory(Path.GetDirectoryName(_cache.GetCacheFilePath("item1", 1))!);
        string path = _cache.GetCacheFilePath("item1", 1);
        await File.WriteAllTextAsync(path, new string('x', 1536 * 1024));

        Chmod(Path.GetDirectoryName(path)!, "000");
        try
        {
            try
            {
                _ = Directory.GetFiles(Path.GetDirectoryName(path)!);
                return; // privileges bypass mode bits (root): no denial to simulate
            }
            catch (UnauthorizedAccessException)
            {
                // expected under a normal (non-root) user
            }

            await _cache.EvictIfNeeded();
        }
        finally
        {
            Chmod(Path.GetDirectoryName(path)!, "755");
        }

        // Assert AFTER restoring permissions: through a mode-000 dir even stat is
        // denied, so File.Exists would report false for a file that is still there.
        Assert.True(File.Exists(path), "An unreadable cache dir means no sweep, not a crash");
    }

    /// <summary>
    /// True when permission bits actually deny THIS process a recursive delete of a
    /// read-only directory (the normal case for a non-root test user; root bypasses the
    /// bits, and the denial the callers simulate does not exist).
    /// </summary>
    private bool PermissionBitsDenyDelete()
    {
        string probeDir = Path.Combine(_tempDir, "perm-probe");
        Directory.CreateDirectory(probeDir);
        File.WriteAllText(Path.Combine(probeDir, "f"), "x");
        File.SetAttributes(probeDir, FileAttributes.ReadOnly);
        try
        {
            Directory.Delete(probeDir, true);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(probeDir, FileAttributes.Normal);
            Directory.Delete(probeDir, true);
            return true;
        }
    }

    /// <summary>Applies a permission mode via chmod (Linux-only, permission-denial tests).</summary>
    private static void Chmod(string path, string mode)
    {
        var psi = new ProcessStartInfo("chmod") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(path);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("chmod is not available");
        process.WaitForExit();
    }

    [Fact]
    public async Task EvictIfNeeded_NoCacheDir_DoesNotThrow()
    {
        // Use a cache pointing at a non-existent subdirectory that won't be created
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(Path.Combine(_tempDir, "nonexistent-path"));
        var cache = new VideoAudioCache(appPaths.Object, _logger);

        // Should not throw
        await cache.EvictIfNeeded();
    }

    [Fact]
    public void Cleanup_RemovesAllVariantsForItem()
    {
        // Create multiple cache entries for same item (different art ticks)
        string path1 = _cache.GetCacheFilePath("item1", 100);
        string path2 = _cache.GetCacheFilePath("item1", 200);
        string path3 = _cache.GetCacheFilePath("item1", 300);
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);

        File.WriteAllText(path1, "v1");
        File.WriteAllText(path2, "v2");
        File.WriteAllText(path3, "v3");

        // Create a different item's cache file — should NOT be deleted
        string otherPath = _cache.GetCacheFilePath("item2", 100);
        File.WriteAllText(otherPath, "other");

        _cache.Cleanup("item1");

        Assert.False(File.Exists(path1));
        Assert.False(File.Exists(path2));
        Assert.False(File.Exists(path3));
        Assert.True(File.Exists(otherPath), "Other item's cache should not be touched");
    }

    [Fact]
    public void Cleanup_NoCacheDir_DoesNotThrow()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(Path.Combine(_tempDir, "no-such-dir"));
        var cache = new VideoAudioCache(appPaths.Object, _logger);

        // Should not throw
        cache.Cleanup("any-item");
    }

    [Fact]
    public void CacheDir_UsesCorrectSubdirectory()
    {
        Assert.Contains("alexaskill-video-audio", _cache.CacheDir);
    }

    /// <summary>
    /// Verify that LockItemAsync returns a disposable that can be awaited and released.
    /// </summary>
    [Fact]
    public async Task LockItemAsync_CanAcquireAndRelease()
    {
        using (await _cache.LockItemAsync("item1", 100))
        {
            // Lock held — should succeed
        }

        // Lock released — should be able to acquire again
        using (await _cache.LockItemAsync("item1", 100))
        {
            // Re-acquired successfully
        }
    }

    /// <summary>
    /// Verify that different items can be locked concurrently (parallel generation).
    /// </summary>
    [Fact]
    public async Task LockItemAsync_DifferentItems_CanLockConcurrently()
    {
        var releaser1 = await _cache.LockItemAsync("item1", 100);
        var releaser2 = await _cache.LockItemAsync("item2", 100);

        // Both locks held simultaneously — different items, no contention
        releaser1.Dispose();
        releaser2.Dispose();
    }

    /// <summary>
    /// Verify that the same item cannot be locked concurrently — second locker waits.
    /// </summary>
    [Fact]
    public async Task LockItemAsync_SameItem_SecondLockerWaits()
    {
        var releaser1 = await _cache.LockItemAsync("item1", 100);

        bool secondAcquired = false;
        var secondTask = Task.Run(async () =>
        {
            await _cache.LockItemAsync("item1", 100);
            secondAcquired = true;
        });

        // Give the second task time to start waiting
        await Task.Delay(50);
        Assert.False(secondAcquired, "Second locker should be waiting");

        // Release first lock — second should proceed
        releaser1.Dispose();
        await secondTask;
        Assert.True(secondAcquired, "Second locker should have acquired after first released");
    }

    /// <summary>
    /// Verify that SemaphoreSlim objects are cleaned up from the dictionary after use
    /// (no memory leak on repeated lock/release cycles).
    /// </summary>
    [Fact]
    public async Task LockItemAsync_ReleasesAndCleansUp()
    {
        // Acquire and release multiple times
        for (int i = 0; i < 5; i++)
        {
            using (await _cache.LockItemAsync("cleanup-item", 100))
            {
                // Lock held
            }
        }

        // After all releases, internal dictionary should be empty or very small.
        // We can't directly inspect the dictionary, but we verify no deadlock occurs.
        using (await _cache.LockItemAsync("cleanup-item", 100))
        {
            // Clean re-acquire after multiple cycles
        }
    }
}
