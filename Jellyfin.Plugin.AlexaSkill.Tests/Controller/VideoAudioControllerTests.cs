using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

using Jellyfin.Plugin.AlexaSkill.Tests;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// Tests for the VideoAudioController — MP4 video generation endpoint
/// that combines album art + audio via ffmpeg for Alexa Echo Show VideoApp playback.
/// </summary>
[Collection("Plugin")]
public class VideoAudioControllerTests : PluginTestBase, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IMediaEncoder> _mediaEncoderMock;
    private readonly VideoAudioCache _cache;
    private readonly string _tempDir;
    private readonly PluginConfiguration _config;

    public VideoAudioControllerTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _mediaEncoderMock = new Mock<IMediaEncoder>();

        // Create a temp dir for the cache service used by controller tests
        _tempDir = CreateRegisteredTempDir("va-ctrl-test");

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(_tempDir);

        var cacheLogger = _loggerFactory.CreateLogger<VideoAudioCache>();
        _cache = new VideoAudioCache(appPaths.Object, cacheLogger);

        EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            cfg => { },
            "video-audio-test");

        _config = Plugin.Instance!.Configuration;
        _config.ServerAddress = "http://localhost:8096";
    }

    public void Dispose()
    {
        // JF-731 class-level backstop: every live encode this test may have
        // left dies HERE, once, before the temp-dir delete (the pid-file half
        // of the sweep needs the cache tree on disk).
        (bool cleanedUpAfterThisTest, string detail) = KillLeftoverEncodesAndDrainGate();

        _config.ServerAddress = string.Empty;

        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);

        // Assert LAST, after all the cleanup above ran. A leftover encode at
        // teardown is a test defect and must red the test whose teardown
        // observed it; the throw cannot mask a body failure on this xUnit
        // (2.7.0, probed on both TFMs 2026-10-04): DisposeTestClass AGGREGATES
        // a body exception and a Dispose exception into one AggregateException
        // preserving both messages and stack traces.
        if (cleanedUpAfterThisTest)
        {
            Assert.Fail(
                $"the JF-731 Dispose backstop had to clean up at this test's teardown ({detail}): "
                + "a leaked gated encode serializes every later gated test in this class behind its run "
                + "(the 299.42s class stall JF-730 removed), and a gate that never refilled hangs them; "
                + "the leaking test must end the encodes it starts "
                + "(the one exception: an encode born after a PRIOR test's sweep completed lands here; see "
                + "the backstop's coverage note)");
        }
    }

    /// <summary>
    /// The JF-731 class-level encode-gate backstop, owned by <see cref="Dispose"/>:
    /// kill every live encode this test may have left, so a test leaking a gated
    /// ffmpeg costs a bounded teardown instead of stranding the serialized class
    /// behind its sleep (the 299.42s stall JF-730 removed per-test finallys for).
    /// THREE kill halves run in every Dispose, each naming an encode the other
    /// two cannot see:
    /// (a) the live speed-encode registry, through the existing
    /// <see cref="KillLiveEncode"/> idiom over the
    /// <see cref="VideoAudioController.LiveSpeedEncodeCacheKeysForTest"/>
    /// snapshot seam. SCOPE, stated honestly (rework round): this registry is
    /// the AUDIO-SPEED path only (<see cref="VideoAudioController.RegisterLiveSpeedEncode"/>'s
    /// one production call site is the speed OnEncodeLive), so an encode on
    /// the episode, song, or audiobook paths never enters it;
    /// (b) a pid-file sweep of the WHOLE per-test cache tree. Scope: only fakes
    /// that write ffmpeg.pid (exactly one committed fake does; real ffmpeg
    /// writes no pid file, and neither do the stop-gated or transcode fakes);
    /// (c) a /proc process scan scoped to this test instance's temp dir
    /// (<see cref="SweepProcessesUnderTempDir"/>), the half that makes the
    /// coverage claim class-wide: every encode this class launches runs a
    /// WriteFakeFfmpeg script stored under that unique per-instance path, so
    /// the scan names a leaked encode on ANY endpoint path, registered or not,
    /// pid file or not. It would miss a real-ffmpeg launch (no test constructs
    /// one) and a process whose cmdline never mentions the temp dir (the
    /// directly planted /bin/sh sleepers, which hold no gate slot and are
    /// owned by their tests' own finallys).
    /// The obligation this backstop imposes on every test: a test that
    /// arranges an encode's death, whether through its own finally kills or
    /// through the monitor's stall-budget kill, must return only after
    /// OBSERVING the death (<see cref="FenceTempDirEncodesDeadAsync"/> is the
    /// shared fence); a kill signal in flight at teardown reads as a leak
    /// here, by design.
    /// When a kill proves a leak, the SAME re-arming kill-and-poll loop drains
    /// the gate (the JF-730 review-round shape: a launch the scenario abandoned
    /// acquires its slot only AFTER a kill frees one, then spawns its own
    /// sleeper; a one-shot pass runs before that zombie exists, so the kills
    /// repeat until the gate actually holds its cap, and the drain that waits
    /// is the drain that kills it). The refill check itself is UNCONDITIONAL
    /// (rework review round): a below-cap gate with NOTHING killable is either
    /// the mid-release transient of an already-exited encode (the gate's 500ms
    /// exit-poll; self-heals, and the loop returns the moment it lands) or a
    /// STUCK slot, an exit-poll release that never fires (the JF-730 teardown
    /// asserted exactly this; skipping it let that regression pass green and
    /// hang the next gated test on the untimed gate wait). The measured price
    /// of waiting out the transients is accepted: ~18.6s per TFM across this
    /// class (70ms average per Dispose, max ~600ms), and a stuck slot reds
    /// within a 2s zero-kill budget instead of the 10s a kill chain may need.
    /// DESIGN CONSTRAINTS carried from JF-730's measured experience:
    /// 1. The drain target is the CONFIGURED capacity of the CURRENT gate
    ///    instance (the <see
    ///    cref="VideoAudioController.EncodeGateConfiguredCapacityForTest"/>
    ///    seam), read fresh on every poll, never a count captured at test
    ///    entry: an entry snapshot captures whatever transient drain a PRIOR
    ///    test's 500ms exit-poll was mid-flight, and the teardown's own compare
    ///    then false-reds (observed live on both TFMs during JF-730).
    /// 2. The assert lives in <see cref="Dispose"/>, deliberately AFTER the
    ///    cleanup, and reds the test whose teardown observed the leak: the
    ///    leaker itself, except the post-sweep-arrival corner of point 3,
    ///    where a late-born zombie lands on the successor (the no-masking
    ///    probe evidence sits on the assert).
    /// 3. Coverage corners, stated honestly: an encode STARTED after this
    ///    sweep completed escapes everything (the JF-704 birth fault-observation
    ///    machinery exists to keep stranded endpoints from outliving their
    ///    test at all).
    /// </summary>
    /// <returns>
    /// Whether the teardown had to clean up after this test (a killed leftover
    /// encode, or a gate that never refilled), and the triage detail for the
    /// failure message.
    /// </returns>
    private (bool CleanedUpAfterThisTest, string Detail) KillLeftoverEncodesAndDrainGate()
    {
        // DISTINCT targets (JF-731 review round): the re-arming loop re-kills
        // a slow-to-die zombie every 100ms pass, so counting kill signals per
        // pass would report one leak as dozens; the sets below count each
        // target once, whichever half named it or how many passes it survived.
        var killedRegistryKeys = new HashSet<string>();
        var killedPidPaths = new HashSet<string>();
        var killedTempDirPids = new HashSet<int>();
        DateTime drainStart = DateTime.UtcNow;
        while (true)
        {
            foreach (string cacheKey in VideoAudioController.LiveSpeedEncodeCacheKeysForTest())
            {
                if (KillLiveEncode(cacheKey))
                {
                    killedRegistryKeys.Add(cacheKey);
                }
            }

            foreach (string pidPath in SweepPidFileEncodes())
            {
                killedPidPaths.Add(pidPath);
            }

            foreach (int pid in SweepProcessesUnderTempDir())
            {
                killedTempDirPids.Add(pid);
            }

            bool killedAnything = killedRegistryKeys.Count > 0 || killedPidPaths.Count > 0 || killedTempDirPids.Count > 0;
            SemaphoreSlim gate = GateField();
            int configuredCap = VideoAudioController.EncodeGateConfiguredCapacityForTest;
            if (gate.CurrentCount == configuredCap)
            {
                return (killedAnything, $"gate refilled to its configured cap" + KillDetail(killedRegistryKeys, killedPidPaths, killedTempDirPids));
            }

            // Nothing killed: a transient needs at most one more 500ms exit-poll
            // tick, so a 2s budget reds a stuck slot fast; a kill chain (an
            // abandoned launch spawning its own sleeper after a kill frees a
            // slot) may legitimately need longer, so it gets the 10s budget.
            DateTime drainDeadline = drainStart.AddSeconds(killedAnything ? 10 : 2);
            if (DateTime.UtcNow >= drainDeadline)
            {
                return (true, $"gate still below its configured cap ({gate.CurrentCount}/{configuredCap} slots free) after the {drainDeadline.Subtract(drainStart).TotalSeconds:F0}s budget"
                    + (killedAnything ? string.Empty : " with NOTHING killable: a stuck slot (an exit-poll release that never landed?)")
                    + KillDetail(killedRegistryKeys, killedPidPaths, killedTempDirPids));
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// The per-half kill report appended to the backstop's failure detail (and
    /// to the green-path detail when a kill happened): distinct targets per
    /// half, capped key list, and the halves-may-overlap caveat.
    /// </summary>
    private static string KillDetail(IReadOnlyCollection<string> registryKeys, IReadOnlyCollection<string> pidPaths, IReadOnlyCollection<int> tempDirPids)
        => $"; {registryKeys.Count} registry kill target(s) (keys: {DescribeKeys(registryKeys)}), "
            + $"{pidPaths.Count} pid-file kill target(s), "
            + $"{tempDirPids.Count} temp-dir process kill target(s) (the halves may name the same encodes)";

    /// <summary>
    /// The pid-file half of the JF-731 backstop: scan the whole per-test cache
    /// tree (the cache's own <see cref="VideoAudioCache.CacheDir"/> root, the
    /// directory the encodes actually wrote into) for fake-ffmpeg pid files,
    /// covering the encodes the registry never saw (fault between process start
    /// and registration). Liveness-guarded per <see cref="KillEncodeByPidFile"/>,
    /// including its recycled-pid caveat. Enumeration is best-effort: a
    /// fire-and-forget monitor's debris cleanup can delete a directory
    /// mid-scan.
    /// </summary>
    /// <returns>The pid-file paths whose named encode was still live and got
    /// the kill signal (the caller dedupes across the drain loop's passes).</returns>
    private List<string> SweepPidFileEncodes()
    {
        var killedPidPaths = new List<string>();
        string cacheRoot = _cache.CacheDir;
        if (!Directory.Exists(cacheRoot))
        {
            return killedPidPaths;
        }

        try
        {
            foreach (string pidPath in Directory.EnumerateFiles(cacheRoot, "ffmpeg.pid", SearchOption.AllDirectories))
            {
                if (KillEncodeByPidFile(Path.GetDirectoryName(pidPath)!))
                {
                    killedPidPaths.Add(pidPath);
                }
            }
        }
        catch (IOException)
        {
            // Best effort: the tree can shift under the scan.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, same reason.
        }

        return killedPidPaths;
    }

    /// <summary>
    /// The process half of the JF-731 backstop (rework round): the LIVE
    /// processes whose command line references this test instance's temp dir.
    /// That path is what makes the probe safe and complete at once: every fake
    /// ffmpeg script lives under it (WriteFakeFfmpeg writes into
    /// <see cref="_tempDir"/>) and every launched fake's argv names its script
    /// path there, while CreateRegisteredTempDir mints a guid suffix, so the
    /// probe can never name another testhost's fakes (the two TFM testhosts run
    /// in parallel processes with disjoint temp dirs) or an ambient process.
    /// This is the half that covers a leaked gated encode on the episode,
    /// song, or audiobook endpoint paths (outside the speed-only registry) and
    /// any fake that writes no pid file (the stop-gated and transcode fakes).
    /// Limits, stated honestly: a real-ffmpeg launch carries no temp-dir
    /// reference and would escape (no test constructs one), a zombie's cmdline
    /// reads empty so already-killed encodes are skipped, and the liveness
    /// check carries the same recycled-pid caveat as
    /// <see cref="KillEncodeByPidFile"/>. Best-effort: pids can vanish between
    /// the directory listing and the cmdline read.
    /// </summary>
    private List<int> LiveTempDirEncodePids()
    {
        var pids = new List<int>();
        try
        {
            foreach (string procDir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(procDir), out int pid) || pid == Environment.ProcessId)
                {
                    continue;
                }

                // The cmdline filter runs BEFORE the liveness check (rework
                // review: the scan runs in every Dispose, and the cmdline read
                // rejects essentially every pid on the machine; the
                // Process.GetProcessById allocation belongs only to the ~one
                // match).
                string? cmdline = TryReadProcCmdline(procDir);
                if (cmdline is null || !cmdline.Contains(_tempDir, StringComparison.Ordinal) || ProcessDead(pid))
                {
                    continue;
                }

                pids.Add(pid);
            }
        }
        catch (IOException)
        {
            // Best effort: /proc shifts under every scan.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, same reason.
        }

        return pids;
    }

    /// <summary>
    /// The kill arm of the process half (see <see cref="LiveTempDirEncodePids"/>
    /// for the scope and its honest limits).
    /// </summary>
    /// <returns>The pids that got a kill signal (the caller dedupes across the
    /// drain loop's passes).</returns>
    private List<int> SweepProcessesUnderTempDir()
    {
        var killedPids = new List<int>();
        foreach (int pid in LiveTempDirEncodePids())
        {
            try
            {
                using var fake = System.Diagnostics.Process.GetProcessById(pid);
                fake.Kill(entireProcessTree: true);
                killedPids.Add(pid);
            }
            catch { /* raced to exit between the probe and the kill */ }
        }

        return killedPids;
    }

    /// <summary>
    /// The shared death fence for every site that arranges an encode's death
    /// (its own finally kills, or the monitor's stall-budget kill): poll until
    /// every fake this test launched is OBSERVED dead, bounded at 5s (the
    /// shared <see cref="TestHelpers.WaitUntilAsync"/>, hoisted under
    /// JF-419.3). <see cref="Process.Kill"/> delivers the signal without
    /// waiting for the exit observation, and the Dispose backstop counts a
    /// still-alive process as a leak; without this fence a correctly
    /// self-cleaned test could false-red on kill-delivery timing. A timeout is
    /// not asserted here: anything surviving its own kill for 5s stays live
    /// for the Dispose backstop to count and red honestly.
    /// </summary>
    private Task FenceTempDirEncodesDeadAsync()
        => WaitUntilAsync(() => LiveTempDirEncodePids().Count == 0, TimeSpan.FromSeconds(5), 50);

    /// <summary>
    /// Reads /proc/&lt;pid&gt;/cmdline as one string (NUL-separated argv), or
    /// null when the entry is unreadable (kernel thread, vanished pid, zombie:
    /// a zombie's cmdline reads empty, which the caller's Contains treats as
    /// no match, exactly the already-dead verdict the probe wants).
    /// </summary>
    private static string? TryReadProcCmdline(string procDir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(procDir, "cmdline"));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The failure-message key list, capped at five so a wide leak cannot drown
    /// the diagnosis (the killed-count in the same message carries the size).
    /// </summary>
    private static string DescribeKeys(IReadOnlyCollection<string> keys)
        => keys.Count == 0
            ? "none (an unregistered pid-file encode)"
            : string.Join(", ", keys.Take(5)) + (keys.Count > 5 ? $", ... (+{keys.Count - 5} more)" : string.Empty);

    /// <summary>
    /// Verify that the endpoint returns 400 when itemId is not a valid GUID.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamVideoAudio("not-a-guid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    // ========== JF-309 Stream Token Security Tests ==========

    /// <summary>
    /// A bare-GUID request with no token must be rejected (401) — the core security fix.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_NoToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        var controller = CreateController(); // no itemIdForToken → no token in query

        ActionResult result = await controller.StreamVideoAudio(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// An expired token must be rejected (401).
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_ExpiredToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        string itemId = Guid.NewGuid().ToString();

        // Manually construct a controller with an expired token.
        string expiredToken = StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret, TimeSpan.FromHours(-1));
        var controller = CreateController();
        controller.ControllerContext.HttpContext.Request.Query =
            new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["token"] = expiredToken });

        ActionResult result = await controller.StreamVideoAudio(itemId);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// A token minted for a different item must be rejected (401).
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_WrongItemToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        string itemId = Guid.NewGuid().ToString();
        string otherItemId = Guid.NewGuid().ToString();

        var controller = CreateController(otherItemId); // token minted for OTHER item

        ActionResult result = await controller.StreamVideoAudio(itemId);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// A segment request with no token must be rejected (401).
    /// </summary>
    [Fact]
    public async Task GetSegment_NoToken_Returns401()
    {
        var controller = CreateController(); // no token

        ActionResult result = await controller.GetSegment(Guid.NewGuid().ToString(), "seg_0000.ts");

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// Verify that the endpoint returns 404 when the item is not found.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_ItemNotFound_Returns404()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemId);
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(itemId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments produces correct arguments with album art URL,
    /// including codec, filter, and streaming flags.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_WithArtUrl_ContainsAllExpectedFlags()
    {
        string artUrl = "http://localhost:8096/Items/123/Images/Primary";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(artUrl, audioUrl, false, outputPath);

        // Verify key flags are present as individual tokens
        Assert.Contains("-loop", args);
        Assert.Contains("1", args);
        Assert.Contains(artUrl, args);
        Assert.Contains(audioUrl, args);
        Assert.Contains("libx264", args);
        Assert.Contains("stillimage", args);
        Assert.Contains("ultrafast", args);
        Assert.Contains("scale=1280x720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black", args);
        Assert.Contains("aac", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("frag_keyframe+empty_moov", args);
        Assert.Contains("-shortest", args);
        Assert.Contains(outputPath, args);
        Assert.DoesNotContain("lavfi", args);
        Assert.DoesNotContain("color=c=black", args);
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments produces correct arguments with black frame fallback.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_BlackFrame_ContainsLavfiInput()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(null, audioUrl, true, outputPath);

        Assert.Contains("-f", args);
        Assert.Contains("lavfi", args);
        Assert.Contains("color=c=black:s=1280x720:d=999", args);
        Assert.Contains(audioUrl, args);
        Assert.DoesNotContain("-loop", args);
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments returns individual tokens (not a concatenated string),
    /// which is the mechanism that prevents command-line injection (CWE-78/CWE-88).
    /// Shell metacharacters in URLs/paths are harmless when passed via ArgumentList.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_ReturnsTokenList_NotConcatenatedString()
    {
        // Use a URL with characters that would be dangerous in a shell string
        string maliciousArtUrl = "http://evil.host/item'; rm -rf /; '";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(maliciousArtUrl, audioUrl, false, outputPath);

        // The malicious URL must be a SINGLE token in the list — not split or interpreted
        Assert.Contains(maliciousArtUrl, args);
        // Verify it's a proper token list, not a single concatenated string
        Assert.True(args.Count > 10, "Should return many individual tokens, not a single string");
        // Verify no shell quoting artifacts — tokens are raw, ArgumentList handles escaping
        Assert.All(args, arg => Assert.DoesNotContain("\"", arg));
    }

    /// <summary>
    /// Verify that argument order is correct: input flags come before output format flags.
    /// This ensures ffmpeg receives arguments in the expected sequence.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_ArgumentOrder_IsCorrect()
    {
        string artUrl = "http://localhost:8096/Items/123/Images/Primary";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(artUrl, audioUrl, false, outputPath);

        // Input comes before codec args, codec args before output
        int inputIdx = args.IndexOf("-i");
        int codecIdx = args.IndexOf("-c:v");
        int outputIdx = args.IndexOf("-f");
        int shortestIdx = args.IndexOf("-shortest");

        Assert.True(inputIdx > 0, "-i should appear after input flags");
        Assert.True(codecIdx > inputIdx, "-c:v should appear after -i");
        Assert.True(outputIdx > codecIdx, "-f (output) should appear after -c:v");
        Assert.True(shortestIdx > outputIdx, "-shortest should appear after output format");
        Assert.Equal(outputPath, args[^1]);
    }

    /// <summary>
    /// Verify that the cache directory is created and the cache file path follows
    /// the expected pattern with itemId and art modification ticks.
    /// </summary>
    [Fact]
    public void CacheFilePath_Format_ContainsExpectedComponents()
    {
        string path = _cache.GetCacheFilePath("abc123def456", 637500000000000000L);

        Assert.Contains("abc123def456", Path.GetFileName(path));
        Assert.Contains("637500000000000000", Path.GetFileName(path));
        Assert.EndsWith(".mp4", path);
    }

    /// <summary>
    /// Verify that a valid cached file (>= 10 KB) is detected as a cache hit.
    /// </summary>
    [Fact]
    public async Task CacheHit_ReturnsExistingFile()
    {
        string path = _cache.GetCacheFilePath("test-item", 12345);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write enough data to exceed the minimum valid file size (10 KB)
        await File.WriteAllTextAsync(path, new string('x', 12 * 1024));

        var result = await _cache.GetCachedFile("test-item", 12345);

        Assert.NotNull(result);
        Assert.Equal(path, result!.FullName);
    }

    /// <summary>
    /// Verify that a cache miss returns null.
    /// </summary>
    [Fact]
    public async Task CacheMiss_ReturnsNull()
    {
        var result = await _cache.GetCachedFile("nonexistent", 0);

        Assert.Null(result);
    }

    /// <summary>
    /// Verify that cleanup removes all cached files for a given item.
    /// </summary>
    [Fact]
    public void Cleanup_RemovesAllVariants()
    {
        string path1 = _cache.GetCacheFilePath("item1", 100);
        string path2 = _cache.GetCacheFilePath("item1", 200);
        Directory.CreateDirectory(Path.GetDirectoryName(path1)!);
        File.WriteAllText(path1, "v1");
        File.WriteAllText(path2, "v2");

        _cache.Cleanup("item1");

        Assert.False(File.Exists(path1));
        Assert.False(File.Exists(path2));
    }

    /// <summary>
    /// Verify that the endpoint returns 400 when the item is a Folder
    /// (not a streamable media type). Folders don't implement IHasMediaSources
    /// and would cause ffmpeg to fail with a 500 from Jellyfin's /Audio/ endpoint.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_FolderItem_Returns400()
    {
        var folder = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Audiobooks",
            Id = Guid.NewGuid()
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(folder.Id)).Returns(folder);

        var controller = CreateController(folder.Id.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(folder.Id.ToString());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>
    /// Verify that on a cache miss, the controller returns a FileStreamResult
    /// (streaming while ffmpeg writes) instead of a PhysicalFileResult.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_CacheMiss_ReturnsFileStreamResult()
    {
        // Create a fake audio item with media sources
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Create a fake ffmpeg script that writes dummy MP4 data to the last argument and exits 0.
        // Uses eval+last arg extraction so the output path (last positional param) is correct
        // regardless of how many preceding flags ffmpeg receives.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg",
            // POSIX sh (dash) has no "${@: -1}": iterate to the last positional arg instead
            "for last_arg in \"$@\"; do :; done\n" +
            "dd if=/dev/zero bs=1024 count=12 of=\"$last_arg\" 2>/dev/null\n" +
            "exit 0\n");

        var controller = CreateController(audioItem.Id.ToString());
        controller.FfmpegPath = fakeFfmpegPath;

        // Set up HttpContext with RequestAborted so the controller can register
        // the client-disconnect callback. Re-apply the token query since the
        // fresh HttpContext replaces the one CreateController populated.
        var httpContext = new DefaultHttpContext
        {
            RequestAborted = CancellationToken.None
        };
        httpContext.Request.Query = controller.ControllerContext.HttpContext.Request.Query;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        ActionResult result = await controller.StreamVideoAudio(audioItem.Id.ToString());

        // On cache miss, the controller should return FileStreamResult (not PhysicalFileResult)
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("video/mp4", fileResult.ContentType);
        Assert.NotNull(fileResult.FileStream);
    }

    /// <summary>
    /// JF-518: when the startup wait window expires while ffmpeg is STILL RUNNING
    /// (output file not created yet), the endpoint must take its designed graceful
    /// path (500 + warning) instead of throwing InvalidOperationException from
    /// reading ExitCode on a live process.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_FfmpegStillRunningAtWindowExpiry_Returns500()
    {
        // Create a fake audio item with media sources
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Fake ffmpeg that stays alive well past the ~1s startup window and never
        // creates the output file.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-slow-start",
            "sleep 10\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(audioItem.Id.ToString(), loggerFactory);
        controller.FfmpegPath = fakeFfmpegPath;

        ActionResult result = await controller.StreamVideoAudio(audioItem.Id.ToString());

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);

        // Pin WHICH arm ran (both arms return the same 500): the still-running
        // diagnostic, not the exit-code one (JF-518 review finding 2).
        Assert.Contains(logRecords, r =>
            r.Level == LogLevel.Warning && r.Message.Contains("still running after ~1s", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-518 companion: when ffmpeg FAILS FAST (exits with a nonzero code before
    /// creating any output), the endpoint takes the same graceful 500 path through
    /// the exit-code diagnostic branch (the HasExited arm of the JF-518 guard).
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_FfmpegFailsFastWithoutOutput_Returns500()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Fake ffmpeg that exits immediately with a failure code and never creates
        // the output file.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-fast-fail",
            "exit 3\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(audioItem.Id.ToString(), loggerFactory);
        controller.FfmpegPath = fakeFfmpegPath;

        ActionResult result = await controller.StreamVideoAudio(audioItem.Id.ToString());

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, error.StatusCode);

        // Pin WHICH arm ran: the exit-code diagnostic with the fake's code 3.
        Assert.Contains(logRecords, r =>
            r.Level == LogLevel.Warning && r.Message.Contains("exit code 3", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verify that on a cache hit, the controller returns a PhysicalFileResult
    /// (with range processing enabled for seeking), not a FileStreamResult.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_CacheHit_ReturnsPhysicalFileResult()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Pre-populate cache with a valid file (>= 10 KB)
        string cachePath = _cache.GetCacheFilePath(audioItem.Id.ToString("D"), 0);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, new string('x', 12 * 1024));

        var controller = CreateController(audioItem.Id.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(audioItem.Id.ToString());

        // On cache hit, PhysicalFileResult is returned (not FileStreamResult)
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("video/mp4", physicalResult.ContentType);
        Assert.True(physicalResult.EnableRangeProcessing);
    }

    private VideoAudioController CreateController(
        string? itemIdForToken = null,
        ILoggerFactory? loggerFactory = null,
        Mock<IMediaSourceManager>? mediaSourceManager = null,
        string? ffmpegPath = null)
    {
        var controller = mediaSourceManager == null
            ? new VideoAudioController(
                _libraryManagerMock.Object,
                _mediaEncoderMock.Object,
                _cache,
                loggerFactory ?? _loggerFactory)
            : new VideoAudioController(
                _libraryManagerMock.Object,
                _mediaEncoderMock.Object,
                _cache,
                loggerFactory ?? _loggerFactory,
                mediaSourceManager.Object);
        if (ffmpegPath != null)
        {
            controller.FfmpegPath = ffmpegPath;
        }

        // Attach an HttpContext so ValidateStreamToken can read the query string.
        // When itemIdForToken is provided, mint a valid token for that item so the request passes
        // the token check. When null, no token is set (simulates a bare-GUID attack).
        var query = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        if (itemIdForToken != null && !string.IsNullOrEmpty(_config.StreamTokenSecret))
        {
            string token = StreamTokenHelper.Mint(itemIdForToken, _config.StreamTokenSecret);
            query["token"] = token;
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Request =
                {
                    Query = new QueryCollection(query)
                }
            }
        };

        return controller;
    }

    /// <summary>
    /// CreateController variant that also carries the JF-636 <c>?d=</c> device hint
    /// (the speed endpoint's supersede-kill reads it to stay device-scoped).
    /// </summary>
    private VideoAudioController CreateController(string itemIdForToken, string deviceHint, string? ffmpegPath = null)
    {
        var controller = CreateController(itemIdForToken, ffmpegPath: ffmpegPath);
        var existing = controller.ControllerContext.HttpContext.Request.Query;
        var query = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(
            existing.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.OrdinalIgnoreCase)
        {
            ["d"] = deviceHint
        };
        controller.ControllerContext.HttpContext.Request.Query = new QueryCollection(query);
        return controller;
    }

    // ========== HLS Tests ==========

    /// <summary>
    /// Verify that the HLS endpoint returns 400 when itemId is not a valid GUID.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamHlsVideoAudio("not-a-guid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>
    /// A bare-GUID HLS playlist request with no token must be rejected (401).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_NoToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        var controller = CreateController(); // no itemIdForToken → no token in query

        ActionResult result = await controller.StreamHlsVideoAudio(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>
    /// Verify that the HLS endpoint returns 404 when the item is not found.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_ItemNotFound_Returns404()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemId);
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(itemId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    /// <summary>
    /// Verify that the HLS endpoint returns 400 when the item is a Folder
    /// (not a streamable media type).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_FolderItem_Returns400()
    {
        var folder = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Audiobooks",
            Id = Guid.NewGuid()
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(folder.Id)).Returns(folder);

        var controller = CreateController(folder.Id.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(folder.Id.ToString());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>
    /// Verify that BuildHlsFfmpegArguments produces correct arguments with HLS-specific flags,
    /// including segment template, base URL, and HLS time/list size settings.
    /// </summary>
    [Fact]
    public void BuildHlsFfmpegArguments_WithArtUrl_ContainsAllExpectedFlags()
    {
        string artUrl = "http://localhost:8096/Items/123/Images/Primary";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string playlistPath = "/tmp/test-output/stream.m3u8";
        string segmentPath = "/tmp/test-output/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/456/segments/";

        List<string> args = VideoAudioController.BuildHlsFfmpegArguments(
            artUrl, audioUrl, false, playlistPath, segmentPath, hlsBaseUrl);

        // Verify key input flags
        Assert.Contains("-loop", args);
        Assert.Contains("1", args);
        Assert.Contains(artUrl, args);
        Assert.Contains(audioUrl, args);

        // Verify codec flags
        Assert.Contains("libx264", args);
        Assert.Contains("stillimage", args);
        Assert.Contains("ultrafast", args);
        Assert.Contains("aac", args);

        // Verify HLS-specific flags
        Assert.Contains("-hls_time", args);
        Assert.Contains("4", args);
        Assert.Contains("-hls_list_size", args);
        Assert.Contains("0", args);
        Assert.Contains("-hls_flags", args);
        Assert.Contains("append_list", args);
        Assert.Contains("-hls_segment_filename", args);
        Assert.Contains(segmentPath, args);
        Assert.Contains("-hls_base_url", args);
        Assert.Contains(hlsBaseUrl, args);

        // Verify output
        Assert.Contains("-shortest", args);
        Assert.Contains(playlistPath, args);

        // Verify no MP4 output format flags (HLS uses its own format)
        Assert.DoesNotContain("frag_keyframe+empty_moov", args);
    }

    /// <summary>
    /// Verify that BuildHlsFfmpegArguments produces correct arguments with black frame fallback.
    /// </summary>
    [Fact]
    public void BuildHlsFfmpegArguments_BlackFrame_ContainsLavfiInput()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string playlistPath = "/tmp/test-output/stream.m3u8";
        string segmentPath = "/tmp/test-output/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/456/segments/";

        List<string> args = VideoAudioController.BuildHlsFfmpegArguments(
            null, audioUrl, true, playlistPath, segmentPath, hlsBaseUrl);

        Assert.Contains("-f", args);
        Assert.Contains("lavfi", args);
        Assert.Contains("color=c=black:s=1280x720:d=999", args);
        Assert.Contains(audioUrl, args);
        Assert.DoesNotContain("-loop", args);
    }

    // ========== Codec-Aware Audio Copy Tests (JF-293) ==========

    /// <summary>
    /// Verify that BuildFfmpegArguments emits -c:a copy (and no bitrate flag) when the
    /// source audio codec is mp3. This avoids a 3-10s AAC re-encode per song.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_SourceMp3_EmitsAudioCopyWithoutBitrate()
    {
        string artUrl = "http://localhost:8096/Items/123/Images/Primary";
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(
            artUrl, audioUrl, false, outputPath, sourceAudioCodec: "mp3");

        // -c:a copy is present as adjacent tokens
        int codecIdx = args.IndexOf("-c:a");
        Assert.True(codecIdx >= 0, "args should contain -c:a");
        Assert.Equal("copy", args[codecIdx + 1]);

        // No bitrate flag — copy does not transcode
        Assert.DoesNotContain("-b:a", args);
        Assert.DoesNotContain("128k", args);
    }

    /// <summary>
    /// Verify that BuildFfmpegArguments emits -c:a copy for AAC sources too.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_SourceAac_EmitsAudioCopy()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(
            null, audioUrl, true, outputPath, sourceAudioCodec: "aac");

        int codecIdx = args.IndexOf("-c:a");
        Assert.Equal("copy", args[codecIdx + 1]);
        Assert.DoesNotContain("-b:a", args);
    }

    /// <summary>
    /// Verify that an incompatible source codec (flac) falls back to AAC transcode.
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_SourceFlac_TranscodesToAac()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        List<string> args = VideoAudioController.BuildFfmpegArguments(
            null, audioUrl, true, outputPath, sourceAudioCodec: "flac");

        int codecIdx = args.IndexOf("-c:a");
        Assert.Equal("aac", args[codecIdx + 1]);
        Assert.Contains("-b:a", args);
        Assert.Contains("128k", args);
    }

    /// <summary>
    /// Verify that an unknown/null source codec defaults to AAC transcode (safe behavior).
    /// </summary>
    [Fact]
    public void BuildFfmpegArguments_SourceUnknown_TranscodesToAac()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string outputPath = "/tmp/test-output.mp4";

        // null codec
        List<string> argsNull = VideoAudioController.BuildFfmpegArguments(
            null, audioUrl, true, outputPath, sourceAudioCodec: null);
        int codecIdxNull = argsNull.IndexOf("-c:a");
        Assert.Equal("aac", argsNull[codecIdxNull + 1]);
        Assert.Contains("128k", argsNull);

        // omitted codec (default) — backwards compatible with pre-JF-293 callers
        List<string> argsDefault = VideoAudioController.BuildFfmpegArguments(
            null, audioUrl, true, outputPath);
        Assert.Equal("aac", argsDefault[argsDefault.IndexOf("-c:a") + 1]);
    }

    /// <summary>
    /// Verify that the HLS arg builder also honors -c:a copy for mp3 sources.
    /// </summary>
    [Fact]
    public void BuildHlsFfmpegArguments_SourceMp3_EmitsAudioCopy()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string playlistPath = "/tmp/test-output/stream.m3u8";
        string segmentPath = "/tmp/test-output/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/456/segments/";

        List<string> args = VideoAudioController.BuildHlsFfmpegArguments(
            null, audioUrl, true, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec: "mp3");

        int codecIdx = args.IndexOf("-c:a");
        Assert.Equal("copy", args[codecIdx + 1]);
        Assert.DoesNotContain("-b:a", args);
    }

    /// <summary>
    /// Verify the video encoder forces a keyframe every 1s (-g 1) so the HLS muxer can
    /// cut segments at -hls_time boundaries. Without -g, libx264's default GOP (250) at
    /// 1fps yields a keyframe only every ~4min → segments span ~4min → the first segment
    /// takes ~18s of encode time to appear (the cache-miss "forever" delay).
    /// </summary>
    [Fact]
    public void BuildHlsFfmpegArguments_ForcesKeyframeEverySecond()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string playlistPath = "/tmp/test-output/stream.m3u8";
        string segmentPath = "/tmp/test-output/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/456/segments/";

        List<string> args = VideoAudioController.BuildHlsFfmpegArguments(
            null, audioUrl, true, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec: null);

        int gIdx = args.IndexOf("-g");
        Assert.True(gIdx >= 0, "expected -g (keyframe interval) in HLS args");
        Assert.Equal("1", args[gIdx + 1]);
    }

    /// <summary>
    /// Verify that the HLS arg builder transcodes incompatible codecs to AAC.
    /// </summary>
    [Fact]
    public void BuildHlsFfmpegArguments_SourceFlac_TranscodesToAac()
    {
        string audioUrl = "http://localhost:8096/Audio/456/stream?static=true";
        string playlistPath = "/tmp/test-output/stream.m3u8";
        string segmentPath = "/tmp/test-output/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/456/segments/";

        List<string> args = VideoAudioController.BuildHlsFfmpegArguments(
            null, audioUrl, true, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec: "flac");

        int codecIdx = args.IndexOf("-c:a");
        Assert.Equal("aac", args[codecIdx + 1]);
        Assert.Contains("128k", args);
    }

    /// <summary>
    /// Verify the BuildAudioCodecArgs helper directly: mp3/aac → copy, others/null → transcode.
    /// </summary>
    [Theory]
    [InlineData("mp3", "copy")]
    [InlineData("aac", "copy")]
    [InlineData("MP3", "copy")] // case-insensitive
    [InlineData("flac", "aac")]
    [InlineData("opus", "aac")]
    [InlineData("", "aac")]
    [InlineData(null, "aac")]
    public void BuildAudioCodecArgs_SelectsCopyOrTranscodeByCodec(string? codec, string expectedFirst)
    {
        string[] audioArgs = VideoAudioController.BuildAudioCodecArgs(codec);

        Assert.Equal("-c:a", audioArgs[0]);
        Assert.Equal(expectedFirst, audioArgs[1]);
    }

    /// <summary>
    /// Verify that the HLS cache hit path serves the playlist with the correct
    /// HLS content type (application/vnd.apple.mpegurl). A token is present
    /// (CreateController mints one), so the serve is the token-rewritten
    /// ContentResult shape, not the token-less PhysicalFile shape.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CacheHit_ServesPlaylistWithCorrectHlsContentType()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Pre-populate the HLS cache with a COMPLETED encode's playlist: since
        // JF-676 the warm-cache path validates the cached playlist (the
        // ticks-scoped debris verdict), and a no-ENDLIST playlist with no live
        // own generation is debris, not a cache hit.
        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");

        var controller = CreateController(audioItem.Id.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        // JF-309: when a token is present (as it is here via CreateController), the playlist is
        // rewritten with ?token= on each segment line and served as ContentResult (not PhysicalFile).
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", contentResult.ContentType);
    }

    // ---- JF-677: one full playlist read per validated warm-cache serve ----

    /// <summary>
    /// JF-677: a validated warm-cache serve performs exactly ONE full read of
    /// stream.m3u8. Before JF-677 the song path paid two full reads (the
    /// ticks-scoped debris verdict's validating read for ENDLIST, then the
    /// token-rewrite serve's re-read of the same file); the verdict's read is
    /// now threaded to the serve (ValidatedHlsCache.Content) behind
    /// ResolveServeContentAsync's O(1) existence probe. The vanish-at-serve
    /// contract that probe preserves is pinned DIRECTLY at the episode and
    /// audio-variant serve sites (the FastPathCacheVanishedAtServe twins) and
    /// at the song site (the JF-677 song twin in the W3 section); the
    /// audiobook path shares the same probe but its serve translates a
    /// vanished read into its own PhysicalFile fallback rather than the
    /// re-encode fall-through, so it has no twin here. RED PROOF: making the
    /// serve ignore the threaded content (a fresh read) raises this pin's
    /// count to 2.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CacheHit_ValidatedServe_ReadsPlaylistOnce()
    {
        (Guid itemId, string hlsDir, string playlistPath) = SetupSongVanishFixture("JF-677 Read Count Song");
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");

        var controller = CreateController(itemId.ToString());
        controller.FfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-readcount-song");

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await controller.StreamHlsVideoAudio(itemId.ToString());

        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_000.ts?token=", contentResult.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// JF-677, the episode twin (ServeEpisodePlaylistAsync's RESUME row): a
    /// completed episode cache served with a start position slices the
    /// VERDICT'S read, not a second fresh read. Same red proof as the song
    /// twin: a serve that ignores the threaded content reads twice.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_CacheHitWithResume_ValidatedServe_ReadsPlaylistOnce()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-677 Read Count S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXTINF:4.000,\nseg_0002.ts\n"
            + "#EXTINF:4.000,\nseg_0003.ts\n#EXTINF:4.000,\nseg_0004.ts\n#EXTINF:4.000,\nseg_0005.ts\n#EXT-X-ENDLIST\n");

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-readcount"));

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString(), 16 * TimeSpan.TicksPerSecond);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0004.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0003.ts", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// JF-677, the audiobook twin (the one validated path outside the
    /// TryServeValidatedHlsCacheAsync wrapper, and the only one whose verdict
    /// hook inspects the content: the undercount check): the fast-path cache
    /// hit serves from the verdict's single read through
    /// ServeAudiobookPlaylistAsync. Same red proof as the song twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CacheHit_ValidatedServe_ReadsPlaylistOnce()
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-677 Read Count Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Chapter 2", Id = Guid.NewGuid() };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        // COMPLETED concat cache: ENDLIST with at least one segment per chapter
        // (the undercount hook's bar), so the verdict validates on its read row.
        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");

        var controller = CreateController(parentId.ToString());
        controller.FfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-readcount-book");

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// JF-677, the audio-variant twin (the fourth validated path, keyed by the
    /// variant cache key against the episode registry): the same one-read
    /// contract through ServePlaylistWithTokenAsync. Same red proof as the
    /// song twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_CacheHit_ValidatedServe_ReadsPlaylistOnce()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "JF-677 Read Count Audio S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(39).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string cacheKey = VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), 0);
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory: null, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-readcount-audio"));

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await controller.StreamHlsEpisodeAudio(episode.Id.ToString(), 0);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// JF-677 in-lock pin core, shared by the four path twins (the gate-marker
    /// F2 gap: the in-lock threading sites had no read-count pin, so an edit
    /// dropping valid.Content at any of them silently re-read, the exact
    /// double-read this task removes, with the full suite green) and, since
    /// JF-680, by the two in-lock own-live prewrite pins. Holds the
    /// per-(key, ticks) cache lock, starts the endpoint (its fast path misses
    /// the not-yet-planted cache and parks on the lock), plants the warm-cache
    /// state the caller passes (the completed encode's playlist on the JF-677
    /// twins; a live partial plus the prewrite on the JF-680 pins),
    /// releases the lock so the endpoint's in-lock double-check hits the warm
    /// cache, and returns the result. The IsCompleted assert converts a broken
    /// construction (a fast-path hit, which would serve and complete without
    /// the lock) into an honest failure instead of a false pass; the twins add
    /// the log assertions that pin WHICH branch served. JF-681: the assert
    /// distinguishes a FAULTED endpoint task from a fast-path hit
    /// (<see cref="ParkAssertFailureMessage"/> unwraps the exception, so a
    /// pre-lock crash surfaces its real cause instead of reading as a fast
    /// serve). Passing <paramref name="controller"/> additionally makes the
    /// in-lock-vs-fast-path attribution DETERMINISTIC: the helper wires the
    /// controller's InLockWarmCacheProbeForTest seam and asserts the probe
    /// fired once the endpoint settles (it can only fire inside the per-item
    /// lock scope, so a pre-lock stall longer than the park window whose request then served from the
    /// fast path fails here instead of passing under a false attribution);
    /// omit the parameter for constructions whose endpoint never reaches a
    /// lock scope (the fault-observation pin). JF-700: a FAULTING endpoint
    /// task (the breach pins' FileNotFoundException) never reaches a
    /// post-await assert, so the same assert also runs in the fault path
    /// BEFORE the rethrow; green (probe fired) rethrows the endpoint's own
    /// exception for the pin's ThrowsAsync, red (probe never fired) fails
    /// the ThrowsAsync with <see cref="InLockProbeNotFiredMessage"/> (plus
    /// the endpoint's real fault embedded) instead of letting a void
    /// attribution ride the ThrowsAsync unchecked. JF-704: a park assert or
    /// plant that THROWS must not strand the parked endpoint either: the
    /// finally below releases the gate, so the still-running endpoint would
    /// later run its in-lock encode after the test method (and its
    /// using-scoped fixtures) had exited, landing static registry writes
    /// after teardown and leaving its eventual fault as an
    /// UnobservedTaskException candidate. Two mechanisms: a fault-observation
    /// backstop attached at the endpoint's birth
    /// (<see cref="MarkEndpointFaultObserved"/>, covering the
    /// fault-observation leg on EVERY exit, including a settle-budget timeout
    /// on the normal path below), and, on the throwing arm only, the catch
    /// observes the endpoint BEFORE rethrowing
    /// (<see cref="ObserveStrandedEndpointAsync"/>, which pulls the settle
    /// inside the test's lifetime); the non-throwing paths never enter the
    /// catch, so the park window (<see cref="ParkWindowMs"/>) and the probe-assert ordering are
    /// unchanged.
    /// </summary>
    private static async Task<ActionResult> ServeInLockWarmCacheAsync(
        Func<Task<IDisposable>> acquireLock,
        Func<Task<ActionResult>> startEndpoint,
        Action plantWarmCache,
        VideoAudioController? controller = null)
    {
        bool inLockProbeFired = false;
        if (controller != null)
        {
            controller.InLockWarmCacheProbeForTest = _ => inLockProbeFired = true;
        }

        IDisposable gate = await acquireLock();
        Task<ActionResult>? endpointTask = null;
        try
        {
            try
            {
                endpointTask = startEndpoint();
                MarkEndpointFaultObserved(endpointTask);
                await Task.Delay(ParkWindowMs);
                Assert.False(
                    endpointTask.IsCompleted,
                    ParkAssertFailureMessage(endpointTask));
                plantWarmCache();
            }
            finally
            {
                gate.Dispose();
            }
        }
        catch
        {
            // JF-704: the inner finally already released the gate, so the
            // parked endpoint is free to settle. Observe it before the
            // rethrow; the plant failure (or park assert) stays the exception
            // the caller sees.
            await ObserveStrandedEndpointAsync(endpointTask).ConfigureAwait(false);
            throw;
        }

        // The one settle-state attribution assert (JF-681 success path, JF-700
        // fault path): called below, whichever way the endpoint settled. The
        // feature toggle is fixed at entry (controller null = no attribution);
        // on the fault path the endpoint's real fault is embedded in the
        // message (the JF-681 park-assert unwrap pattern) instead of discarded
        // by the assert that replaces it.
        bool assertAttribution = controller != null;
        void AssertProbeFired(Exception? endpointFailure)
        {
            if (!assertAttribution || inLockProbeFired)
            {
                return;
            }

            string message = InLockProbeNotFiredMessage;
            if (endpointFailure != null)
            {
                message += " THE ENDPOINT FAULTED, KEPT FOR TRIAGE: "
                    + $"{endpointFailure.GetType().FullName}: {endpointFailure.Message}{Environment.NewLine}{endpointFailure.StackTrace}";
            }

            Assert.Fail(message);
        }

        ActionResult result;
        try
        {
            result = await endpointTask.WaitAsync(EndpointSettleBudget).ConfigureAwait(false);
        }
        catch (Exception) when (endpointTask.IsFaulted)
        {
            // JF-700: a faulting endpoint (the breach pins' FileNotFoundException)
            // never reaches the post-settle assert, so assert BEFORE the rethrow.
            // The filter admits only endpoint faults: a WaitAsync timeout or a
            // canceled endpoint propagates untouched with its own diagnosis
            // instead of reading as a void attribution. The embedded fault comes
            // from endpointTask, not the caught exception: in the timeout-vs-fault
            // race the caught one is the TimeoutException while the task's real
            // fault is what the triage text must carry.
            AssertProbeFired(endpointTask.Exception!.GetBaseException());
            throw;
        }

        AssertProbeFired(null);

        return result;
    }

    /// <summary>
    /// The one settle budget for the helper's endpoint awaits (JF-704 hoisted:
    /// the stranded-endpoint settle below must wait on the same budget the
    /// normal settle uses, never a shorter one that strands again).
    /// </summary>
    // GATE-MARKER NOTE: if the inner finally's gate.Dispose() itself throws
    // before releasing the semaphore, the catch's observation await burns this
    // full budget (the endpoint stays parked forever: it never settles, never
    // faults, so neither observation mechanism has anything to see) and the
    // endpoint task leaks for the process lifetime. Pre-existing corner (the
    // old code leaked identically); documented rather than hardened because a
    // throwing semaphore Dispose is not a shape any test constructs.
    private static readonly TimeSpan EndpointSettleBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The park window the helper gives the endpoint to walk its pre-lock path
    /// (validation, cache miss, arg build, all in-process) and block on the
    /// per-item lock before the not-completed assert. JF-730 measured the
    /// requirement well below this value on this host (the sweep record lives
    /// in the backlog task); the failure mode of a too-short window is a LOUD
    /// red (the probe assert's <see cref="InLockProbeNotFiredMessage"/> or the
    /// not-completed assert itself), never a false pass, since both asserts
    /// read the endpoint's own outcome. The one residual: on a SLOW host a
    /// scheduler stall can push the pre-lock path past the window, the plant
    /// then lands while the endpoint is still walking, and the endpoint
    /// serves the warm cache from its fast path without ever entering the
    /// lock scope, so the probe assert reds as a false red. That environment
    /// red is this constant's trade: the remedy is RAISING this value, never
    /// loosening the asserts (a fast-path serve from a broken fixture is a
    /// different, real red that raising cannot fix).
    /// </summary>
    private const int ParkWindowMs = 250;

    /// <summary>
    /// JF-704 fault-observation backstop, attached at the endpoint's birth so
    /// EVERY exit of <see cref="ServeInLockWarmCacheAsync"/> observes an
    /// eventual fault: Task.WaitAsync's timeout does NOT observe the inner
    /// task, so without this the settle-budget expiry on the normal path (an
    /// endpoint that neither completes nor faults within budget) would leave
    /// its eventual fault unobserved. Scope, honestly: the backstop covers
    /// only the fault-observation leg of the JF-704 harm. On that timeout
    /// exit the endpoint still runs its encode past the test's lifetime and
    /// still lands static registry writes after teardown; pulling the settle
    /// inside the test's lifetime happens solely on the throwing arm, via
    /// <see cref="ObserveStrandedEndpointAsync"/>. Reading
    /// <c>t.Exception</c> marks the fault observed; OnlyOnFaulted plus
    /// ExecuteSynchronously keeps this a zero-cost registration until a fault
    /// actually lands. Pinned since JF-726 by the IL-shape pin
    /// <see cref="ServeInLockWarmCacheHelper_BirthBackstop_AttachesOnlyOnFaultedContinuationBeforeParkAssert"/>
    /// (one methoddef, one birth-site call before the park assert, the
    /// documented options operand, the Exception read, and the
    /// argument-identity tie to the started endpoint); the residual gap that
    /// pin states honestly is the RUNTIME observation effect, provable only
    /// via UnobservedTaskException after a forced GC (finalizer-timing
    /// flaky) or a marker inside the continuation (rejected: it would modify
    /// this mechanism and break its zero-allocation static-lambda shape).
    /// </summary>
    private static void MarkEndpointFaultObserved(Task<ActionResult> endpointTask)
    {
        _ = endpointTask.ContinueWith(
            static t =>
            {
                _ = t.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// JF-704 stranded-endpoint settle, the plant-throw (and park-assert) arm
    /// of <see cref="ServeInLockWarmCacheAsync"/>: the endpoint was parked on
    /// the gate the failure just released, so it is still running and will
    /// run its in-lock work AFTER the caller's test method has returned
    /// unless someone awaits it here (inside the test's lifetime, with its
    /// using-scoped fixtures still alive). Awaits the endpoint under the
    /// helper's settle budget and swallows the outcome: the rethrown failure
    /// (the plant's own exception, or the park assert) is the one under test,
    /// and this await is what pulls the settle inside the test's lifetime
    /// (the eventual-fault observation half is the birth backstop, see
    /// <see cref="MarkEndpointFaultObserved"/>). A null endpoint means
    /// startEndpoint itself threw (nothing started, nothing stranded);
    /// GATE-MARKER NOTE: that skip assumes a SINGLE-EXPRESSION startEndpoint
    /// delegate (start-then-return) - a future call site that starts work and
    /// THEN throws synchronously would strand that work with nothing here
    /// covering it (all current call sites are single-expression).
    /// </summary>
    private static async Task ObserveStrandedEndpointAsync(Task<ActionResult>? endpointTask)
    {
        if (endpointTask == null)
        {
            return;
        }

        try
        {
            _ = await endpointTask.WaitAsync(EndpointSettleBudget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the endpoint's settle outcome is not
            // under test on this path. The await observes whatever settle
            // happened inside the budget, and the birth backstop holds the
            // observation on the budget-expiry corner.
        }
    }

    /// <summary>The one failure message for the helper's in-lock attribution assert (JF-681), owned here once.</summary>
    private const string InLockProbeNotFiredMessage =
        "the JF-681 in-lock probe never fired, so the request settled WITHOUT entering the per-item lock scope (a fast-path serve won the race past the park window, or the endpoint faulted before reaching the lock) and this pin's in-lock attribution is void";

    /// <summary>
    /// The park assert's message (JF-681): the original single message conflated
    /// the two ways a task can be completed at the park-window probe. A RAN-TO-COMPLETION
    /// task IS a fast-path hit and keeps the original guidance verbatim; a FAULTED
    /// task is a crash before the lock (a pre-lock NRE, an argument throw) whose
    /// real exception the suite would otherwise never surface (nothing awaited the
    /// task yet, so the fault stayed unobserved and triage read the crash as a
    /// fast serve), so its exception is unwrapped into the message; a CANCELED
    /// task names itself (there is no exception to unwrap).
    /// </summary>
    private static string ParkAssertFailureMessage(Task<ActionResult> endpointTask)
    {
        const string BaseMessage =
            "the endpoint must be parked on the per-item lock when the warm cache is planted (a fast-path hit would have completed without the lock)";

        if (endpointTask.IsFaulted)
        {
            // The TPL guarantees a faulted task's Exception is non-null, so the
            // unwrap is total here.
            Exception failure = endpointTask.Exception!.GetBaseException();
            return BaseMessage
                + " THE TASK FAULTED BEFORE PARKING: the real cause is the exception below, not a fast-path hit. "
                + $"{failure.GetType().FullName}: {failure.Message}{Environment.NewLine}{failure.StackTrace}";
        }

        if (endpointTask.IsCanceled)
        {
            return BaseMessage + " THE TASK WAS CANCELED BEFORE PARKING (no fault to unwrap).";
        }

        return BaseMessage;
    }

    /// <summary>
    /// JF-681 park-assert fault observation: a task that faults BEFORE parking (a
    /// pre-lock NRE, an argument throw) used to trip the helper's IsCompleted
    /// assert with only the generic "must be parked" wording, so the real
    /// exception stayed unobserved (nothing had awaited the task) and triage read
    /// the crash as a fast-path hit. The helper now unwraps the fault into the
    /// assert message (see <see cref="ParkAssertFailureMessage"/>). RED PROOF:
    /// reverting <see cref="ParkAssertFailureMessage"/> to the old constant drops
    /// the exception text from the message and this pin fails on the Contains
    /// asserts.
    /// </summary>
    [Fact]
    public async Task ServeInLockWarmCacheHelper_FaultedEndpointTask_SurfacesExceptionThroughParkAssert()
    {
        var fault = new InvalidOperationException("pre-lock boom (JF-681 fault observation)");

        var failed = await Assert.ThrowsAsync<Xunit.Sdk.FalseException>(() => ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(Guid.NewGuid().ToString("D"), 0),
            () => Task.FromException<ActionResult>(fault),
            () => { }));

        Assert.Contains("THE TASK FAULTED BEFORE PARKING", failed.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", failed.Message, StringComparison.Ordinal);
        Assert.Contains("pre-lock boom (JF-681 fault observation)", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-704 plant-throw observation: a plantWarmCache that throws used to
    /// propagate its failure while the PARKED endpoint task kept running
    /// unobserved (the helper's finally had released the gate, so the endpoint
    /// later entered the lock scope and ran its encode after the test method
    /// and its using-scoped fixtures had exited: static registry writes after
    /// teardown, UnobservedTaskException candidate on its eventual fault).
    /// The helper now observes the stranded endpoint before rethrowing (see
    /// <see cref="ObserveStrandedEndpointAsync"/>). GREEN: the plant's own
    /// exception is the one that surfaces (identity, not type: the endpoint's
    /// own fault must not replace it) AND the endpoint had already settled by
    /// then (the settle flag is written inside the endpoint before its fault,
    /// so the flag's true value is reachable only through the helper's await
    /// of the settle). The endpoint parks on a REAL per-item gate and holds a
    /// 150ms settle distance after re-acquiring it, so the un-observed rethrow
    /// (which fires at gate release, microseconds earlier) provably loses the
    /// race. RED PROOF: removing the catch's ObserveStrandedEndpointAsync call
    /// rethrows at gate release while the endpoint still holds its settle
    /// distance, the flag reads false, and this pin fails on the settle assert.
    /// </summary>
    [Fact]
    public async Task ServeInLockWarmCacheHelper_ThrowingPlant_ObservesStrandedEndpointBeforeRethrow()
    {
        string key = Guid.NewGuid().ToString("D");
        bool endpointSettledBeforePlantSurfaced = false;
        var plant = new IOException("plant boom (JF-704 stranded-endpoint observation)");

        Task<ActionResult> StrandedEndpoint() => Task.Run<ActionResult>(async () =>
        {
            // Park exactly like a real endpoint: this acquisition blocks until
            // the helper releases its gate, then keep the settle distance so
            // the un-observed rethrow at gate release cannot win the race, and
            // FAULT: the observation must swallow this fault without letting
            // it replace the plant failure the caller asserts on.
            using IDisposable reAcquired = await _cache.LockItemAsync(key, 0);
            await Task.Delay(150);
            endpointSettledBeforePlantSurfaced = true;
            throw new InvalidOperationException("stranded endpoint fault (JF-704 pin)");
        });

        var surfaced = await Assert.ThrowsAsync<IOException>(() => ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(key, 0),
            StrandedEndpoint,
            () => throw plant));

        Assert.Same(plant, surfaced);
        Assert.True(
            endpointSettledBeforePlantSurfaced,
            "the helper surfaced the plant failure before the parked endpoint settled, so the endpoint is still running past this test: static registry writes after teardown and an unobserved eventual fault (JF-704)");
    }

    /// <summary>
    /// JF-726 IL-shape pin for the JF-704 birth fault-observation backstop
    /// (<see cref="MarkEndpointFaultObserved"/>): the backstop is pinned by no
    /// behavioral test, because on every behaviorally-pinned arm the
    /// endpoint's observation is provided by a direct await (the catch-arm
    /// <see cref="ObserveStrandedEndpointAsync"/> or the normal-path settle),
    /// and the ONE exit it uniquely covers, the settle-budget timeout (an
    /// endpoint that neither completes nor faults within
    /// <see cref="EndpointSettleBudget"/>, whose eventual fault only the birth
    /// continuation observes), costs the whole 20s budget per TFM to exercise.
    /// This pin holds the mechanism's SHAPE instead (the
    /// CaptureRefreshPairingTests IL-roster idiom applied to this assembly's
    /// own helper): the backstop method exists exactly once, is called from
    /// exactly one place (ServeInLockWarmCacheAsync's own body, stub or state
    /// machine, the endpoint's birth), the call precedes the helper's first
    /// assert call, the park assert (attached before any await of the
    /// endpoint, so a fault landing between a settle-budget timeout and a
    /// later attachment cannot slip through), the call's ARGUMENT is the
    /// field the startEndpoint delegate's result was just stored into (the
    /// argument-identity tie; a wrong-task refactor cannot hide behind the
    /// other asserts), the continuation-options operand is the documented
    /// OnlyOnFaulted | ExecuteSynchronously constant (the filter; without
    /// OnlyOnFaulted the continuation fires on every completion and observes
    /// nothing on the timeout exit), and this class's one compiler-generated
    /// continuation body reads <see cref="Task.Exception"/> (the observation
    /// itself). RED PROOFS (run on both TFMs): dropping the OnlyOnFaulted
    /// flag reds the options assert; emptying the continuation body reds the
    /// exception-read assert; deleting the birth-site call reds the one-caller
    /// assert; attaching late (after the settle) reds the ordering assert;
    /// passing a task other than the started endpoint reds the
    /// argument-identity tie. HONEST SCOPE:
    /// this pins presence and shape, not the runtime observation effect.
    /// Proving the effect needs UnobservedTaskException after a forced GC
    /// (finalizer-timing flaky, rejected in the JF-726 filing) or a marker
    /// inside the continuation (rejected here: it would modify the mechanism
    /// under test and break its zero-allocation static-lambda shape, the
    /// property the JF-704 doc names); the filing's budget-seam shape was not
    /// taken either, since it grows the helper's signature to prove the
    /// seam's plumbing, still not the observation.
    /// </summary>
    [Fact]
    public void ServeInLockWarmCacheHelper_BirthBackstop_AttachesOnlyOnFaultedContinuationBeforeParkAssert()
    {
        Assembly testAssembly = typeof(VideoAudioControllerTests).Assembly;
        Module module = typeof(VideoAudioControllerTests).Module;

        List<int> backstopTokens = IlCallScanner.MethodTokens(
            typeof(VideoAudioControllerTests),
            nameof(MarkEndpointFaultObserved)).ToList();
        Assert.True(
            backstopTokens.Count == 1,
            $"expected exactly one MarkEndpointFaultObserved methoddef token, found {backstopTokens.Count} (the backstop was renamed or duplicated; update this pin consciously)");

        var birthCalls = new List<(MethodBase Method, int Offset)>();
        var continuationBodies = new List<MethodBase>();
        foreach ((_, MethodBase method) in IlCallScanner.DeclaredMethods(testAssembly))
        {
            string logical = IlCallScanner.LogicalMethodName(method);
            if (logical == nameof(MarkEndpointFaultObserved)
                && method.MetadataToken != backstopTokens[0]
                && method.DeclaringType is not null
                && IlCallScanner.TopLevelType(method.DeclaringType) == typeof(VideoAudioControllerTests))
            {
                // This class's backstop family minus the outer method itself
                // (the code-review RC1 scoping: a same-named helper in ANOTHER
                // test class must not satisfy the continuation-body assert
                // below): today the static continuation lambda; any future
                // capture shape lands here too.
                continuationBodies.Add(method);
            }

            foreach ((int offset, int token) in IlCallScanner.InstructionOperands(method, 0x28, 0x6F))
            {
                if (token == backstopTokens[0])
                {
                    birthCalls.Add((method, offset));
                }
            }
        }

        Assert.True(
            birthCalls.Count == 1
                && IlCallScanner.LogicalMethodName(birthCalls[0].Method) == nameof(ServeInLockWarmCacheAsync)
                // Gate-marker tail (the RC1 asymmetry): confine by top-level
                // type too, the same guard the continuation-body set carries,
                // so a same-named method on a NESTED type (which can legally
                // call this private static) cannot satisfy the fact while the
                // real helper lost its birth attachment.
                && birthCalls[0].Method.DeclaringType is not null
                && IlCallScanner.TopLevelType(birthCalls[0].Method.DeclaringType) == typeof(VideoAudioControllerTests),
            $"MarkEndpointFaultObserved must be called exactly once, from ServeInLockWarmCacheAsync's own body (the endpoint's birth site; zero = the call was deleted, the JF-704 regression; a wrong site or a higher count = the attachment moved or multiplied); found {birthCalls.Count} at [{string.Join(", ", birthCalls.Select(call => $"{call.Method.DeclaringType!.FullName}.{IlCallScanner.LogicalMethodName(call.Method)}"))}]");

        // The birth property, as an ordering fact: the backstop call must
        // precede the helper's first assert call (the park assert) inside the
        // same body. The anchor is any Xunit.Assert member, not Assert.False
        // specifically, so an assert-flavor rewrite does not false-red this
        // pin. parkAssertOffset -1 means no Assert call was found there (the
        // helper's shape changed; update this pin).
        (MethodBase birthMethod, int backstopOffset) = birthCalls[0];
        int parkAssertOffset = -1;
        foreach ((int offset, int token) in IlCallScanner.InstructionOperands(birthMethod, 0x28, 0x6F))
        {
            if (IlCallScanner.TryResolveMethod(module, token) is { } callee
                && callee.DeclaringType == typeof(Assert))
            {
                parkAssertOffset = offset;
                break;
            }
        }

        Assert.True(
            parkAssertOffset > backstopOffset,
            $"the birth backstop must be attached BEFORE the park assert (backstop call at IL offset {backstopOffset}, first Assert call at {parkAssertOffset}): a late attachment, after an await of the endpoint, re-opens the window where a fault lands between the settle-budget timeout and the attachment, unobserved (JF-704/JF-726)");

        // The argument-identity tie (code-review RC2): the task handed to the
        // backstop must be the one startEndpoint just produced, so a refactor
        // that passes some other task cannot keep every other assert green
        // while the endpoint's eventual fault goes unobserved. Emission shape
        // this rides on (verified Debug and Release): the call's argument is
        // an ldfld of the state machine's endpoint-task field, and that same
        // field's stfld sits immediately after the startEndpoint delegate's
        // Func<Task<ActionResult>>.Invoke callvirt. Raw token equality, no
        // resolution; a broken adjacency reds loudly for a conscious update.
        byte[] birthIl = birthMethod.GetMethodBody()!.GetILAsByteArray()!;
        int argumentLoadOffset = backstopOffset - 5;
        Assert.True(
            argumentLoadOffset >= 0 && birthIl[argumentLoadOffset] == 0x7B,
            "the birth call's argument must be a field load (ldfld) of the just-started endpoint task; the emission shape changed, update this pin consciously (JF-726 argument-identity tie)");
        int endpointFieldToken = BitConverter.ToInt32(birthIl, argumentLoadOffset + 1);
        bool argumentTiedToStartedEndpoint = false;
        // The stfld (0x7D) sites AND their callvirt (0x6F) predecessors via the
        // SAME shared opcode-aware decode (JF-736 /simplify + gate-marker
        // GM-F3: the first cut graduated the stfld scan but kept checking the
        // predecessor through a raw storeOffset-5 fixed window; the decoder
        // makes the real instruction boundary decodable, so no fixed-window
        // assumption survives here).
        (short Opcode, int OperandStart)? previous = null;
        foreach ((_, short opcode, int operandStart, _) in IlCallScanner.Instructions(birthIl))
        {
            if (opcode == 0x7D
                && BitConverter.ToInt32(birthIl, operandStart) == endpointFieldToken
                && previous is { Opcode: 0x6F } prev
                && IlCallScanner.TryResolveMethod(module, BitConverter.ToInt32(birthIl, prev.OperandStart)) is { } invoked
                && invoked.Name == nameof(Action.Invoke)
                && invoked.DeclaringType == typeof(Func<Task<ActionResult>>))
            {
                argumentTiedToStartedEndpoint = true;
                break;
            }

            previous = (opcode, operandStart);
        }

        Assert.True(
            argumentTiedToStartedEndpoint,
            "the backstop must observe the task the startEndpoint delegate produced (the field it loads at the birth call must be the one stored right after startEndpoint's Invoke): otherwise the endpoint's eventual fault is unobserved while every other shape assert stays green (JF-726 argument-identity tie)");

        MethodBase? backstopBody = IlCallScanner.TryResolveMethod(module, backstopTokens[0]);
        Assert.True(
            backstopBody != null,
            "the MarkEndpointFaultObserved methoddef did not resolve in its own module");
        // The exact combined constant, not a bits-contains mask (code-review
        // RC4). PHANTOM-WINDOW VERDICT (JF-736): the old every-offset window
        // could surface a PHANTOM ldc.i4 (a 0x20 byte inside another
        // instruction's operand bytes), and for this one assert a phantom
        // worked in the GREEN direction, against the loud-only discipline.
        // The graduated IlCallScanner.InstructionOperands walk is OPCODE-AWARE
        // (it decodes instruction boundaries from the runtime's own opcode
        // table), so only a real ldc.i4 instruction yields here; the residual
        // the JF-726 review recorded is closed, not carried.
        int documentedOptions = (int)(TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        Assert.True(
            IlCallScanner.LdcI4Operands(backstopBody).Contains(documentedOptions),
            $"the backstop's ContinueWith must pass TaskContinuationOptions.OnlyOnFaulted | ExecuteSynchronously (0x{documentedOptions:X}); without OnlyOnFaulted the continuation fires on every completion instead of observing the fault, re-creating the unobserved-fault stranding on the settle-budget timeout exit (JF-704/JF-726)");

        Assert.True(
            continuationBodies.Count == 1,
            $"the backstop's compiler-generated continuation body must exist exactly once in this class (found {continuationBodies.Count}; a rename of MarkEndpointFaultObserved or a named-method refactor of the lambda changes the emission shape; update this pin consciously)");
        MethodInfo exceptionGetter = typeof(Task).GetProperty(nameof(Task.Exception))!.GetGetMethod()!;
        Assert.True(
            IlCallScanner.CallsGetter(continuationBodies[0], module, exceptionGetter),
            "the backstop's continuation body must read Task.Exception: reading it IS the observation, and an emptied body re-creates the unobserved-fault stranding the JF-704 backstop exists to close");

        // Gate-marker tail F2 (the body-scoped receiver tie): the RC2 tie at
        // the call site stops at the method boundary - nothing tied the
        // ContinueWith RECEIVER inside MarkEndpointFaultObserved's body to
        // the endpointTask parameter, so a wrong-task refactor one level
        // deeper escaped all six facts. This straight-line body's first
        // instruction being the parameter load (ldarg.0) makes the one
        // ContinueWith's receiver the parameter by construction (nothing
        // else can sit under it on the stack); a body that never loads the
        // parameter, or loads something else first, reds here.
        byte[] backstopMethodIl = backstopBody!.GetMethodBody()!.GetILAsByteArray()
            ?? throw new InvalidOperationException("MarkEndpointFaultObserved has no IL body");
        int receiverLoad = 0;
        while (receiverLoad < backstopMethodIl.Length && backstopMethodIl[receiverLoad] == 0x00)
        {
            receiverLoad++; // Debug-build nops precede the real first instruction
        }

        Assert.True(
            receiverLoad < backstopMethodIl.Length && backstopMethodIl[receiverLoad] == 0x02,
            "MarkEndpointFaultObserved's body's first real instruction must load the endpointTask parameter (ldarg.0), the receiver of its one ContinueWith: a body loading anything else first has detached the backstop from the task it exists to observe (JF-726 gate-marker F2)");
    }

    /// <summary>
    /// JF-681 code-review hardening pin: a THROWING in-lock probe observer must
    /// not orphan the per-item lock. LockHlsItemAsync acquires the cache gate
    /// BEFORE the caller's using binds it, so an observer that throws inside the
    /// wrapper would (without the dispose-and-rethrow) leave the gate acquired
    /// forever and deadlock every later request for the key. GREEN: the
    /// observer's exception surfaces out of the endpoint AND a follow-up
    /// acquisition of the same per-item lock completes immediately (the gate
    /// was released). RED PROOF: removing the wrapper's catch (dispose +
    /// rethrow) keeps the gate held and the follow-up acquisition below times
    /// out instead of returning.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-681 Throwing Probe Song",
            Id = Guid.NewGuid()
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        var controller = CreateController(audioItem.Id.ToString());
        controller.InLockWarmCacheProbeForTest = _ => throw new InvalidOperationException("probe boom (JF-681 lock-release pin)");

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StreamHlsVideoAudio(audioItem.Id.ToString()));

        // The gate must be free: this acquisition completes (bounded) exactly
        // when the wrapper released it, and times out into the red shape when a
        // leaked gate holds the key.
        IDisposable reAcquired = await _cache.LockItemAsync(audioItem.Id.ToString("D"), 0)
            .WaitAsync(TimeSpan.FromSeconds(15));
        reAcquired.Dispose();
    }

    /// <summary>
    /// The two-path read-count funnel shared by the own-live prewrite pins and
    /// the mid-registration twins (JF-681): installs a
    /// PlaylistContentReadForTest observer that counts full reads per playlist
    /// file and returns the two readers. Pure counting plumbing: the pins keep
    /// their own expected values and their production-side red proofs, so the
    /// shared funnel cannot couple the pins' outcomes.
    /// </summary>
    /// <param name="controller">The pin's controller instance (per-test, never shared).</param>
    /// <param name="prewrittenPath">The prewrite playlist path to count (playlist-full.m3u8).</param>
    /// <param name="playlistPath">The live playlist path to count (stream.m3u8).</param>
    /// <returns>Readers for the prewrite-path and live-path read counts.</returns>
    private static (Func<int> Prewrites, Func<int> Lives) TrackPlaylistReads(
        VideoAudioController controller,
        string prewrittenPath,
        string playlistPath)
    {
        int prewriteReads = 0;
        int liveReads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == prewrittenPath)
            {
                prewriteReads++;
            }

            if (path == playlistPath)
            {
                liveReads++;
            }
        };

        return (() => prewriteReads, () => liveReads);
    }

    /// <summary>
    /// JF-677 in-lock twin (song path, the site at StreamHlsVideoAudioCore's
    /// in-lock double check): the concurrent-generated warm cache serves from
    /// the verdict's ONE read, and the in-lock branch (not the fast path) is
    /// the one that served. Same red proof as the fast-path pins: dropping
    /// the threaded content re-reads (2 != 1).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_InLockWarmCacheServe_ValidatedServe_ReadsPlaylistOnce()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-677 In-Lock Song",
            Id = Guid.NewGuid()
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(audioItem.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-inlock-song"));

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(audioItem.Id.ToString("D"), 0),
            () => controller.StreamHlsVideoAudio(audioItem.Id.ToString()),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");
            }, controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving playlist generated by concurrent request", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving cached playlist for item", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-677 in-lock twin (episode remux path). Same construction and red
    /// proof as the song twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_InLockWarmCacheServe_ValidatedServe_ReadsPlaylistOnce()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-677 In-Lock S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-inlock-episode"));

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(episode.Id.ToString(), 0),
            () => controller.StreamHlsEpisode(episode.Id.ToString()),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
            }, controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving playlist generated by concurrent request", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving cached playlist for item", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-677 in-lock twin (audio-variant path, keyed by the variant cache
    /// key). Same construction and red proof as the song twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_InLockWarmCacheServe_ValidatedServe_ReadsPlaylistOnce()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "JF-677 In-Lock Audio S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(39).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string cacheKey = VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), 0);
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(episode.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-inlock-audio"));

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(cacheKey, 0),
            () => controller.StreamHlsEpisodeAudio(episode.Id.ToString(), 0),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
            }, controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving playlist generated by concurrent request", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving cached playlist for item", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-677 in-lock twin (audiobook path, the only in-lock site outside the
    /// wrapper and the one whose verdict hook inspects the content): the
    /// concurrent-generated warm cache serves from the verdict's ONE read.
    /// Same construction and red proof as the song twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_InLockWarmCacheServe_ValidatedServe_ReadsPlaylistOnce()
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-677 In-Lock Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Chapter 2", Id = Guid.NewGuid() };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-inlock-book"));

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(parentId.ToString(), 0),
            () => controller.StreamHlsAudiobook(parentId.ToString()),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                // 2 segments >= 2 chapters: the undercount hook's bar.
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
            }, controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Equal(1, reads);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving playlist generated by concurrent request", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving cached playlist for parent", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-680 episode pin (the IN-LOCK own-live prewrite row, the gap the JF-679
    /// gate-marker round filed): a concurrent lock-waiter whose OWN art-tick
    /// generation is live must receive the PRE-WRITTEN full listing, not ffmpeg's
    /// live partial, which is the whole point of the JF-531 prewrite mechanism
    /// for lock waiters. The existing roster left this row unpinned: the JF-677
    /// in-lock twins all drive the own-dead ENDLIST row (no active encode, the
    /// helper's gate returns null) and the JF-675 own-ticks twins drive the FAST
    /// path, so a regression dropping or miswiring the gate at the in-lock site
    /// compiles green and silently reverts concurrent lock-waiters to the
    /// live-edge serve. Construction: the JF-677 ServeInLockWarmCacheAsync core
    /// plus the seam family's own-live marking (SetEncodeActiveForTest, episode
    /// registry, at the ticks the artless endpoint computes: 0; marked before
    /// the endpoint starts and no encode runs in the green world, so nothing
    /// displaces the marked generation), planting BOTH playlists so the content
    /// assert discriminates which file served. The live partial (stream.m3u8,
    /// no ENDLIST) carries the live-only marker seg_9999; the prewrite
    /// (playlist-full.m3u8, the production prewrite file name, no ENDLIST)
    /// carries seg_0999, deliberately OUTSIDE the production 45min/4s listing
    /// (675 entries, tail seg_0674): a debris-verdict regression that deletes
    /// the plant and re-encodes would regenerate the prewrite with the
    /// production tail and serve it ungated from the first-serve site under the
    /// same "serving pre-written full listing" log, so only a marker the
    /// production listing cannot contain keeps the pin honest there
    /// (code-review high, 2026-09-30). GREEN: the
    /// prewrite serves (the "serving pre-written full
    /// listing" log fires from inside the gate, and the in-lock fall-through's
    /// "serving playlist generated by concurrent request" never does). RED
    /// PROOF (both forms run before this landed, documented in the task notes):
    /// disabling the gate inside ServeEpisodeWarmCacheAsync, or miswiring its
    /// ticks at the call site (+1), must flip the pin to the live-edge serve
    /// (seg_9999 served, the concurrent-request log fires); the verdict still
    /// answers own-live-or-registering at the correct ticks, so the fall-through
    /// is exactly the silent live-edge revert the task describes.
    /// JF-681 ADDITIONS: (1) ATTRIBUTION is now deterministic, closing the
    /// boundary the original pin stated honestly (it rested on the park-window
    /// assert, and a pre-lock stall longer than the window would re-target the pin at the
    /// fast-path gate, which reads the SAME predicate at the SAME ticks and
    /// serves byte-identically): the InLockWarmCacheProbeForTest seam fires only
    /// inside the per-item lock scope, so a fast-path serve can no longer
    /// satisfy this pin silently; the probe assert fails and names the void
    /// attribution (red proof: removing the song/episode probe invocations
    /// flips this pin and the JF-677 twins on those paths, nothing else).
    /// (2) READ-COUNT FUNNEL: on this row the verdict reads nothing (own-live
    /// row, Content null) and the prewrite serve reads the prewrite EXACTLY
    /// ONCE fresh (no preloaded content to reuse), so a double-read regression
    /// on the prewrite serve flips the count (red proof: a discarded read
    /// hoisted into TryServePrewrittenEpisodePlaylist flips this pin to 2).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_InLockOwnLiveGeneration_ServesPrewriteNotLivePartial()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-680 In-Lock Own-Live S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf680-inlock-episode"));

        var reads = TrackPlaylistReads(controller, prewrittenPath, playlistPath);

        // The own-live marking (doc: no encode runs, so nothing displaces it).
        VideoAudioController.SetEncodeActiveForTest(episode.Id.ToString(), active: true);
        try
        {
            ActionResult result = await ServeInLockWarmCacheAsync(
                () => _cache.LockItemAsync(episode.Id.ToString(), 0),
                () => controller.StreamHlsEpisode(episode.Id.ToString()),
                () =>
                {
                    Directory.CreateDirectory(hlsDir);
                    File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_9999.ts\n");
                    File.WriteAllText(
                        prewrittenPath,
                        "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0999.ts\n");
                },
                controller);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_0999", StringComparison.Ordinal),
                $"a concurrent lock-waiter under a live own-ticks generation must receive the pre-written full listing, not ffmpeg's live partial (JF-680); live-only marker seg_9999 present: {content.Content.Contains("seg_9999", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_9999", content.Content, StringComparison.Ordinal);
            Assert.Equal(1, reads.Prewrites());
            Assert.Equal(0, reads.Lives());
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving pre-written full listing", StringComparison.Ordinal));
            Assert.DoesNotContain(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving playlist generated by concurrent request", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(episode.Id.ToString(), active: false);
        }
    }

    /// <summary>
    /// JF-680 song twin (the IN-LOCK own-live prewrite row on
    /// TryServeOwnLiveVideoAudioPrewriteAsync, reached from
    /// StreamHlsVideoAudioCore's in-lock double check): the same gap as the
    /// episode pin above, on the gate whose doc deliberately keeps it BEFORE the
    /// verdict (strict live vs live-or-registering), so the twin cannot share
    /// the episode pin. Same construction with the single-item registry
    /// (song: true), 3-digit song segments, and the seg_899/seg_999 markers
    /// (the prewrite marker deliberately outside any production-shaped listing,
    /// the episode pin's debris-regression rationale; BOUND: safe while the
    /// fixture stays runtime-less or under 60min, since a re-encoded listing
    /// of >= 60min at 4s segments would contain seg_899 - derive the marker
    /// from the fixture runtime if it ever grows). TWIN INDEPENDENCE:
    /// the red proof toggles ONLY the song gate (disabled, or its call-site
    /// ticks miswired), which must fail THIS pin while the episode pin stays
    /// green. JF-681 ADDITIONS (the episode twin's shape): the in-lock probe
    /// assert makes the in-lock-vs-fast-path attribution deterministic, and the
    /// read-count funnel pins the row's reads (this gate sits BEFORE the
    /// verdict, so the verdict never runs on this row: the prewrite serve reads
    /// the prewrite EXACTLY ONCE fresh and the live partial ZERO times; red
    /// proof for the count: a discarded read hoisted into
    /// TryServePrewrittenVideoAudioPlaylist flips this pin to 2; red proof for
    /// the probe: removing the song probe invocation flips this pin's probe
    /// assert).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_InLockOwnLiveGeneration_ServesPrewriteNotLivePartial()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-680 In-Lock Own-Live Song",
            Id = Guid.NewGuid()
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(audioItem.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf680-inlock-song"));

        var reads = TrackPlaylistReads(controller, prewrittenPath, playlistPath);

        // The own-live marking in the song registry (doc: the episode pin's
        // construction; no encode runs, so nothing displaces it).
        VideoAudioController.SetEncodeActiveForTest(audioItem.Id.ToString(), active: true, song: true);
        try
        {
            ActionResult result = await ServeInLockWarmCacheAsync(
                () => _cache.LockItemAsync(audioItem.Id.ToString("D"), 0),
                () => controller.StreamHlsVideoAudio(audioItem.Id.ToString()),
                () =>
                {
                    Directory.CreateDirectory(hlsDir);
                    File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_999.ts\n");
                    File.WriteAllText(
                        prewrittenPath,
                        "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_899.ts\n");
                },
                controller);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_899", StringComparison.Ordinal),
                $"a concurrent lock-waiter under a live own-ticks generation must receive the pre-written full listing, not ffmpeg's live partial (JF-680 song gate); live-only marker seg_999 present: {content.Content.Contains("seg_999", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_999", content.Content, StringComparison.Ordinal);
            Assert.Equal(1, reads.Prewrites());
            Assert.Equal(0, reads.Lives());
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving pre-written full listing", StringComparison.Ordinal));
            Assert.DoesNotContain(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving playlist generated by concurrent request", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(audioItem.Id.ToString(), active: false, song: true);
        }
    }

    /// <summary>
    /// JF-681 mid-registration split pin (the EPISODE row): the strict-vs-conservative
    /// gate split (OwnTicksGenerationLive at the prewrite serve gates vs
    /// OwnTicksGenerationLiveOrRegistering at the debris verdicts) had ZERO mechanical
    /// coverage, because SetEncodeActiveForTest always creates a FULL slot where the
    /// two predicates agree, so a regression swapping the warm-cache gate's predicate
    /// (or hoisting the gate behind the verdict, the exact move
    /// ServeEpisodeWarmCacheAsync's doc warns against) kept the suite green.
    /// Construction: the SetEncodeRegisteringForTest zero-slot seam (the
    /// mid-registration state: entry stored, NO slot written, the production
    /// GetOrAdd-before-RegisterIfStored window) plus the JF-680 in-lock construction
    /// (both playlists planted under the held lock, the seg_0999/seg_9999 marker
    /// scheme). In the window the serve gate must stay STRICT (the prewrite skipped:
    /// seg_0999 absent, zero prewrite reads, no "serving pre-written full listing"
    /// log) while the verdict stays CONSERVATIVE (the "serving live ffmpeg playlist
    /// (encoding in progress)" own-live row fires; an ABSENT entry could not produce
    /// it, it would send the verdict down the no-ENDLIST debris row instead), and the
    /// request falls through to the live partial (seg_9999 served, one fresh read).
    /// RED PROOF: swapping ServeEpisodeWarmCacheAsync's gate to the conservative
    /// predicate serves the prewrite (seg_0999 present, the prewrite log fires, live
    /// reads 0) and flips this pin.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_InLockMidRegistrationWindow_SkipsPrewriteAndVerdictStaysConservative()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-681 Mid-Registration S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf681-midreg-episode"));

        var reads = TrackPlaylistReads(controller, prewrittenPath, playlistPath);

        // The MID-REGISTRATION marking (doc: a stored holder with ZERO slots, the
        // window where the strict gate and the conservative verdict disagree).
        VideoAudioController.SetEncodeRegisteringForTest(episode.Id.ToString(), registering: true);
        try
        {
            ActionResult result = await ServeInLockWarmCacheAsync(
                () => _cache.LockItemAsync(episode.Id.ToString(), 0),
                () => controller.StreamHlsEpisode(episode.Id.ToString()),
                () =>
                {
                    Directory.CreateDirectory(hlsDir);
                    File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_9999.ts\n");
                    File.WriteAllText(
                        prewrittenPath,
                        "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0999.ts\n");
                },
                controller);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_9999", StringComparison.Ordinal),
                $"in the mid-registration window the strict serve gate must skip the prewrite and fall through to ffmpeg's live partial (JF-681); prewrite marker seg_0999 present: {content.Content.Contains("seg_0999", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_0999", content.Content, StringComparison.Ordinal);
            Assert.Equal(0, reads.Prewrites());
            Assert.Equal(1, reads.Lives());
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving live ffmpeg playlist (encoding in progress)", StringComparison.Ordinal));
            Assert.DoesNotContain(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving pre-written full listing", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeRegisteringForTest(episode.Id.ToString(), registering: false);
        }
    }

    /// <summary>
    /// JF-681 mid-registration split pin (the SONG row, the twin on the gate whose
    /// doc deliberately keeps it BEFORE the verdict at both sites): same gap and
    /// same construction as the episode pin, on the song registry (song: true) with
    /// the seg_899/seg_999 markers (the JF-680 song pin's boundary: the prewrite
    /// marker stays outside any production-shaped listing). Here the strict gate is
    /// TryServeOwnLiveVideoAudioPrewriteAsync, whose doc warns that hoisting it
    /// behind the verdict would change the mid-registration serve; this pin is the
    /// mechanical guard of that warning. In the window the gate stays STRICT
    /// (prewrite skipped: seg_899 absent, zero prewrite reads, no "serving
    /// pre-written full listing" log) while the verdict stays CONSERVATIVE (the
    /// "serving live ffmpeg playlist (encoding in progress)" own-live row fires,
    /// the discriminator an ABSENT entry could not produce), and the request falls
    /// through to the live partial (seg_999 served, one fresh read).
    /// RED PROOF: swapping TryServeOwnLiveVideoAudioPrewriteAsync's gate to the
    /// conservative predicate serves the prewrite (seg_899 present, the prewrite
    /// log fires, live reads 0) and flips this pin.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_InLockMidRegistrationWindow_SkipsPrewriteAndVerdictStaysConservative()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-681 Mid-Registration Song",
            Id = Guid.NewGuid()
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });
        var controller = CreateController(audioItem.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf681-midreg-song"));

        var reads = TrackPlaylistReads(controller, prewrittenPath, playlistPath);

        // The MID-REGISTRATION marking in the song registry (doc: the episode
        // twin's construction).
        VideoAudioController.SetEncodeRegisteringForTest(audioItem.Id.ToString(), registering: true, song: true);
        try
        {
            ActionResult result = await ServeInLockWarmCacheAsync(
                () => _cache.LockItemAsync(audioItem.Id.ToString("D"), 0),
                () => controller.StreamHlsVideoAudio(audioItem.Id.ToString()),
                () =>
                {
                    Directory.CreateDirectory(hlsDir);
                    File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_999.ts\n");
                    File.WriteAllText(
                        prewrittenPath,
                        "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_899.ts\n");
                },
                controller);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_999", StringComparison.Ordinal),
                $"in the mid-registration window the strict serve gate must skip the prewrite and fall through to ffmpeg's live partial (JF-681 song gate); prewrite marker seg_899 present: {content.Content.Contains("seg_899", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_899", content.Content, StringComparison.Ordinal);
            Assert.Equal(0, reads.Prewrites());
            Assert.Equal(1, reads.Lives());
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving live ffmpeg playlist (encoding in progress)", StringComparison.Ordinal));
            Assert.DoesNotContain(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("serving pre-written full listing", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeRegisteringForTest(audioItem.Id.ToString(), registering: false, song: true);
        }
    }

    /// <summary>
    /// Verify that on a cache miss, the HLS controller starts ffmpeg, waits for the
    /// first segment to appear, and returns a PhysicalFileResult with the playlist.
    /// The fake ffmpeg script creates both the segment file and playlist, then exits.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CacheMiss_ReturnsPhysicalFileResult()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Create a fake ffmpeg script that writes a segment file and playlist.
        // The new HLS endpoint waits for seg_000.ts to appear before serving the playlist.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-hls",
            // POSIX sh (dash) has no "${@: -1}": iterate to the last positional arg instead
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            // Create a segment file so the endpoint detects it and serves the playlist
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_000.ts\" 2>/dev/null\n" +
            // Create the playlist file (ffmpeg uses .tmp + rename in production)
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:4.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(audioItem.Id.ToString());
        controller.FfmpegPath = fakeFfmpegPath;

        var httpContext = new DefaultHttpContext
        {
            RequestAborted = CancellationToken.None
        };
        httpContext.Request.Query = controller.ControllerContext.HttpContext.Request.Query;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        // JF-309: when a token is present, the playlist is rewritten and served as ContentResult.
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", contentResult.ContentType);
    }

    // ========== Segment Endpoint Tests ==========

    /// <summary>
    /// Verify that the segment endpoint returns 400 for an invalid itemId.
    /// </summary>
    [Fact]
    public async Task GetSegment_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.GetSegment("not-a-guid", "seg_000.ts");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verify that the segment endpoint returns 400 for an invalid segment name
    /// (directory traversal prevention).
    /// </summary>
    [Fact]
    public async Task GetSegment_InvalidSegmentName_Returns400()
    {
        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemId);

        // Test various traversal and injection attempts
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, "../etc/passwd"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, "../../secret"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, "seg_000.ts/../../etc/passwd"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, ""));
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, "seg_00.ts"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, "seg_00000.ts"));   // 5 digits
        Assert.IsType<BadRequestObjectResult>(await controller.GetSegment(itemId, "segment.ts"));
    }

    /// <summary>
    /// Verify that the segment endpoint returns 404 when the segment file doesn't exist.
    /// </summary>
    [Fact]
    public async Task GetSegment_SegmentNotFound_Returns404()
    {
        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemId);

        ActionResult result = await controller.GetSegment(itemId, "seg_000.ts");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Verify that the segment endpoint returns a .ts file with correct content type
    /// when the segment exists in the HLS cache directory.
    /// </summary>
    [Fact]
    public async Task GetSegment_ValidSegment_ReturnsFile()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");
        long artTicks = 0;

        // Create an HLS directory with a segment file
        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, artTicks);
        Directory.CreateDirectory(hlsDir);
        string segmentPath = Path.Combine(hlsDir, "seg_000.ts");
        File.WriteAllText(segmentPath, new string('x', 1024));

        var controller = CreateController(itemIdStr);

        ActionResult result = await controller.GetSegment(itemIdStr, "seg_000.ts");

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("video/mp2t", physicalResult.ContentType);
        Assert.True(physicalResult.EnableRangeProcessing);
    }

    // ========== JF-503 Hold-For-Segment Tests ==========

    /// <summary>
    /// JF-503: a near-ahead miss (highest+1) for an item with an ACTIVE encode is
    /// HELD: the segment file written by a background task during the poll window is
    /// served instead of a 404 (the encode runs ~20x realtime, so the segment is
    /// imminent).
    /// </summary>
    [Fact]
    public async Task GetSegment_NearAheadMiss_ActiveEncode_HoldsUntilSegmentAppears()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        // Encode head at seg_0000.ts; the player asks for the next one (seek just
        // past the head of a running encode).
        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0000.ts"), new string('x', 1024));
        _cache.RegisterHlsDirectory(itemIdStr, 0);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);

        var controller = CreateController(itemIdStr);
        controller.SegmentHoldBudget = TimeSpan.FromSeconds(5);
        controller.SegmentHoldPollInterval = TimeSpan.FromMilliseconds(20);

        // Simulate ffmpeg finishing the requested segment shortly after the hold
        // starts: the file AND its live-playlist entry (the completion signal).
        string targetPath = Path.Combine(hlsDir, "seg_0001.ts");
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await File.WriteAllTextAsync(targetPath, new string('x', 512));
            await File.WriteAllTextAsync(
                Path.Combine(hlsDir, "stream.m3u8"),
                "#EXTM3U\n#EXTINF:4.000,\nseg_0001.ts\n");
        });

        ActionResult result = await controller.GetSegment(itemIdStr, "seg_0001.ts");

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("video/mp2t", physicalResult.ContentType);
        Assert.Equal(targetPath, physicalResult.FileName);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
    }

    /// <summary>
    /// JF-503: a near-ahead miss that never appears returns 404 only AFTER the bounded
    /// hold budget expires (the encode did not catch up in time).
    /// </summary>
    [Fact]
    public async Task GetSegment_NearAheadMiss_ActiveEncode_Returns404AfterBoundedBudget()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0000.ts"), new string('x', 1024));

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);

        var controller = CreateController(itemIdStr);
        controller.SegmentHoldBudget = TimeSpan.FromMilliseconds(250);
        controller.SegmentHoldPollInterval = TimeSpan.FromMilliseconds(25);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ActionResult result = await controller.GetSegment(itemIdStr, "seg_0001.ts");
        stopwatch.Stop();

        Assert.IsType<NotFoundObjectResult>(result);
        // The 404 came from the EXPIRED hold, not an immediate miss.
        Assert.True(stopwatch.ElapsedMilliseconds >= 150,
            $"expected the bounded hold (~250ms) before the 404, took {stopwatch.ElapsedMilliseconds}ms");

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
    }

    /// <summary>
    /// JF-503: a seek FAR ahead of the encode head (beyond the +2 lookahead) is never
    /// held, even with an active encode: the 404 is immediate (a bounded wait cannot
    /// serve a jump into unencoded minutes).
    /// </summary>
    [Fact]
    public async Task GetSegment_FarAheadMiss_ActiveEncode_Returns404Immediately()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0000.ts"), new string('x', 1024));

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);

        var controller = CreateController(itemIdStr);
        controller.SegmentHoldBudget = TimeSpan.FromSeconds(5); // a hold would blow the assert below
        controller.SegmentHoldPollInterval = TimeSpan.FromMilliseconds(20);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ActionResult result = await controller.GetSegment(itemIdStr, "seg_0500.ts");
        stopwatch.Stop();

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"far-ahead miss must 404 immediately, took {stopwatch.ElapsedMilliseconds}ms");

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
    }

    /// <summary>
    /// JF-503: a miss with NO active encode (stale cache debris, e.g. after a server
    /// restart mid-encode) 404s immediately: the hold keys on the active-encode flags,
    /// so a dead cache never imposes a wait.
    /// </summary>
    [Fact]
    public async Task GetSegment_Miss_NoActiveEncode_Returns404Immediately()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        // A stale cache directory WITHOUT an active encode flag.
        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0000.ts"), new string('x', 1024));

        var controller = CreateController(itemIdStr);
        controller.SegmentHoldBudget = TimeSpan.FromSeconds(5); // a hold would blow the assert below
        controller.SegmentHoldPollInterval = TimeSpan.FromMilliseconds(20);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ActionResult result = await controller.GetSegment(itemIdStr, "seg_0001.ts");
        stopwatch.Stop();

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"no-active-encode miss must 404 immediately, took {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// JF-503: the AUDIOBOOK active-encode flag drives the same hold (the pre-written
    /// event playlist lists every segment from first play, so a seek past the encode
    /// head of a running audiobook hits the same listed-but-missing 404).
    /// </summary>
    [Fact]
    public async Task GetSegment_NearAheadMiss_ActiveAudiobookEncode_HoldsUntilSegmentAppears()
    {
        Guid parentId = Guid.NewGuid();
        string parentIdStr = parentId.ToString("D");

        string hlsDir = _cache.GetHlsDirectoryPath(parentIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0007.ts"), new string('x', 1024));
        _cache.RegisterHlsDirectory(parentIdStr, 0);

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: true, audiobook: true);

        var controller = CreateController(parentIdStr);
        controller.SegmentHoldBudget = TimeSpan.FromSeconds(5);
        controller.SegmentHoldPollInterval = TimeSpan.FromMilliseconds(20);

        string targetPath = Path.Combine(hlsDir, "seg_0008.ts");
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await File.WriteAllTextAsync(targetPath, new string('x', 512));
            await File.WriteAllTextAsync(
                Path.Combine(hlsDir, "stream.m3u8"),
                "#EXTM3U\n#EXTINF:10.000,\nseg_0008.ts\n");
        });

        ActionResult result = await controller.GetSegment(parentIdStr, "seg_0008.ts");

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(targetPath, physicalResult.FileName);

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: false, audiobook: true);
    }

    /// <summary>
    /// JF-536 (on the JF-503 hold): the SINGLE-ITEM path's active-encode flag now
    /// drives the same hold. The pre-JF-536 exclusion rationale (no flag, live
    /// playlist only, seconds-long encode) died with the prewrite: the pre-written
    /// listing lists every segment from first play, and the single-chapter
    /// audiobooks this path serves encode for minutes, so a seek just past the
    /// running encode's head must be smoothed exactly like the twins'.
    /// </summary>
    [Fact]
    public async Task GetSegment_NearAheadMiss_ActiveSingleItemEncode_HoldsUntilSegmentAppears()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_000.ts"), new string('x', 1024));
        _cache.RegisterHlsDirectory(itemIdStr, 0);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true, song: true);

        var controller = CreateController(itemIdStr);
        controller.SegmentHoldBudget = TimeSpan.FromSeconds(5);
        controller.SegmentHoldPollInterval = TimeSpan.FromMilliseconds(20);

        string targetPath = Path.Combine(hlsDir, "seg_001.ts");
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await File.WriteAllTextAsync(targetPath, new string('x', 512));
            await File.WriteAllTextAsync(
                Path.Combine(hlsDir, "stream.m3u8"),
                "#EXTM3U\n#EXTINF:4.000,\nseg_001.ts\n");
        });

        ActionResult result = await controller.GetSegment(itemIdStr, "seg_001.ts");

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(targetPath, physicalResult.FileName);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false, song: true);
    }

    /// <summary>
    /// JF-503 observability: every GetSegment miss logs at Debug with the requested
    /// segment name and the highest existing segment number, so a device session can
    /// confirm the seek-head mechanism from the logs.
    /// </summary>
    [Fact]
    public async Task GetSegment_Miss_LogsRequestedSegmentAndHighestExistingAtDebug()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        // Head at seg_0007.ts; the player requests far-ahead seg_0100.ts.
        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0007.ts"), new string('x', 1024));

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = new VideoAudioController(
            _libraryManagerMock.Object, _mediaEncoderMock.Object, _cache, loggerFactory);
        string token = StreamTokenHelper.Mint(itemIdStr, _config.StreamTokenSecret);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Request =
                {
                    Query = new QueryCollection(
                        new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["token"] = token })
                }
            }
        };

        ActionResult result = await controller.GetSegment(itemIdStr, "seg_0100.ts");

        Assert.IsType<NotFoundObjectResult>(result);
        var missLogs = TestCaptureLogger.Snapshot(logRecords)
            .Where(r => r.Message.Contains("GetSegment miss", StringComparison.Ordinal))
            .ToList();
        Assert.True(missLogs.Count > 0, "a GetSegment miss must be logged");
        Assert.All(missLogs, r => Assert.Equal(LogLevel.Debug, r.Level));
        Assert.Contains("seg_0100.ts", missLogs[0].Message, StringComparison.Ordinal);
        Assert.Contains("highest existing segment 7", missLogs[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Capture logger provider so tests can assert on Debug log output (same shape as
    /// SkillResponseLoggingTests' CaptureLoggerProvider).
    /// </summary>

    // ========== VideoAudioCache HLS Tests ==========

    /// <summary>
    /// Verify that GetHlsDirectoryPath returns the expected path format.
    /// </summary>
    [Fact]
    public void HlsDirectoryPath_Format_ContainsExpectedComponents()
    {
        string path = _cache.GetHlsDirectoryPath("abc123", 999);

        Assert.Contains("abc123_999", path);
        Assert.EndsWith("abc123_999", path);
    }

    /// <summary>
    /// Verify that GetHlsPlaylistPath returns the expected path format with stream.m3u8.
    /// </summary>
    [Fact]
    public void HlsPlaylistPath_Format_ContainsM3u8()
    {
        string path = _cache.GetHlsPlaylistPath("abc123", 999);

        Assert.EndsWith("stream.m3u8", path);
        Assert.Contains("abc123_999", path);
    }

    /// <summary>
    /// Verify that IsValidSegmentName accepts valid names and rejects invalid ones.
    /// </summary>
    [Fact]
    public void IsValidSegmentName_AcceptsValidAndRejectsInvalid()
    {
        // Valid names (3 digits — single-item HLS)
        Assert.True(VideoAudioCache.IsValidSegmentName("seg_000.ts"));
        Assert.True(VideoAudioCache.IsValidSegmentName("seg_001.ts"));
        Assert.True(VideoAudioCache.IsValidSegmentName("seg_999.ts"));

        // Valid names (4 digits — audiobook concat HLS)
        Assert.True(VideoAudioCache.IsValidSegmentName("seg_0000.ts"));
        Assert.True(VideoAudioCache.IsValidSegmentName("seg_0001.ts"));
        Assert.True(VideoAudioCache.IsValidSegmentName("seg_9999.ts"));

        // Invalid names (traversal, wrong format, etc.)
        Assert.False(VideoAudioCache.IsValidSegmentName(""));
        Assert.False(VideoAudioCache.IsValidSegmentName("../etc/passwd"));
        Assert.False(VideoAudioCache.IsValidSegmentName("seg_00.ts"));      // only 2 digits
        Assert.False(VideoAudioCache.IsValidSegmentName("seg_00000.ts"));   // 5 digits
        Assert.False(VideoAudioCache.IsValidSegmentName("segment.ts"));
        Assert.False(VideoAudioCache.IsValidSegmentName("SEG_000.ts"));     // uppercase
        Assert.False(VideoAudioCache.IsValidSegmentName("seg_000.mp4"));
    }

    /// <summary>
    /// Verify that FindHlsDirectoryByScan returns the correct directory when it exists,
    /// and null when it doesn't.
    /// </summary>
    [Fact]
    public void FindHlsDirectoryByScan_ReturnsCorrectDirectory()
    {
        string itemId = Guid.NewGuid().ToString("D");

        // No directory exists yet
        Assert.Null(_cache.FindHlsDirectoryByScan(itemId));

        // Create the HLS directory with a playlist
        string hlsDir = _cache.GetHlsDirectoryPath(itemId, 12345);
        Directory.CreateDirectory(hlsDir);
        File.WriteAllText(Path.Combine(hlsDir, "stream.m3u8"), "#EXTM3U");

        string? found = _cache.FindHlsDirectoryByScan(itemId);
        Assert.NotNull(found);
        Assert.Equal(hlsDir, found);
    }

    /// <summary>
    /// Verify that FindHlsDirectoryByScan returns the most recent directory when
    /// multiple directories exist for the same item.
    /// </summary>
    [Fact]
    public void FindHlsDirectoryByScan_MultipleDirs_ReturnsMostRecent()
    {
        string itemId = Guid.NewGuid().ToString("D");

        // Create two directories with different art ticks
        string oldDir = _cache.GetHlsDirectoryPath(itemId, 100);
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "stream.m3u8"), "#EXTM3U");

        // Ensure the new directory has a later creation time
        System.Threading.Thread.Sleep(50);
        string newDir = _cache.GetHlsDirectoryPath(itemId, 200);
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "stream.m3u8"), "#EXTM3U");

        string? found = _cache.FindHlsDirectoryByScan(itemId);
        Assert.NotNull(found);
        Assert.Equal(newDir, found);
    }

    /// <summary>
    /// Verify that GetCachedHlsPlaylist returns the playlist when it exists with valid size.
    /// </summary>
    [Fact]
    public async Task HlsCacheHit_ReturnsExistingPlaylist()
    {
        string itemId = Guid.NewGuid().ToString("D");
        string hlsDir = _cache.GetHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(playlistPath, new string('x', 12 * 1024));

        FileInfo? result = await _cache.GetCachedHlsPlaylist(itemId, 0);

        Assert.NotNull(result);
        Assert.Equal(playlistPath, result!.FullName);
    }

    /// <summary>
    /// Verify that GetCachedHlsPlaylist returns null on cache miss.
    /// </summary>
    [Fact]
    public async Task HlsCacheMiss_ReturnsNull()
    {
        FileInfo? result = await _cache.GetCachedHlsPlaylist("nonexistent", 0);

        Assert.Null(result);
    }

    /// <summary>
    /// Verify that Cleanup removes HLS directories in addition to flat MP4 files.
    /// </summary>
    [Fact]
    public void HlsCleanup_RemovesDirectory()
    {
        string itemId = Guid.NewGuid().ToString("D");
        string hlsDir = _cache.GetHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(hlsDir);
        File.WriteAllText(Path.Combine(hlsDir, "stream.m3u8"), "#EXTM3U");
        File.WriteAllText(Path.Combine(hlsDir, "seg_000.ts"), "data");

        Assert.True(Directory.Exists(hlsDir));

        _cache.Cleanup(itemId);

        Assert.False(Directory.Exists(hlsDir));
    }

    // ========== Audiobook HLS Tests ==========

    /// <summary>
    /// Verify that BuildHlsAudiobookFfmpegArguments produces correct concat demuxer arguments.
    /// </summary>
    [Fact]
    public void BuildHlsAudiobookFfmpegArguments_WithArt_ContainsConcatDemuxer()
    {
        string concatListPath = "/tmp/test/chapters.txt";
        string artUrl = "http://localhost:8096/Items/parent/Images/Primary";
        string playlistPath = "/tmp/test/stream.m3u8";
        string segmentPath = "/tmp/test/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/parent-id/segments/";

        List<string> args = VideoAudioController.BuildHlsAudiobookFfmpegArguments(
            concatListPath, artUrl, false, playlistPath, segmentPath, hlsBaseUrl);

        // Verify concat demuxer input
        Assert.Contains("-f", args);
        Assert.Contains("concat", args);
        Assert.Contains("-safe", args);
        Assert.Contains("0", args);
        Assert.Contains(concatListPath, args);

        // Verify art input (looped)
        Assert.Contains("-loop", args);
        Assert.Contains(artUrl, args);

        // Verify codecs: 1fps video (not re-encoded art) + audio copy (no AAC re-encode)
        Assert.Contains("libx264", args);
        Assert.Contains("copy", args);
        Assert.DoesNotContain("aac", args); // audiobook uses audio copy, not AAC re-encode

        // Verify HLS flags: 10-second segments (ExoPlayer requirement), no append_list
        Assert.Contains("-hls_time", args);
        Assert.Contains("10", args);
        Assert.DoesNotContain("-hls_flags", args);
        Assert.Contains("-hls_base_url", args);
        Assert.Contains(hlsBaseUrl, args);

        // Verify output
        Assert.Contains("-shortest", args);
        Assert.Contains(playlistPath, args);

        // No MP4 format flags
        Assert.DoesNotContain("frag_keyframe+empty_moov", args);

        // No single audio -i input (concat demuxer replaces it)
        int inputIndex = args.IndexOf("-i");
        Assert.Equal(concatListPath, args[inputIndex + 1]);
    }

    /// <summary>
    /// Verify that BuildHlsAudiobookFfmpegArguments uses a long-duration black frame
    /// (999999s ≈ 11.5 days) to cover any audiobook length.
    /// </summary>
    [Fact]
    public void BuildHlsAudiobookFfmpegArguments_BlackFrame_UsesLongDuration()
    {
        string concatListPath = "/tmp/test/chapters.txt";
        string playlistPath = "/tmp/test/stream.m3u8";
        string segmentPath = "/tmp/test/seg_%03d.ts";
        string hlsBaseUrl = "/alexaskill/api/video-audio/parent-id/segments/";

        List<string> args = VideoAudioController.BuildHlsAudiobookFfmpegArguments(
            concatListPath, null, true, playlistPath, segmentPath, hlsBaseUrl);

        // Black frame with long duration (not the 999s used for songs)
        Assert.Contains("color=c=black:s=1280x720:d=999999", args);
        Assert.DoesNotContain("color=c=black:s=1280x720:d=999", args);
        Assert.Contains(concatListPath, args);
    }

    /// <summary>
    /// Verify that StreamHlsAudiobook returns 400 for an invalid parentId.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_InvalidParentId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamHlsAudiobook("not-a-guid");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verify that StreamHlsAudiobook returns 404 when the parent is not found.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_ParentNotFound_Returns404()
    {
        Guid parentId = Guid.NewGuid();
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var controller = CreateController(parentId.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Verify that StreamHlsAudiobook returns 404 when the parent has no AudioBook children.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_NoChapters_Returns404()
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Empty Book",
            Id = parentId
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());

        var controller = CreateController(parentId.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Verify that StreamHlsAudiobook with a single chapter redirects to single-item HLS.
    /// This avoids unnecessary concat overhead for single-file audiobooks.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SingleChapter_RedirectsToSingleItemHls()
    {
        Guid parentId = Guid.NewGuid();
        Guid chapterId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Single Chapter Book",
            Id = parentId
        };
        var chapterItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = chapterId
        };

        // The ONE EncoderPath setup in this file that is actually consumed (JF-759
        // marker probe): the controller below sets no FfmpegPath override, so both
        // the entry check and the redirected single-item encode resolve ffmpeg
        // through the mock (else the PATH scan or an empty path; the assert below
        // stays green either way). Do not blind-delete in a file-wide EncoderPath
        // sweep; give this test an explicit fake first (JF-765).
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemById(chapterId)).Returns(chapterItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapterItem });

        var controller = CreateController(parentId.ToString());

        // Single chapter should redirect to single-item HLS which will return 400
        // because the folder doesn't have media sources — but the key behavior is
        // that the method was called (not the concat path)
        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        // The redirect calls StreamHlsVideoAudio with the chapter ID,
        // which validates the item has IHasMediaSources — Audio items do have it.
        // With the mock setup, this should work as a cache miss path.
        // Should NOT be "no chapters found" — single chapter redirects to single-item HLS
        Assert.IsNotType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Verify that StreamHlsAudiobook generates concat HLS for multi-chapter audiobooks.
    /// Uses a fake ffmpeg script to simulate HLS segment generation.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_MultiChapter_ReturnsConcatHlsPlaylist()
    {
        Guid parentId = Guid.NewGuid();
        Guid chapter1Id = Guid.NewGuid();
        Guid chapter2Id = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Test Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = chapter1Id
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = chapter2Id
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        // Create a fake ffmpeg script that simulates HLS generation
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-audiobook",
            // POSIX sh (dash) has no "${@: -1}": iterate to the last positional arg instead
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:10.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_0000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(parentId.ToString());
        controller.FfmpegPath = fakeFfmpegPath;

        var httpContext = new DefaultHttpContext
        {
            RequestAborted = CancellationToken.None
        };
        httpContext.Request.Query = controller.ControllerContext.HttpContext.Request.Query;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        // JF-309: when a token is present, the audiobook playlist is rewritten with ?token= and
        // served as ContentResult (not PhysicalFile).
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", contentResult.ContentType);
    }

    /// <summary>
    /// Verify that the concat chapters.txt file is written correctly with all chapter URLs.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_WritesCorrectConcatList()
    {
        Guid parentId = Guid.NewGuid();
        Guid chapter1Id = Guid.NewGuid();
        Guid chapter2Id = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Concat Test Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = chapter1Id
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = chapter2Id
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        // Create a fake ffmpeg that writes the concat list and creates segments
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-concat-check",
            // POSIX sh (dash) has no "${@: -1}": iterate to the last positional arg instead
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:10.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_0000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(parentId.ToString());
        controller.FfmpegPath = fakeFfmpegPath;

        var preservedQuery = controller.ControllerContext.HttpContext.Request.Query;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = CancellationToken.None }
        };
        controller.ControllerContext.HttpContext.Request.Query = preservedQuery;

        await controller.StreamHlsAudiobook(parentId.ToString());

        // Verify the concat list was written with correct chapter URLs
        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        string concatListPath = Path.Combine(hlsDir, "chapters.txt");
        Assert.True(File.Exists(concatListPath));

        string concatContent = File.ReadAllText(concatListPath);
        Assert.Contains(chapter1Id.ToString(), concatContent);
        Assert.Contains(chapter2Id.ToString(), concatContent);
        Assert.Contains("/Audio/", concatContent);
        Assert.Contains("/stream?static=true", concatContent);
        Assert.Equal(2, concatContent.Split("file '", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // ========== JF-310: encode gate + pre-encode disk budget ==========

    /// <summary>
    /// The gate capacity update is idempotent for the same value and accepts a new
    /// value without throwing (drain-safe rebuild).
    /// </summary>
    [Fact]
    public void EncodeGate_UpdateCapacity_IdempotentAndSafe()
    {
        VideoAudioController.UpdateEncodeGateCapacity(3);
        VideoAudioController.UpdateEncodeGateCapacity(3); // same value: no-op
        VideoAudioController.UpdateEncodeGateCapacity(2); // restore default
    }

    private static SemaphoreSlim GateField()
        => (SemaphoreSlim)typeof(VideoAudioController)
            .GetField("_encodeGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

    /// <summary>
    /// JF-421: the gate is not rebuilt when the CONFIGURED capacity is unchanged,
    /// even with an encode in flight (the old check compared free slots from the
    /// per-request ctor, so any in-flight encode rebuilt a full semaphore and the
    /// JF-310 bound never bound).
    /// </summary>
    [Fact]
    public async Task EncodeGate_InFlightEncode_UnchangedConfigDoesNotRebuild()
    {
        // Force a fresh gate with a known state: the static gate is shared across
        // the test class, and earlier tests may hold unreleased slots.
        VideoAudioController.UpdateEncodeGateCapacity(4);
        VideoAudioController.UpdateEncodeGateCapacity(2);
        var original = GateField();

        // Simulate one encode holding a slot (cap 2 -> one free)
        await original.WaitAsync();
        try
        {
            VideoAudioController.UpdateEncodeGateCapacity(2);
            Assert.Same(original, GateField());
            Assert.Equal(1, GateField().CurrentCount); // the held slot still holds
        }
        finally
        {
            original.Release();
        }

        VideoAudioController.UpdateEncodeGateCapacity(2); // restore default
    }

    /// <summary>JF-421: a real capacity change rebuilds with the new cap available.</summary>
    [Fact]
    public void EncodeGate_CapacityRaise_RebuildsWithFullCount()
    {
        VideoAudioController.UpdateEncodeGateCapacity(2);
        var original = GateField();

        VideoAudioController.UpdateEncodeGateCapacity(3);
        var raised = GateField();

        Assert.NotSame(original, raised);
        Assert.Equal(3, raised.CurrentCount);
        VideoAudioController.UpdateEncodeGateCapacity(2); // restore default
    }

    /// <summary>
    /// JF-421: lowering the cap while slots are held takes effect for NEW
    /// acquisitions immediately (in-flight holders finish on the old instance:
    /// drain-safe).
    /// </summary>
    [Fact]
    public async Task EncodeGate_CapacityLowerWhileBusy_NewCallersGetSmallerCap()
    {
        VideoAudioController.UpdateEncodeGateCapacity(3);
        var raised = GateField();

        await raised.WaitAsync();
        await raised.WaitAsync();
        try
        {
            VideoAudioController.UpdateEncodeGateCapacity(1);
            Assert.Equal(1, GateField().CurrentCount);
        }
        finally
        {
            raised.Release();
            raised.Release();
            VideoAudioController.UpdateEncodeGateCapacity(2); // restore default
        }
    }

    /// <summary>
    /// The pre-encode disk budget check must run the eviction sweep with headroom for
    /// the incoming encode, and entries that fit the budget survive (no over-eviction).
    /// </summary>
    [Fact]
    public async Task EnsureDiskBudgetBeforeEncode_FitWithinBudget_NoEviction()
    {
        string cacheDir = Path.Combine(_tempDir, "alexaskill-video-audio");
        Directory.CreateDirectory(cacheDir);
        string old = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_1000.mp4");
        string recent = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_2000.mp4");
        await File.WriteAllBytesAsync(old, new byte[50 * 1024]);
        await File.WriteAllBytesAsync(recent, new byte[50 * 1024]);
        File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(recent, DateTime.UtcNow);

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 1; // 1 MB cap, files total 100 KB

        try
        {
            // 512 KB headroom: 1 MB - 512 KB = 512 KB budget (exactly at the JF-428
            // floor); the 100 KB of files fit.
            await _cache.EnsureDiskBudgetBeforeEncodeAsync(512 * 1024);
            Assert.True(File.Exists(old), "both files fit the budget; nothing should be evicted");
            Assert.True(File.Exists(recent));
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// When the headroom exceeds the remaining budget, the OLDEST entry is evicted
    /// before the encode is allowed to start.
    /// </summary>
    [Fact]
    public async Task EnsureDiskBudgetBeforeEncode_HeadroomForcesEvictionOfOldest()
    {
        string cacheDir = Path.Combine(_tempDir, "alexaskill-video-audio");
        Directory.CreateDirectory(cacheDir);
        string old = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_1000.mp4");
        string recent = Path.Combine(cacheDir, $"{Guid.NewGuid():N}_2000.mp4");
        await File.WriteAllBytesAsync(old, new byte[600 * 1024]);
        await File.WriteAllBytesAsync(recent, new byte[100 * 1024]);
        File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        File.SetLastAccessTimeUtc(recent, DateTime.UtcNow);

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 1;

        try
        {
            // 500 KB headroom on a 1 MB cap: target 524 KB, above the JF-428 floor
            // (512 KB) so eviction is not floored; 700 KB total > 524 KB, so the
            // oldest entry must go; the recent one survives (LRU order).
            await _cache.EnsureDiskBudgetBeforeEncodeAsync(500 * 1024);
            Assert.False(File.Exists(old), "the oldest entry should be evicted to make headroom");
            Assert.True(File.Exists(recent), "only enough entries to fit are evicted");
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// JF-428: the pre-encode headroom reservation scales with content duration
    /// (64MB/h, measured ~57MB/h on an 8.3h audiobook) with a one-hour floor; the
    /// old flat 64MB under-reserved every encode longer than an hour.
    /// </summary>
    [Theory]
    [InlineData(0L, 64L * 1024 * 1024)]
    [InlineData(30L * TimeSpan.TicksPerMinute, 64L * 1024 * 1024)]
    [InlineData(90L * TimeSpan.TicksPerMinute, 128L * 1024 * 1024)]   // 1.5h rounds UP to 2h
    [InlineData(2L * TimeSpan.TicksPerHour, 128L * 1024 * 1024)]
    [InlineData(8L * TimeSpan.TicksPerHour, 512L * 1024 * 1024)]
    public void EstimateEncodeBytes_ScalesWithDuration_FloorsAtOneHour(long runtimeTicks, long expectedBytes)
    {
        Assert.Equal(expectedBytes, VideoAudioController.EstimateEncodeBytes(runtimeTicks));
    }

    // ========== JF-498: episode HLS remux (static-vs-HLS routing for video items) ==========

    /// <summary>
    /// EAC3 source (the evidenced library shape): video stream COPY at full
    /// framerate, audio transcode to AAC 192k, 4s segments, explicit V:0/a:0
    /// mapping (capital V: ffmpeg video streams EXCLUDING attached pictures, so
    /// an embedded cover cannot steal the video slot), and NO -shortest (both
    /// streams come from one finite input).
    /// </summary>
    [Fact]
    public void BuildEpisodeHlsFfmpegArguments_Eac3Source_CopiesVideoAndTranscodesAudio()
    {
        string videoUrl = "http://localhost:8096/Videos/abc/stream?static=true";

        List<string> args = VideoAudioController.BuildEpisodeHlsFfmpegArguments(
            videoUrl, "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/segments/", "eac3");

        // Input: the item's own static stream
        Assert.Contains(videoUrl, args);

        // Explicit mapping: first video + first audio only (drops subtitles and
        // attached-picture covers)
        int mapIdx = args.IndexOf("-map");
        Assert.True(mapIdx >= 0, "expected explicit -map");
        Assert.Equal("0:V:0", args[mapIdx + 1]);
        Assert.Equal("-map", args[mapIdx + 2]);
        Assert.Equal("0:a:0", args[mapIdx + 3]);

        // Video: copy (the sources routed here are H.264) with the GOP hint
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("48", args[args.IndexOf("-g") + 1]);

        // Audio: AAC stereo downmix at 192k (EAC3 has no Echo decoder, and the
        // Echo's ExoPlayer decodes STEREO AAC only: a 6-channel AAC track
        // black-screened on-device 2026-09-06)
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("2", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("192k", args[args.IndexOf("-b:a") + 1]);

        // HLS: 4-second segments, full listing, event-style growth, MPEG-TS
        Assert.Equal("4", args[args.IndexOf("-hls_time") + 1]);
        Assert.Equal("0", args[args.IndexOf("-hls_list_size") + 1]);
        Assert.Equal("append_list", args[args.IndexOf("-hls_flags") + 1]);
        Assert.Equal("mpegts", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Equal("/tmp/hls/seg_%04d.ts", args[args.IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal("/alexaskill/api/video-audio/abc/segments/", args[args.IndexOf("-hls_base_url") + 1]);

        // No -shortest: it exists to bound the audiobook path's infinite art input;
        // here both output streams come from ONE finite input.
        Assert.DoesNotContain("-shortest", args);

        // Playlist is the final positional output
        Assert.Equal("/tmp/hls/stream.m3u8", args[^1]);
    }

    /// <summary>
    /// A copy-compatible source audio codec (aac/mp3) streams audio as-is: only the
    /// container changes (mkv -> MPEG-TS), matching BuildAudioCodecArgs' selection.
    /// </summary>
    [Fact]
    public void BuildEpisodeHlsFfmpegArguments_AacSource_CopiesAudio()
    {
        List<string> args = VideoAudioController.BuildEpisodeHlsFfmpegArguments(
            "http://localhost:8096/Videos/abc/stream?static=true",
            "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/segments/", "aac");

        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.Null(args.FirstOrDefault(a => a == "-b:a"));
    }

    /// <summary>Unknown audio codec: transcode to AAC (the conservative default).</summary>
    [Fact]
    public void BuildEpisodeHlsFfmpegArguments_UnknownAudio_TranscodesToAac()
    {
        List<string> args = VideoAudioController.BuildEpisodeHlsFfmpegArguments(
            "http://localhost:8096/Videos/abc/stream?static=true",
            "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/segments/", null);

        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
    }

    /// <summary>
    /// JF-498: the episode remux COPIES the video bytes, so its on-disk estimate is
    /// ~20x the art+audio path (1280MB/h vs 64MB/h): ~2.5Mbps H.264 video + 192k AAC
    /// + TS overhead is ~1.2GB per hour, i.e. a 45min episode reserves one hour's
    /// worth by the same round-UP/floor shape as EstimateEncodeBytes.
    /// </summary>
    [Theory]
    [InlineData(0L, 1280L * 1024 * 1024)]
    [InlineData(45L * TimeSpan.TicksPerMinute, 1280L * 1024 * 1024)]  // 45min rounds UP to 1h
    [InlineData(90L * TimeSpan.TicksPerMinute, 2560L * 1024 * 1024)]  // 1.5h rounds UP to 2h
    [InlineData(2L * TimeSpan.TicksPerHour, 2560L * 1024 * 1024)]
    public void EstimateEpisodeEncodeBytes_ScalesWithDuration_FloorsAtOneHour(long runtimeTicks, long expectedBytes)
    {
        Assert.Equal(expectedBytes, VideoAudioController.EstimateEpisodeEncodeBytes(runtimeTicks));
    }

    /// <summary>A bare-GUID episode playlist request with no token must be rejected (401), like every other stream endpoint.</summary>
    [Fact]
    public async Task StreamHlsEpisode_NoToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");

        var controller = CreateController(); // no token in query

        ActionResult result = await controller.StreamHlsEpisode(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>Invalid GUID: 400 before anything else.</summary>
    [Fact]
    public async Task StreamHlsEpisode_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamHlsEpisode("not-a-guid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>
    /// Cache-miss flow: the endpoint starts ffmpeg (a fake script here), waits for
    /// the first segment, and serves the partial playlist with the stream token
    /// injected into the segment lines. The recorded ffmpeg arguments must target
    /// the item's STATIC /Videos/ stream as input (the no-auth shape the song path
    /// uses for /Audio/) with the video-copy argument set: a KNOWN h264 source
    /// (probed via the media source manager) keeps the remux tier.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_CacheMiss_ServesPartialPlaylistAndFeedsStaticVideoUrlToFfmpeg()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        // Fake ffmpeg: record its arguments next to the output playlist, create the
        // first segment + playlist, exit 0.
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-episode");

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        // Partial playlist served as content with the token injected on segment lines
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);

        // The encode ran against the item's static video stream URL
        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string recordedArgs = File.ReadAllText(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Contains($"/Videos/{episode.Id}/stream?static=true", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("-c:v", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("copy", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("0:V:0", recordedArgs, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-500: the same cache-miss flow with an HEVC video source (read via the
    /// media source manager) runs the video TRANSCODE tier: the recorded ffmpeg
    /// arguments carry the measured libx264 ultrafast CRF 23 set and AAC stereo
    /// audio, NOT the remux's video copy, plus the UNCONDITIONAL height clamp.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_HevcSource_BuildsVideoTranscodeArgs()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "hevc", Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-hevc");

        var controller = new VideoAudioController(
            _libraryManagerMock.Object, _mediaEncoderMock.Object, _cache, _loggerFactory, mediaSourceManager.Object);
        controller.FfmpegPath = fakeFfmpegPath;
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Request =
                {
                    Query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                    {
                        ["token"] = StreamTokenHelper.Mint(episode.Id.ToString(), _config.StreamTokenSecret)
                    })
                }
            }
        };

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string[] tokens = File.ReadAllLines(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Equal("libx264", tokens[Array.IndexOf(tokens, "-c:v") + 1]);
        Assert.Equal("ultrafast", tokens[Array.IndexOf(tokens, "-preset") + 1]);
        Assert.Equal("23", tokens[Array.IndexOf(tokens, "-crf") + 1]);
        Assert.Equal("48", tokens[Array.IndexOf(tokens, "-g") + 1]);
        Assert.Equal("aac", tokens[Array.IndexOf(tokens, "-c:a") + 1]);
        Assert.DoesNotContain("copy", tokens);
        Assert.Contains("0:V:0", tokens);
        Assert.Equal($"scale=-2:min(ih\\,{VideoAudioController.MaxEpisodeTranscodeHeight})", tokens[Array.IndexOf(tokens, "-vf") + 1]);
    }

    /// <summary>
    /// JF-500 review R1: an UNKNOWN video codec (here: no media source manager at
    /// all, the transient stream-read failure shape) takes the TRANSCODE tier, not
    /// the copy remux. The endpoint owns the cache: a copy remux of undecodable
    /// bytes completes with ENDLIST and would be served forever by the cache-hit
    /// path, which never re-probes.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_UnknownVideoCodec_BuildsVideoTranscodeArgs()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Unprobed S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        // No media source manager (4-arg ctor): the codec probe returns null.
        var controller = CreateController(episode.Id.ToString(), null, null, WriteRecordingFakeFfmpeg("fake-ffmpeg-unknown"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string[] tokens = File.ReadAllLines(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Equal("libx264", tokens[Array.IndexOf(tokens, "-c:v") + 1]);
        Assert.DoesNotContain("copy", tokens);
        Assert.Contains("0:V:0", tokens);
    }

    /// <summary>
    /// JF-500 reviews R1/R2: video streams with a BLANK codec are skipped (the
    /// handler-side ExtractCodecs semantics, so the two probes cannot disagree)
    /// and an attached-picture mjpeg cover ahead of the real track cannot win the
    /// pick: the REAL h264 track decides, and the endpoint builds REMUX args.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_BlankAndCoverStreamsFirst_BuildsRemuxArgs()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Cover-First S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = string.Empty },
                new() { Type = MediaStreamType.Video, Codec = "mjpeg" },
                new() { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-coverfirst"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string[] tokens = File.ReadAllLines(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Equal("copy", tokens[Array.IndexOf(tokens, "-c:v") + 1]);
        Assert.DoesNotContain("libx264", tokens);
        Assert.Contains("0:V:0", tokens);
    }

    /// <summary>
    /// Writes a fake ffmpeg shell script under the test temp dir, chmods it
    /// executable, and returns its path: the ONE home of the write + chmod
    /// boilerplate (and its CA3003/CA1416 pragmas) that the tier tests
    /// previously carried inline (JF-525). The body is the script AFTER the
    /// shebang line.
    /// </summary>
    private string WriteFakeFfmpeg(string name, string scriptBody)
    {
        string fakeFfmpegPath = Path.Combine(_tempDir, name);
        // Tail guard (JF-731 rework): dash (ubuntu-latest's /bin/sh) exec-optimizes
        // a script's final simple external command into the same pid, so a script
        // ending in a bare `sleep` would have its /proc cmdline rewritten to just
        // "sleep N" and the temp-dir process half of the Dispose backstop could
        // no longer name it on CI. Appending `exit $?` (a builtin, preserving the
        // script's status exactly) keeps the shell image, and with it the script
        // path in its cmdline, alive for the whole script, on every /bin/sh.
        File.WriteAllText(fakeFfmpegPath, "#!/bin/sh\n" + scriptBody + "\nexit $?\n");
#pragma warning disable CA3003, CA1416 // test-created path; Unix-only test
        File.SetUnixFileMode(fakeFfmpegPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA3003, CA1416
        return fakeFfmpegPath;
    }

    /// <summary>
    /// The cross-tick pins' shared teardown (extracted at the FIFTH copy,
    /// 2026-09-30: the JF-675 simplify round deferred this extraction until a
    /// third cross-tick pin appeared, and JF-676 added the fourth and fifth).
    /// Releases every parked fake ffmpeg of the scenario (writes the stop file
    /// into each generation directory, best-effort), lets the monitors run
    /// their generation clears, then force-drops the registry entry so nothing
    /// leaks into other tests. Every pin's key is a fresh Guid, so this is
    /// hygiene, not correctness.
    /// </summary>
    /// <param name="key">The pin's cache key (episode itemId, song itemId, or audiobook parentId).</param>
    /// <param name="dirs">The scenario's generation directories (stop-file targets).</param>
    /// <param name="song">True when the pin drives the single-item registry.</param>
    /// <param name="audiobook">True when the pin drives the audiobook registry.</param>
    private async Task ReleaseParkedEncodeFixturesAsync(string key, string[] dirs, bool song = false, bool audiobook = false)
    {
        foreach (string dir in dirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "stop"), string.Empty);
            }
            catch (IOException)
            {
                // Best effort: the parked fake loops are short and exit on
                // their own otherwise.
            }
        }

        await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(key, audiobook, song) == 0, TimeSpan.FromSeconds(10), 100);
        VideoAudioController.SetEncodeActiveForTest(key, active: false, audiobook, song);
    }

    /// <summary>
    /// Fake ffmpeg for the tier-pick tests: records its arguments next to the
    /// output playlist, creates the first segment + playlist, exits 0 (the
    /// first-segment wait then succeeds immediately). The segment name is a
    /// parameter because the SONG path's first-segment wait polls the 3-digit
    /// <c>seg_000.ts</c> while every other path polls 4-digit names; a twin
    /// whose re-encode must actually SERVE on the song path passes the 3-digit
    /// name (the JF-499/JF-677/JF-678 song twins' rationale: the 4-digit
    /// default fails those twins fast but cleanly, without the re-encode their
    /// own asserts require).
    /// </summary>
    private string WriteRecordingFakeFfmpeg(string name, string firstSegmentName = "seg_0000.ts")
        => WriteFakeFfmpeg(name,
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "printf '%s\\n' \"$@\" > \"$dir/episode-args.txt\"\n" +
            $"dd if=/dev/zero bs=1024 count=4 of=\"$dir/{firstSegmentName}\" 2>/dev/null\n" +
            $"printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\n{firstSegmentName}\\n' > \"$last_arg\"\n" +
            "exit 0\n");

    /// <summary>
    /// JF-498 review C1b (folded into the combined probe, JF-539): the bitrate side
    /// of <see cref="VideoAudioController.ResolveSourceCodecs"/> sums the FIRST
    /// video stream's BitRate and the FIRST audio stream's BitRate; later audio
    /// streams and subtitle streams are ignored (the remux maps 0:V:0 + 0:a:0).
    /// </summary>
    [Fact]
    public void ResolveSourceCodecs_TotalBitrateBps_SumsFirstVideoAndAudioStreams()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E02",
            Id = Guid.NewGuid()
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "h264", BitRate = 2_500_000 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3", BitRate = 384_000 },
                new() { Type = MediaStreamType.Audio, Codec = "aac", BitRate = 192_000 },
                new() { Type = MediaStreamType.Subtitle, Codec = "subrip", BitRate = 1_000_000 }
            });

        var controller = new VideoAudioController(
            _libraryManagerMock.Object, _mediaEncoderMock.Object, _cache, _loggerFactory, mediaSourceManager.Object);

        Assert.Equal(2_884_000L, controller.ResolveSourceCodecs(episode).TotalBitrateBps);
    }

    /// <summary>
    /// C1b (folded into the combined probe, JF-539): the bitrate side returns null
    /// (flat-estimate fallback) when no stream carries a BitRate and when the media
    /// source manager is unavailable (the 4-arg ctor leaves it null, the same shape
    /// the song path runs with).
    /// </summary>
    [Fact]
    public void ResolveSourceCodecs_TotalBitrateBps_NoBitrateOrNoManager_ReturnsNull()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E03",
            Id = Guid.NewGuid()
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "h264" },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        var withStreams = new VideoAudioController(
            _libraryManagerMock.Object, _mediaEncoderMock.Object, _cache, _loggerFactory, mediaSourceManager.Object);
        Assert.Null(withStreams.ResolveSourceCodecs(episode).TotalBitrateBps);

        var withoutManager = CreateController();
        Assert.Null(withoutManager.ResolveSourceCodecs(episode).TotalBitrateBps);
    }

    /// <summary>
    /// C1b: with a source bitrate the estimate scales from it (bytes = bits/s / 8 *
    /// seconds, +10% container overhead): a 10Mbps Blu-ray remux reserves ~4.95GB per
    /// hour instead of the flat 1280MB, and a TV-shaped ~2.9Mbps source reserves
    /// slightly more than the flat rate.
    /// </summary>
    [Theory]
    [InlineData(45L * TimeSpan.TicksPerMinute, 10_000_000L, 3_712_500_000L)]
    [InlineData(TimeSpan.TicksPerHour, 10_000_000L, 4_950_000_000L)]
    [InlineData(TimeSpan.TicksPerHour, 2_884_000L, 1_427_580_000L)]
    public void EstimateEpisodeEncodeBytes_BitRateAware_ScalesWithSourceBitrate(long runtimeTicks, long totalBitRateBps, long expectedBytes)
    {
        Assert.Equal(expectedBytes, VideoAudioController.EstimateEpisodeEncodeBytes(runtimeTicks, totalBitRateBps));
    }

    /// <summary>
    /// C1b: absent (null) or zero bitrate falls back to the flat 1280MB/h with the
    /// round-UP/floor shape, and a bitrate with an unknown runtime reserves the flat
    /// one-hour floor.
    /// </summary>
    [Theory]
    [InlineData(2L * TimeSpan.TicksPerHour, null, 2560L * 1024 * 1024)]
    [InlineData(2L * TimeSpan.TicksPerHour, 0L, 2560L * 1024 * 1024)]
    [InlineData(0L, 10_000_000L, 1280L * 1024 * 1024)]
    public void EstimateEpisodeEncodeBytes_AbsentBitrateOrUnknownRuntime_FallsBackToFlat(long runtimeTicks, long? totalBitRateBps, long expectedBytes)
    {
        Assert.Equal(expectedBytes, VideoAudioController.EstimateEpisodeEncodeBytes(runtimeTicks, totalBitRateBps));
    }

    // ========== JF-500: episode HLS video transcode tier (hevc/av1 sources) ==========

    /// <summary>
    /// The measured transcode parameter set (2026-09-08, minix, Adolescence E1
    /// 1080p HEVC): libx264 ultrafast CRF 23 with a live GOP of 48 (a keyframe at
    /// least every ~2s at TV framerates so the muxer can cut 4s segments), audio
    /// exactly as the remux pipeline (AAC 192k stereo for the EAC3 family), and the
    /// shared segment/playlist machinery (4s MPEG-TS, append_list, no -shortest).
    /// The UNCONDITIONAL height clamp rides in the video token list: a no-op at
    /// &lt;=1080p, it removes the height-probe dependency entirely (R3; a >1080p
    /// source whose probe failed open used to run unscaled and hit the monitor
    /// kill). Verified on ffmpeg 8.1.2: 3840x2160 -> 1920x1080, 1280x720 unchanged.
    /// </summary>
    [Fact]
    public void BuildEpisodeHlsTranscodeFfmpegArguments_HevcSource_UsesMeasuredParameterSet()
    {
        string videoUrl = "http://localhost:8096/Videos/abc/stream?static=true";

        List<string> args = VideoAudioController.BuildEpisodeHlsTranscodeFfmpegArguments(
            videoUrl, "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/segments/", "eac3");

        // Input: the item's own static stream (same input as the remux)
        Assert.Contains(videoUrl, args);

        // Explicit mapping: first video + first audio only (drops subtitles and
        // attached-picture covers: capital V excludes them)
        int mapIdx = args.IndexOf("-map");
        Assert.True(mapIdx >= 0, "expected explicit -map");
        Assert.Equal("0:V:0", args[mapIdx + 1]);
        Assert.Equal("-map", args[mapIdx + 2]);
        Assert.Equal("0:a:0", args[mapIdx + 3]);

        // Video: the MEASURED H.264 re-encode set (not the remux's copy)
        Assert.Equal("libx264", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("ultrafast", args[args.IndexOf("-preset") + 1]);
        Assert.Equal("23", args[args.IndexOf("-crf") + 1]);
        Assert.Equal("48", args[args.IndexOf("-g") + 1]);

        // Pixel format: forced 8-bit 4:2:0 so a 10-bit HEVC source transcodes to
        // a profile the Echo decodes (libx264 would otherwise emit High-10 H.264)
        Assert.Equal("yuv420p", args[args.IndexOf("-pix_fmt") + 1]);

        // The constant height clamp (always present, no probe dependency)
        Assert.Equal($"scale=-2:min(ih\\,{VideoAudioController.MaxEpisodeTranscodeHeight})", args[args.IndexOf("-vf") + 1]);

        // Audio: exactly the remux pipeline's selection (AAC stereo downmix at 192k)
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("2", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("192k", args[args.IndexOf("-b:a") + 1]);

        // HLS machinery shared with the remux, unchanged
        Assert.Equal("4", args[args.IndexOf("-hls_time") + 1]);
        Assert.Equal("0", args[args.IndexOf("-hls_list_size") + 1]);
        Assert.Equal("append_list", args[args.IndexOf("-hls_flags") + 1]);
        Assert.Equal("mpegts", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Equal("/tmp/hls/seg_%04d.ts", args[args.IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal("/alexaskill/api/video-audio/abc/segments/", args[args.IndexOf("-hls_base_url") + 1]);
        Assert.DoesNotContain("-shortest", args);
        Assert.Equal("/tmp/hls/stream.m3u8", args[^1]);
    }

    /// <summary>A copy-compatible source audio codec (aac/mp3) streams audio as-is, mirroring the remux's selection.</summary>
    [Fact]
    public void BuildEpisodeHlsTranscodeFfmpegArguments_AacSource_CopiesAudio()
    {
        List<string> args = VideoAudioController.BuildEpisodeHlsTranscodeFfmpegArguments(
            "http://localhost:8096/Videos/abc/stream?static=true",
            "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/segments/", "aac");

        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.Null(args.FirstOrDefault(a => a == "-b:a"));
    }

    /// <summary>
    /// JF-500 size estimate: CRF output size is complexity-driven, not
    /// bitrate-driven, so the tier reserves a flat 3GB/h (~2-3GB/h measured band
    /// for H.264 CRF 23 at 1080p) with the round-UP/floor shape of the other
    /// estimates. Drives the JF-428 pre-encode headroom for multi-GB transcodes.
    /// </summary>
    [Theory]
    [InlineData(0L, 3072L * 1024 * 1024)]
    [InlineData(45L * TimeSpan.TicksPerMinute, 3072L * 1024 * 1024)]  // 45min rounds UP to 1h
    [InlineData(90L * TimeSpan.TicksPerMinute, 6144L * 1024 * 1024)]  // 1.5h rounds UP to 2h
    [InlineData(2L * TimeSpan.TicksPerHour, 6144L * 1024 * 1024)]
    public void EstimateEpisodeTranscodeEncodeBytes_ScalesWithRuntime_FloorsAtOneHour(long runtimeTicks, long expectedBytes)
    {
        Assert.Equal(expectedBytes, VideoAudioController.EstimateEpisodeTranscodeEncodeBytes(runtimeTicks));
    }

    /// <summary>
    /// JF-534: the transcode tier's one-hour reserve must fit under the DEFAULT
    /// cache cap (why the old 2048 default failed: the VideoAudioCacheSizeMB field
    /// doc). Pins the config default (4096) against the estimator: change the two
    /// together or the tier regresses to encode churn on every play.
    /// </summary>
    [Fact]
    public void EstimateEpisodeTranscodeEncodeBytes_OneHourReserve_FitsUnderDefaultCacheCap()
    {
        long oneHourReserve = VideoAudioController.EstimateEpisodeTranscodeEncodeBytes(TimeSpan.FromHours(1).Ticks);
        int defaultCapMB = new PluginConfiguration().VideoAudioCacheSizeMB;

        Assert.Equal(4096, defaultCapMB);
        Assert.True(
            oneHourReserve < defaultCapMB * 1024L * 1024L,
            $"transcode reserve ({oneHourReserve / (1024L * 1024L)}MB) must fit under the default cap ({defaultCapMB}MB)");
    }

    /// <summary>
    /// JF-537: the oversize boundary is strictly-greater. An estimate EQUAL to the cap
    /// still fits (the sweep target is cap minus headroom and the JF-428 half-cap floor
    /// bounds how far it evicts); one byte over cannot be retained.
    /// </summary>
    [Theory]
    [InlineData(0L, 4096, false)]
    [InlineData(4096L * 1024 * 1024, 4096, false)]
    [InlineData(4096L * 1024 * 1024 + 1, 4096, true)]
    [InlineData(6144L * 1024 * 1024, 512, true)]
    public void TranscodeEstimateExceedsCacheCap_Boundary_AtCapFits_OneByteOverExceeds(
        long estimatedEncodeBytes, int cacheCapMB, bool expectedOverCap)
    {
        Assert.Equal(expectedOverCap, VideoAudioController.TranscodeEstimateExceedsCacheCap(estimatedEncodeBytes, cacheCapMB));
    }

    /// <summary>
    /// JF-537 (announced churn): a transcode-tier estimate above the configured cap
    /// must log a WARNING naming the item, the estimate, and the cap (with the
    /// VideoAudioCacheSizeMB hint), while the encode itself proceeds exactly as
    /// before: playlist served, transcode argument set, i.e. no behavior change on
    /// the encode path. 2h HEVC at the 3072MB/h flat rate reserves 6144MB against a
    /// 512MB cap.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_EstimateOverCap_LogsOversizeWarningAndEncodesNormally()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromHours(2).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "hevc", Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf537-overcap");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 512;
        try
        {
            var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath, loggerFactory);

            ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

            // The encode was NOT refused or rerouted: the playlist is served.
            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);

            // And it ran the transcode tier as usual.
            string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
            string[] tokens = File.ReadAllLines(Path.Combine(hlsDir, "episode-args.txt"));
            Assert.Equal("libx264", tokens[Array.IndexOf(tokens, "-c:v") + 1]);

            // The oversize warning fired with item, estimate, and cap.
            var warnings = TestCaptureLogger.Snapshot(logRecords)
                .Where(r => r.Level == LogLevel.Warning && r.Message.Contains("cannot be retained", StringComparison.Ordinal))
                .ToList();
            Assert.Single(warnings);
            Assert.Contains(episode.Id.ToString(), warnings[0].Message, StringComparison.Ordinal);
            Assert.Contains("6144MB", warnings[0].Message, StringComparison.Ordinal);
            Assert.Contains("512MB", warnings[0].Message, StringComparison.Ordinal);
            Assert.Contains("VideoAudioCacheSizeMB", warnings[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// JF-537: the under-cap transcode encode logs the cacheable DECISION at Debug
    /// (the debug-logging policy: handler branching decisions are debug-visible) and
    /// emits NO oversize warning. 45min HEVC reserves 3072MB under the default 4096.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_EstimateUnderCap_NoOversizeWarning_LogsCacheableDecision()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E03",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "hevc", Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf537-undercap");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath, loggerFactory);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);
        // The encode keeps logging from its background poll thread after the
        // awaited call returns; enumerate a lock-consistent snapshot (CI flake
        // 2026-09-13: "Collection was modified").
        var finalRecords = TestCaptureLogger.Snapshot(logRecords);
        Assert.DoesNotContain(
            finalRecords,
            r => r.Message.Contains("cannot be retained", StringComparison.Ordinal));
        var decisions = finalRecords
            .Where(r => r.Message.Contains("cacheable", StringComparison.Ordinal))
            .ToList();
        Assert.Single(decisions);
        Assert.Equal(LogLevel.Debug, decisions[0].Level);
        Assert.Contains("3072MB", decisions[0].Message, StringComparison.Ordinal);
        Assert.Contains("4096MB", decisions[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-537 scope pin: the oversize decision targets the TRANSCODE tier ONLY. A
    /// REMUX-tier estimate above the cap (h264 source, 4h runtime, flat 1280MB/h =
    /// 5120MB against a 256MB cap) must NOT fire the oversize warning: the remux
    /// copies at the source's own bitrate and replays at ~20x realtime, so its churn
    /// is not the multi-hour re-encode pain the decision exists to announce (full
    /// rationale on <see cref="VideoAudioController.TranscodeEstimateExceedsCacheCap"/>).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_RemuxTier_EstimateOverCap_NoOversizeWarning()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "H264 Long Movie",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromHours(4).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = "h264", Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "aac" }
            });

        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf537-remux");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 256;
        try
        {
            var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath, loggerFactory);

            ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

            // Remux tier ran (video copy), no oversize warning fired.
            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("cannot be retained", StringComparison.Ordinal));
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// JF-500 reviews R1/R2: the video codec probe SKIPS streams with a blank
    /// codec (the handler-side <c>ExtractCodecs</c> semantics, so the two probes
    /// cannot disagree and diverge the route from the tier) and attached-picture
    /// covers (mjpeg/png ahead of the real track), landing on the REAL track's
    /// codec. No manager (the 4-arg ctor) yields null, which the endpoint maps to
    /// the transcode tier.
    /// </summary>
    [Fact]
    public void ResolveSourceCodecs_Video_SkipsBlankAndCoverStreams_PicksTheRealTrack()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E04",
            Id = Guid.NewGuid()
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = string.Empty },
                new() { Type = MediaStreamType.Video, Codec = "mjpeg" },
                new() { Type = MediaStreamType.Video, Codec = " " },
                new() { Type = MediaStreamType.Video, Codec = "H264" },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        var controller = new VideoAudioController(
            _libraryManagerMock.Object, _mediaEncoderMock.Object, _cache, _loggerFactory, mediaSourceManager.Object);
        Assert.Equal("h264", controller.ResolveSourceCodecs(episode).Video);

        // No manager (the 4-arg ctor): unknown (null).
        Assert.Null(CreateController().ResolveSourceCodecs(episode).Video);
    }

    /// <summary>
    /// JF-500 review F1: the monitor kill timeout is sized for the tier. The remux
    /// and audio tiers keep the historical 30-minute ceiling regardless of runtime
    /// (byte-unchanged behavior); the transcode tier (measured 4.40x realtime)
    /// scales from the item runtime at the conservative 2.0x floor plus 10 minutes
    /// of startup slack, floored at 30 minutes for short content. A transcode tier
    /// with an UNKNOWN runtime (null/&lt;=0, review R4) gets 120 minutes, not 30:
    /// 30 would still hard-kill any movie longer than the ~132-minute boundary,
    /// while 120 bounds how long a hung encode holds its transcode slot. 132
    /// minutes is the old kill boundary (30 wall min at 4.40x): it now clears it
    /// with margin.
    /// </summary>
    [Theory]
    [InlineData(false, null, 30)]
    [InlineData(false, 65L * TimeSpan.TicksPerMinute, 30)]
    [InlineData(true, null, 120)]
    [InlineData(true, 0L, 120)]
    [InlineData(true, 10L * TimeSpan.TicksPerMinute, 30)]
    [InlineData(true, 45L * TimeSpan.TicksPerMinute, 33)]
    [InlineData(true, 65L * TimeSpan.TicksPerMinute, 43)]
    [InlineData(true, 132L * TimeSpan.TicksPerMinute, 76)]
    [InlineData(true, 150L * TimeSpan.TicksPerMinute, 85)]
    public void HlsMonitorTimeoutMinutes_RemuxKeepsCeiling_TranscodeScalesWithRuntime(bool videoTranscodeTier, long? runTimeTicks, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, VideoAudioController.HlsMonitorTimeoutMinutes(videoTranscodeTier, runTimeTicks));
    }

    /// <summary>
    /// JF-500 review F2: two concurrent HEVC transcodes run ONE ffmpeg at a time.
    /// The second request waits on the dedicated transcode slot while holding NO
    /// shared gate slot, and proceeds only after the first encode finishes (the
    /// release file makes the fake ffmpeg exit, which frees the slot).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TwoConcurrentTranscodes_SecondWaitsForTheSlot()
    {
        // The "one spawn" premise needs the gate at its default capacity even if an
        // earlier test in this collection failed before restoring its own override.
        VideoAudioController.UpdateEncodeGateCapacity(2);

        string spawnLog = Path.Combine(_tempDir, "transcode-spawns.log");
        string releaseFile = Path.Combine(_tempDir, "transcode-release");
        string fakeFfmpegPath = WriteBlockingFakeFfmpeg(spawnLog, releaseFile);

        var episodeA = NewHevcEpisode("Adolescence S01E01");
        var episodeB = NewHevcEpisode("Adolescence S01E02");
        var mediaSourceManager = SetupEpisodeMediaStreams(episodeA, episodeB);
        SetupLibraryItems(episodeA, episodeB);

        try
        {
            // First transcode: acquires the slot, spawns its ffmpeg, serves the playlist.
            var controllerA = CreateEpisodeController(mediaSourceManager, episodeA.Id.ToString(), fakeFfmpegPath);
            Task<ActionResult> taskA = controllerA.StreamHlsEpisode(episodeA.Id.ToString());
            ActionResult resultA = await taskA.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ContentResult>(resultA);
            Assert.Equal(1, CountFfmpegSpawns(spawnLog));

            // Second transcode for a DIFFERENT item (no per-item lock sharing): it must
            // wait on the transcode slot without spawning a second ffmpeg.
            var controllerB = CreateEpisodeController(mediaSourceManager, episodeB.Id.ToString(), fakeFfmpegPath);
            Task<ActionResult> taskB = controllerB.StreamHlsEpisode(episodeB.Id.ToString());
            await Task.Delay(1500);
            Assert.False(taskB.IsCompleted, "second transcode should still be waiting on the slot");
            Assert.Equal(1, CountFfmpegSpawns(spawnLog));

            // First encode finishes: the slot frees and the queued transcode proceeds.
            File.WriteAllText(releaseFile, "go");
            ActionResult resultB = await taskB.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ContentResult>(resultB);
            Assert.Equal(2, CountFfmpegSpawns(spawnLog));

            await AssertTranscodeSlotReleasedAsync();
        }
        finally
        {
            // Even on an assertion failure, let the fake ffmpeg exit so its static
            // slot/gate hold cannot cascade into the next test of this collection.
            File.WriteAllText(releaseFile, "go");
        }
    }

    /// <summary>
    /// JF-500 review F2, the household-collision scenario: with one transcode
    /// running and a second queued on the slot, a MUSIC-path gated start still
    /// proceeds immediately (the queued transcode holds no shared gate slot, so
    /// the default-capacity-2 gate always has a slot for the shorter paths).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_MusicStartProceedsWhileTranscodesOccupyTheTier()
    {
        // See the companion test: the gate must be at its default capacity.
        VideoAudioController.UpdateEncodeGateCapacity(2);

        string spawnLog = Path.Combine(_tempDir, "transcode-spawns.log");
        string releaseFile = Path.Combine(_tempDir, "transcode-release");
        string fakeFfmpegPath = WriteBlockingFakeFfmpeg(spawnLog, releaseFile);

        var episodeA = NewHevcEpisode("Adolescence S01E03");
        var episodeB = NewHevcEpisode("Adolescence S01E04");
        var mediaSourceManager = SetupEpisodeMediaStreams(episodeA, episodeB);

        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(audioItem.Id))
            .Returns(new List<MediaStream> { new() { Type = MediaStreamType.Audio, Codec = "mp3" } });
        SetupLibraryItems(episodeA, episodeB, audioItem);

        try
        {
            // Transcode running (slot + one gate slot) + a second one queued on the slot.
            var controllerA = CreateEpisodeController(mediaSourceManager, episodeA.Id.ToString(), fakeFfmpegPath);
            Task<ActionResult> taskA = controllerA.StreamHlsEpisode(episodeA.Id.ToString());
            Assert.IsType<ContentResult>(await taskA.WaitAsync(TimeSpan.FromSeconds(20)));

            var controllerB = CreateEpisodeController(mediaSourceManager, episodeB.Id.ToString(), fakeFfmpegPath);
            Task<ActionResult> taskB = controllerB.StreamHlsEpisode(episodeB.Id.ToString());
            await Task.Delay(1500);
            Assert.Equal(1, CountFfmpegSpawns(spawnLog));

            // Music path: must start and complete while the second transcode still waits.
            var musicController = CreateEpisodeController(mediaSourceManager, audioItem.Id.ToString(), fakeFfmpegPath);
            Task<ActionResult> musicTask = musicController.StreamHlsVideoAudio(audioItem.Id.ToString());
            ActionResult musicResult = await musicTask.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ContentResult>(musicResult);
            Assert.False(taskB.IsCompleted, "second transcode should still be waiting on the slot");
            Assert.Equal(2, CountFfmpegSpawns(spawnLog));

            // Cleanup: let both queued/running encodes finish.
            File.WriteAllText(releaseFile, "go");
            Assert.IsType<ContentResult>(await taskB.WaitAsync(TimeSpan.FromSeconds(20)));
            await AssertTranscodeSlotReleasedAsync();
        }
        finally
        {
            // See the companion test: release the fakes even on failure.
            File.WriteAllText(releaseFile, "go");
        }
    }

    private static MediaBrowser.Controller.Entities.TV.Episode NewHevcEpisode(string name)
        => new()
        {
            Name = name,
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

    /// <summary>Serves the given items from the shared library manager mock.</summary>
    private void SetupLibraryItems(params MediaBrowser.Controller.Entities.BaseItem[] items)
    {
        var byId = items.ToDictionary(i => i.Id);
        _libraryManagerMock
            .Setup(m => m.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => byId.TryGetValue(id, out var item) ? item : null);
    }

    /// <summary>
    /// Media-stream mock giving both episodes the HEVC shape that routes them to
    /// the video transcode tier.
    /// </summary>
    private static Mock<IMediaSourceManager> SetupEpisodeMediaStreams(
        params MediaBrowser.Controller.Entities.TV.Episode[] episodes)
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        foreach (var episode in episodes)
        {
            mediaSourceManager
                .Setup(m => m.GetMediaStreams(episode.Id))
                .Returns(new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Video, Codec = "hevc", Height = 1080 },
                    new() { Type = MediaStreamType.Audio, Codec = "eac3" }
                });
        }

        return mediaSourceManager;
    }

    /// <summary>
    /// A controller for the episode tests: 5-arg ctor (with the media source
    /// manager the codec probe needs) plus a fake ffmpeg path, on the shared
    /// <see cref="CreateController"/> factory (which mints the stream token).
    /// </summary>
    private VideoAudioController CreateEpisodeController(
        Mock<IMediaSourceManager> mediaSourceManager,
        string itemId,
        string fakeFfmpegPath,
        ILoggerFactory? loggerFactory = null)
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        return CreateController(itemId, loggerFactory, mediaSourceManager, fakeFfmpegPath);
    }

    /// <summary>
    /// Fake ffmpeg for the transcode-slot tests: records its spawn (one line per
    /// invocation), creates the first segment + playlist for BOTH the episode
    /// (seg_0000.ts) and the song (seg_000.ts) wait loops, then stays alive until
    /// the release file appears so the encode holds its slot + gate. The wait is
    /// bounded (60s) so an aborted test cannot leave a fake ffmpeg holding the
    /// static gate/slot forever; a passing run releases it within ~5s.
    /// </summary>
    private string WriteBlockingFakeFfmpeg(string spawnLog, string releaseFile)
        => WriteFakeFfmpeg("fake-ffmpeg-transcode-" + Guid.NewGuid().ToString("N"),
            "printf 'spawn\\n' >> \"" + spawnLog + "\"\n" +
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "cp \"$dir/seg_0000.ts\" \"$dir/seg_000.ts\"\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "i=0\n" +
            "while [ ! -e \"" + releaseFile + "\" ] && [ \"$i\" -lt 60 ]; do sleep 1; i=$((i+1)); done\n" +
            "exit 0\n");

    private static int CountFfmpegSpawns(string spawnLog)
        => File.Exists(spawnLog) ? File.ReadAllLines(spawnLog).Length : 0;

    /// <summary>
    /// The transcode slot is STATIC: prove both encodes released it (the exit poll
    /// frees it within ~500ms of ffmpeg exiting) so later tests in this collection
    /// start from a free slot.
    /// </summary>
    private static async Task AssertTranscodeSlotReleasedAsync()
    {
        Assert.True(
            await WaitUntilAsync(() => VideoAudioController.EpisodeTranscodeSlotFree),
            "episode transcode slot was not released after the encodes exited");
    }

    // ========== JF-507: the audio-only episode HLS variant ==========

    /// <summary>
    /// JF-507 args, resume shape (start &gt; 0): an input seek (-ss BEFORE -i), the
    /// audio stream mapped ALONE (no 0:V:0, no -c:v), AAC stereo downmix at 192k,
    /// 10-second MPEG-TS segments, and the base URL pointing at the dedicated
    /// audio-segments route with the start position in the path.
    /// </summary>
    [Fact]
    public void BuildEpisodeAudioHlsFfmpegArguments_Eac3SourceWithStart_MapsAudioOnlyAndSeeks()
    {
        string videoUrl = "http://localhost:8096/Videos/abc/stream?static=true";
        long startTicks = TimeSpan.FromMinutes(5).Ticks;

        List<string> args = VideoAudioController.BuildEpisodeAudioHlsFfmpegArguments(
            videoUrl, startTicks, "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts",
            "/alexaskill/api/video-audio/abc/audio-segments/30000000000/", "eac3");

        // Input seek BEFORE -i, so the encode starts at the resume position
        int ssIdx = args.IndexOf("-ss");
        Assert.True(ssIdx >= 0, "expected -ss for a start-shifted encode");
        Assert.Equal(videoUrl, args[ssIdx + 3]);
        Assert.Equal("-i", args[ssIdx + 2]);
        Assert.Equal(
            (startTicks / (double)TimeSpan.TicksPerSecond).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            args[ssIdx + 1]);

        // ONE map: audio only. No video mapping, no video codec args.
        Assert.Equal("0:a:0", args[args.IndexOf("-map") + 1]);
        Assert.Equal(1, args.Count(a => a == "-map"));
        Assert.DoesNotContain("0:V:0", args);
        Assert.Null(args.FirstOrDefault(a => a == "-c:v"));

        // Audio: AAC stereo downmix at 192k (the EAC3 family has no Echo decoder)
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("2", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("192k", args[args.IndexOf("-b:a") + 1]);

        // HLS: 10-second MPEG-TS segments, event-style growth
        Assert.Equal("10", args[args.IndexOf("-hls_time") + 1]);
        Assert.Equal("0", args[args.IndexOf("-hls_list_size") + 1]);
        Assert.Equal("append_list", args[args.IndexOf("-hls_flags") + 1]);
        Assert.Equal("mpegts", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Equal("/tmp/hls/seg_%04d.ts", args[args.IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal("/alexaskill/api/video-audio/abc/audio-segments/30000000000/", args[args.IndexOf("-hls_base_url") + 1]);

        // No -shortest (one finite input), playlist is the positional output
        Assert.DoesNotContain("-shortest", args);
        Assert.Equal("/tmp/hls/stream.m3u8", args[^1]);
    }

    /// <summary>A from-zero encode carries no -ss (the fresh-listen shape).</summary>
    [Fact]
    public void BuildEpisodeAudioHlsFfmpegArguments_FromZero_HasNoSeek()
    {
        List<string> args = VideoAudioController.BuildEpisodeAudioHlsFfmpegArguments(
            "http://localhost:8096/Videos/abc/stream?static=true",
            0, "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/audio-segments/0/", "eac3");

        Assert.DoesNotContain("-ss", args);
        Assert.Equal("-i", args[0]);
    }

    /// <summary>A copy-compatible source (aac/mp3) streams audio as-is, mirroring the remux's selection.</summary>
    [Fact]
    public void BuildEpisodeAudioHlsFfmpegArguments_AacSource_CopiesAudio()
    {
        List<string> args = VideoAudioController.BuildEpisodeAudioHlsFfmpegArguments(
            "http://localhost:8096/Videos/abc/stream?static=true",
            0, "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/alexaskill/api/video-audio/abc/audio-segments/0/", "aac");

        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.Null(args.FirstOrDefault(a => a == "-b:a"));
    }

    /// <summary>
    /// JF-507 size estimate: measured 60MB for the 39-minute incident episode (~92MB/h),
    /// so the flat rate is 96MB/h with the round-UP/floor shape of EstimateEncodeBytes.
    /// </summary>
    [Theory]
    [InlineData(0L, 96L * 1024 * 1024)]
    [InlineData(39L * TimeSpan.TicksPerMinute, 96L * 1024 * 1024)]
    [InlineData(90L * TimeSpan.TicksPerMinute, 192L * 1024 * 1024)]
    [InlineData(2L * TimeSpan.TicksPerHour, 192L * 1024 * 1024)]
    public void EstimateEpisodeAudioEncodeBytes_ScalesWithRuntime_FloorsAtOneHour(long runtimeTicks, long expectedBytes)
    {
        Assert.Equal(expectedBytes, VideoAudioController.EstimateEpisodeAudioEncodeBytes(runtimeTicks));
    }

    /// <summary>
    /// The variant cache key is distinct from the video remux's bare-itemId key (no
    /// collision in the in-memory lookup or the {key}_* directory scan) and distinct per
    /// start position (a seeked encode serves a different timeline).
    /// </summary>
    [Fact]
    public void EpisodeAudioCacheKey_DistinctFromRemuxKeyAndPerStart()
    {
        string id = Guid.NewGuid().ToString();
        long startTicks = TimeSpan.FromMinutes(5).Ticks;

        Assert.NotEqual(id, VideoAudioController.EpisodeAudioCacheKey(id, 0));
        Assert.Equal($"{id}-audio", VideoAudioController.EpisodeAudioCacheKey(id, 0));
        Assert.Equal($"{id}-audio-{startTicks}", VideoAudioController.EpisodeAudioCacheKey(id, startTicks));
        Assert.NotEqual(VideoAudioController.EpisodeAudioCacheKey(id, 0), VideoAudioController.EpisodeAudioCacheKey(id, startTicks));
    }

    /// <summary>A bare-GUID audio-variant playlist request with no token must be rejected (401).</summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_NoToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");

        var controller = CreateController();

        ActionResult result = await controller.StreamHlsEpisodeAudio(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>Invalid GUID: 400 before anything else.</summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamHlsEpisodeAudio("not-a-guid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>
    /// Cache-miss flow of the audio variant: ffmpeg is fed the item's STATIC /Videos/
    /// stream, maps ONLY the audio track (no 0:V:0, no -c:v), seeks by the start
    /// position, and writes into the VARIANT cache directory; the partial playlist is
    /// served with the token injected into the audio-segments URLs.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_CacheMiss_MapsAudioOnlyIntoVariantDirectory()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "The Bear S01E02 Ribs",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(39).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        long startTicks = TimeSpan.FromMinutes(5).Ticks;
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-episode-audio",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "printf '%s\\n' \"$@\" > \"$dir/episode-audio-args.txt\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "exit 0\n");

        var controller = CreateController(episode.Id.ToString());
        controller.FfmpegPath = fakeFfmpegPath;

        ActionResult result = await controller.StreamHlsEpisodeAudio(episode.Id.ToString(), startTicks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);

        // The encode ran in the VARIANT directory (not the remux's bare-itemId one)
        string hlsDir = _cache.GetHlsDirectoryPath(VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), startTicks), 0);
        string recordedArgs = File.ReadAllText(Path.Combine(hlsDir, "episode-audio-args.txt"));

        Assert.Contains($"/Videos/{episode.Id}/stream?static=true", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("-ss", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("0:a:0", recordedArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("0:V:0", recordedArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:v", recordedArgs, StringComparison.Ordinal);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{episode.Id}/audio-segments/{startTicks}/", recordedArgs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The audio-segments route resolves the VARIANT directory (start position in the
    /// path), serves its segments, and rejects a missing token.
    /// </summary>
    [Fact]
    public async Task GetEpisodeAudioSegment_ServesVariantDirectory_AndRejectsMissingToken()
    {
        string itemId = Guid.NewGuid().ToString();
        long startTicks = TimeSpan.FromMinutes(5).Ticks;

        string hlsDir = _cache.GetHlsDirectoryPath(VideoAudioController.EpisodeAudioCacheKey(itemId, startTicks), 0);
        Directory.CreateDirectory(hlsDir);
        string segPath = Path.Combine(hlsDir, "seg_0000.ts");
        File.WriteAllText(segPath, "segment-bytes");

        var authorized = CreateController(itemId);
        ActionResult served = await authorized.GetEpisodeAudioSegment(itemId, startTicks, "seg_0000.ts");
        var file = Assert.IsType<PhysicalFileResult>(served);
        Assert.Equal("video/mp2t", file.ContentType);

        // A different start position is a DIFFERENT directory: not found
        ActionResult wrongStart = await authorized.GetEpisodeAudioSegment(itemId, 0, "seg_0000.ts");
        Assert.IsType<NotFoundObjectResult>(wrongStart);

        // No token: rejected
        var anonymous = CreateController();
        ActionResult rejected = await anonymous.GetEpisodeAudioSegment(itemId, startTicks, "seg_0000.ts");
        Assert.IsType<UnauthorizedObjectResult>(rejected);
    }

    /// <summary>
    /// Verify that the audio-segments endpoint returns 400 when itemId is not a valid GUID.
    /// </summary>
    [Fact]
    public async Task GetEpisodeAudioSegment_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.GetEpisodeAudioSegment("not-a-guid", 0, "seg_0000.ts");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// JF-636 atempo arguments: the CONTENT-relative input seek before -i, audio-only
    /// mapping, atempo at perMille/1000, the ALWAYS-AAC encode (a filter forbids
    /// stream copy), 10-second MPEG-TS segments, and the base URL pointing at the
    /// dedicated speed-segments route with rate and start position in the path.
    /// </summary>
    [Fact]
    public void BuildAudioSpeedHlsFfmpegArguments_SeekAndAtempo_ShapeTheEncode()
    {
        string sourceUrl = "http://localhost:8096/Audio/abc/stream?static=true";
        long startTicks = TimeSpan.FromMinutes(20).Ticks;

        List<string> args = VideoAudioController.BuildAudioSpeedHlsFfmpegArguments(
            sourceUrl, startTicks, 1500, "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts",
            "/alexaskill/api/audio-speed/abc/1500/segments/12000000000/");

        // Content-relative input seek BEFORE -i
        int ssIdx = args.IndexOf("-ss");
        Assert.True(ssIdx >= 0, "expected -ss for a start-shifted speed encode");
        Assert.Equal(
            (startTicks / (double)TimeSpan.TicksPerSecond).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            args[ssIdx + 1]);
        Assert.Equal("-i", args[ssIdx + 2]);
        Assert.Equal(sourceUrl, args[ssIdx + 3]);

        // ONE map: audio only.
        Assert.Equal("0:a:0", args[args.IndexOf("-map") + 1]);
        Assert.Equal(1, args.Count(a => a == "-map"));

        // atempo at the per-mille rate, then AAC 192k stereo: atempo is a filter,
        // so copy is impossible regardless of the source codec.
        Assert.Equal("atempo=1.5", args[args.IndexOf("-af") + 1]);
        Assert.Equal("aac", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("2", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("192k", args[args.IndexOf("-b:a") + 1]);
        Assert.DoesNotContain("copy", args);

        // 10-second MPEG-TS segments, event growth, the speed-segments base URL.
        Assert.Equal("10", args[args.IndexOf("-hls_time") + 1]);
        Assert.Equal("append_list", args[args.IndexOf("-hls_flags") + 1]);
        Assert.Equal("mpegts", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Equal("/alexaskill/api/audio-speed/abc/1500/segments/12000000000/", args[args.IndexOf("-hls_base_url") + 1]);
        Assert.Equal("/tmp/hls/stream.m3u8", args[^1]);
    }

    /// <summary>
    /// The six quarter steps all fit ONE atempo instance (range 0.5-2.0): no
    /// chaining, no locale decimal separators in the filter value.
    /// </summary>
    [Theory]
    [InlineData(750, "atempo=0.75")]
    [InlineData(1000, "atempo=1")]
    [InlineData(1250, "atempo=1.25")]
    [InlineData(1500, "atempo=1.5")]
    [InlineData(1750, "atempo=1.75")]
    [InlineData(2000, "atempo=2")]
    public void BuildAudioSpeedHlsFfmpegArguments_EveryServedRate_SingleAtempoInstance(int ratePerMille, string expectedFilter)
    {
        List<string> args = VideoAudioController.BuildAudioSpeedHlsFfmpegArguments(
            "http://localhost:8096/Audio/abc/stream?static=true", 0, ratePerMille,
            "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/base/");

        Assert.Equal(expectedFilter, args[args.IndexOf("-af") + 1]);
        Assert.Equal(1, args.Count(a => a.StartsWith("atempo=", StringComparison.Ordinal)));
    }

    /// <summary>A from-zero speed encode carries no -ss (the fresh-launch shape).</summary>
    [Fact]
    public void BuildAudioSpeedHlsFfmpegArguments_FromZero_HasNoSeek()
    {
        List<string> args = VideoAudioController.BuildAudioSpeedHlsFfmpegArguments(
            "http://localhost:8096/Audio/abc/stream?static=true", 0, 2000,
            "/tmp/hls/stream.m3u8", "/tmp/hls/seg_%04d.ts", "/base/");

        Assert.DoesNotContain("-ss", args);
        Assert.Equal("-i", args[0]);
    }

    /// <summary>
    /// The variant cache key is distinct per rate AND start position and from every
    /// other variant of the same item (the bare remux key and the audio-only twin).
    /// </summary>
    [Fact]
    public void AudioSpeedCacheKey_DistinctPerRateAndStart_AndFromSiblingVariants()
    {
        string id = Guid.NewGuid().ToString();
        long startTicks = TimeSpan.FromMinutes(5).Ticks;

        Assert.Equal($"{id}-speed-1500", VideoAudioController.AudioSpeedCacheKey(id, 1500, 0));
        Assert.Equal($"{id}-speed-1500-{startTicks}", VideoAudioController.AudioSpeedCacheKey(id, 1500, startTicks));
        Assert.NotEqual(
            VideoAudioController.AudioSpeedCacheKey(id, 1500, startTicks),
            VideoAudioController.AudioSpeedCacheKey(id, 1750, startTicks));
        Assert.NotEqual(
            VideoAudioController.AudioSpeedCacheKey(id, 1500, startTicks),
            VideoAudioController.EpisodeAudioCacheKey(id, startTicks));
        Assert.NotEqual(id, VideoAudioController.AudioSpeedCacheKey(id, 1500, 0));
    }

    /// <summary>A bare-GUID speed playlist request with no token must be rejected (401).</summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_NoToken_Returns401()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");

        var controller = CreateController();

        ActionResult result = await controller.StreamHlsAudioSpeed(Guid.NewGuid().ToString(), 1500);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    /// <summary>Invalid GUID: 400 before anything else; unserved rate: 400 after the token.</summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.StreamHlsAudioSpeed("not-a-guid", 1500);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>An unserved rate (off the six-step ladder) is rejected even with a valid token.</summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_UnservedRate_Returns400()
    {
        string itemId = Guid.NewGuid().ToString();
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");

        var controller = CreateController(itemId);

        ActionResult result = await controller.StreamHlsAudioSpeed(itemId, 1337);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    /// <summary>Serialize an ObjectResult's anonymous body for Contains assertions.</summary>
    private static string Body(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>
    /// JF-664 pin: a doubly-invalid request (non-GUID id AND unserved rate) gets
    /// the itemId 400 on BOTH audio-speed sibling routes, matching every other
    /// route. The playlist route used to answer "Unsupported playback rate"
    /// (its Core checked the rate before the id); the precedence is now decided:
    /// the id 400 wins everywhere.
    /// </summary>
    [Fact]
    public async Task AudioSpeedRoutes_DoublyInvalid_NonGuidIdPlusUnservedRate_BothReturnItemId400()
    {
        var controller = CreateController();

        ActionResult playlistResult = await controller.StreamHlsAudioSpeed("not-a-guid", 1337);
        var playlist400 = Assert.IsType<BadRequestObjectResult>(playlistResult);
        Assert.Contains("Invalid itemId format", Body(playlist400.Value!), StringComparison.Ordinal);

        ActionResult segmentResult = await controller.GetAudioSpeedSegment("not-a-guid", 1337, 0, "seg_0000.ts");
        var segment400 = Assert.IsType<BadRequestObjectResult>(segmentResult);
        Assert.Contains("Invalid itemId format", Body(segment400.Value!), StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-671 pin: the TRIPLY-invalid shape (GUID id + unserved rate + no token)
    /// is deliberately SPLIT between the siblings, and this pin ratifies it:
    /// the playlist route checks id -> token -> rate (the playlist fetch is where
    /// the client first presents its token, so auth precedes semantics there and
    /// the answer is the 401 token body), while the segment route checks
    /// id -> rate -> token (the rate is structural there: it names the variant
    /// directory the segment URL resolves into, so the rate 400 wins). A future
    /// alignment of either route to the other's order must flip THIS pin loudly,
    /// not silently.
    /// </summary>
    [Fact]
    public async Task AudioSpeedRoutes_TriplyInvalid_GuidPlusUnservedRatePlusNoToken_RatifiedSplit()
    {
        var controller = CreateController();
        string guid = Guid.NewGuid().ToString();

        ActionResult playlistResult = await controller.StreamHlsAudioSpeed(guid, 1337);
        var playlist401 = Assert.IsType<UnauthorizedObjectResult>(playlistResult);
        Assert.Contains("Invalid or expired stream token", Body(playlist401.Value!), StringComparison.Ordinal);

        ActionResult segmentResult = await controller.GetAudioSpeedSegment(guid, 1337, 0, "seg_0000.ts");
        var segment400 = Assert.IsType<BadRequestObjectResult>(segmentResult);
        Assert.Contains("Unsupported playback rate", Body(segment400.Value!), StringComparison.Ordinal);
    }

    /// <summary>
    /// Cache-miss flow of the speed variant: an AUDIO item's static /Audio/ stream is
    /// fed to ffmpeg with atempo + AAC, the encode lands in the variant directory,
    /// and the partial playlist is served with the token injected into the
    /// speed-segments URLs.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_CacheMiss_EncodesAtempoIntoVariantDirectory()
    {
        var episode = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "IlPost episode",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        long startTicks = TimeSpan.FromMinutes(10).Ticks;
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-audio-speed",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "printf '%s\\n' \"$@\" > \"$dir/audio-speed-args.txt\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "exit 0\n");

        var controller = CreateController(episode.Id.ToString());
        controller.FfmpegPath = fakeFfmpegPath;

        ActionResult result = await controller.StreamHlsAudioSpeed(episode.Id.ToString(), 1500, startTicks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);

        // The encode ran in the VARIANT directory with the atempo arguments and the
        // audio-kind source URL.
        string hlsDir = _cache.GetHlsDirectoryPath(VideoAudioController.AudioSpeedCacheKey(episode.Id.ToString(), 1500, startTicks), 0);
        string recordedArgs = File.ReadAllText(Path.Combine(hlsDir, "audio-speed-args.txt"));

        Assert.Contains($"/Audio/{episode.Id}/stream?static=true", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("-ss", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("atempo=1.5", recordedArgs, StringComparison.Ordinal);
        Assert.Contains("0:a:0", recordedArgs, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:v", recordedArgs, StringComparison.Ordinal);
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/1500/segments/{startTicks}/", recordedArgs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The speed-segments route resolves the VARIANT directory (rate + start in the
    /// path), serves its segments, and rejects a missing token.
    /// </summary>
    [Fact]
    public async Task GetAudioSpeedSegment_ServesVariantDirectory_AndRejectsMissingToken()
    {
        string itemId = Guid.NewGuid().ToString();
        long startTicks = TimeSpan.FromMinutes(10).Ticks;

        string hlsDir = _cache.GetHlsDirectoryPath(VideoAudioController.AudioSpeedCacheKey(itemId, 1500, startTicks), 0);
        Directory.CreateDirectory(hlsDir);
        string segPath = Path.Combine(hlsDir, "seg_0000.ts");
        File.WriteAllText(segPath, "segment-bytes");

        var authorized = CreateController(itemId);
        ActionResult served = await authorized.GetAudioSpeedSegment(itemId, 1500, startTicks, "seg_0000.ts");
        var file = Assert.IsType<PhysicalFileResult>(served);
        Assert.Equal("video/mp2t", file.ContentType);

        // A different rate is a DIFFERENT directory: not found
        ActionResult wrongRate = await authorized.GetAudioSpeedSegment(itemId, 1750, startTicks, "seg_0000.ts");
        Assert.IsType<NotFoundObjectResult>(wrongRate);

        // No token: rejected
        var anonymous = CreateController();
        ActionResult rejected = await anonymous.GetAudioSpeedSegment(itemId, 1500, startTicks, "seg_0000.ts");
        Assert.IsType<UnauthorizedObjectResult>(rejected);
    }

    /// <summary>
    /// JF-636 efficiency review: a speed change mints a NEW (rate, start) encode
    /// while the previous variant's ffmpeg is still running; the new encode must
    /// KILL the superseded one minted by the SAME device (it can never be played
    /// again by that launch), so quick speed cycling cannot strand abandoned
    /// ~49x-realtime encodes on the shared encode gate. A different device's
    /// variant of the same item must SURVIVE (tokens are item-scoped, queues
    /// per-device). The fake ffmpeg records its PID, writes the first segment,
    /// then sleeps.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_NewLaunch_KillsSameDeviceSupersededEncode_SparesOtherDevices()
    {
        var episode = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Cycling episode",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(60).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-audio-speed-sleeper",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "echo $$ > \"$dir/ffmpeg.pid\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "sleep 300\n");

        // JF-730: the launches and asserts sit inside try/finally because the
        // scenario deliberately leaves live sleep-300 encodes (device B's spared
        // 1.75x and device A's final 2.0x), each holding an encode-gate slot
        // (default cap 2), and the encode gate releases a slot only on the
        // process's own exit. A failing assert must not strand them: without the
        // finally's kills the whole serialized class waited out the fake's sleep
        // behind the full gate (the next gated test, measured on the pre-JF-730
        // suite, blocked 299.42s on gate.WaitAsync, 84% of the class's wall
        // clock, until the first sleeper's sleep expired).
        // Since JF-731 the finally is the KILL half only (defense-in-depth with
        // the test's own scenario knowledge): the gate DRAIN and its assert are
        // owned once for the whole class by the Dispose-level backstop
        // (KillLeftoverEncodesAndDrainGate), which re-kills anything the
        // scenario missed and reds this test if anything had to be killed.
        // Every launch and the kill loop read the SAME rate list, so a rate
        // added to the scenario cannot strand its encode behind the gate.
        int[] speedRates = { 1500, 1750, 2000 };
        try
        {
            // Device A starts the 1.5x variant.
            var first = CreateController(episode.Id.ToString(), "device-A", fakeFfmpegPath);
            ActionResult firstResult = await LaunchWithinAsync(
                () => first.StreamHlsAudioSpeed(episode.Id.ToString(), speedRates[0], 0),
                "device A's initial");
            Assert.IsType<ContentResult>(firstResult);

            string hlsDir = _cache.GetHlsDirectoryPath(VideoAudioController.AudioSpeedCacheKey(episode.Id.ToString(), speedRates[0], 0), 0);
            string pidPath = Path.Combine(hlsDir, "ffmpeg.pid");
            Assert.True(File.Exists(pidPath), "the fake ffmpeg never ran");
            int pid = int.Parse(File.ReadAllText(pidPath).Trim());

            // Device B launches a different variant of the SAME item: A's encode must
            // survive (a different Echo may be actively consuming it).
            var other = CreateController(episode.Id.ToString(), "device-B", fakeFfmpegPath);
            ActionResult otherResult = await LaunchWithinAsync(
                () => other.StreamHlsAudioSpeed(episode.Id.ToString(), speedRates[1], 0),
                "device B's spared-variant");
            Assert.IsType<ContentResult>(otherResult);
            Assert.False(ProcessDead(pid), "another device's launch must not kill device A's live variant");

            // Device A cycles to a new rate: its own 1.5x encode is superseded and dies.
            var second = CreateController(episode.Id.ToString(), "device-A", fakeFfmpegPath);
            ActionResult secondResult = await LaunchWithinAsync(
                () => second.StreamHlsAudioSpeed(episode.Id.ToString(), speedRates[2], 0),
                "device A's superseding");
            Assert.IsType<ContentResult>(secondResult);

            bool dead = false;
            for (int i = 0; i < 50; i++)
            {
                if (ProcessDead(pid))
                {
                    dead = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.True(dead, $"the superseded speed encode (pid {pid}) survived the new launch");
        }
        finally
        {
            // The kill half (JF-731 reduced this teardown from its own drain
            // loop + assert to the scenario's kills; the Dispose-level backstop
            // owns the re-arming drain and its assert for the whole class).
            foreach (int rate in speedRates)
            {
                string cacheKey = VideoAudioController.AudioSpeedCacheKey(episode.Id.ToString(), rate, 0);
                KillLiveEncode(cacheKey);
                KillEncodeByPidFile(_cache.GetHlsDirectoryPath(cacheKey, 0));
            }

            await FenceTempDirEncodesDeadAsync();
        }
    }

    /// <summary>
    /// The ONE kill-through-the-registry idiom (the JF-731 Dispose backstop, the
    /// JF-730 speed-cycling kill-half, and the JF-668 finally are its callers):
    /// a live speed encode holds an encode-gate slot until its process exits,
    /// so a test that leaves one behind serializes every later gated test
    /// behind its run (the 299.42s class stall JF-730 removed).
    /// </summary>
    /// <returns>Whether a live process was targeted (the JF-731 backstop's
    /// leak-detection signal); false for an absent or already-exited entry.</returns>
    private static bool KillLiveEncode(string cacheKey)
    {
        try
        {
            var live = VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey);
            if (live is { HasExited: false })
            {
                live.Kill(entireProcessTree: true);
                return true;
            }
        }
        catch { /* raced to exit between the guard and the kill */ }

        return false;
    }

    /// <summary>
    /// The ONE pid-file read (JF-731 review round): the pid of the variant
    /// directory's fake-ffmpeg pid file, when the file exists, parses, and
    /// names a LIVE process; null otherwise (absent, unparsable, already dead,
    /// or the directory deleted between the exists check and the read: the
    /// fire-and-forget monitors' debris cleanup races exactly there, and an
    /// unguarded throw would escape the kill-half finallys and mask a body
    /// failure). The liveness guard carries the recycled-pid caveat of
    /// <see cref="KillEncodeByPidFile"/> unchanged.
    /// </summary>
    private static int? TryReadLivePidFromVariantDir(string variantDir)
    {
        try
        {
            string pidPath = Path.Combine(variantDir, "ffmpeg.pid");
            return File.Exists(pidPath)
                && int.TryParse(File.ReadAllText(pidPath).Trim(), out int encodePid)
                && !ProcessDead(encodePid)
                ? encodePid
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pid-file corner backstop (JF-730 review round): an encode that
    /// faults between process start and its registration never enters the
    /// registry, so only its pid file names it; the fake writes the pid
    /// first, so the file covers even that window. The kill is guarded by
    /// liveness only, so a RECYCLED pid would name an unrelated process: a
    /// theoretical hazard on this host class (64-bit pid_max makes a wrap
    /// within one test run effectively impossible), accepted because a
    /// shebang script has no stable owner handle to check (comm reads the
    /// interpreter, not the script).
    /// </summary>
    private static bool KillEncodeByPidFile(string variantDir)
    {
        int? encodePid = TryReadLivePidFromVariantDir(variantDir);
        if (encodePid is null)
        {
            return false;
        }

        try
        {
            using var encode = System.Diagnostics.Process.GetProcessById(encodePid.Value);
            encode.Kill(entireProcessTree: true);
            return true;
        }
        catch { /* raced to exit between the probe and the kill */ }

        return false;
    }

    /// <summary>
    /// Per-launch tripwire (JF-730 gate round): a healthy speed launch
    /// returns after its first-segment wait, bounded by the endpoint's own
    /// ~20s ceiling; a supersede-kill ordering regression that moves the kill
    /// AFTER the gate acquisition instead queues the launch behind the full
    /// gate for a whole encode lifetime and would otherwise pass silently
    /// ~300s later. The 30s budget sits above that ceiling and an order of
    /// magnitude below one sleep-300 lifetime, so it reds the disease without
    /// flaking on a slow host.
    /// </summary>
    private static async Task<ActionResult> LaunchWithinAsync(Func<Task<ActionResult>> launch, string label)
    {
        Task<ActionResult> request = launch();
        if (!await CompletedWithinAsync(request, TimeSpan.FromSeconds(30)))
        {
            Assert.Fail($"{label} launch did not complete within 30s: is it queued behind the encode gate (a supersede-kill ordering regression moving the kill after the gate acquisition)?");
        }

        return await request;
    }

    /// <summary>
    /// The <see cref="StreamHlsAudioSpeed_NewLaunch_KillsSameDeviceSupersededEncode_SparesOtherDevices"/>
    /// pid probe (kept next to its only caller).
    /// </summary>
    private static bool ProcessDead(int p)
    {
        try
        {
            using var probe = System.Diagnostics.Process.GetProcessById(p);
            return probe.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// JF-647: the speed-encode exit watcher must remove ONLY the registry entry
    /// it registered. When the OLD encode exits and a NEW same-key encode
    /// registers before the old watcher's 1s poll notices (a re-request after
    /// the variant's cache directory was evicted mid-encode), the old watcher's
    /// key-only TryRemove deleted the LIVE entry, so the supersede kill later
    /// degraded to its conservative no-kill and the abandoned ffmpeg held an
    /// encode-gate slot to completion. Pinned directly on the registration seam:
    /// old process exits, new process registers under the same key, the old
    /// watcher wakes and must leave the new entry alone; once the new process
    /// also exits, its OWN watcher must still clear the key.
    /// </summary>
    [Fact]
    public async Task RegisterLiveSpeedEncode_OldWatcherWake_DoesNotDeleteReRegisteredLiveEntry()
    {
        var controller = CreateController();
        string cacheKey = VideoAudioController.AudioSpeedCacheKey(Guid.NewGuid().ToString(), 1500, 0);

        using var oldProcess = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 30\""))!;
        using var newProcess = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 30\""))!;
        try
        {
            controller.RegisterLiveSpeedEncode(oldProcess, cacheKey, "device-A");

            // The OLD encode exits while its watcher sleeps out the 1s poll.
            oldProcess.Kill();
            oldProcess.WaitForExit();

            // The NEW same-key encode registers inside that window: the registry
            // entry now names the live new process.
            controller.RegisterLiveSpeedEncode(newProcess, cacheKey, "device-A");
            Assert.Same(newProcess, VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey));

            // Past the old watcher's first wake after the exit (and, at the second
            // assert, past a slow-scheduled second wake): unfixed, the stale
            // watcher would have deleted the live entry by then.
            await Task.Delay(1300);
            Assert.Same(newProcess, VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey));
            await Task.Delay(1300);
            Assert.Same(newProcess, VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey));

            // The OWN watcher still clears the key once its process exits.
            newProcess.Kill();
            newProcess.WaitForExit();
            Assert.True(
                await WaitUntilAsync(() => VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey) == null, TimeSpan.FromSeconds(5), 100),
                "the entry must still be removed once its own process exits");
        }
        finally
        {
            try { if (!oldProcess.HasExited) { oldProcess.Kill(); } } catch { /* already exited */ }
            try { if (!newProcess.HasExited) { newProcess.Kill(); } } catch { /* already exited */ }
        }
    }

    /// <summary>
    /// JF-665: a same-key re-register while the prior encode is STILL LIVE (the
    /// variant's cache directory evicted mid-encode, a re-request restarting it)
    /// displaces the prior registry entry; after the JF-647 compare-and-remove
    /// the prior watcher no longer cleans it up, so the registration itself must
    /// KILL the displaced encode instead of orphaning it to run to completion
    /// holding an encode-gate slot (the gate releases only on the process's own
    /// exit). Pinned directly on the registration seam, like the JF-647 pin: the
    /// endpoint cannot produce the interleave without racing a live encode's
    /// directory.
    /// </summary>
    [Fact]
    public async Task RegisterLiveSpeedEncode_SameKeyReRegister_KillsDisplacedLiveEncode()
    {
        var controller = CreateController();
        string cacheKey = VideoAudioController.AudioSpeedCacheKey(Guid.NewGuid().ToString(), 1500, 0);

        using var oldProcess = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 30\""))!;
        using var newProcess = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 30\""))!;
        try
        {
            controller.RegisterLiveSpeedEncode(oldProcess, cacheKey, "device-A");

            // The prior encode is still running when the same key re-registers:
            // the registry must name the new process AND kill the displaced one.
            controller.RegisterLiveSpeedEncode(newProcess, cacheKey, "device-A");
            Assert.Same(newProcess, VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey));

            Assert.True(
                await WaitUntilAsync(() => oldProcess.HasExited, TimeSpan.FromSeconds(5), 100),
                "the displaced live encode must be killed at the overwrite, not orphaned onto the encode gate");

            // The surviving entry is the new generation's, and its own watcher
            // still owns the cleanup once it exits.
            newProcess.Kill();
            newProcess.WaitForExit();
            Assert.True(
                await WaitUntilAsync(() => VideoAudioController.LiveSpeedEncodeProcessForTest(cacheKey) == null, TimeSpan.FromSeconds(5), 100),
                "the new generation's own watcher must still clear the key");
        }
        finally
        {
            try { if (!oldProcess.HasExited) { oldProcess.Kill(); } } catch { /* already exited */ }
            try { if (!newProcess.HasExited) { newProcess.Kill(); } } catch { /* already exited */ }
        }
    }

    /// <summary>
    /// JF-668 (the earlier displacement kill): a same-key re-encode must kill
    /// the displaced prior ffmpeg at the pre-start position inside the per-key
    /// lock, BEFORE the new ffmpeg starts. The JF-665 registration-time kill
    /// fires only after the new encode's first-segment wait succeeds, and in
    /// that window the abandoned prior ffmpeg keeps writing BY PATH into the
    /// recreated directory (both processes can truncate each other's
    /// seg_NNNN.ts; the new encode's liveness proof can even be satisfied by
    /// the prior encode's seg_0000.ts). Driven through the REAL endpoint with
    /// the prior encode planted on the registration seam (the endpoint cannot
    /// produce the interleave without racing a live encode's directory): the
    /// fake ffmpeg probes the prior process's /proc state at its start and
    /// writes prior-state.txt BEFORE the first segment, so the file reads dead
    /// iff the kill landed within the probe's 2s tolerance (the 40x0.05s loop
    /// accepts SIGKILL delivery latency under loaded runners; a strict single
    /// read would trade the deterministic red for a scheduler-dependent CI
    /// false-red; state Z counts as dead because a
    /// killed-but-unreaped process stays a zombie, and a missing /proc entry
    /// counts as dead too, all via shell builtins so no tool absence can fake
    /// a dead read). A kill that only follows the first-segment wait reads
    /// alive deterministically: the snapshot precedes the segment write the
    /// wait polls for, which is the red shape (verified: with the earlier kill
    /// commented out the pin fails with prior-state=alive on both TFMs).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_SameKeyReEncode_KillsDisplacedEncodeBeforeNewFfmpegStarts()
    {
        var episode = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-668 earlier kill episode",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(60).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        // The displaced prior encode: a real sh sleeper planted under the exact
        // cache key this endpoint is about to re-encode.
        string cacheKey = VideoAudioController.AudioSpeedCacheKey(episode.Id.ToString(), 1500, 0);
        using var prior = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 300\""))!;

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf668-earlier-kill",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "state=alive\n" +
            "i=0\n" +
            "while [ $i -lt 40 ]; do\n" +
            "  pstate=S\n" +
            $"  if [ -r /proc/{prior.Id}/stat ]; then read -r _p _c pstate _r < /proc/{prior.Id}/stat; fi\n" +
            $"  if [ ! -e /proc/{prior.Id}/stat ] || [ \"$pstate\" = \"Z\" ]; then state=dead; break; fi\n" +
            "  sleep 0.05\n" +
            "  i=$((i+1))\n" +
            "done\n" +
            "echo $state > \"$dir/prior-state.txt\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "sleep 300\n");

        var controller = CreateController(episode.Id.ToString(), "device-A", fakeFfmpegPath);
        try
        {
            controller.RegisterLiveSpeedEncode(prior, cacheKey, "device-A");

            ActionResult result = await controller.StreamHlsAudioSpeed(episode.Id.ToString(), 1500, 0);
            Assert.IsType<ContentResult>(result);

            string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
            string priorState = File.ReadAllText(Path.Combine(hlsDir, "prior-state.txt")).Trim();
            Assert.True(
                string.Equals(priorState, "dead", StringComparison.Ordinal),
                $"the displaced prior encode was still {priorState} when the new ffmpeg started: the earlier displacement kill did not fire before the encode");
        }
        finally
        {
            try { if (!prior.HasExited) { prior.Kill(entireProcessTree: true); } } catch { /* already exited */ }
            KillLiveEncode(cacheKey);

            // Fence the kills to OBSERVED death (JF-731 review round; prior is
            // this test's own handle, WaitForExit bounds it, the shared fence
            // bounds the endpoint-launched encode).
            prior.WaitForExit(5000);
            await FenceTempDirEncodesDeadAsync();
        }
    }

    /// <summary>
    /// JF-665: the monitor's encode-flag clear is generation-aware. A prior
    /// generation's monitor that fires late (its encode hung; the stall budget
    /// kills it while a NEWER same-key encode has already re-registered after
    /// the eviction) must NOT clear the newer generation's flag: the old
    /// key-only clear turned the near-ahead segment hold off mid-encode (device
    /// seeks 404ed). Gen 1 runs the REAL endpoint (hung fake ffmpeg, shrunk
    /// stall budget so its monitor's finally lands within seconds); gen 2's
    /// registration is simulated through the test seam the moment the endpoint
    /// returns, well inside gen 1's stall budget.
    /// </summary>
    [Fact]
    public async Task MonitorHls_LatePriorGenerationClear_KeepsNewerEncodeFlagSet()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-665 Late Clear S01E01", "h264", TimeSpan.FromMinutes(45));

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf665-hung",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "sleep 30\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, fakeFfmpegPath);
        controller.HlsMonitorStallBudgetOverride = TimeSpan.FromSeconds(5);

        string itemIdStr = episode.Id.ToString();
        try
        {
            ActionResult result = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(result);
            Assert.True(VideoAudioController.EncodeActiveForTest(itemIdStr), "the endpoint's registration set the flag");

            // The newer same-key generation registers while gen 1's monitor is
            // still pending (the mid-encode eviction + re-request race): it takes
            // over the flag's generation. The 5s budget leaves a wide margin for
            // a descheduled test thread, and the premise assert below turns a
            // budget overrun into a loud failure instead of a vacuous pass.
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
            Assert.False(
                TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("HLS encoding STALLED", StringComparison.Ordinal)),
                "gen 2 must register before gen 1's monitor stalls (raise the stall budget if this fires)");

            // Gen 1's monitor stalls out and runs its finally (the clear attempt).
            Assert.True(
                await WaitUntilAsync(
                    () => TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("HLS encoding STALLED", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(20)),
                "gen 1's monitor must reach its stall clear");

            // The late prior-generation clear must not drop the newer encode's
            // flag: it has to stay set through a settle window that comfortably
            // spans the finally's execution after the stall log.
            Assert.False(
                await WaitUntilAsync(() => !VideoAudioController.EncodeActiveForTest(itemIdStr), TimeSpan.FromSeconds(2), 50),
                "the prior generation's late clear dropped the newer encode's flag");
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// JF-665 companion: the generation-aware clear must still FIRE for the
    /// generation that owns the flag (the healthy path): once this encode's own
    /// monitor finishes, the flag is gone, so future requests re-encode instead
    /// of being told an encode is active forever.
    /// </summary>
    [Fact]
    public async Task MonitorHls_OwnGenerationExit_StillClearsEncodeFlag()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-665 Own Clear S01E01", "h264", TimeSpan.FromMinutes(45));

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf665-done",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "sleep 2\n" +
            "exit 0\n");

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());
        Assert.IsType<ContentResult>(result);
        Assert.True(VideoAudioController.EncodeActiveForTest(episode.Id.ToString()), "the registration set the flag");

        Assert.True(
            await WaitUntilAsync(() => !VideoAudioController.EncodeActiveForTest(episode.Id.ToString()), TimeSpan.FromSeconds(10), 100),
            "the owning generation's own exit must still clear the flag");
    }

    /// <summary>
    /// JF-669: the active-encode flag must be art-tick aware, so a NEWER-ticks
    /// encode finishing first cannot orphan an OLDER-ticks encode's liveness.
    /// Exposure: the flag registry keys by the bare cache key while the locks
    /// and cache directories key by (key, artModifiedTicks); an art change
    /// mid-encode lets the gen-B encode displace gen-A's flag entry, gen-B's
    /// monitor then compare-and-removes ITS (correct) generation and - pre
    /// fix - the whole entry drops while gen A still writes, so a tick-stale
    /// request (art ticks read before the change) sees flag-absent plus a
    /// no-ENDLIST playlist in dir_A and ValidateEpisodeCacheAsync declares the
    /// LIVE directory interrupted-encode debris (Cleanup wipes every {id}_*
    /// directory). Driven through the REAL episode endpoint twice with the
    /// item's primary-art DateModified mutated between the calls (that is what
    /// GetArtModifiedTicks reads): gen A parks on a stop file, gen B parks on
    /// its own stop file so both generations are structurally live together,
    /// then B exits 0 with ENDLIST so B's monitor clear lands while A still
    /// runs; call 3 re-reads art ticks A (the tick-stale request). The fake
    /// script branches on the art-tick suffix of its output directory, so one
    /// script serves all three calls. The debris assertions come FIRST so the
    /// red run (all generations pinned into one slot, the pre-fix conflation)
    /// fails on exactly "the debris cleanup fired", not on the premise; the
    /// both-live premise is observed with a non-fatal wait for the same
    /// reason. The verdict's log line is the primary discriminator (a
    /// directory-content probe alone is vacuous: the re-encode's fake rewrites
    /// the same files); the surviving gen-B ENDLIST playlist is the secondary,
    /// file-level evidence (Cleanup deletes every {id}_* directory and nothing
    /// recreates dir_B).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_NewerArtTickGenerationClearsFirst_OlderTicksLiveDirNotDebris()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-669 Cross Tick S01E01", "h264", TimeSpan.FromMinutes(45));

        // Primary art with a controllable DateModified: this is the cache-key
        // component the endpoint (and only the cache/lock layer) keys on.
        var artA = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long ticksA = artA.Ticks;
        long ticksB = artA.AddHours(6).Ticks;
        var artImage = new MediaBrowser.Controller.Entities.ItemImageInfo
        {
            Path = "/tmp/jf669-art.jpg",
            Type = ImageType.Primary,
            DateModified = artA
        };
        episode.ImageInfos = new[] { artImage };

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf669-cross-tick",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "ticks=${dir##*_}\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            $"if [ \"$ticks\" = \"{ticksB}\" ]; then\n" +
            "  while [ ! -f \"$dir/stop\" ]; do sleep 0.1; done\n" +
            "  printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "  exit 0\n" +
            "fi\n" +
            "while [ ! -f \"$dir/stop\" ]; do sleep 0.2; done\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, fakeFfmpegPath);

        string itemIdStr = episode.Id.ToString();
        string dirA = _cache.GetHlsDirectoryPath(itemIdStr, ticksA);
        string dirB = _cache.GetHlsDirectoryPath(itemIdStr, ticksB);
        try
        {
            // Gen A: encodes under ticks A, then parks (stop file not written).
            ActionResult first = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(first);
            Assert.True(VideoAudioController.EncodeActiveForTest(itemIdStr), "the gen-A registration set the flag");

            // Art changed mid-encode: gen B computes DIFFERENT ticks (a
            // different lock and directory), registers its own generation.
            artImage.DateModified = artA.AddHours(6);
            ActionResult second = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(second);

            // Both generations live together (B parks on its own stop file):
            // observed, not asserted, so the red run still reaches the debris
            // assertions below instead of failing at this premise.
            await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr) == 2, TimeSpan.FromSeconds(5), 50);

            // Gen B finishes FIRST: its monitor's generation-aware clear fires
            // while gen A still writes. The completion log is B's monitor's (A's
            // is parked on its stop file), and the settle spans the finally's
            // clear that follows it.
            File.WriteAllText(Path.Combine(dirB, "stop"), string.Empty);
            Assert.True(
                await WaitUntilAsync(
                    () => TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("Episode HLS encoding complete", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(20)),
                "gen B's monitor must reach its completion log");
            await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr) <= 1, TimeSpan.FromSeconds(5), 50);
            await Task.Delay(500);

            // The tick-stale request: art ticks A read before the change. It
            // must NOT fire the interrupted-debris verdict on dir_A.
            artImage.DateModified = artA;
            ActionResult third = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(third);

            Assert.False(
                TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("Episode HLS cache invalidated", StringComparison.Ordinal)),
                "the interrupted-debris verdict fired for a key whose older-ticks generation is still live (JF-669)");
            Assert.True(
                File.Exists(Path.Combine(dirB, "stream.m3u8")),
                "the debris verdict's Cleanup wiped every directory of the key, including gen B's completed encode, while gen A still writes");

            // Premise (green world only; the red run already failed above):
            // exactly gen A's generation remains after gen B's clear.
            Assert.Equal(1, VideoAudioController.EncodeGenerationCountForTest(itemIdStr));
        }
        finally
        {
            artImage.DateModified = artA;
            await ReleaseParkedEncodeFixturesAsync(itemIdStr, new[] { dirA, dirB });
        }
    }

    /// <summary>
    /// JF-675: the prewrite serve gates ask the CALLER'S OWN art-tick slot
    /// (OwnTicksGenerationLive), not bare any-generation presence. Exposure
    /// (the widened window JF-669's review filed): gen A (ticks A) live, gen B
    /// (ticks B) completes and writes ENDLIST; under bare presence, EVERY
    /// subsequent ticks-B playlist fetch serves dir_B's surviving no-ENDLIST
    /// pre-written listing for gen A's whole remaining duration, so ExoPlayer
    /// reaches the tail of completed content, finds no ENDLIST, and keeps
    /// live-edge polling for growth that never comes. Driven through the REAL
    /// episode endpoint with the JF-669 pin's construction (primary-art
    /// DateModified mutated between calls, one fake ffmpeg branching on the
    /// art-tick suffix of its output directory): gen A parks on a stop file,
    /// gen B parks on its own then completes with ENDLIST, and the ticks-B
    /// fetch after B's clear must receive the ENDLIST playlist while gen A
    /// still runs. A mid-encode fetch BEFORE B's completion pins the
    /// unchanged common case (own ticks live: the pre-write IS served); the
    /// ENDLIST assertion carries the prewrite-tail marker in its failure
    /// message so the red run (own-ticks check disabled, the JF-669 shape)
    /// fails showing the pre-write served.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_CompletedOwnTicksEncode_ServesEndlistWhileForeignTicksGenerationRuns()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-675 Own Ticks Serve S01E01", "h264", TimeSpan.FromMinutes(45));

        // Primary art with a controllable DateModified: this is the cache-key
        // component the endpoint (and only the cache/lock layer) keys on.
        var artA = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long ticksA = artA.Ticks;
        long ticksB = artA.AddHours(6).Ticks;
        var artImage = new MediaBrowser.Controller.Entities.ItemImageInfo
        {
            Path = "/tmp/jf675-art.jpg",
            Type = ImageType.Primary,
            DateModified = artA
        };
        episode.ImageInfos = new[] { artImage };

        // The ticks-A branch parks gen A on a stop file (still running at the
        // verdict fetch); the ticks-B branch parks on its OWN stop file (both
        // generations structurally live together), then rewrites the playlist
        // WITH ENDLIST and exits 0 so B's monitor clears only B's slot.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf675-own-ticks",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "ticks=${dir##*_}\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            $"if [ \"$ticks\" = \"{ticksB}\" ]; then\n" +
            "  while [ ! -f \"$dir/stop\" ]; do sleep 0.1; done\n" +
            "  printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "  exit 0\n" +
            "fi\n" +
            "while [ ! -f \"$dir/stop\" ]; do sleep 0.2; done\n" +
            "exit 0\n");

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, fakeFfmpegPath);

        string itemIdStr = episode.Id.ToString();
        string dirA = _cache.GetHlsDirectoryPath(itemIdStr, ticksA);
        string dirB = _cache.GetHlsDirectoryPath(itemIdStr, ticksB);
        try
        {
            // Gen A: encodes under ticks A, then parks (stop file not written).
            ActionResult first = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(first);
            Assert.True(VideoAudioController.EncodeActiveForTest(itemIdStr), "the gen-A registration set the flag");

            // Art changed mid-encode: gen B computes DIFFERENT ticks (a
            // different lock and directory) and registers its own generation.
            artImage.DateModified = artA.AddHours(6);
            ActionResult second = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(second);

            // Both generations live together (B parks on its own stop file):
            // observed, not asserted, so the red run still reaches the serve
            // assertions below instead of failing at this premise.
            await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr) == 2, TimeSpan.FromSeconds(5), 50);

            // Common case unchanged (same-ticks serve is byte-identical): while
            // the caller's OWN ticks-B generation is live, a ticks-B fetch
            // serves the pre-written full listing, with no ENDLIST and the tail
            // segment seg_0674 (45min / 4s) present.
            ActionResult midEncode = await controller.StreamHlsEpisode(itemIdStr);
            var midContent = Assert.IsType<ContentResult>(midEncode);
            Assert.Contains("seg_0674.ts", midContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", midContent.Content, StringComparison.Ordinal);

            // Gen B finishes FIRST: its monitor's generation-aware clear drops
            // ONLY the ticks-B slot; the entry survives on gen A (the count is
            // gate-serialized with the clear, so 1 means B's clear landed and
            // A's slot is still live).
            File.WriteAllText(Path.Combine(dirB, "stop"), string.Empty);
            Assert.True(
                await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr) == 1, TimeSpan.FromSeconds(20), 100),
                "gen B's monitor must clear its own slot while gen A still runs");
            await Task.Delay(500);

            // The JF-675 fetch: same ticks B (art already dated B). The own-ticks
            // generation is dead while a foreign-ticks generation holds the
            // entry: the serve gate must fall through to ffmpeg's ENDLIST
            // playlist, not dir_B's surviving no-ENDLIST pre-write.
            ActionResult third = await controller.StreamHlsEpisode(itemIdStr);
            var content = Assert.IsType<ContentResult>(third);
            Assert.True(
                content.Content.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal),
                $"a completed own-ticks encode must serve ffmpeg's ENDLIST playlist, not the surviving no-ENDLIST pre-write, while a foreign-ticks generation runs (JF-675); pre-write tail marker seg_0674 present: {content.Content.Contains("seg_0674", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_0674", content.Content, StringComparison.Ordinal);

            // Premise (green world only; the red run already failed above):
            // exactly gen A's generation holds the entry.
            Assert.Equal(1, VideoAudioController.EncodeGenerationCountForTest(itemIdStr));
        }
        finally
        {
            artImage.DateModified = artA;
            await ReleaseParkedEncodeFixturesAsync(itemIdStr, new[] { dirA, dirB });
        }
    }

    /// <summary>
    /// JF-675 song twin (gate-marker F3): the SONG-path prewrite serve gates
    /// (StreamHlsVideoAudioCore's fast path + in-lock double check) got the
    /// identical own-ticks refinement as the episode pin above, and twin
    /// copy-paste drift is this controller's historical failure mode (the
    /// JF-637 consolidation exists because of it), so the new behavior is
    /// pinned on BOTH paths. Same construction as the episode pin (real
    /// endpoint, primary-art DateModified mutated between calls, one fake
    /// ffmpeg branching on the art-tick suffix; 3-digit song segments and the
    /// song prewrite's seg_674 tail marker instead of the episode shapes): gen
    /// A (ticks A) parks on a stop file, gen B (ticks B) parks on its own then
    /// completes with ENDLIST, and the ticks-B fetch after B's monitor clear
    /// must receive ffmpeg's ENDLIST playlist, not dir_B's surviving no-ENDLIST
    /// pre-write, while gen A still runs. The mid-encode fetch before B's
    /// completion pins the unchanged same-ticks common case. TWIN
    /// INDEPENDENCE: the red proof toggles ONLY the song registry's gate back
    /// to bare presence, which must fail THIS pin while the episode pin stays
    /// green.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CompletedOwnTicksEncode_ServesEndlistWhileForeignTicksGenerationRuns()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString();
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-675 Own Ticks Song",
            Id = itemId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        // Primary art with a controllable DateModified (the cache-key component
        // the endpoint keys on; set on the item itself, no album fallback).
        var artA = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long ticksA = artA.Ticks;
        long ticksB = artA.AddHours(6).Ticks;
        var artImage = new MediaBrowser.Controller.Entities.ItemImageInfo
        {
            Path = "/tmp/jf675-song-art.jpg",
            Type = ImageType.Primary,
            DateModified = artA
        };
        audioItem.ImageInfos = new[] { artImage };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(audioItem);

        // The ticks-A branch parks gen A on a stop file (still running at the
        // verdict fetch); the ticks-B branch parks on its OWN stop file (both
        // generations structurally live together), then rewrites the playlist
        // WITH ENDLIST and exits 0 so B's monitor clears only B's slot. Song
        // segments are 3-digit (seg_000.ts).
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf675-song-own-ticks",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "ticks=${dir##*_}\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_000.ts\\n' > \"$last_arg\"\n" +
            $"if [ \"$ticks\" = \"{ticksB}\" ]; then\n" +
            "  while [ ! -f \"$dir/stop\" ]; do sleep 0.1; done\n" +
            "  printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_000.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "  exit 0\n" +
            "fi\n" +
            "while [ ! -f \"$dir/stop\" ]; do sleep 0.2; done\n" +
            "exit 0\n");

        var controller = CreateController(itemIdStr, ffmpegPath: fakeFfmpegPath);

        string dirA = _cache.GetHlsDirectoryPath(itemIdStr, ticksA);
        string dirB = _cache.GetHlsDirectoryPath(itemIdStr, ticksB);
        try
        {
            // Gen A: encodes under ticks A (45min runtime pre-writes the full
            // listing, above the 10min threshold), then parks.
            ActionResult first = await controller.StreamHlsVideoAudio(itemIdStr);
            Assert.IsType<ContentResult>(first);
            Assert.True(VideoAudioController.EncodeActiveForTest(itemIdStr, song: true), "the gen-A registration set the song flag");

            // Art changed mid-encode: gen B computes DIFFERENT ticks (a
            // different lock and directory) and registers its own generation.
            artImage.DateModified = artA.AddHours(6);
            ActionResult second = await controller.StreamHlsVideoAudio(itemIdStr);
            Assert.IsType<ContentResult>(second);

            // Both generations live together (B parks on its own stop file):
            // observed, not asserted, so the red run still reaches the serve
            // assertions below instead of failing at this premise.
            await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr, song: true) == 2, TimeSpan.FromSeconds(5), 50);

            // Common case unchanged (same-ticks serve is byte-identical): while
            // the caller's OWN ticks-B generation is live, a ticks-B fetch
            // serves the pre-written full listing, with no ENDLIST and the tail
            // segment seg_674 (45min / 4s) present.
            ActionResult midEncode = await controller.StreamHlsVideoAudio(itemIdStr);
            var midContent = Assert.IsType<ContentResult>(midEncode);
            Assert.Contains("seg_674.ts", midContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", midContent.Content, StringComparison.Ordinal);

            // Gen B finishes FIRST: its monitor's generation-aware clear drops
            // ONLY the ticks-B slot; the entry survives on gen A.
            File.WriteAllText(Path.Combine(dirB, "stop"), string.Empty);
            Assert.True(
                await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr, song: true) == 1, TimeSpan.FromSeconds(20), 100),
                "gen B's monitor must clear its own slot while gen A still runs");
            await Task.Delay(500);

            // The JF-675 fetch: same ticks B. The own-ticks generation is dead
            // while a foreign-ticks generation holds the entry: the SONG serve
            // gate must fall through to ffmpeg's ENDLIST playlist, not dir_B's
            // surviving no-ENDLIST pre-write.
            ActionResult third = await controller.StreamHlsVideoAudio(itemIdStr);
            var content = Assert.IsType<ContentResult>(third);
            Assert.True(
                content.Content.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal),
                $"a completed own-ticks encode must serve ffmpeg's ENDLIST playlist, not the surviving no-ENDLIST pre-write, while a foreign-ticks generation runs (JF-675 song gate); pre-write tail marker seg_674 present: {content.Content.Contains("seg_674", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_674", content.Content, StringComparison.Ordinal);

            // Premise (green world only; the red run already failed above):
            // exactly gen A's generation holds the entry.
            Assert.Equal(1, VideoAudioController.EncodeGenerationCountForTest(itemIdStr, song: true));
        }
        finally
        {
            artImage.DateModified = artA;
            await ReleaseParkedEncodeFixturesAsync(itemIdStr, new[] { dirA, dirB }, song: true);
        }
    }

    /// <summary>
    /// JF-676: the episode debris verdict is TICKS-SCOPED (it reads the
    /// CALLER'S OWN art-tick generation, and its cleanup deletes only the
    /// caller's own generation directory). Exposure (the residual the JF-675
    /// review filed): gen A (ticks A) parked live, gen B (ticks B) KILLED
    /// mid-encode (nonzero exit, monitor clear) leaving its partial no-ENDLIST
    /// playlist on disk; the JF-669 any-generation short-circuit then held the
    /// verdict back for gen A's whole remaining duration and the own-dead
    /// fall-through served the DEAD partial (ExoPlayer joins at the dead live
    /// edge and polls a playlist that never grows). Construction mirrors the
    /// JF-675 episode pin (real endpoint, primary-art DateModified mutated
    /// between calls, one fake ffmpeg branching on the art-tick suffix of its
    /// output directory): the ticks-B branch parks then exits NONZERO on the
    /// stop file (the killed shape; ffmpeg's partial playlist survives because
    /// the monitor's failure path only logs), and the ticks-B fetch after B's
    /// clear must NOT serve the dead partial: the verdict fires (its own
    /// generation is dead), cleans dir_B only, and the re-encode serves its
    /// fresh pre-written listing (tail marker seg_0674), with gen A's directory
    /// untouched throughout. Red proof: the verdict's skip toggled back to bare
    /// any-generation presence (the JF-675 shape) fails this pin showing the
    /// partial served.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_KilledOwnTicksEncode_StalePartialCleanedNotServedWhileForeignTicksGenerationRuns()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-676 Killed Own Ticks S01E01", "h264", TimeSpan.FromMinutes(45));

        // Primary art with a controllable DateModified: this is the cache-key
        // component the endpoint (and only the cache/lock layer) keys on.
        var artA = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long ticksA = artA.Ticks;
        long ticksB = artA.AddHours(6).Ticks;
        var artImage = new MediaBrowser.Controller.Entities.ItemImageInfo
        {
            Path = "/tmp/jf676-art.jpg",
            Type = ImageType.Primary,
            DateModified = artA
        };
        episode.ImageInfos = new[] { artImage };

        // The ticks-A branch parks gen A on a stop file (foreign generation
        // still live at the verdict fetch); the ticks-B branch parks on its OWN
        // stop file, then exits NONZERO when it appears: the KILLED-encode
        // shape, leaving the partial no-ENDLIST playlist (seg_0000 only) on
        // disk with the generation's flag cleared by the monitor's finally.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf676-killed-own-ticks",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "ticks=${dir##*_}\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            $"if [ \"$ticks\" = \"{ticksB}\" ]; then\n" +
            "  while [ ! -f \"$dir/stop\" ]; do sleep 0.1; done\n" +
            "  exit 1\n" +
            "fi\n" +
            "while [ ! -f \"$dir/stop\" ]; do sleep 0.2; done\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, fakeFfmpegPath);

        string itemIdStr = episode.Id.ToString();
        string dirA = _cache.GetHlsDirectoryPath(itemIdStr, ticksA);
        string dirB = _cache.GetHlsDirectoryPath(itemIdStr, ticksB);
        try
        {
            // Gen A: encodes under ticks A, then parks (stop file not written).
            ActionResult first = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(first);
            Assert.True(VideoAudioController.EncodeActiveForTest(itemIdStr), "the gen-A registration set the flag");

            // Art changed mid-encode: gen B computes DIFFERENT ticks (a
            // different lock and directory) and registers its own generation.
            artImage.DateModified = artA.AddHours(6);
            ActionResult second = await controller.StreamHlsEpisode(itemIdStr);
            Assert.IsType<ContentResult>(second);

            // Both generations live together (B parks on its own stop file):
            // observed, not asserted, so the red run still reaches the serve
            // assertions below instead of failing at this premise.
            await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr) == 2, TimeSpan.FromSeconds(5), 50);

            // Gen B is KILLED: the stop file makes its fake exit NONZERO, the
            // monitor logs the failure and its finally clears ONLY the ticks-B
            // slot; the partial no-ENDLIST playlist survives on disk.
            File.WriteAllText(Path.Combine(dirB, "stop"), string.Empty);
            Assert.True(
                await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemIdStr) == 1, TimeSpan.FromSeconds(20), 100),
                "gen B's monitor must clear its own killed slot while gen A still runs");
            await Task.Delay(500);

            // The JF-676 fetch: same ticks B. The own-ticks generation is dead
            // (killed) while a foreign-ticks generation holds the entry: the
            // ticks-scoped verdict must fire (clean dir_B's dead partial, never
            // dir_A) and the re-encode must serve its fresh pre-write, not the
            // stale partial.
            ActionResult third = await controller.StreamHlsEpisode(itemIdStr);
            var content = Assert.IsType<ContentResult>(third);
            Assert.True(
                content.Content.Contains("seg_0674.ts", StringComparison.Ordinal),
                $"a killed own-ticks encode's stale partial must be cleaned and re-encoded, not served, while a foreign-ticks generation runs (JF-676); dead-partial marker seg_0000.ts present: {content.Content.Contains("seg_0000.ts", StringComparison.Ordinal)}");
            Assert.True(
                TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("Episode HLS cache invalidated", StringComparison.Ordinal)),
                "the ticks-scoped debris verdict must fire for the killed own-ticks generation");
            Assert.True(
                Directory.Exists(dirA),
                "the verdict's cleanup must never touch the live foreign-ticks generation's directory");

            // Premise (green world only; the red run already failed above):
            // gen A plus the verdict-triggered ticks-B re-encode hold the entry.
            Assert.Equal(2, VideoAudioController.EncodeGenerationCountForTest(itemIdStr));
        }
        finally
        {
            artImage.DateModified = artA;
            await ReleaseParkedEncodeFixturesAsync(itemIdStr, new[] { dirA, dirB });
        }
    }

    /// <summary>
    /// JF-676: the audiobook debris verdict gains the liveness gate it lacked
    /// ENTIRELY plus ticks scoping. Exposure (the JF-675 review's stronger
    /// sibling finding): gen B live under ticks B while a request validating an
    /// OLDER ENDLIST-with-fewer-segments-than-chapters playlist at ticks A
    /// fired the KEY-wide <c>Cleanup(parentId)</c> unconditionally, wiping gen
    /// B's live directory mid-write. Construction (audiobook patterns: Folder
    /// parent with controllable primary-art DateModified, Audio chapters, fake
    /// ffmpeg parking on a stop file): a fixture undercounting ENDLIST playlist
    /// is planted at ticks A, gen B is started live under ticks B, and the
    /// ticks-A verdict fetch must fire (its own generation is dead) while gen
    /// B's directory SURVIVES: the cleanup is scoped to the caller's own ticks
    /// directory. The fetch itself then hits the pre-existing
    /// concurrent-encode guard (deliberately any-generation, JF-669/JF-675) and
    /// 503s until gen B exits, the honest one-retry-later degraded answer.
    /// Red proof: with the validator reverted to the ungated key-wide shape
    /// (the pre-JF-676 body) the pin fails showing dir_B wiped.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_UndercountVerdictIsTicksScoped_LiveForeignTicksGenerationDirectorySurvives()
    {
        Guid parentId = Guid.NewGuid();
        string parentIdStr = parentId.ToString();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-676 Undercount Book",
            Id = parentId
        };

        // Primary art with a controllable DateModified (the cache-key component
        // the concat path keys on; set on the parent folder itself).
        var artA = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long ticksA = artA.Ticks;
        long ticksB = artA.AddHours(6).Ticks;
        var artImage = new MediaBrowser.Controller.Entities.ItemImageInfo
        {
            Path = "/tmp/jf676-book-art.jpg",
            Type = ImageType.Primary,
            DateModified = artA
        };
        parentItem.ImageInfos = new[] { artImage };

        var chapters = new List<MediaBrowser.Controller.Entities.BaseItem>();
        for (int i = 1; i <= 3; i++)
        {
            chapters.Add(new MediaBrowser.Controller.Entities.Audio.Audio
            {
                Name = $"JF-676 Chapter {i}",
                Id = Guid.NewGuid(),
                Path = $"/book/jf676-{i:000}.mp3",
                RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
            });
        }

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(chapters);

        // The fixture the verdict judges: an ENDLIST playlist with FEWER
        // segments than the 3 chapters (the "older undercounting ENDLIST
        // playlist of the same key" of the filing).
        string dirA = _cache.GetHlsDirectoryPath(parentIdStr, ticksA);
        string dirB = _cache.GetHlsDirectoryPath(parentIdStr, ticksB);
        Directory.CreateDirectory(dirA);
        await File.WriteAllTextAsync(
            Path.Combine(dirA, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXT-X-ENDLIST\n");

        // Fake ffmpeg (audiobook concat shape, last positional arg is the
        // playlist): writes the first segment + a live no-ENDLIST playlist,
        // then parks on the stop file; used by BOTH generations.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf676-undercount",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n' > \"$playlist_path\"\n" +
            "while [ ! -f \"$playlist_dir/stop\" ]; do sleep 0.1; done\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(parentIdStr, loggerFactory, ffmpegPath: fakeFfmpegPath);
        try
        {
            // Gen B: art dated B, no cache under ticks B, so the concat path
            // starts a live generation that parks on its stop file.
            artImage.DateModified = artA.AddHours(6);
            ActionResult first = await controller.StreamHlsAudiobook(parentIdStr);
            Assert.IsType<ContentResult>(first);
            Assert.True(
                await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(parentIdStr, audiobook: true) == 1, TimeSpan.FromSeconds(5), 50),
                "gen B must register its live audiobook generation");

            // The verdict fetch: art back to A finds the undercounting fixture
            // playlist. Its own (parentId, ticks A) generation is dead while
            // gen B (ticks B) is live: the verdict must fire TICKS-SCOPED.
            artImage.DateModified = artA;
            ActionResult second = await controller.StreamHlsAudiobook(parentIdStr);
            var degraded = Assert.IsType<ObjectResult>(second);
            Assert.Equal(503, degraded.StatusCode);

            Assert.True(
                File.Exists(Path.Combine(dirB, "stream.m3u8")),
                "the undercount verdict's key-wide Cleanup wiped the live foreign-ticks generation's directory mid-write (JF-676)");
            Assert.True(
                TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("Audiobook HLS cache invalidated", StringComparison.Ordinal)),
                "the ticks-scoped undercount verdict must fire for the dead own-ticks generation");
            Assert.False(
                File.Exists(Path.Combine(dirA, "stream.m3u8")),
                "the verdict must clean its own ticks directory's debris playlist");

            // Premise (green world only): the guard refused a second encode of
            // the book, so exactly gen B's generation holds the registry.
            Assert.Equal(1, VideoAudioController.EncodeGenerationCountForTest(parentIdStr, audiobook: true));
        }
        finally
        {
            artImage.DateModified = artA;
            await ReleaseParkedEncodeFixturesAsync(parentIdStr, new[] { dirA, dirB }, audiobook: true);
        }
    }

    /// <summary>
    /// JF-676 rework (gate-marker F1), SONG path: the encode generation must be
    /// registered BEFORE the ffmpeg process starts (the mark-before-start
    /// invariant all four HLS paths share). The arming race the review found:
    /// the song mark used to sit after the process start (and the prewrite),
    /// so a concurrent fast-path ticks-scoped debris verdict landing between
    /// ffmpeg's first playlist write and the mark classified the genuinely-live
    /// no-ENDLIST playlist as debris and deleted the running encode's
    /// directory. STRUCTURAL order assertion via the
    /// <see cref="VideoAudioController.FfmpegProcessStartedForTest"/> seam (a
    /// fake ffmpeg cannot read the in-process registry, so the observer
    /// snapshots it AT process-start time, the exact instant the invariant is
    /// about): the song registry must already carry the generation when the
    /// process starts. Red proof: the mark temporarily moved back after the
    /// prewrite (the pre-rework order) fails this pin with
    /// generationLiveAtStart=false.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_EncodeGenerationMarkedBeforeFfmpegStart()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString();
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-676 Rework Song",
            Id = itemId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(audioItem);

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf676-rework-song",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_000.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "exit 0\n");

        var controller = CreateController(itemIdStr, ffmpegPath: fakeFfmpegPath);
        bool observerFired = false;
        bool generationLiveAtStart = false;
        controller.FfmpegProcessStartedForTest = _ =>
        {
            observerFired = true;
            generationLiveAtStart = VideoAudioController.EncodeActiveForTest(itemIdStr, song: true);
        };

        ActionResult result = await controller.StreamHlsVideoAudio(itemIdStr);
        Assert.IsType<ContentResult>(result);

        try
        {
            Assert.True(observerFired, "the process-start observer must fire (the seam is wired)");
            Assert.True(
                generationLiveAtStart,
                "the song path must register its encode generation BEFORE ffmpeg starts: a playlist a concurrent debris verdict could judge cannot exist until ffmpeg writes it, and by then the own-ticks generation must already be live (JF-676 rework)");
        }
        finally
        {
            await ReleaseParkedEncodeFixturesAsync(itemIdStr, Array.Empty<string>(), song: true);
        }
    }

    /// <summary>
    /// JF-676 rework (gate-marker F1), AUDIOBOOK twin: the same
    /// mark-before-start invariant on the concat path (its mark also used to
    /// sit after the process start). Same structural order assertion through
    /// <see cref="VideoAudioController.FfmpegProcessStartedForTest"/>: the
    /// audiobook registry must already carry the generation when the process
    /// starts. Red proof: the mark temporarily moved back after the prewrite
    /// (the pre-rework order) fails this pin.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_EncodeGenerationMarkedBeforeFfmpegStart()
    {
        Guid parentId = Guid.NewGuid();
        string parentIdStr = parentId.ToString();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-676 Rework Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-676 Rework Chapter 1",
            Id = Guid.NewGuid(),
            Path = "/book/jf676rework-001.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-676 Rework Chapter 2",
            Id = Guid.NewGuid(),
            Path = "/book/jf676rework-002.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf676-rework-book",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n#EXT-X-ENDLIST\\n' > \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(parentIdStr, ffmpegPath: fakeFfmpegPath);
        bool observerFired = false;
        bool generationLiveAtStart = false;
        controller.FfmpegProcessStartedForTest = _ =>
        {
            observerFired = true;
            generationLiveAtStart = VideoAudioController.EncodeActiveForTest(parentIdStr, audiobook: true);
        };

        ActionResult result = await controller.StreamHlsAudiobook(parentIdStr);
        Assert.IsType<ContentResult>(result);

        try
        {
            Assert.True(observerFired, "the process-start observer must fire (the seam is wired)");
            Assert.True(
                generationLiveAtStart,
                "the audiobook path must register its encode generation BEFORE ffmpeg starts: a playlist a concurrent debris verdict could judge cannot exist until ffmpeg writes it, and by then the own-ticks generation must already be live (JF-676 rework)");
        }
        finally
        {
            await ReleaseParkedEncodeFixturesAsync(parentIdStr, Array.Empty<string>(), audiobook: true);
        }
    }

    /// <summary>
    /// JF-498 review I1, endpoint level: a re-encode over interrupted-encode debris
    /// (non-empty playlist WITHOUT ENDLIST, no active encode) must start ffmpeg over a
    /// CLEAN target: the fake ffmpeg snapshots the pre-existing playlist/segments
    /// before writing anything, and the snapshot must show neither the stale playlist
    /// nor the stale segment, so append_list cannot bake a doubled playlist into the
    /// cache. The debris is removed by the layered cleanups (the validation Cleanup,
    /// then DeleteHlsEncodeDebris immediately before the process starts, which covers
    /// the whole-directory-delete-failed case those layers cannot).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_DebrisBeforeEncode_FfmpegStartsOverCleanTarget()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E04",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        // Fake ffmpeg: snapshot whether the stale playlist/segment exist at start,
        // then write the fresh first segment + playlist and exit 0.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-debris",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "snapshot=\"$dir/snapshot-before-encode.txt\"\n" +
            ": > \"$snapshot\"\n" +
            "[ -f \"$dir/stream.m3u8\" ] && echo STALE-PLAYLIST >> \"$snapshot\"\n" +
            "[ -f \"$dir/seg_0999.ts\" ] && echo STALE-SEGMENT >> \"$snapshot\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "exit 0\n");

        // Debris of an interrupted encode: a live-looking playlist (no ENDLIST)
        // referencing segments, with a stale segment file on disk.
        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0999.ts\n");
        await File.WriteAllBytesAsync(Path.Combine(hlsDir, "seg_0999.ts"), new byte[1024]);

        var controller = CreateController(episode.Id.ToString());
        controller.FfmpegPath = fakeFfmpegPath;
        controller.ControllerContext.HttpContext = new DefaultHttpContext
        {
            Request =
            {
                Query = controller.ControllerContext.HttpContext.Request.Query
            }
        };

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        // The debris invalidated the cache: a re-encode ran and the fresh partial
        // playlist was served, carrying only the new segment.
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0999", content.Content, StringComparison.Ordinal);

        // At ffmpeg start the target was clean: neither the stale playlist nor the
        // stale segment existed (append_list had nothing to append over).
        string snapshot = await File.ReadAllTextAsync(Path.Combine(hlsDir, "snapshot-before-encode.txt"));
        Assert.DoesNotContain("STALE-PLAYLIST", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE-SEGMENT", snapshot, StringComparison.Ordinal);
    }

    // ========== JF-531: pre-written full episode listing (live-edge fix) ==========

    /// <summary>
    /// Shared endpoint-test setup for the JF-531 episode family: an Episode with the
    /// given codec (1080p video + eac3 audio streams) and the GetItemById wiring.
    /// No EncoderPath setup: every caller passes an explicit fake ffmpeg, and
    /// ResolveFfmpegPath returns that override before ever consulting
    /// IMediaEncoder (JF-759).
    /// </summary>
    private (MediaBrowser.Controller.Entities.TV.Episode Episode, Mock<IMediaSourceManager> MediaSources)
        SetupEpisodeForHls(string name, string videoCodec, TimeSpan? runtime)
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = name,
            Id = Guid.NewGuid(),
            RunTimeTicks = runtime?.Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(episode.Id))
            .Returns(new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Codec = videoCodec, Height = 1080 },
                new() { Type = MediaStreamType.Audio, Codec = "eac3" }
            });

        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);
        return (episode, mediaSourceManager);
    }


    /// <summary>
    /// JF-531 unit level: WriteEpisodePlaylist lists ceil(runtime/4s) segments whose
    /// EXTINF durations sum to the full runtime (what the seekbar shows), with the
    /// event-playlist header, NO #EXT-X-ENDLIST and NO per-segment
    /// #EXT-X-DISCONTINUITY (one continuous encode, unlike audiobook chapter
    /// boundaries), and the stream token embedded on segment lines.
    /// </summary>
    [Fact]
    public void WriteEpisodePlaylist_FullListing_NoEndList_SumsToRuntime()
    {
        string playlistPath = Path.Combine(_tempDir, "playlist-full-unit-test.m3u8");

        // The live-incident shape: Adolescence E2, 51 minutes (corr=c0c21c6a).
        long runtimeTicks = TimeSpan.FromMinutes(51).Ticks;
        VideoAudioController.WriteEpisodePlaylist(
            playlistPath, "/alexaskill/api/video-audio/ITEM/segments/", runtimeTicks, "tok123");

        string content = File.ReadAllText(playlistPath);
        string[] lines = content.Split('\n');

        Assert.StartsWith("#EXTM3U", lines[0], StringComparison.Ordinal);
        Assert.Contains("#EXT-X-VERSION:3", content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-TARGETDURATION:4", content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content, StringComparison.Ordinal);

        // 51 min = 3060s at 4s per segment = 765 entries: seg_0000..seg_0764.
        string[] extInfLines = lines.Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(765, extInfLines.Length);
        Assert.Contains("/alexaskill/api/video-audio/ITEM/segments/seg_0000.ts?token=tok123", content, StringComparison.Ordinal);
        Assert.Contains("/alexaskill/api/video-audio/ITEM/segments/seg_0764.ts?token=tok123", content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0765", content, StringComparison.Ordinal);

        // Event playlist (no ENDLIST) and a continuous timeline (no discontinuities).
        Assert.DoesNotContain("#EXT-X-ENDLIST", content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", content, StringComparison.Ordinal);

        double totalSeconds = extInfLines.Sum(l =>
            double.Parse(l["#EXTINF:".Length..].TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3060.0, totalSeconds, 3);
    }

    /// <summary>
    /// JF-531 endpoint level: on a cache miss the FIRST serve returns the pre-written
    /// FULL listing (every segment name, durations summing to the runtime, no
    /// ENDLIST), not ffmpeg's growing stream.m3u8, whose no-ENDLIST partial shape
    /// ExoPlayer treats as LIVE (playback at the live edge, partial seekbar).
    /// ffmpeg still writes its own stream.m3u8: it remains the recorded target.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_CacheMiss_ServesPrewrittenFullListingNotLiveEdge()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Adolescence S01E02", "h264", TimeSpan.FromMinutes(45));

        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-remux");

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);

        // The FULL listing: 45 min = 2700s / 4s = 675 segments (seg_0000..seg_0674),
        // no ENDLIST (event playlist), token injected on the segment lines.
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0674.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0675", content.Content, StringComparison.Ordinal);
        Assert.Equal(675, VideoAudioController.CountSegmentsInPlaylist(content.Content));

        // The pre-written file exists next to ffmpeg's own target, and ffmpeg was
        // still pointed at stream.m3u8 (the prewrite never feeds ffmpeg's file).
        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Assert.True(File.Exists(Path.Combine(hlsDir, "playlist-full.m3u8")), "pre-written listing must exist");
        string[] recordedArgs = File.ReadAllLines(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Equal("stream.m3u8", Path.GetFileName(recordedArgs[^1]));
    }

    /// <summary>
    /// JF-531 AC#1: the TRANSCODE tier gets the same pre-written listing at first
    /// serve. The live-edge mechanism is tier-independent (any no-ENDLIST partial
    /// playlist is treated as live), and the incident episode itself was
    /// transcode-tier HEVC.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_FirstServeIsTheFullListing()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Adolescence S01E02", "hevc", TimeSpan.FromMinutes(51));

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-hevc"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        // 51 min = 3060s / 4s = 765 segments, no ENDLIST.
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Equal(765, VideoAudioController.CountSegmentsInPlaylist(content.Content));
        Assert.Contains("seg_0764.ts?token=", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-525: the tier probe reads the item's media streams ONCE per request.
    /// The video and audio codec resolvers each paid their own
    /// IMediaSourceManager.GetMediaStreams DB read; the combined probe folds them.
    /// The TRANSCODE tier pays no bitrate read (its estimate is runtime-based), so
    /// one call is the tier's whole stream-read budget.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_ReadsMediaStreamsOnce()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Adolescence S01E05", "hevc", TimeSpan.FromMinutes(45));

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf525-once"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);
        mediaSourceManager.Verify(m => m.GetMediaStreams(episode.Id), Times.Once);
    }

    /// <summary>
    /// JF-539: the REMUX tier also reads the item's media streams ONCE per request.
    /// Unlike the transcode tier, the remux estimate is bitrate-aware, and the
    /// bitrate used to be a SECOND GetMediaStreams read
    /// (<c>ResolveTotalMediaBitrateBps</c> after the codec probe); the fold into
    /// the combined probe (JF-525 codec probe + JF-539 bitrate) makes one call
    /// the tier's whole stream-read budget. Before the fold this path read twice.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_RemuxTier_ReadsMediaStreamsOnce()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Adolescence S01E06", "h264", TimeSpan.FromMinutes(45));

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf539-once"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);
        mediaSourceManager.Verify(m => m.GetMediaStreams(episode.Id), Times.Once);
    }

    /// <summary>
    /// JF-531 cache completion: once the encode completes (ENDLIST in stream.m3u8, no
    /// active flag), the served playlist is ffmpeg's COMPLETE one. The pre-written
    /// listing that stays on disk must not shadow it (the active-flag gate on the
    /// pre-written serve path).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_EncodeCompleted_ServesEndlistPlaylistNotStalePrewritten()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E02",
            Id = itemId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(episode);

        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);

        // A completed encode leaves BOTH files behind: ffmpeg's ENDLIST playlist and
        // the pre-written listing (never deleted post-encode, like the audiobook path).
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
        VideoAudioController.WriteEpisodePlaylist(
            Path.Combine(hlsDir, "playlist-full.m3u8"),
            $"/alexaskill/api/video-audio/{itemIdStr}/segments/",
            TimeSpan.FromMinutes(45).Ticks,
            token: null);

        // ffmpeg path injected even though this test never encodes: request
        // validation File.Exists-gates the encoder path, and CI runners do not
        // ship /usr/bin/ffmpeg (the ambient-binary CI divergence class).
        var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-completed"));

        ActionResult result = await controller.StreamHlsEpisode(itemIdStr);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);

        // NOT the pre-written full listing (its exclusive tail segment is absent).
        Assert.DoesNotContain("seg_0674", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-531 mid-encode fetch: while the encode flag is up, a playlist re-fetch (the
    /// player polls an event playlist) returns the full pre-written listing, which
    /// still includes the segments ffmpeg has already appended. Append semantics are
    /// preserved: the listing never drops a segment that was listed before.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_ActiveEncode_MidEncodeFetchServesStableFullListing()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");

        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E02",
            Id = itemId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(episode);

        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);

        // ffmpeg's live playlist has appended two segments so far.
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n");
        VideoAudioController.WriteEpisodePlaylist(
            Path.Combine(hlsDir, "playlist-full.m3u8"),
            $"/alexaskill/api/video-audio/{itemIdStr}/segments/",
            TimeSpan.FromMinutes(45).Ticks,
            token: null);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            // Same ambient-ffmpeg note as the completed-encode test above.
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-midencode"));

            ActionResult result = await controller.StreamHlsEpisode(itemIdStr);

            var content = Assert.IsType<ContentResult>(result);
            // The already-appended segments stay listed, and the full runtime tail is
            // there too, with no ENDLIST (encode still running).
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0674.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// JF-531 fallback: an item with no runtime cannot have an honest full listing;
    /// the first serve falls back to ffmpeg's live playlist (the pre-JF-531
    /// behavior) and no pre-written file is left for the active-encode guard to serve.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_NoRuntime_ServesLivePlaylistAndWritesNoPrewrittenFile()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Unprobed Runtime S01E01", "h264", null);

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-noruntime"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        // ffmpeg's own (partial) playlist is served, not a full listing.
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0674", content.Content, StringComparison.Ordinal);

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Assert.False(File.Exists(Path.Combine(hlsDir, "playlist-full.m3u8")), "no pre-written listing without a runtime");
    }

    /// <summary>
    /// JF-531 no-runtime fallback, stale-listing half: a leftover playlist-full.m3u8
    /// from an earlier encode must be DELETED when the new encode cannot write an
    /// honest listing, so the active-encode guard can never serve a listing this
    /// encode did not write (review coverage nit).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_NoRuntime_DeletesStalePrewrittenListing()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Stale Listing S01E01", "h264", null);

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        string staleListing = Path.Combine(hlsDir, "playlist-full.m3u8");
        File.WriteAllText(staleListing, "#EXTM3U\n#EXT-X-ENDLIST\n");

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-stale"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);
        Assert.False(File.Exists(staleListing), "a stale pre-written listing must not survive a no-runtime encode start");
    }

    // ========== JF-536: shared prewritten-playlist writer core + pin-window prewrite ==========

    /// <summary>
    /// JF-536 scope (a): the shared writer core emits the event-playlist header with
    /// the caller's TARGETDURATION, token-suffixed segment URLs in the caller's
    /// index-width naming, invariant-culture EXTINF durations, and NO ENDLIST; the
    /// per-segment discontinuity flag is honored per entry.
    /// </summary>
    [Fact]
    public void WritePrewrittenEventPlaylist_ParameterizedAxes_EmitExactLines()
    {
        string continuous = Path.Combine(_tempDir, "core-continuous.m3u8");
        VideoAudioController.WritePrewrittenEventPlaylist(
            continuous, "/alexaskill/api/video-audio/ITEM/segments/", new[] { 4.5 }, 6, 3, false, "tok7");

        string content = File.ReadAllText(continuous);
        string[] lines = content.Split('\n');
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#EXT-X-VERSION:3", lines[1]);
        Assert.Equal("#EXT-X-TARGETDURATION:6", lines[2]);
        Assert.Equal("#EXT-X-MEDIA-SEQUENCE:0", lines[3]);
        Assert.Equal("#EXTINF:4.500000,", lines[4]);
        Assert.Equal("/alexaskill/api/video-audio/ITEM/segments/seg_000.ts?token=tok7", lines[5]);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", content, StringComparison.Ordinal);

        // The audiobook axis: a discontinuity before EVERY segment (chapter-file
        // boundaries), 4-digit naming, no token means no suffix.
        string chaptered = Path.Combine(_tempDir, "core-chaptered.m3u8");
        VideoAudioController.WritePrewrittenEventPlaylist(
            chaptered, "/base/", new[] { 10.0, 10.0 }, 10, 4, true, null);

        string chapteredContent = File.ReadAllText(chaptered);
        Assert.Equal(2, chapteredContent.Split("#EXT-X-DISCONTINUITY").Length - 1);
        Assert.Contains("/base/seg_0000.ts\n", chapteredContent, StringComparison.Ordinal);
        Assert.Contains("/base/seg_0001.ts\n", chapteredContent, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", chapteredContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-536 scope (a): the index-width parameter is a MINIMUM width, matching
    /// ffmpeg's <c>%03d</c> semantics: past the 3-digit cap the index renders wider
    /// (seg_1000), and IsValidSegmentName accepts both widths, so the single-item
    /// path's hours-long single-chapter audiobooks keep a servable listing.
    /// </summary>
    [Fact]
    public void WritePrewrittenEventPlaylist_IndexWidthIsMinimum_PastCapRendersWider()
    {
        string playlistPath = Path.Combine(_tempDir, "core-index-width.m3u8");
        VideoAudioController.WritePrewrittenEventPlaylist(
            playlistPath, "/base/", Enumerable.Repeat(1.0, 1001).ToArray(), 1, 3, false, null);

        string content = File.ReadAllText(playlistPath);
        Assert.Contains("/base/seg_000.ts\n", content, StringComparison.Ordinal);
        Assert.Contains("/base/seg_999.ts\n", content, StringComparison.Ordinal);
        Assert.Contains("/base/seg_1000.ts\n", content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0999.ts", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-536 (carrying JF-531's invariant-culture review nit to the audiobook twin
    /// through the shared core): EXTINF durations must format INVARIANT even under a
    /// comma-decimal host culture. The pre-core writer interpolated {duration:F6}
    /// with the current culture, which on an it-IT host rendered "9,250000," and HLS
    /// parsers read the duration as 9.
    /// </summary>
    [Fact]
    public void WriteAudiobookPlaylist_CommaDecimalHostCulture_ExtInfStaysInvariant()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("it-IT");
        try
        {
            string playlistPath = Path.Combine(_tempDir, "audiobook-culture.m3u8");
            var chapters = new List<MediaBrowser.Controller.Entities.BaseItem>
            {
                new MediaBrowser.Controller.Entities.Audio.Audio { RunTimeTicks = TimeSpan.FromSeconds(83.25).Ticks }
            };
            VideoAudioController.WriteAudiobookPlaylist(playlistPath, "/base/", chapters, token: null);

            string content = File.ReadAllText(playlistPath);
            string[] extInfLines = content.Split('\n')
                .Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
                .ToArray();

            // ceil(83.25/10) segments, each a dot-decimal F6 duration (no comma anywhere
            // inside the numeric field; the trailing list separator is the only comma).
            Assert.Equal(9, extInfLines.Length);
            Assert.All(extInfLines, l => Assert.Matches(@"^#EXTINF:\d+\.\d+,", l));

            double totalSeconds = extInfLines.Sum(l =>
                double.Parse(l["#EXTINF:".Length..].TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(83.25, totalSeconds, 6);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// JF-536 scope (a), audiobook wrapper: per-chapter splitting (each chapter
    /// ceil-split into 10s segments carrying an equal share), a DISCONTINUITY per
    /// segment, 4-digit names, the 250s fallback for chapters without a runtime,
    /// and no ENDLIST.
    /// </summary>
    [Fact]
    public void WriteAudiobookPlaylist_PerChapterSplit_DiscontinuityPerSegment()
    {
        string playlistPath = Path.Combine(_tempDir, "audiobook-split.m3u8");
        var chapters = new List<MediaBrowser.Controller.Entities.BaseItem>
        {
            new MediaBrowser.Controller.Entities.Audio.Audio { RunTimeTicks = TimeSpan.FromSeconds(83.25).Ticks },
            new MediaBrowser.Controller.Entities.Audio.Audio { RunTimeTicks = TimeSpan.FromSeconds(100).Ticks },
            new MediaBrowser.Controller.Entities.Audio.Audio { RunTimeTicks = null }
        };

        VideoAudioController.WriteAudiobookPlaylist(playlistPath, "/base/", chapters, "tk");

        string content = File.ReadAllText(playlistPath);
        string[] extInfLines = content.Split('\n')
            .Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .ToArray();

        // ceil(83.25/10)=9, ceil(100/10)=10, the null-runtime fallback 250s -> 25.
        int expectedSegments = 9 + 10 + 25;
        Assert.Equal(expectedSegments, extInfLines.Length);
        Assert.Equal(expectedSegments, content.Split("#EXT-X-DISCONTINUITY").Length - 1);
        Assert.Contains("#EXT-X-TARGETDURATION:10", content, StringComparison.Ordinal);
        Assert.Contains("/base/seg_0000.ts?token=tk", content, StringComparison.Ordinal);
        Assert.Contains($"/base/seg_{expectedSegments - 1:D4}.ts?token=tk", content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content, StringComparison.Ordinal);

        double totalSeconds = extInfLines.Sum(l =>
            double.Parse(l["#EXTINF:".Length..].TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(83.25 + 100.0 + 250.0, totalSeconds, 6);
    }

    /// <summary>
    /// JF-536 scope (c) unit level: the single-item writer (songs + the
    /// single-chapter audiobooks redirected from the audiobook endpoint) lists
    /// ceil(runtime/4s) segments in the path's 3-digit naming with a 4s
    /// TARGETDURATION and no ENDLIST or discontinuities.
    /// </summary>
    [Fact]
    public void WriteVideoAudioPlaylist_FullListing_ThreeDigitNames_SumsToRuntime()
    {
        string playlistPath = Path.Combine(_tempDir, "videoaudio-listing.m3u8");

        VideoAudioController.WriteVideoAudioPlaylist(
            playlistPath, "/alexaskill/api/video-audio/ITEM/segments/", TimeSpan.FromMinutes(45).Ticks, "tk");

        string content = File.ReadAllText(playlistPath);
        Assert.Contains("#EXT-X-TARGETDURATION:4", content, StringComparison.Ordinal);
        Assert.Contains("/alexaskill/api/video-audio/ITEM/segments/seg_000.ts?token=tk", content, StringComparison.Ordinal);
        Assert.Contains("/alexaskill/api/video-audio/ITEM/segments/seg_674.ts?token=tk", content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_675", content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", content, StringComparison.Ordinal);
        Assert.Equal(675, VideoAudioController.CountSegmentsInPlaylist(content));
    }

    /// <summary>
    /// JF-536 scope (c) endpoint level: on a cache miss the single-item path's FIRST
    /// serve returns the pre-written FULL listing, not ffmpeg's growing stream.m3u8
    /// (the live-edge shape the same VideoApp/ExoPlayer consumer showed on the
    /// episode path, 2026-09-09 corr=c0c21c6a). The runtime here is the long tail
    /// this path exists to fix: single-chapter-audiobook length.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CacheMiss_ServesPrewrittenFullListingNotLiveEdge()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Long Single Chapter",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf536-song",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:4.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(audioItem.Id.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_674.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_675", content.Content, StringComparison.Ordinal);
        Assert.Equal(675, VideoAudioController.CountSegmentsInPlaylist(content.Content));

        // The pre-written file exists next to ffmpeg's own target, which ffmpeg
        // still owns (the prewrite never feeds ffmpeg's file).
        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString(), 0);
        Assert.True(File.Exists(Path.Combine(hlsDir, "playlist-full.m3u8")), "pre-written listing must exist");
        Assert.True(File.Exists(Path.Combine(hlsDir, "stream.m3u8")), "ffmpeg keeps writing its own playlist");
    }

    /// <summary>
    /// JF-536: once the encode completed (no active flag), the served playlist is
    /// ffmpeg's COMPLETE one; the pre-written listing that stays on disk must not
    /// shadow it (the active-flag gate on the pre-written serve path).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_EncodeCompleted_ServesEndlistPlaylistNotStalePrewritten()
    {
        Guid itemId = Guid.NewGuid();
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Done Song",
            Id = itemId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(audioItem);

        // A completed encode leaves BOTH files behind: ffmpeg's ENDLIST playlist and
        // the pre-written listing (never deleted post-encode, like the twins).
        string hlsDir = _cache.GetHlsDirectoryPath(itemId.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_001.ts\n#EXT-X-ENDLIST\n");
        VideoAudioController.WriteVideoAudioPlaylist(
            Path.Combine(hlsDir, "playlist-full.m3u8"),
            $"/alexaskill/api/video-audio/{itemId}/segments/",
            TimeSpan.FromMinutes(45).Ticks,
            token: null);

        var controller = CreateController(itemId.ToString(), ffmpegPath: WriteFakeFfmpeg("fake-ffmpeg-jf536-done", "exit 0\n"));

        ActionResult result = await controller.StreamHlsVideoAudio(itemId.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_001.ts?token=", content.Content, StringComparison.Ordinal);

        // NOT the pre-written full listing (its exclusive tail segment is absent).
        Assert.DoesNotContain("seg_674", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-536 mid-encode fetch: while the encode flag is up, a playlist re-fetch (the
    /// player polls an event playlist) returns the stable full pre-written listing,
    /// which still includes the segments ffmpeg has already appended to its live one.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_ActiveEncode_MidEncodeFetchServesStableFullListing()
    {
        Guid itemId = Guid.NewGuid();
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Encoding Song",
            Id = itemId,
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(itemId.ToString(), 0);
        Directory.CreateDirectory(hlsDir);

        // ffmpeg's live playlist has appended two segments so far.
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_001.ts\n");
        VideoAudioController.WriteVideoAudioPlaylist(
            Path.Combine(hlsDir, "playlist-full.m3u8"),
            $"/alexaskill/api/video-audio/{itemId}/segments/",
            TimeSpan.FromMinutes(45).Ticks,
            token: null);

        VideoAudioController.SetEncodeActiveForTest(itemId.ToString(), active: true, song: true);
        try
        {
            var controller = CreateController(itemId.ToString(), ffmpegPath: WriteFakeFfmpeg("fake-ffmpeg-jf536-midencode", "exit 0\n"));

            ActionResult result = await controller.StreamHlsVideoAudio(itemId.ToString());

            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_001.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_674.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemId.ToString(), active: false, song: true);
        }
    }

    /// <summary>
    /// JF-536 fallback: an item with no runtime cannot have an honest full listing;
    /// the first serve falls back to ffmpeg's live playlist (the pre-JF-536
    /// behavior) and no pre-written file is left for the active-encode guard to serve.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_NoRuntime_ServesLivePlaylistAndWritesNoPrewrittenFile()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Unprobed Song",
            Id = Guid.NewGuid()
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf536-noruntime",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXTINF:4.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(audioItem.Id.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        // ffmpeg's own (partial) playlist is served, not a full listing.
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_674", content.Content, StringComparison.Ordinal);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString(), 0);
        Assert.False(File.Exists(Path.Combine(hlsDir, "playlist-full.m3u8")), "no pre-written listing without a runtime");
    }

    /// <summary>
    /// JF-536 scope (d) machinery: hold the ONLY encode-gate slot so the endpoint
    /// parks inside StartFfmpegProcessGatedAsync AFTER its Pin but BEFORE the
    /// process starts, then prove the pre-written listing is absent while parked. A
    /// prewrite emitted before the gated call would be on disk there, exposed to a
    /// concurrent eviction sweep: the JF-428 creation-to-pin class this task closes.
    /// <paramref name="play"/> must invoke a controller constructed BEFORE this
    /// helper narrows the gate (the ctor re-applies the configured capacity).
    /// </summary>
    private async Task AssertPrewriteInsidePinWindow(Func<Task<ActionResult>> play, string hlsDir, string prewrittenPath)
    {
        VideoAudioController.UpdateEncodeGateCapacity(1);
        var gate = GateField();
        await gate.WaitAsync();
        Task<ActionResult> request = Task.Run(play);
        try
        {
            Assert.True(
                await WaitUntilAsync(() => _cache.IsPinned(hlsDir), TimeSpan.FromSeconds(10)),
                "the parked request must have pinned its HLS directory");
            Assert.False(
                File.Exists(prewrittenPath),
                "pre-written listing must not exist before the pin: a concurrent eviction sweep could delete it (JF-428/JF-536)");
        }
        finally
        {
            // A gate swap mid-test (a controller constructed under a different config
            // re-applies the configured capacity in its ctor) would silently un-park
            // the request and turn the assertion above into a race; fail loudly.
            Assert.Same(gate, GateField());
            gate.Release();
            VideoAudioController.UpdateEncodeGateCapacity(2); // restore default

            // The endpoint's own waits are bounded (~20s first-segment poll); cap the
            // completion so a deadlock fails the test instead of hanging the suite.
            Task completed = await Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(ReferenceEquals(request, completed), "the parked request must complete once the gate frees");
            ActionResult served = await request;
            Assert.IsType<ContentResult>(served);
        }

        Assert.True(File.Exists(prewrittenPath), "the pre-written listing must land once the encode starts");
    }

    /// <summary>
    /// JF-536 scope (d), episode path: the pre-write of the full listing happens
    /// INSIDE the JF-428 pin window (after StartFfmpegProcessGatedAsync pinned the
    /// HLS directory), never before it.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_PrewriteLandsInsidePinWindow_AfterThePin()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Pin Window S01E01", "h264", TimeSpan.FromMinutes(45));
        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf536-pin-episode"));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        await AssertPrewriteInsidePinWindow(
            () => controller.StreamHlsEpisode(episode.Id.ToString()),
            hlsDir,
            Path.Combine(hlsDir, "playlist-full.m3u8"));
    }

    /// <summary>
    /// JF-536 scope (d), audiobook path: same pin-window invariant on the original
    /// prewriting path (its prewrite used to sit before the gated call entirely).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_PrewriteLandsInsidePinWindow_AfterThePin()
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Pin Window Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromSeconds(83.25).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid()
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf536-pin-book",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXTINF:10.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_0000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        await AssertPrewriteInsidePinWindow(
            () => controller.StreamHlsAudiobook(parentId.ToString()),
            hlsDir,
            Path.Combine(hlsDir, "playlist-full.m3u8"));
    }

    /// <summary>
    /// JF-536 scope (c)+(d), single-item path: the NEW prewrite honors the same
    /// pin-window invariant it was born with.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_PrewriteLandsInsidePinWindow_AfterThePin()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Pin Window Song",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf536-pin-song",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXTINF:4.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(audioItem.Id.ToString(), ffmpegPath: fakeFfmpegPath);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString(), 0);
        await AssertPrewriteInsidePinWindow(
            () => controller.StreamHlsVideoAudio(audioItem.Id.ToString()),
            hlsDir,
            Path.Combine(hlsDir, "playlist-full.m3u8"));
    }

    // ========== JF-515: ffmpeg stderr aggregating drain ==========

    /// <summary>
    /// Real ffmpeg failure lines must classify as errors so the drain logs them
    /// immediately at Warning instead of aggregating them (JF-515).
    /// </summary>
    [Theory]
    [InlineData("Error opening 'http://localhost:8096/Videos/abc/stream': server returned 404")] // bare StartsWith
    [InlineData("error: no decoder for stream")] // mixed-case StartsWith
    [InlineData("ERROR while opening output")] // all-caps StartsWith
    [InlineData("[hls @ 0x7f8b2400] Error opening '/cache/x/seg_0001.ts'")] // component prefix + StartsWith
    [InlineData("[mov,mp4,m4a,3gp,3g2,mj2 @ 0x5591c0a1e400] moov atom not found")] // real mov.c:11603 shape, contains 'moov atom'
    [InlineData("[error] unable to parse option value")] // contains [error]
    [InlineData("Conversion failed!")] // contains, bare
    [InlineData("conversion FAILED: unknown option '-foo'")] // contains, mixed case
    [InlineData("[mp3 @ 0x7f8b2c0d0e80] Invalid data found when processing input")] // contains, prefixed
    [InlineData("Cannot open '/cache/xyz/stream.m3u8': No such file or directory")] // contains
    [InlineData("Cannot open '/cache/xyz/seg_0001.ts': Permission denied")] // contains
    [InlineData("[hls @ 0x7f8b2400] Failed to open file '/cache/xxx/seg_0001.ts'")] // real hlsenc.c segment-open failure
    [InlineData("[mp3 @ 0x7f8b2c0d0e80] Header missing")] // truncated mp3, mpegaudiodec_template.c
    [InlineData("Cannot open '/cache/x/seg_0002.ts': No space left on device")] // ENOSPC (JF-428 cache-budget condition)
    public void IsFfmpegErrorLine_FailureLines_ReturnTrue(string line)
    {
        Assert.True(VideoAudioController.IsFfmpegErrorLine(line), $"expected error classification: {line}");
    }

    /// <summary>
    /// Routine progress chatter must classify as non-errors so the drain aggregates
    /// it into the periodic summaries instead of logging per line (JF-515).
    /// </summary>
    [Theory]
    [InlineData("Opening 'seg_0001.ts' for writing")] // the flood line, bare form
    [InlineData("[hls @ 0x7f8b2400] Opening '/cache/xyz_1337/seg_0001.ts' for writing")] // the flood line, prefixed form
    [InlineData("frame= 123 fps=45 q=-1.0")]
    [InlineData("size=N/A time=00:01:23 bitrate=N/A speed=2.5x")]
    [InlineData("Output #0, hls, to '/cache/xyz/stream.m3u8':")]
    [InlineData("  Metadata:")]
    [InlineData("Stream mapping:")]
    [InlineData("   ")] // empty-ish whitespace
    [InlineData("")] // empty
    public void IsFfmpegErrorLine_RoutineLines_ReturnFalse(string line)
    {
        Assert.False(VideoAudioController.IsFfmpegErrorLine(line), $"expected routine classification: {line}");
    }

    /// <summary>
    /// JF-519: SafeExitCode returns the real exit code of an exited process, the
    /// -1 contract for a still-running one (ExitCode would throw there), and keeps
    /// the inherited throw for a never-started or disposed Process (the wait sites
    /// read the code before their Dispose).
    /// </summary>
    [Fact]
    public void SafeExitCode_ExitedProcess_ReturnsExitCode()
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exit 3\""))!;
        process.WaitForExit();
        Assert.Equal(3, VideoAudioController.SafeExitCode(process));
    }

    [Fact]
    public void SafeExitCode_DisposedAfterExit_Throws()
    {
        var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exit 3\""))!;
        process.WaitForExit();
        process.Dispose();
        Assert.Throws<InvalidOperationException>(() => VideoAudioController.SafeExitCode(process));
    }

    [Fact]
    public void SafeExitCode_LiveProcess_ReturnsMinusOne()
    {
        // 2s sleep: the process is deterministically alive at the check right after Start.
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 2\""))!;
        Assert.Equal(-1, VideoAudioController.SafeExitCode(process));
        process.Kill();
        process.WaitForExit();
    }

    [Fact]
    public void SafeExitCode_NeverStartedProcess_Throws()
    {
        using var process = new Process();
        Assert.Throws<InvalidOperationException>(() => VideoAudioController.SafeExitCode(process));
    }

    // ========== JF-499: episode HLS lifecycle watch items ==========

    // ---- W1: restart-safe position-tracking gate ----

    /// <summary>
    /// JF-499 W1: segment fetches keyed by a FOLDER (the audiobook-parent shape the
    /// resume path reads via GetPositionTicks) still grow the tracker.
    /// </summary>
    [Fact]
    public async Task GetSegment_FolderBackedItem_RecordsPositionTracking()
    {
        Guid bookId = Guid.NewGuid();
        string bookIdStr = bookId.ToString("D");
        var folder = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "A Book",
            Id = bookId
        };
        _libraryManagerMock.Setup(m => m.GetItemById(bookId)).Returns(folder);

        string hlsDir = _cache.GetHlsDirectoryPath(bookIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0005.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(bookIdStr, 0);

        var tracker = CreatePositionTracker("jf499-tracker-folder");
        using var trackerSwap = SwapPluginPositionTracker(tracker);

        var controller = CreateController(bookIdStr);

        ActionResult result = await controller.GetSegment(bookIdStr, "seg_0005.ts");

        Assert.IsType<PhysicalFileResult>(result);
        // The tracker's conservative resume position: (highWaterMark - 1) * 10s.
        Assert.Equal(4 * 10 * TimeSpan.TicksPerSecond, tracker.GetPositionTicks(bookIdStr));
    }

    /// <summary>
    /// JF-499 W1: after a restart (no encode ever started in this process, so the
    /// retired process-static skip would be empty), an EPISODE's cached-segment
    /// fetches must NOT grow the tracker: the gate derives episode-ness from the
    /// library item, which survives restarts.
    /// </summary>
    [Fact]
    public async Task GetSegment_EpisodeItem_SkipsPositionTracking_RestartSafe()
    {
        Guid episodeId = Guid.NewGuid();
        string episodeIdStr = episodeId.ToString("D");
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Adolescence S01E01",
            Id = episodeId
        };
        _libraryManagerMock.Setup(m => m.GetItemById(episodeId)).Returns(episode);

        string hlsDir = _cache.GetHlsDirectoryPath(episodeIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0005.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(episodeIdStr, 0);

        var tracker = CreatePositionTracker("jf499-tracker-episode");
        using var trackerSwap = SwapPluginPositionTracker(tracker);

        var controller = CreateController(episodeIdStr);

        ActionResult result = await controller.GetSegment(episodeIdStr, "seg_0005.ts");

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(0, tracker.GetPositionTicks(episodeIdStr));
    }

    /// <summary>
    /// JF-694: segment fetches keyed by an AUDIOBOOK LEAF (the one-chapter redirect
    /// serves the single chapter's segments keyed by the chapter id, so pre-JF-694 the
    /// Folder-only gate never recorded and every mint site saw a permanently cold
    /// tracker) must record under the BOOK key the resume path reads (ParentId, the
    /// ResumeMath.GetAudiobookBookKey shape), TRANSLATED onto the tracker's 10s concat
    /// timeline: the redirected core is the song core cutting SongHlsSegmentSeconds=4
    /// segments, so a raw 4s index read as 10s would resume 2.5x past the listening
    /// point. seg_0015 at 4s = 60s of content → book-timeline index 15*4/10 = 6 →
    /// conservative resume (6-1)*10s = 50s under the PARENT key, and nothing under the
    /// dead leaf key.
    /// RED (the AudioBook arm reverted, gate back to Folder-only): the book-key assert
    /// reads 0.
    /// </summary>
    [Fact]
    public async Task GetSegment_SingleChapterAudioBookLeaf_RecordsUnderBookKeyOnConcatTimeline()
    {
        Guid parentId = Guid.NewGuid();
        string parentIdStr = parentId.ToString("D");
        var parentFolder = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "One-Chapter Book",
            Id = parentId
        };
        Guid chapterId = Guid.NewGuid();
        string chapterIdStr = chapterId.ToString("D");
        var chapter = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Only Chapter",
            Id = chapterId,
            ParentId = parentId,
            Path = "/audiobooks/one-chapter-book/only-chapter.mp3"
        };

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentFolder);
        _libraryManagerMock.Setup(m => m.GetItemById(chapterId)).Returns(chapter);

        // The segment sits in the CHAPTER's HLS cache dir (the redirect keys segments
        // by the chapter leaf, not the parent).
        string hlsDir = _cache.GetHlsDirectoryPath(chapterIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0015.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(chapterIdStr, 0);

        var tracker = CreatePositionTracker("jf694-tracker-onechapter");
        using var trackerSwap = SwapPluginPositionTracker(tracker);

        var controller = CreateController(chapterIdStr);

        ActionResult result = await controller.GetSegment(chapterIdStr, "seg_0015.ts");

        Assert.IsType<PhysicalFileResult>(result);
        // The read-aligned key: the mint sites read GetAudiobookBookKey(chapter) =
        // chapter.ParentId, so the record must land there, at the TRANSLATED position.
        Assert.Equal(5 * 10 * TimeSpan.TicksPerSecond, tracker.GetPositionTicks(parentIdStr));
        // Nothing under the dead leaf key (the JF-499 W1 dead-weight contract).
        Assert.Equal(0, tracker.GetPositionTicks(chapterIdStr));
    }

    /// <summary>
    /// JF-625/JF-694 regression: a plain Audio track under a MusicAlbum (every song's
    /// single-item HLS serve, and the single-track album redirect's shape) must NOT
    /// start recording under ANY key. The album key is READ BACK: in seek mode
    /// AlbumPlayService reads the tracker's album position as the resume truth (JF-625
    /// criterion 3), so an Audio-leaf-to-parent canonicalization would not merely grow
    /// the persisted file with dead entries, it would write a track-local 4s-timeline
    /// index under a key whose reads count the 10s concat timeline, poisoning the
    /// album resume mapping.
    /// RED (the gate widened to any Audio leaf, canonicalizing to ParentId): the
    /// album-key assert flips positive.
    /// </summary>
    [Fact]
    public async Task GetSegment_AlbumTrackLeaf_DoesNotRecordUnderAnyKey()
    {
        Guid albumId = Guid.NewGuid();
        string albumIdStr = albumId.ToString("D");
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Name = "An Album",
            Id = albumId
        };
        Guid trackId = Guid.NewGuid();
        string trackIdStr = trackId.ToString("D");
        var track = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "A Song",
            Id = trackId,
            ParentId = albumId
        };

        _libraryManagerMock.Setup(m => m.GetItemById(albumId)).Returns(album);
        _libraryManagerMock.Setup(m => m.GetItemById(trackId)).Returns(track);

        string hlsDir = _cache.GetHlsDirectoryPath(trackIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0005.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(trackIdStr, 0);

        var tracker = CreatePositionTracker("jf694-tracker-album");
        using var trackerSwap = SwapPluginPositionTracker(tracker);

        var controller = CreateController(trackIdStr);

        ActionResult result = await controller.GetSegment(trackIdStr, "seg_0005.ts");

        Assert.IsType<PhysicalFileResult>(result);
        // Neither the leaf key nor the canonicalized album key may grow.
        Assert.Equal(0, tracker.GetPositionTicks(trackIdStr));
        Assert.Equal(0, tracker.GetPositionTicks(albumIdStr));
    }

    /// <summary>
    /// JF-694 code-review finding 1: a ROOT-LEVEL single-file AudioBook (empty
    /// ParentId, served by the plain single-item route because the builder's concat
    /// branch requires a ParentId) must NOT start recording. Its book key falls back to
    /// its OWN id, and the resume mint would then build audiobook/{ownId}, an URL the
    /// concat endpoint 404s on (a leaf has no AudioBook children), so the key must stay
    /// cold for resume to keep the working plain single-item launch.
    /// RED (the AudioBook arm's ParentId condition dropped): the leaf records under its
    /// own id and the assert flips positive.
    /// </summary>
    [Fact]
    public async Task GetSegment_RootLevelAudioBookLeaf_EmptyParentId_DoesNotRecord()
    {
        Guid bookId = Guid.NewGuid();
        string bookIdStr = bookId.ToString("D");
        var book = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Root-Level Book",
            Id = bookId,
            Path = "/audiobooks/root-level-book.m4b"
        };

        _libraryManagerMock.Setup(m => m.GetItemById(bookId)).Returns(book);

        string hlsDir = _cache.GetHlsDirectoryPath(bookIdStr, 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "seg_0005.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(bookIdStr, 0);

        var tracker = CreatePositionTracker("jf694-tracker-rootlevel");
        using var trackerSwap = SwapPluginPositionTracker(tracker);

        var controller = CreateController(bookIdStr);

        ActionResult result = await controller.GetSegment(bookIdStr, "seg_0005.ts");

        Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(0, tracker.GetPositionTicks(bookIdStr));
    }

    // ---- W2a: evidence-based monitor budget ----

    /// <summary>
    /// JF-499 W2: the monitor budget bounds time WITHOUT PROGRESS, not total wall
    /// time. A slow-but-progressing encode (fake ffmpeg keeps touching the encode
    /// directory well past the 300ms test budget) must be EXTENDED, run to
    /// completion, and never be declared stalled.
    /// </summary>
    [Fact]
    public async Task MonitorHls_SlowButProgressingEncode_IsExtendedNotKilled()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Slow Nas S01E01", "h264", TimeSpan.FromMinutes(45));

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf499-slow",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "i=1\n" +
            "while [ \"$i\" -le 12 ]; do\n" +
            "  sleep 0.1\n" +
            "  touch \"$dir/progress.bin\"\n" +
            "  i=$((i+1))\n" +
            "done\n" +
            "printf 'done\\n' > \"$dir/encode-done.txt\"\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, fakeFfmpegPath);
        controller.HlsMonitorStallBudgetOverride = TimeSpan.FromMilliseconds(300);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());
        Assert.IsType<ContentResult>(result);

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string doneMarker = Path.Combine(hlsDir, "encode-done.txt");

        Assert.True(
            await WaitUntilAsync(() => File.Exists(doneMarker), TimeSpan.FromSeconds(20)),
            "a still-writing encode must run to completion, not be killed by the stall budget");
        Assert.True(
            await WaitUntilAsync(
                () => TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("HLS encoding complete", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)),
            "the monitor must report the encode as complete");

        var snapshot = TestCaptureLogger.Snapshot(logRecords);
        Assert.DoesNotContain(snapshot, r => r.Message.Contains("HLS encoding STALLED", StringComparison.Ordinal));
        Assert.Contains(snapshot, r => r.Message.Contains("extending the monitor budget", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-499 W2: a HUNG encode (first segment written, then nothing while the
    /// process stays alive) is killed after ONE no-progress budget window.
    /// </summary>
    [Fact]
    public async Task MonitorHls_HungEncode_KilledAfterOneStallBudget()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Hung Encode S01E01", "h264", TimeSpan.FromMinutes(45));

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf499-hung",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
            "sleep 15\n" +
            "printf 'done\\n' > \"$dir/encode-done.txt\"\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(episode.Id.ToString(), loggerFactory, mediaSourceManager, fakeFfmpegPath);
        controller.HlsMonitorStallBudgetOverride = TimeSpan.FromMilliseconds(300);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());
        Assert.IsType<ContentResult>(result);

        // JF-731: the stall-budget kill is the MONITOR's, and its delivery is
        // asynchronous from this test's perspective; the Dispose backstop
        // counts a still-alive fake at teardown as a leak, so observe the death
        // before returning (the first run of the process half caught exactly
        // this gap: the killed fake still read live at Dispose). In a FINALLY
        // (rework review): a body assert failure must not skip the fence and
        // then bury its own diagnostics under the backstop's second failure.
        try
        {
            Assert.True(
                await WaitUntilAsync(
                    () => TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("HLS encoding STALLED", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(10)),
                "a hung encode must be declared STALLED and killed");

            string doneMarker = Path.Combine(
                _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0), "encode-done.txt");
            Assert.False(File.Exists(doneMarker), "the hung encode must be killed before its sleep finishes");
        }
        finally
        {
            await FenceTempDirEncodesDeadAsync();
        }
    }

    // ---- W2b: resume slice on the episode remux path ----

    /// <summary>
    /// JF-499 W2: a completed episode cache served with ?start= returns the RESUME
    /// SLICE (segments from the resume position on, MEDIA-SEQUENCE shifted at the
    /// 4-second segment arithmetic), not the from-zero playlist.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_CacheHit_WithStart_ServesSlicedPlaylist()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Resumable S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXTINF:4.000,\nseg_0002.ts\n"
            + "#EXTINF:4.000,\nseg_0003.ts\n#EXTINF:4.000,\nseg_0004.ts\n#EXTINF:4.000,\nseg_0005.ts\n#EXT-X-ENDLIST\n");

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf499-slicecache"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString(), 16 * TimeSpan.TicksPerSecond);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0004.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0005.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0000", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0003.ts", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-499 W2, the watch-item scenario: the SELF-HEAL RE-ENCODE over interrupted
    /// debris honors ?start=. The served pre-written full listing is sliced at the
    /// resume position instead of restarting the timeline from 0:00.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_SelfHealReencode_WithStart_ServesSlicedFullListing()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Interrupted S01E01", "h264", TimeSpan.FromMinutes(45));

        // Debris of an interrupted encode: live-looking playlist (no ENDLIST), no
        // active flag.
        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n");

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf499-selfheal"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString(), 16 * TimeSpan.TicksPerSecond);

        var content = Assert.IsType<ContentResult>(result);
        // The re-encode's full listing (675 segments for 45min) sliced at 16s / 4s:
        Assert.Contains("seg_0004.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0674.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0003.ts", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0000", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:4", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
    }

    // ---- W3: fast-path FileNotFoundException race ----

    /// <summary>
    /// JF-499 W3: logger provider that deletes a file the first time a message
    /// containing the trigger is logged. Reproduces the fast-path race
    /// deterministically: the controller logs "serving cached playlist" between the
    /// cache-hit FileInfo read and the playlist content read, so deleting the file at
    /// that log leaves exactly the state a concurrent lock-holder's Cleanup leaves
    /// behind.
    /// </summary>
    private sealed class FileDeletingLoggerProvider : ILoggerProvider
    {
        private readonly string _trigger;
        private readonly string _pathToDelete;
        private int _fired;

        internal FileDeletingLoggerProvider(string trigger, string pathToDelete)
        {
            _trigger = trigger;
            _pathToDelete = pathToDelete;
        }

        public ILogger CreateLogger(string categoryName) => new DeletingLogger(this);

        /// <summary>Whether the trigger fired and the file was deleted.</summary>
        internal bool Fired => Volatile.Read(ref _fired) == 1;

        public void Dispose()
        {
        }

        private sealed class DeletingLogger : ILogger
        {
            private readonly FileDeletingLoggerProvider _owner;

            internal DeletingLogger(FileDeletingLoggerProvider owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string message = formatter(state, exception);
                if (message.Contains(_owner._trigger, StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref _owner._fired, 1, 0) == 0)
                {
#pragma warning disable CA3003 // test-created path
                    File.Delete(_owner._pathToDelete);
#pragma warning restore CA3003
                }
            }
        }
    }

    /// <summary>
    /// JF-751 shared micro-helper collapsing the 15 vanish-race logger factory
    /// constructions (the JF-499/JF-677/JF-678/JF-682/JF-685 families): Trace
    /// minimum plus the <see cref="FileDeletingLoggerProvider"/> on the given
    /// trigger, with any extra providers registered AHEAD of it (each extras
    /// site registers its single extra provider, a capture or clearing
    /// provider, first and the deleting provider last). The provider overload
    /// serves the two sites that assert
    /// <see cref="FileDeletingLoggerProvider.Fired"/> on their own named local.
    /// </summary>
    private static ILoggerFactory CreateDeletingLoggerFactory(string trigger, string playlistPath, params ILoggerProvider[] extraProviders)
        => CreateDeletingLoggerFactory(new FileDeletingLoggerProvider(trigger, playlistPath), extraProviders);

    /// <summary>
    /// See the string overload; construct the deleting provider yourself when
    /// the test asserts its <see cref="FileDeletingLoggerProvider.Fired"/> flag.
    /// MIND THE ORDER: the deleting provider is the FIRST argument but is
    /// registered LAST (extras are registered ahead of it, the original sites'
    /// AddProvider sequence), so a provider whose same-message side effect must
    /// run before the file delete goes into <paramref name="extraProviders"/>,
    /// not after it.
    /// </summary>
    private static ILoggerFactory CreateDeletingLoggerFactory(FileDeletingLoggerProvider deletingProvider, params ILoggerProvider[] extraProviders)
        => LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            foreach (ILoggerProvider provider in extraProviders)
            {
                b.AddProvider(provider);
            }

            b.AddProvider(deletingProvider);
        });

    /// <summary>
    /// JF-499 W3: when the fast path's cached playlist vanishes between the cache
    /// read and the serve read, the request falls through to the RE-ENCODE path
    /// instead of throwing FileNotFoundException (a 500 in flight that only
    /// self-heals on the Echo's retry).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_FastPathCacheVanishedAtServe_FallsThroughToReencode()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Vanishing S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");

        using var loggerFactory = CreateDeletingLoggerFactory("serving cached playlist for item", playlistPath);

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf499-race"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the re-encode path must have run after the vanished-cache fallthrough");
    }

    /// <summary>
    /// JF-499 W3, the audio-variant twin of the same race and fix.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_FastPathCacheVanishedAtServe_FallsThroughToReencode()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Vanishing Audio S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(39).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string cacheKey = VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), 0);
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");

        using var loggerFactory = CreateDeletingLoggerFactory("serving cached playlist for item", playlistPath);

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf499-race-audio"));

        ActionResult result = await controller.StreamHlsEpisodeAudio(episode.Id.ToString(), 0);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the re-encode path must have run after the vanished-cache fallthrough");
    }

    /// <summary>
    /// JF-499 W3, the SONG twin (added with JF-677, whose read threading is the
    /// reason the contract needs pinning per path): since JF-677 the song fast
    /// path's serve reuses the verdict's threaded content instead of re-reading,
    /// so the vanish-at-serve fall-through now rides
    /// ResolveServeContentAsync's existence probe. A probe skipped on this
    /// path (the regression class the threading enables) would serve the
    /// remembered bytes of a deleted generation directory: this pin requires
    /// the same re-encode fall-through the fresh-read era had.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_FastPathCacheVanishedAtServe_FallsThroughToReencode()
    {
        (Guid itemId, string hlsDir, string playlistPath) = SetupSongVanishFixture("Vanishing Song");
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");

        using var loggerFactory = CreateDeletingLoggerFactory("serving cached playlist for item", playlistPath);

        // The recording fake's SONG shape: 3-digit segments (seg_%03d). The
        // wait is bounded either way (WaitForFirstSegmentOrKillAsync polls a
        // ~20s budget, breaking early on process exit, and returns a clean
        // failure ActionResult), so the episode fake's 4-digit segment fails
        // this twin FAST but CLEANLY; the 3-digit shape is needed because the
        // twin's own assertions require the re-encode to actually serve
        // seg_000.ts, not merely to run.
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf677-race-song", "seg_000.ts");

        var controller = CreateController(
            itemId.ToString(), loggerFactory, ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsVideoAudio(itemId.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the re-encode path must have run after the vanished-cache fallthrough (the probe must not serve the threaded content of a deleted playlist)");
    }

    // ---- JF-678: vanish-at-serve hardening family (no-token probe, in-lock rows, audiobook rows) ----

    /// <summary>
    /// JF-678: logger provider that clears <c>StreamTokenSecret</c> the first
    /// time a message containing the trigger is logged. Mirrors
    /// <see cref="FileDeletingLoggerProvider"/>'s fire-once shape; reproduces
    /// the secret-clearing race deterministically: the route gate validates
    /// the parentId token while the secret is still set, then a config save
    /// empties it before the single-chapter redirect's re-mint reads. Since
    /// JF-682 that race ends at the route gate's own 503 (the redirect refuses
    /// to mint an empty chapter token), and the JF-682 twin uses this provider
    /// to drive the race and pin that 503.
    /// </summary>
    private sealed class SecretClearingLoggerProvider : ILoggerProvider
    {
        private readonly string _trigger;
        private int _fired;

        internal SecretClearingLoggerProvider(string trigger)
        {
            _trigger = trigger;
        }

        /// <summary>Whether the trigger fired and the secret was cleared.</summary>
        internal bool Fired => Volatile.Read(ref _fired) == 1;

        public ILogger CreateLogger(string categoryName) => new ClearingLogger(this);

        public void Dispose()
        {
        }

        private sealed class ClearingLogger : ILogger
        {
            private readonly SecretClearingLoggerProvider _owner;

            internal ClearingLogger(SecretClearingLoggerProvider owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string message = formatter(state, exception);
                if (message.Contains(_owner._trigger, StringComparison.Ordinal)
                    && Interlocked.CompareExchange(ref _owner._fired, 1, 0) == 0)
                {
                    Plugin.Instance!.Configuration.StreamTokenSecret = string.Empty;
                }
            }
        }
    }

    /// <summary>
    /// JF-678 (a) pinned the NO-TOKEN serve of a verdict-validated playlist
    /// (materialized read, re-encode fall-through); JF-682 closed that shape's
    /// production driver (the single-chapter redirect now serves the route
    /// gate's own 503 instead of minting an empty chapter token) and this twin
    /// was REWRITTEN to pin the replacement shape. Construction unchanged: the
    /// secret-clearing provider empties <c>StreamTokenSecret</c> on the
    /// single-chapter log line (after the route gate validated the parentId
    /// token, before the re-mint reads), the deleting provider stays wired on
    /// the fast-path serve log, and the 3-digit fake is kept (the redirected
    /// core is the song core). GREEN: the redirect answers the gate's own 503
    /// ("Stream token secret not configured") and the core never runs, so no
    /// re-encode starts (no args file) and the serve log never fires (both
    /// provider constructions are PINNED via their Fired flags: the clearing
    /// provider must have fired, the deleting provider must have stayed cold).
    /// RED (the
    /// JF-682 check removed, the empty-token mint back): the old no-token flow
    /// reappears and the outcome assertions flip (the clearingProvider.Fired construction assert stays green in both shapes, as it should) - the vanish fall-through re-encodes
    /// (args file exists) and the materialized fresh playlist answers as a
    /// ContentResult, not an ObjectResult. The no-token branch keeps its
    /// last-resort safety-net role for any future no-token shape; this red
    /// proof is the only construction that still reaches it.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SecretEmptiedAtChapterRemint_ServesGate503_NoReencode()
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-682 Empty-Secret Book",
            Id = parentId
        };
        var chapter = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Only Chapter", Id = Guid.NewGuid() };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        // The redirected core resolves the CHAPTER item itself (ValidateVideoAudioRequest
        // on chapterId), so both lookups must answer.
        _libraryManagerMock.Setup(m => m.GetItemById(chapter.Id)).Returns(chapter);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter });

        string hlsDir = _cache.GetHlsDirectoryPath(chapter.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXTINF:4.000,\nseg_000.ts\n#EXTINF:4.000,\nseg_001.ts\n#EXT-X-ENDLIST\n");

        var clearingProvider = new SecretClearingLoggerProvider("single chapter, serving single-item HLS inline");
        var deletingProvider = new FileDeletingLoggerProvider("serving cached playlist for item", playlistPath);
        using var loggerFactory = CreateDeletingLoggerFactory(deletingProvider, clearingProvider);

        // 3-digit song shape: the redirected core is the song core (kept from
        // the JF-678 twin so the red proof reproduces the old re-encode shape).
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf682-remint503", "seg_000.ts");

        string? originalSecret = _config.StreamTokenSecret;
        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: fakeFfmpegPath);
        try
        {
            ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

            Assert.True(
                clearingProvider.Fired,
                "the clearing provider must have fired on the single-chapter log line (the route-gate-to-remint race window was exercised)");
            ObjectResult gate503 = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, gate503.StatusCode);
            Assert.Contains("Stream token secret not configured", Body(gate503.Value!), StringComparison.Ordinal);
            Assert.False(
                File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
                "the 503 must land before the core: no re-encode may run for an empty-secret redirect");
            Assert.False(
                deletingProvider.Fired,
                "the serve log must never fire for an empty-secret redirect (the 503 precedes every serve; the deleting provider stays cold so the red-proof vanish construction stays honest)");
        }
        finally
        {
            _config.StreamTokenSecret = originalSecret;
        }
    }

    /// <summary>
    /// Arrange a one-chapter audiobook for the JF-686 redirect tests: the parent folder plus
    /// the single chapter, BOTH GetItemById lookups stubbed (the redirected core resolves
    /// the CHAPTER item itself via ValidateVideoAudioRequest on chapterId), GetItemList
    /// answering the one chapter, and a COMPLETED playlist planted as the chapter's HLS
    /// cache in the PRODUCTION shape (JF-686 review F6): #EXT-X-TARGETDURATION +
    /// #EXT-X-MEDIA-SEQUENCE headers (so the slice exercises the tag-REPLACE branch, the
    /// one production playlists take) and full <c>/alexaskill/api/video-audio/{chapterId}/segments/seg_NNN.ts</c>
    /// URIs (the <c>-hls_base_url</c> lines ffmpeg actually writes), 4s segments, ENDLIST.
    /// <paramref name="segments"/> 0 skips the planting entirely (cold cache: the encode
    /// path runs and the fake's own live partial is what serves). Returns the parent id
    /// the endpoint is called with.
    /// </summary>
    private async Task<Guid> ArrangeSingleChapterBookAsync(string bookName, int segments)
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = bookName,
            Id = parentId
        };
        var chapter = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Only Chapter", Id = Guid.NewGuid() };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemById(chapter.Id)).Returns(chapter);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter });

        if (segments <= 0)
        {
            return parentId;
        }

        string hlsDir = _cache.GetHlsDirectoryPath(chapter.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        var playlist = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < segments; i++)
        {
            playlist.Append("#EXTINF:4.000,\n/alexaskill/api/video-audio/")
                .Append(chapter.Id.ToString())
                .Append("/segments/seg_").Append(i.ToString("000", System.Globalization.CultureInfo.InvariantCulture)).Append(".ts\n");
        }

        playlist.Append("#EXT-X-ENDLIST\n");
        await File.WriteAllTextAsync(Path.Combine(hlsDir, "stream.m3u8"), playlist.ToString());

        return parentId;
    }

    /// <summary>
    /// JF-686: a resume launch on a ONE-chapter book must serve a playlist SLICED at the
    /// resume position. The builder mints <c>audiobook/{parentId}/stream.m3u8?start=&lt;ticks&gt;</c>
    /// (pinned builder-side in VideoAppCapabilityGateTests) and the multi-chapter path slices
    /// via <c>ServeAudiobookPlaylistAsync</c>, but the single-chapter redirect used to drop
    /// startTicks at <c>StreamHlsVideoAudioCore</c>: the song core served the full unsliced
    /// playlist, so "resume from 20 minutes" played from 0:00 while the device showed a fresh
    /// timeline. Construction mirrors the JF-682 twin with a COMPLETED (ENDLIST) 12-segment/4s
    /// playlist planted in the chapter's cache dir in the production shape (full segment URIs
    /// + the TARGETDURATION/MEDIA-SEQUENCE headers, JF-686 review F6) and start = 40s: the
    /// EXTINF walk resolves segment 10 so the serve must carry MEDIA-SEQUENCE:10, keep
    /// seg_010 with the re-minted chapter token, drop the seg_000..009 prefix, and preserve
    /// the ENDLIST (a completed VOD stays a completed VOD under the slice).
    /// GREEN: the redirect threads startTicks into the core and the slice-aware serve
    /// (<c>ServeVideoAudioPlaylistAsync</c>, SongHlsSegmentSeconds=4 as the flat fallback)
    /// answers the sliced playlist.
    /// RED (the redirect reverted to <c>StreamHlsVideoAudioCore(chapterId, chapterToken)</c>):
    /// the full unsliced playlist answers, the sequence tag stays :0, seg_000 rides
    /// first with the token, and the MEDIA-SEQUENCE/seg_010/prefix-drop asserts all flip.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SingleChapterResume_Start_ServesSlicedPlaylistAtOffset()
    {
        // Completed encode: 12 segments x 4s = 48s of content with ENDLIST (the shape the
        // fast-path verdict serves without touching ffmpeg).
        Guid parentId = await ArrangeSingleChapterBookAsync("JF-686 Single-Chapter Book", 12);

        // 3-digit song shape: the redirected core is the song core (kept hermetic so an
        // accidental verdict fall-through encodes instead of reaching a real ffmpeg).
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf686-slice", "seg_000.ts");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);
        long startTicks = TimeSpan.FromSeconds(40).Ticks;

        // The bound parameter is passed DIRECTLY: a direct action call runs no model
        // binder, so a QueryCollection entry would never reach it. The wire name
        // (?start=) is pinned builder-side by the resume-URL mint test.
        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString(), startTicks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        // JF-686 review F2, the honest direction: the JF-499 P2 walk serves the FIRST
        // segment whose cumulative start is >= the offset (rounds UP, at-or-after; the
        // shared live-verified multi-chapter direction). start=40s over 4s segments is
        // the exact boundary, so seg_010's true start (40s) EQUALS the offset here.
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:10", content.Content, StringComparison.Ordinal);
        Assert.Contains("segments/seg_010.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("segments/seg_009.ts", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("segments/seg_000.ts", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-686 review F2: a MID-SEGMENT resume position pins the walk's round-UP direction.
    /// start=41s over 4s segments cannot be served exactly; the JF-499 P2 walk serves the
    /// first segment whose cumulative start reaches the offset, seg_011 (true start 44s),
    /// skipping up to one segment of unheard audio rather than re-hearing it. The direction
    /// is kept DELIBERATELY (shared with the live-verified multi-chapter and episode
    /// paths; the tracker's conservative under-report, (highWaterMark-1) segments, is the
    /// re-hear margin on the other side), so this pin LOCKS it: switching the walk to
    /// round-down flips MEDIA-SEQUENCE to 11 and the seg_010 keeps. Green by construction
    /// against the current walk; its red shape is the round-down behavior.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SingleChapterResume_Start_MidSegment_RoundsUpToNextSegmentBoundary()
    {
        Guid parentId = await ArrangeSingleChapterBookAsync("JF-686 Mid-Segment Book", 12);

        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf686-midsegment", "seg_000.ts");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);
        long startTicks = TimeSpan.FromSeconds(41).Ticks;

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString(), startTicks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:11", content.Content, StringComparison.Ordinal);
        Assert.Contains("segments/seg_011.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("segments/seg_010.ts", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-686 review F1: the COLD-cache resume on a one-chapter book (no runtime, so the
    /// prewritten listing is skipped) must keep serving the FULL live partial. The
    /// still-growing playlist cannot honor a position: slicing start=40s over the fake's
    /// 8s-so-far partial lands the flat divisor past the last written segment, the emitter
    /// keeps ZERO segments, and the player dies on a header-only playlist, where
    /// pre-JF-686 the same request served the full live playlist and merely restarted at
    /// 0:00 (the benign degradation; the album twin's cold-serve drops the offset the same
    /// way). GREEN: the live-partial fallback serves both segments with the chapter token
    /// and the base sequence tag untouched.
    /// RED (the fallback reverted to passing startTicks): MEDIA-SEQUENCE:10 is injected,
    /// both segment lines vanish, and every content assert flips.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SingleChapterResume_ColdCacheLivePartial_ServesFullUnsliced()
    {
        // Cold cache: nothing planted, the encode runs and the fake's own live partial
        // (2 segments x 4s, no ENDLIST) is what serves.
        Guid parentId = await ArrangeSingleChapterBookAsync("JF-686 Cold-Cache Book", 0);

        // POSIX sh (dash) has no "${@: -1}": iterate to the last positional arg instead.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf686-livepartial",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_000.ts\" 2>/dev/null\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_001.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXT-X-TARGETDURATION:4' >> \"$playlist_path\"\n" +
            "echo '#EXT-X-MEDIA-SEQUENCE:0' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:4.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_000.ts' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:4.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_001.ts' >> \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString(), TimeSpan.FromSeconds(40).Ticks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        // Full live partial: both segments ride (token-rewritten), the base sequence tag
        // passes through untouched, and no resume slice rewrote it.
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-MEDIA-SEQUENCE:10", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-686 control: the SAME one-chapter redirect WITHOUT <c>?start=</c> must keep the
    /// pre-JF-686 byte shape (the full unsliced playlist, chapter-token rewritten, the
    /// base sequence tag untouched). Pins the boundary of the resume slice: startTicks absent
    /// means the slice-aware serve delegates to <c>ServePlaylistWithTokenAsync</c>
    /// unchanged, so the song route and every position-less single-chapter play are
    /// untouched. GREEN before AND after the fix (the no-overblock control).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SingleChapterResume_NoStart_ServesUnslicedPlaylist()
    {
        Guid parentId = await ArrangeSingleChapterBookAsync("JF-686 No-Start Book", 2);

        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf686-nostart", "seg_000.ts");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        // Unsliced: both segments ride with the token, and the base sequence tag passes
        // through exactly as planted (a slice would have rewritten it to the start index).
        Assert.Contains("segments/seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("segments/seg_001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-685: the committed re-drive of the JF-678 (a) pin the JF-682 twin
    /// rewrite orphaned. Since JF-682 no PUBLIC construction reaches
    /// <c>ServePlaylistWithTokenAsync</c>'s no-token branch (every public HLS
    /// entry is gate-gated 401/503, and the single-chapter redirect's
    /// empty-token shape serves the gate's own 503), so the pin drives the
    /// token-free <c>StreamHlsVideoAudioCore</c> seam (JF-685,
    /// InternalsVisibleTo, visibility only) with a request query that carries
    /// NO token: every serve inside the core then takes the no-token branch.
    /// Construction: a completed (ENDLIST) cache planted, the deleting
    /// provider on the fast-path serve log ("serving cached playlist for
    /// item", fired AFTER the verdict's read, BEFORE the serve), and the
    /// 3-digit song-shape fake. GREEN: the no-token serve of the
    /// verdict-threaded content probes the now-vanished path and throws the
    /// vanish exception at ACTION time, the fast-path translation falls
    /// through to the re-encode (episode-args.txt), and the post-encode
    /// no-token serve of the FRESH playlist answers a MATERIALIZED
    /// ContentResult with the raw (token-less) bytes - never a raw
    /// PhysicalFile riding over a path. The core performed exactly TWO full
    /// reads through ReadPlaylistContentAsync: the verdict's validating read
    /// and the post-encode fresh read;
    /// the vanish serve itself consumed the threaded content probe-only (the
    /// JF-677 one-read invariant survives on the no-token branch). The
    /// translation log's "vanished or became unreadable" wording is pinned
    /// too (the honesty contract of the ProbePlaylistExists conflation
    /// decision; this test re-established it after the JF-678(a) original
    /// was orphaned).
    /// RED (the branch reverted to the pre-JF-678 raw PhysicalFile): the
    /// vanish probe is gone, the fast-path serve "succeeds" with a
    /// PhysicalFileResult over the DELETED path (the filed JF-678(a) bug: a
    /// 500 at RESULT EXECUTION), no re-encode runs, no vanish log fires, and
    /// the count drops to the verdict's single read - the ContentResult,
    /// vanish-log, args-file, and read-count asserts all flip (the deleting
    /// provider's Fired construction assert stays green by design, the
    /// JF-682 twin's red note).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudioCore_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode()
    {
        (Guid itemId, string hlsDir, string playlistPath) = SetupSongVanishFixture("JF-685 No-Token Vanish Song");
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        var deletingProvider = new FileDeletingLoggerProvider("serving cached playlist for item", playlistPath);
        using var loggerFactory = CreateDeletingLoggerFactory(deletingProvider, TestCaptureLogger.Into(logRecords));

        // 3-digit song shape: the re-encode must actually serve for the
        // content assert (the JF-499/JF-677/JF-682 song twins' rationale).
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf685-notoken", "seg_000.ts");

        // itemIdForToken null: the query carries NO token, so the core's
        // serves (fast path AND post-encode) take the no-token branch.
        var controller = CreateController(null, loggerFactory, ffmpegPath: fakeFfmpegPath);

        int reads = 0;
        controller.PlaylistContentReadForTest = path =>
        {
            if (path == playlistPath)
            {
                reads++;
            }
        };

        ActionResult result = await controller.StreamHlsVideoAudioCore(itemId.ToString());

        Assert.True(
            deletingProvider.Fired,
            "the deleting provider must have fired on the fast-path serve log (the verdict-to-serve race window was exercised)");
        // The vanish translation's honesty wording (the conflation decision:
        // the log must not claim a pure vanish when File.Exists conflates an
        // ACL revocation with a delete).
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("vanished or became unreadable", StringComparison.Ordinal));
        var content = Assert.IsType<ContentResult>(result);
        // The MATERIALIZED no-token serve: the fresh playlist's raw bytes,
        // unrewritten (a token-less serve appends no ?token= - the inverse of
        // the tokened twins' seg_000.ts?token= assert).
        Assert.Contains("seg_000.ts", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the re-encode path must have run after the no-token vanish fallthrough (the probe must not let a raw PhysicalFile ride over a deleted playlist)");
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// JF-678 (b), the song path's in-lock verdict+serve row: the playlist a
    /// verdict just validated vanishing BETWEEN the verdict and the serve must
    /// fall through to the row's own encode branch (inside the same lock), not
    /// propagate a FileNotFoundException (the bare 500 the row let through
    /// before this task). Construction: the JF-677
    /// <see cref="ServeInLockWarmCacheAsync"/> core (fast path parked on the
    /// lock, warm cache planted, lock released so the in-lock double check
    /// hits it) plus the deleting provider on the row's own serve log, which
    /// fires after the verdict's read and before the serve probe, exactly the
    /// verdict-to-serve window. RED (row un-wrapped): the FNF faults the
    /// endpoint task and the await rethrows it (the in-flight 500 shape).
    /// JF-700: the controller is passed to the helper, so the in-lock
    /// attribution is deterministic (the probe assert, not the park window
    /// alone; red proof: removing the LockHlsItemAsync invoke flips this pin).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_InLockCacheVanishedAtServe_FallsThroughToReencode()
    {
        (Guid itemId, string hlsDir, string playlistPath) = SetupSongVanishFixture("JF-678 In-Lock Vanish Song");

        using var loggerFactory = CreateDeletingLoggerFactory("serving playlist generated by concurrent request", playlistPath);

        // 3-digit song shape: the fall-through RE-ENCODES, so the fake's segment
        // must match the song path's seg_000.ts first-segment wait for the twin's
        // own content assert to hold (the JF-499 song twin's rationale).
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-inlock-song", "seg_000.ts");

        var controller = CreateController(itemId.ToString(), loggerFactory, ffmpegPath: fakeFfmpegPath);

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(itemId.ToString("D"), 0),
            () => controller.StreamHlsVideoAudio(itemId.ToString()),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");
            },
            controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the in-lock row must fall through to its own encode branch when the playlist vanishes between verdict and serve, not propagate the FileNotFoundException");
    }

    /// <summary>
    /// JF-678 (b), the episode path's in-lock verdict+serve row. Same
    /// construction and red proof as the song twin; the re-encode's first
    /// serve is the episode pre-write (45min runtime -> full listing), so the
    /// content assert rides the prewrite's tokened first segment. JF-700:
    /// deterministic in-lock attribution (controller passed; see the song
    /// twin).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_InLockCacheVanishedAtServe_FallsThroughToReencode()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-678 In-Lock Vanish S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        using var loggerFactory = CreateDeletingLoggerFactory("serving playlist generated by concurrent request", playlistPath);

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-inlock-episode"));

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(episode.Id.ToString(), 0),
            () => controller.StreamHlsEpisode(episode.Id.ToString()),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
            },
            controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the in-lock row must fall through to its own encode branch when the playlist vanishes between verdict and serve, not propagate the FileNotFoundException");
    }

    /// <summary>
    /// JF-678 (b), the audio-variant path's in-lock verdict+serve row (keyed
    /// by the variant cache key). Same construction and red proof as the song
    /// twin; the re-encode serves ffmpeg's live partial (variants have no
    /// prewrite by the JF-536 scope decision). JF-700: deterministic in-lock
    /// attribution (controller passed; see the song twin).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_InLockCacheVanishedAtServe_FallsThroughToReencode()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "JF-678 In-Lock Vanish Audio S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(39).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string cacheKey = VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), 0);
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        using var loggerFactory = CreateDeletingLoggerFactory("serving playlist generated by concurrent request", playlistPath);

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-inlock-audio"));

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(cacheKey, 0),
            () => controller.StreamHlsEpisodeAudio(episode.Id.ToString(), 0),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
            },
            controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the in-lock row must fall through to its own encode branch when the playlist vanishes between verdict and serve, not propagate the FileNotFoundException");
    }

    /// <summary>
    /// JF-678 shared fixture of the three audiobook vanish twins: a two-chapter
    /// book (chapters carry 10-minute runtimes so a fall-through re-encode's
    /// pre-write lists real segments for the content asserts) with the
    /// library mocks wired. No EncoderPath setup, same JF-759 reasoning as
    /// <see cref="SetupEpisodeForHls"/>. Returns the parent id and the warm-cache
    /// paths; each twin plants the playlist itself (the in-lock twin plants it
    /// through the lock-park core, the others eagerly).
    /// </summary>
    private (Guid ParentId, string HlsDir, string PlaylistPath) SetupAudiobookVanishFixture(string bookName)
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = bookName,
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        return (parentId, hlsDir, Path.Combine(hlsDir, "stream.m3u8"));
    }

    /// <summary>
    /// JF-692 shared fixture of the four song-path vanish/read-count pins
    /// (<see cref="SetupAudiobookVanishFixture"/>'s sibling): a mock Audio
    /// item with the library lookups wired (no EncoderPath setup, same
    /// JF-759 reasoning as the sibling), returning the item id and
    /// the warm-cache paths. Each pin plants the playlist itself (the in-lock
    /// pin plants it through the lock-park core, the others eagerly) with its
    /// own bytes and wires its own logger factory: the four constructions
    /// diverge on the provider trigger, the planted bytes, the read-count
    /// wiring and the lock choreography, so only the item+mock+path arrange
    /// is shared. The arrange is all three vanish pins exercise in common;
    /// the fourth member (the JF-677 read-count twin) is a CLEAN cache-hit
    /// pin that never vanishes anything and shares only this arrange, kept in
    /// the family for its identical fixture shape. Song family only: the
    /// episode/episode-audio vanish twins arrange the item through
    /// <see cref="SetupEpisodeForHls"/> and the EpisodeAudioCacheKey, so they
    /// cannot ride this helper.
    /// </summary>
    private (Guid ItemId, string HlsDir, string PlaylistPath) SetupSongVanishFixture(string songName)
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = songName,
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        return (audioItem.Id, hlsDir, Path.Combine(hlsDir, "stream.m3u8"));
    }

    /// <summary>
    /// JF-678 (c), the audiobook FAST-PATH verdict row: the vanish must reach
    /// the translation (the row previously fell into
    /// ServeAudiobookPlaylistAsync's generic catch, whose PhysicalFile
    /// fallback 500s at result execution over the dead path). GREEN: null from
    /// the translation falls through the concurrent-encode guard to the lock
    /// path, which re-encodes. RED (catch unfiltered again, or the row
    /// un-wrapped): the generic catch answers PhysicalFile over the deleted
    /// path and IsType&lt;ContentResult&gt; fails, which is the filed red shape.
    /// Chapters carry runtimes so the re-encode's pre-write lists real
    /// segments for the content assert.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_FastPathCacheVanishedAtServe_FallsThroughToReencode()
    {
        (Guid parentId, string hlsDir, string playlistPath) = SetupAudiobookVanishFixture("JF-678 Fast-Path Vanish Book");
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");

        using var loggerFactory = CreateDeletingLoggerFactory("serving cached playlist for parent", playlistPath);

        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-fast-book"));

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the audiobook fast-path row must fall through to the lock+re-encode path when the playlist vanishes between verdict and serve, not answer PhysicalFile over the deleted playlist");
    }

    /// <summary>
    /// JF-678 (c), the audiobook IN-LOCK verdict row: same vanish treatment as
    /// the fast-path twin, falling through to this scope's own concat+encode
    /// branch. Construction: the JF-677 in-lock core plus the deleting
    /// provider on the row's serve log. RED: the generic catch answers
    /// PhysicalFile over the deleted path. JF-700: deterministic in-lock
    /// attribution (controller passed; see the song twin).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_InLockCacheVanishedAtServe_FallsThroughToReencode()
    {
        (Guid parentId, string hlsDir, string playlistPath) = SetupAudiobookVanishFixture("JF-678 In-Lock Vanish Book");

        using var loggerFactory = CreateDeletingLoggerFactory("serving playlist generated by concurrent request", playlistPath);

        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-inlock-book"));

        ActionResult result = await ServeInLockWarmCacheAsync(
            () => _cache.LockItemAsync(parentId.ToString(), 0),
            () => controller.StreamHlsAudiobook(parentId.ToString()),
            () =>
            {
                Directory.CreateDirectory(hlsDir);
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
            },
            controller);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the audiobook in-lock row must fall through to its own concat+encode branch when the playlist vanishes between verdict and serve, not answer PhysicalFile over the deleted playlist");
    }

    /// <summary>
    /// JF-678 (c), the RESUME half of the audiobook content branch
    /// (<see cref="VideoAudioController"/> ServeResumePlaylistAsync): the
    /// catch-to-PhysicalFile fallback must not swallow a vanish either, or a
    /// resume serve over a deleted cache answers PhysicalFile over the dead
    /// path (a 500 at result execution). Drives the fast-path row with a
    /// positive ?start so the serve routes through the resume builder; GREEN:
    /// the vanish falls through to the lock+re-encode, whose pre-write serve
    /// is itself resume-sliced at the same offset (segment 1 onward at the
    /// 10-second arithmetic). RED: PhysicalFile over the deleted path.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_FastPathCacheVanishedAtServe_WithResume_FallsThroughToReencode()
    {
        (Guid parentId, string hlsDir, string playlistPath) = SetupAudiobookVanishFixture("JF-678 Resume Vanish Book");
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            playlistPath,
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");

        using var loggerFactory = CreateDeletingLoggerFactory("serving cached playlist for parent", playlistPath);

        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-resume-book"));

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString(), TimeSpan.FromSeconds(10).Ticks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0000.ts", content.Content, StringComparison.Ordinal);
        Assert.True(
            File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
            "the resume serve must fall through to the lock+re-encode path when the playlist vanishes between verdict and serve, not answer PhysicalFile over the deleted playlist");
    }

    // ---- JF-678 rework: the in-lock breach guard (F1) and the flush-lag boundary (F3) ----

    /// <summary>
    /// JF-678 rework F1, the breach shape of the in-lock vanish fall-through:
    /// when the playlist vanishes between verdict and serve WHILE a generation
    /// of the key is STILL LIVE (the seam's own-live marking, the JF-680
    /// construction), the vanish is a JF-428 pin breach, and the row must FAIL
    /// LOUD (the action-time FileNotFoundException plus the breach-named
    /// warning) instead of falling through into a SECOND encode against the
    /// live writer's directory. GREEN: ThrowsAsync(FileNotFoundException), the
    /// breach log fires, and episode-args.txt does NOT exist (no re-encode
    /// ran). RED (the site's GuardInLockVanishFallThrough call removed): the
    /// fall-through runs the encode, the request answers 200, and ThrowsAsync
    /// fails with no exception thrown. Four per-path twins below share this
    /// construction (episode, song, audio-variant, audiobook); each red proof
    /// removes only its own site's guard call. JF-700: all four twins pass the
    /// controller to the helper (deterministic in-lock attribution even on
    /// the faulting path; the helper asserts the probe fired before
    /// rethrowing); red proof: removing the LockHlsItemAsync invoke flips
    /// these four on the ThrowsAsync type-mismatch failure that embeds
    /// InLockProbeNotFiredMessage (plus the endpoint's real fault).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_InLockLiveGenerationVanishAtServe_FailsLoudNoReencode()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-678 Breach S01E01", "h264", TimeSpan.FromMinutes(45));

        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = CreateDeletingLoggerFactory(
            "serving playlist generated by concurrent request", playlistPath, TestCaptureLogger.Into(logRecords));

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-breach-episode"));

        // The own-live marking (the JF-680 construction: no encode runs, so
        // nothing displaces it; the guard reads it as the live writer).
        VideoAudioController.SetEncodeActiveForTest(episode.Id.ToString(), active: true);
        try
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => ServeInLockWarmCacheAsync(
                    () => _cache.LockItemAsync(episode.Id.ToString(), 0),
                    () => controller.StreamHlsEpisode(episode.Id.ToString()),
                    () =>
                    {
                        Directory.CreateDirectory(hlsDir);
                        File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
                    },
                    controller));

            Assert.Contains("live pinned encode", ex.Message, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("failing the request loud instead of starting a second encode", StringComparison.Ordinal));
            Assert.False(
                File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
                "a breach vanish must NOT fall through to a second encode against the still-live writer's directory");
            Assert.Equal(
                1,
                VideoAudioController.EncodeGenerationCountForTest(episode.Id.ToString()));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(episode.Id.ToString(), active: false);
        }
    }

    /// <summary>
    /// JF-678 rework F1, the song-path breach twin (the single-item registry).
    /// Same construction and red proof as the episode twin; the 3-digit fake
    /// is the song-path shape so the RED run (guard removed) completes a real
    /// re-encode.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_InLockLiveGenerationVanishAtServe_FailsLoudNoReencode()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "JF-678 Breach Song",
            Id = Guid.NewGuid()
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = CreateDeletingLoggerFactory(
            "serving playlist generated by concurrent request", playlistPath, TestCaptureLogger.Into(logRecords));

        var controller = CreateController(
            audioItem.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-breach-song", "seg_000.ts"));

        VideoAudioController.SetEncodeActiveForTest(audioItem.Id.ToString(), active: true, song: true);
        try
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => ServeInLockWarmCacheAsync(
                    () => _cache.LockItemAsync(audioItem.Id.ToString("D"), 0),
                    () => controller.StreamHlsVideoAudio(audioItem.Id.ToString()),
                    () =>
                    {
                        Directory.CreateDirectory(hlsDir);
                        File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_000.ts\n#EXT-X-ENDLIST\n");
                    },
                    controller));

            Assert.Contains("live pinned encode", ex.Message, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("failing the request loud instead of starting a second encode", StringComparison.Ordinal));
            Assert.False(
                File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
                "a breach vanish must NOT fall through to a second encode against the still-live writer's directory");
            Assert.Equal(
                1,
                VideoAudioController.EncodeGenerationCountForTest(audioItem.Id.ToString(), song: true));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(audioItem.Id.ToString(), active: false, song: true);
        }
    }

    /// <summary>
    /// JF-678 rework F1, the audio-variant breach twin (keyed by the variant
    /// cache key in the episode registry). Same construction and red proof as
    /// the episode twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_InLockLiveGenerationVanishAtServe_FailsLoudNoReencode()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "JF-678 Breach Audio S01E01",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(39).Ticks
        };
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string cacheKey = VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), 0);
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = CreateDeletingLoggerFactory(
            "serving playlist generated by concurrent request", playlistPath, TestCaptureLogger.Into(logRecords));

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-breach-audio"));

        VideoAudioController.SetEncodeActiveForTest(cacheKey, active: true);
        try
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => ServeInLockWarmCacheAsync(
                    () => _cache.LockItemAsync(cacheKey, 0),
                    () => controller.StreamHlsEpisodeAudio(episode.Id.ToString(), 0),
                    () =>
                    {
                        Directory.CreateDirectory(hlsDir);
                        File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
                    },
                    controller));

            Assert.Contains("live pinned encode", ex.Message, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("failing the request loud instead of starting a second encode", StringComparison.Ordinal));
            Assert.False(
                File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
                "a breach vanish must NOT fall through to a second encode against the still-live writer's directory");
            Assert.Equal(
                1,
                VideoAudioController.EncodeGenerationCountForTest(cacheKey));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(cacheKey, active: false);
        }
    }

    /// <summary>
    /// JF-678 rework F1, the audiobook breach twin (the audiobook registry;
    /// the vanish rides the content branch's confirmed-path filter, so the
    /// twin also proves the filter still propagates the vanish to the guard).
    /// Same construction and red proof as the episode twin.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_InLockLiveGenerationVanishAtServe_FailsLoudNoReencode()
    {
        (Guid parentId, string hlsDir, string playlistPath) = SetupAudiobookVanishFixture("JF-678 Breach Book");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = CreateDeletingLoggerFactory(
            "serving playlist generated by concurrent request", playlistPath, TestCaptureLogger.Into(logRecords));

        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf678-breach-book"));

        try
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(
                () => ServeInLockWarmCacheAsync(
                    () => _cache.LockItemAsync(parentId.ToString(), 0),
                    () => controller.StreamHlsAudiobook(parentId.ToString()),
                    () =>
                    {
                        // The live marking happens INSIDE the park window, not
                        // before the endpoint starts: the audiobook flow's
                        // registry-presence guard runs BEFORE the lock and
                        // would 503 the parked-outside construction.
                        VideoAudioController.SetEncodeActiveForTest(parentId.ToString(), active: true, audiobook: true);
                        Directory.CreateDirectory(hlsDir);
                        File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
                    },
                    controller));

            Assert.Contains("live pinned encode", ex.Message, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("failing the request loud instead of starting a second encode", StringComparison.Ordinal));
            Assert.False(
                File.Exists(Path.Combine(hlsDir, "episode-args.txt")),
                "a breach vanish must NOT fall through to a second encode against the still-live writer's directory");
            Assert.Equal(
                1,
                VideoAudioController.EncodeGenerationCountForTest(parentId.ToString(), audiobook: true));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(parentId.ToString(), active: false, audiobook: true);
        }
    }

    /// <summary>
    /// JF-678 rework F3, the FLUSH-LAG boundary: the album FIRST-FETCH live
    /// serve reads stream.m3u8 with NO prior existence evidence (the
    /// first-segment wait polls seg_0000.ts, never the playlist), so a read
    /// miss one flush cycle before ffmpeg's first playlist write must keep
    /// the generic catch's PhysicalFile DEGRADE, not surface as an action-time
    /// vanish 500. Construction: a MusicAlbum parent whose fake ffmpeg writes
    /// the first segment but NEVER the playlist, so the post-wait serve reads
    /// a missing file. GREEN: PhysicalFileResult (the degrade), no exception.
    /// RED (the site forced confirmed / the filter unscoped): the FNF
    /// propagates and the await throws.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AlbumFirstFetch_PlaylistNotYetFlushed_DegradesNotVanish500()
    {
        Guid parentId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Name = "JF-678 Flush-Lag Album",
            Id = parentId
        };
        var track1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Track 1",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
        };
        var track2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Track 2",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(album);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { track1, track2 });

        // Writes the first SEGMENT only: the first-segment wait passes, the
        // playlist read one flush cycle later still misses.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf678-flushlag",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(TestCaptureLogger.Into(logRecords));
        });

        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        // The DEGRADE row itself must have produced this (the token branch's
        // generic catch warning), not some other PhysicalFile return path:
        // the boundary can rot while the pin stays green otherwise.
        Assert.IsType<PhysicalFileResult>(result);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("Failed to rewrite audiobook playlist with token", StringComparison.Ordinal));
    }

    // ---- W4: permission-denied deletes must not surface as 500s ----

    private const UnixFileMode WritableDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode ReadOnlyDirMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;

    /// <summary>
    /// JF-499 W4 shared shape: run the act inside a directory whose mode denies
    /// writes (unlink needs write permission on the directory), always restoring the
    /// mode so the fixture's recursive temp cleanup still works.
    /// </summary>
    private static void AssertSurvivesDeniedDirectory(string dir, Action act)
    {
#pragma warning disable CA1416, CA3003 // Unix-only test; test-created path
        File.SetUnixFileMode(dir, ReadOnlyDirMode);
        try
        {
            var ex = Record.Exception(act);
            Assert.Null(ex);
        }
        finally
        {
            File.SetUnixFileMode(dir, WritableDirMode);
        }
#pragma warning restore CA1416, CA3003
    }

    /// <summary>
    /// JF-499 W4: CleanupHlsStub's whole-directory delete on a permission-denied dir
    /// must be swallowed (Debug log), not propagate out of the playlist fast path.
    /// </summary>
    [Fact]
    public void CleanupHlsStub_DeniedDirectory_DoesNotThrow()
    {
        string itemId = Guid.NewGuid().ToString();
        string hlsDir = _cache.GetHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(hlsDir);
        File.WriteAllText(Path.Combine(hlsDir, "seg_0000.ts"), "x");

        AssertSurvivesDeniedDirectory(hlsDir, () => _cache.CleanupHlsStub(itemId, 0));
    }

    /// <summary>
    /// JF-499 W4: Cleanup (the debris invalidation the episode paths call) must
    /// swallow UnauthorizedAccessException for both its file and directory deletes.
    /// </summary>
    [Fact]
    public void Cleanup_DeniedDirectory_DoesNotThrow()
    {
        string itemId = Guid.NewGuid().ToString();
        string hlsDir = _cache.GetHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(hlsDir);
        File.WriteAllText(Path.Combine(hlsDir, "seg_0000.ts"), "x");

        AssertSurvivesDeniedDirectory(hlsDir, () => _cache.Cleanup(itemId));
    }

    /// <summary>
    /// JF-499 W4: DeleteStubIfPresent's stub delete inside a write-denied directory
    /// must be swallowed.
    /// </summary>
    [Fact]
    public void DeleteStubIfPresent_DeniedDirectory_DoesNotThrow()
    {
        string itemId = Guid.NewGuid().ToString();
        string cachePath = _cache.GetCacheFilePath(itemId, 123);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath, "stub"); // < MinValidFileSize: the stub branch

        AssertSurvivesDeniedDirectory(Path.GetDirectoryName(cachePath)!, () => _cache.DeleteStubIfPresent(itemId, 123));
    }

    /// <summary>
    /// JF-499 W4 (and the JF-531 implementation note): the TryDelete helper is
    /// reachable from play paths (the no-runtime stale-listing delete, the failed
    /// remux faststart cleanup); a permission-denied path must be swallowed instead
    /// of 500ing the play.
    /// </summary>
    [Fact]
    public void TryDelete_DeniedDirectory_DoesNotThrow()
    {
        string dir = Path.Combine(_tempDir, "jf499-trydelete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "stale.m3u8");
        File.WriteAllText(target, "#EXTM3U\n");

        AssertSurvivesDeniedDirectory(dir, () => VideoAudioController.TryDelete(target));
    }
}
