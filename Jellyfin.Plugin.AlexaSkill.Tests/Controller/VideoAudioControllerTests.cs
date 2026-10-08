using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
/// Tests for the VideoAudioController, the MP4 video generation endpoint
/// that combines album art + audio via ffmpeg for Alexa Echo Show VideoApp playback.
/// JF-792: the per-instance fixture lives on <see cref="VideoAudioControllerTestHarness"/>;
/// this class keeps every family that needs the shared static surface (the
/// ensured Plugin.Instance whose secret mints stream tokens, the encode gate,
/// the live-encode registries, the JF-731 teardown backstop, the ServeInLock IL
/// pins) and therefore stays a Plugin-collection member, serialized against all
/// other static-touching classes and never overlapped by the parallel phase.
/// The static-free families moved to <see cref="VideoAudioControllerPureTests"/>:
/// a NEW test that touches none of that shared static surface (transitively,
/// helpers included) belongs THERE, in the parallel phase.
/// </summary>
[Collection("Plugin")]
public class VideoAudioControllerTests : VideoAudioControllerTestHarness
{
    private readonly PluginConfiguration _config;

    public VideoAudioControllerTests()
    {
        // The former PluginTestBase inheritance, whose base slot the harness took
        // (JF-792): the ONE reset sequence keeps its single owner, called from here.
        PluginTestBase.ResetSharedStatics();

        EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            cfg => { },
            "video-audio-test");

        _config = Plugin.Instance!.Configuration;
        _config.ServerAddress = "http://localhost:8096";
    }

    public override void Dispose()
    {
        // JF-731 class-level backstop: every live encode this test may have
        // left dies HERE, once, before the temp-dir delete (the pid-file half
        // of the sweep needs the cache tree on disk).
        (bool cleanedUpAfterThisTest, string detail) = KillLeftoverEncodesAndDrainGate();

        _config.ServerAddress = string.Empty;

        // The harness base owns the recursive temp-dir delete (best-effort,
        // IOException-swallowed) plus GC.SuppressFinalize.
        base.Dispose();

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
        _libraryManagerMock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemId);
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(itemId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        string? ffmpegPath = null,
        VideoAudioCache? cache = null,
        Guid[]? tokenScope = null)
    {
        // cache: a FRESH cache instance over the same cache path simulates the
        // post-restart state (no in-memory directory registration) for the
        // scan-fallback pins (JF-774 finding 1); null shares the fixture cache.
        VideoAudioCache resolvedCache = cache ?? _cache;
        var controller = mediaSourceManager == null
            ? new VideoAudioController(
                _libraryManagerMock.Object,
                _mediaEncoderMock.Object,
                resolvedCache,
                loggerFactory ?? _loggerFactory)
            : new VideoAudioController(
                _libraryManagerMock.Object,
                _mediaEncoderMock.Object,
                resolvedCache,
                loggerFactory ?? _loggerFactory,
                mediaSourceManager.Object);
        if (ffmpegPath != null)
        {
            controller.FfmpegPath = ffmpegPath;
        }

        // Attach an HttpContext so ValidateStreamToken can read the query string.
        // When itemIdForToken is provided, mint a valid token for that item so the request passes
        // the token check. When null, no token is set (simulates a bare-GUID attack).
        // tokenScope (JF-767): mint the library-scoped form a restricted user's launch
        // would present instead of the legacy two-field one.
        var query = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        if (itemIdForToken != null && !string.IsNullOrEmpty(_config.StreamTokenSecret))
        {
            string token = StreamTokenHelper.MintScoped(itemIdForToken, _config.StreamTokenSecret, tokenScope);
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

        _libraryManagerMock.Setup(m => m.GetItemById(folder.Id)).Returns(folder);

        var controller = CreateController(folder.Id.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(folder.Id.ToString());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
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

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        // COMPLETED concat cache: ENDLIST with at least one segment per chapter
        // (the undercount hook's bar), so the verdict validates on its read row.
        // JF-784: the timeline verdict also reads the encode-metadata sidecar;
        // seeded to the matching (2 chapters, RunTimeTicks unset => 0) timeline
        // so the read-count contract under test stays the discriminator.
        string playlistPath = await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 2, encodedChapterCount: 2, encodedDurationTicks: 0);

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
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // JF-765: explicit fake ffmpeg. Entry validation 503s on an UNRESOLVED
        // ffmpeg before the in-lock probe can run, so this flow reads the ffmpeg
        // resolution but never executes it (the marker-invisible consumer class:
        // an execution-counting marker probe cannot see it; caught by a
        // no-ffmpeg-on-PATH class run). A bare exit-0 stub suffices.
        var controller = CreateController(audioItem.Id.ToString(), ffmpegPath: WriteFakeFfmpeg("fake-ffmpeg-jf681-probe", "exit 0\n"));
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
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        string cacheKey = VideoAudioController.EpisodeAudioCacheKey(episode.Id.ToString(), 0);
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
                // 2 segments >= 2 chapters: the undercount hook's bar. JF-784:
                // the matching encode-metadata sidecar (2 chapters, RunTimeTicks
                // unset => 0), or the timeline verdict would invalidate the
                // plant and re-encode instead of serving it.
                File.WriteAllText(playlistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:10.000,\nseg_0000.ts\n#EXTINF:10.000,\nseg_0001.ts\n#EXT-X-ENDLIST\n");
                WriteEncodeMetadata(hlsDir, chapterCount: 2, durationTicks: 0);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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

    // ========== Audiobook HLS Tests ==========

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

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemById(chapterId)).Returns(chapterItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapterItem });

        // JF-765: explicit fake ffmpeg, 3-digit first segment because the redirect
        // lands in the single-item SONG core (its first-segment wait polls
        // seg_000.ts). Before this the controller carried no FfmpegPath override,
        // so the encode resolved the AMBIENT /usr/bin/ffmpeg (via the EncoderPath
        // mock or the PATH scan) and failed against the unreachable stream URL,
        // tolerated by an IsNotType<NotFound> assert (the JF-759 marker probe's
        // one live consumer). The fake makes the test hermetic (no ambient binary,
        // no EncoderPath setup) and the serve observable.
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-single-chapter-redirect", "seg_000.ts");
        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        // Single chapter redirects to the single-item HLS core (not the concat
        // path): with the fake encoding successfully, the redirect serves the
        // playlist itself, so a "no chapters" 404 AND a failed encode both red.
        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", contentResult.ContentType);
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

    // ========== JF-498: episode HLS remux (static-vs-HLS routing for video items) ==========

    /// <summary>A bare-GUID episode playlist request with no token must be rejected (401), like every other stream endpoint.</summary>
    [Fact]
    public async Task StreamHlsEpisode_NoToken_Returns401()
    {
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
    /// Fake ffmpeg for the run-counter pins (JF-537.1 arrange, JF-774 finding 3):
    /// appends one line per invocation to the run counter (so a test can prove a
    /// replay never started ffmpeg), then writes the first 4-digit segment and the
    /// playlist, exiting 0. The playlist carries ENDLIST for the completed-encode
    /// shape and omits it for the live/killed-partial shape.
    /// </summary>
    private string WriteRunCountingFakeFfmpeg(string name, string runCounterPath, bool endlist)
    {
        string playlist = "#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n"
            + (endlist ? "#EXT-X-ENDLIST\\n" : string.Empty);
        return WriteFakeFfmpeg(name,
            $"printf 'run\\n' >> \"{runCounterPath}\"\n" +
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            $"printf '{playlist}' > \"$last_arg\"\n" +
            "exit 0\n");
    }

    /// <summary>
    /// The FLUSH-LAG degrade fake (extracted at the third byte-identical copy,
    /// JF-763): writes the FIRST SEGMENT only, never the playlist, so the
    /// first-segment wait passes while the playlist read one flush cycle later
    /// still misses. This is the PhysicalFile-degrade shape the JF-678 flush-lag
    /// pin and the JF-763 fold-in pins share. Do NOT switch these to
    /// <see cref="WriteRecordingFakeFfmpeg"/>: that one also writes the playlist
    /// and would flip the degrade into a full serve.
    /// </summary>
    private string WriteFlushLagFakeFfmpeg(string name)
        => WriteFakeFfmpeg(name,
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "exit 0\n");

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

    // ========== JF-500: episode HLS video transcode tier (hevc/av1 sources) ==========

    /// <summary>
    /// JF-537.1 RED PROOF (ran red on the unmodified base, both TFMs, before the
    /// fix landed): an oversize transcode encode (2h HEVC = 6144MB estimate vs a
    /// 2MB cap) must go to the TRANSIENT root and must NOT evict the real cache.
    /// The seeded real cache holds three 512KB entries (1.5MB total): above the
    /// 1MB half-cap floor, under the 2MB cap. On the base the oversize leg
    /// reserved 6144MB of headroom, the sweep's target floored at half the cap,
    /// and it EVICTED seeded entries for an encode that could never be retained;
    /// the encode also landed in the capped cache root. With the transient mode:
    /// headroom 0 (the sweep's post-encode shape) keeps every seed, the encode
    /// dir is under {cache}/transient/, the pin covers that dir, the transcode
    /// tier ran as usual, and the serve works. Also asserts the decision log
    /// (the JF-537 churn Warning's replacement) and that the pin the encode
    /// holds is the TRANSIENT dir (the JF-428 protocol stays intact, reaper-aware).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_OversizeEncode_GoesTransient_RealCacheSurvives_StillServes()
    {
        (var episode, var mediaSourceManager) = CreateOversizeHevcEpisode("jf5371-oversize");
        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf5371-oversize");

        // Seed the REAL cache: three 512KB entries (1.5MB total).
        var seededPaths = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            string seedPath = _cache.GetCacheFilePath($"00000000-0000-0000-0000-{i:x012}", 1);
            Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
            File.WriteAllBytes(seedPath, new byte[512 * 1024]);
            seededPaths.Add(seedPath);
        }

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        string? observedPinPath = null;
        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 2;
        try
        {
            var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath, loggerFactory);
            controller.FfmpegProcessStartedForTest = pinPath => observedPinPath = pinPath;

            ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

            // (a) The encode was NOT refused or rerouted: the playlist is served.
            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);

            // (b) The encode landed in the TRANSIENT root (transcode args recorded
            // there) and NOTHING sits at the key's cache-root dir. The dir is
            // hand-built (not GetTransientHlsDirectoryPath) so this test also
            // compiles against the pre-JF-537.1 base, where this exact file ran
            // red on both TFMs; keep it base-compilable.
            string transientDir = Path.Combine(_cache.CacheDir, "transient", $"{episode.Id}_0");
            Assert.True(Directory.Exists(transientDir), $"the oversize encode must target the transient root ({transientDir})");
            string[] tokens = File.ReadAllLines(Path.Combine(transientDir, "episode-args.txt"));
            Assert.Equal("libx264", tokens[Array.IndexOf(tokens, "-c:v") + 1]);
            Assert.False(
                Directory.Exists(_cache.GetHlsDirectoryPath(episode.Id.ToString(), 0)),
                "the oversize encode must not write into the capped cache root");

            // (c) The real cache SURVIVED the pre-encode sweep: every seeded
            // entry is still on disk.
            foreach (string seed in seededPaths)
            {
                Assert.True(File.Exists(seed), $"the oversize encode's pre-encode sweep must not evict the real cache ({seed} was deleted)");
            }

            // (d) The transient-mode decision is announced (the JF-537 churn
            // Warning's replacement) with item, estimate, and cap.
            var decisions = TestCaptureLogger.Snapshot(logRecords)
                .Where(r => r.Message.Contains("transient root", StringComparison.Ordinal))
                .ToList();
            Assert.Single(decisions);
            Assert.Equal(LogLevel.Information, decisions[0].Level);
            Assert.Contains(episode.Id.ToString(), decisions[0].Message, StringComparison.Ordinal);
            Assert.Contains("6144MB", decisions[0].Message, StringComparison.Ordinal);
            Assert.Contains("2MB", decisions[0].Message, StringComparison.Ordinal);

            // (e) The JF-428 pin the encode holds is the TRANSIENT directory
            // (path-keyed as before; the reaper honors it).
            Assert.Equal(transientDir, observedPinPath);
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// The shared arrange of the JF-537.1 transient-mode pins below: an oversize
    /// (2h HEVC, 6144MB estimate vs a 2MB cap) episode encode through the
    /// controller, using a fake ffmpeg that writes the COMPLETED-encode playlist
    /// shape (ENDLIST present) and appends one line per invocation to a run
    /// counter, so a test can prove a replay never started ffmpeg again. The
    /// endpoint call is AWAITED (the file's rule: a scenario never abandons an
    /// endpoint task for the Dispose backstop to find).
    /// Returns the episode's transient directory, the counter path, and the item id.
    /// </summary>
    private async Task<(string TransientDir, string RunCounterPath, string ItemId)> ArrangeOversizeTransientEncodeAsync(
        string counterName)
    {
        (var episode, var mediaSourceManager) = CreateOversizeHevcEpisode("jf5371-" + counterName);

        string runCounterPath = Path.Combine(_tempDir, counterName);
        string fakeFfmpegPath = WriteRunCountingFakeFfmpeg(
            "fake-ffmpeg-jf5371-" + counterName,
            runCounterPath,
            endlist: true);

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 2;
        try
        {
            var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath);
            _ = await controller.StreamHlsEpisode(episode.Id.ToString());
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }

        string transientDir = _cache.GetTransientHlsDirectoryPath(episode.Id.ToString(), 0);
        return (transientDir, runCounterPath, episode.Id.ToString());
    }

    /// <summary>
    /// The shared arrange episode of the JF-537.1 transient-mode pins (extracted
    /// at the fourth copy): a 2h HEVC 1080p + EAC3 episode whose transcode-tier
    /// estimate (6144MB) exceeds any small test cap, with the media-streams mock
    /// wired. Returns the episode and its mock for the caller's own wiring.
    /// </summary>
    private (MediaBrowser.Controller.Entities.TV.Episode Episode, Mock<IMediaSourceManager> MediaSourceManager) CreateOversizeHevcEpisode(string name)
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = name,
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
        return (episode, mediaSourceManager);
    }

    /// <summary>
    /// JF-537.1: the transient entry serves a REPLAY without re-encoding: after
    /// the oversize encode completes (the fake writes the ENDLIST completed-encode
    /// shape), a second request must hit the warm-cache path on the TRANSIENT
    /// playlist and never start ffmpeg again (the run counter stays at one; the
    /// served playlist is ffmpeg's own ENDLIST one, proving the validated-serve
    /// row, not a re-encode that would coincidentally serve too).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_OversizeTransientReplay_ServesFromTransientRootWithoutReEncode()
    {
        (string transientDir, string runCounterPath, string itemId) = await ArrangeOversizeTransientEncodeAsync("replay-runs");

        // Let the monitor's generation clear land so the second request reads the
        // deterministic own-dead ENDLIST row (not the bounded own-live window).
        await WaitUntilAsync(() => VideoAudioController.EncodeGenerationCountForTest(itemId, audiobook: false, song: false) == 0, TimeSpan.FromSeconds(10), 100);
        Assert.Single(File.ReadAllLines(runCounterPath));

        // The replay: same cache, same item, fresh controller (with an unused
        // fake; the counter proves it never runs).
        var replayController = CreateController(itemId, ffmpegPath: WriteFakeFfmpeg("fake-ffmpeg-jf5371-replay-unused", "exit 0\n"));

        ActionResult replay = await replayController.StreamHlsEpisode(itemId);

        var replayContent = Assert.IsType<ContentResult>(replay);
        Assert.Contains("#EXT-X-ENDLIST", replayContent.Content, StringComparison.Ordinal);
        Assert.Single(File.ReadAllLines(runCounterPath));
        Assert.True(Directory.Exists(transientDir), "the transient entry survives its own replay (only the idle reaper may delete it)");
    }

    /// <summary>
    /// JF-537.1: segments of a transient encode resolve through the generic
    /// segment route (the same URLs a cache-rooted encode serves): the encode
    /// registered its TRANSIENT directory, and the resolved file serves from
    /// there. This is the registration half of the "own registration/scan/dedup
    /// plumbing" the JF-537 scope-out demanded.
    /// </summary>
    [Fact]
    public async Task GetSegment_ResolvesFromTransientRoot_RegisteredByOversizeEncode()
    {
        (string transientDir, _, string itemId) = await ArrangeOversizeTransientEncodeAsync("segment-runs");

        var controller = CreateController(itemId);

        ActionResult result = await controller.GetSegment(itemId, "seg_0000.ts");

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(transientDir, "seg_0000.ts"), file.FileName);
    }

    /// <summary>
    /// JF-537.1 root preference pin: the CACHE root wins a key that has a valid
    /// (ENDLIST) cache-root entry AND a debris (no-ENDLIST, not live) transient
    /// entry: the serve must come from the cache root, no re-encode may start,
    /// and the verdict's cleanup must NOT destroy the other root's directory
    /// (the scoped CleanupHlsGenerationAt contract; a key-wide wipe would delete
    /// a live generation's directory).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_ValidCacheEntry_ShadowsTransientDebris_NeitherDeleted()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Dual Root Episode",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
        string itemId = episode.Id.ToString();
        _libraryManagerMock.Setup(m => m.GetItemById(episode.Id)).Returns(episode);

        // Cache root: a valid completed encode (ENDLIST playlist + a segment).
        string cacheDir = _cache.GetHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "stream.m3u8"), "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n#EXT-X-ENDLIST\n");
        await File.WriteAllBytesAsync(Path.Combine(cacheDir, "seg_0000.ts"), new byte[16]);

        // Transient root: debris of a killed encode (no ENDLIST, nothing live).
        string transientDir = _cache.GetTransientHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(transientDir);
        await File.WriteAllTextAsync(Path.Combine(transientDir, "stream.m3u8"), "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n");
        await File.WriteAllBytesAsync(Path.Combine(transientDir, "seg_0000.ts"), new byte[16]);

        string runCounterPath = Path.Combine(_tempDir, "shadow-runs");
        var controller = CreateController(itemId, ffmpegPath: WriteFakeFfmpeg(
            "fake-ffmpeg-jf5371-shadow",
            $"printf 'run\\n' >> \"{runCounterPath}\"\nexit 0\n"));

        ActionResult result = await controller.StreamHlsEpisode(itemId);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(runCounterPath), "a valid cache-root entry must serve without any encode");
        Assert.True(Directory.Exists(cacheDir), "the valid cache-root generation must survive its own serve");
        Assert.True(Directory.Exists(transientDir), "the verdict must delete only the directory it served from, never the other root's");
    }

    /// <summary>
    /// JF-537.1: ALONE transient debris (no-ENDLIST, nothing live, no cache-root
    /// sibling) is verdict-cleaned on the next request for its key: the no-ENDLIST
    /// row deletes the served-from TRANSIENT directory (the scoped cleanup) and
    /// the re-encode runs INTO THE TRANSIENT ROOT again, over a clean target
    /// (the stale segment is gone, so append_list cannot bake a doubled playlist).
    /// On the unmodified base both halves fail: the debris is invisible to the
    /// cache-root-only lookup and the re-encode lands in the cache root.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TransientDebrisAlone_CleanedByVerdict_ReencodedIntoTransient()
    {
        (var episode, var mediaSourceManager) = CreateOversizeHevcEpisode("jf5371-debris");

        // Seed debris in the transient root only: a stale no-ENDLIST playlist and
        // a stale segment a fresh encode must never see.
        string transientDir = _cache.GetTransientHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(transientDir);
        string staleSegment = Path.Combine(transientDir, "seg_0009.ts");
        await File.WriteAllTextAsync(Path.Combine(transientDir, "stream.m3u8"), "#EXTM3U\n#EXTINF:4.000,\nseg_0009.ts\n");
        await File.WriteAllBytesAsync(staleSegment, new byte[16]);

        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf5371-debris");

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 2;
        try
        {
            var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath);

            ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.True(Directory.Exists(transientDir), "the re-encode targets the transient root again");
            Assert.False(File.Exists(staleSegment), "the verdict's cleanup must remove the served-from transient debris before the re-encode");
            Assert.False(
                Directory.Exists(_cache.GetHlsDirectoryPath(episode.Id.ToString(), 0)),
                "the re-encode must stay out of the capped cache root");
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// JF-537 (kept through JF-537.1): the under-cap transcode encode stays in
    /// the CACHE ROOT and logs the cacheable DECISION at Debug (the debug-logging
    /// policy: handler branching decisions are debug-visible); the transient mode
    /// is never announced. 45min HEVC reserves 3072MB under the default 4096.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_EstimateUnderCap_StaysInCacheRoot_LogsCacheableDecision()
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath, loggerFactory);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        Assert.IsType<ContentResult>(result);
        Assert.True(
            Directory.Exists(_cache.GetHlsDirectoryPath(episode.Id.ToString(), 0)),
            "an under-cap transcode must keep encoding into the cache root");
        Assert.False(
            Directory.Exists(_cache.GetTransientHlsDirectoryPath(episode.Id.ToString(), 0)),
            "an under-cap transcode must not touch the transient root");
        // The encode keeps logging from its background poll thread after the
        // awaited call returns; enumerate a lock-consistent snapshot (CI flake
        // 2026-09-13: "Collection was modified").
        var finalRecords = TestCaptureLogger.Snapshot(logRecords);
        Assert.DoesNotContain(
            finalRecords,
            r => r.Message.Contains("transient root", StringComparison.Ordinal));
        var decisions = finalRecords
            .Where(r => r.Message.Contains("cacheable", StringComparison.Ordinal))
            .ToList();
        Assert.Single(decisions);
        Assert.Equal(LogLevel.Debug, decisions[0].Level);
        Assert.Contains("3072MB", decisions[0].Message, StringComparison.Ordinal);
        Assert.Contains("4096MB", decisions[0].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-537 scope pin, carried into JF-537.1: the oversize decision targets the
    /// TRANSCODE tier ONLY. A REMUX-tier estimate above the cap (h264 source, 4h
    /// runtime, flat 1280MB/h = 5120MB against a 256MB cap) must stay in the
    /// CACHE ROOT and never announce the transient mode: the remux copies at the
    /// source's own bitrate and replays at ~20x realtime, so its churn is not
    /// the multi-hour re-encode pain the transient mode exists to avoid (full
    /// rationale on <see cref="VideoAudioController.TranscodeEstimateExceedsCacheCap"/>).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_RemuxTier_EstimateOverCap_StaysInCacheRoot_NoTransientMode()
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 256;
        try
        {
            var controller = CreateEpisodeController(mediaSourceManager, episode.Id.ToString(), fakeFfmpegPath, loggerFactory);

            ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

            // Remux tier ran (video copy) into the CACHE root, and no transient
            // mode was announced.
            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.True(
                Directory.Exists(_cache.GetHlsDirectoryPath(episode.Id.ToString(), 0)),
                "the remux tier must keep encoding into the cache root");
            Assert.DoesNotContain(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("transient root", StringComparison.Ordinal));
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    // ========== JF-774: the two-root split's same-root self-healing residuals ==========

    /// <summary>
    /// JF-774 finding 1 pin: the restart scan fallback must resolve the NEWEST
    /// generation ACROSS BOTH ROOTS, not whichever the cache root happens to
    /// hold. Shape: a media change re-keyed the item (t1 -> t2 = the caller's
    /// art ticks), an oversize t2 play minted the transient root's {E}_t2, and
    /// the cache root's {E}_t1 lingers as the documented JF-676 orphan. A
    /// post-restart GetSegment (in-memory registration gone: a FRESH cache
    /// instance over the same cache path) must serve the NEW generation's
    /// segment; the cache-root-first order resolved the old t1 directory purely
    /// because it existed (wrong content or 404 past the old cut, pinned for
    /// the process lifetime). Creation times are set EXPLICITLY so filesystem
    /// timestamp granularity cannot decide the pin.
    /// </summary>
    [Fact]
    public async Task GetSegment_AfterRestart_ScanPrefersNewestGenerationAcrossRoots()
    {
        string itemId = Guid.NewGuid().ToString();

        // The OLD cache-root generation (orphaned by the media change's re-key).
        string orphanDir = _cache.GetHlsDirectoryPath(itemId, 637000000000000000L);
        Directory.CreateDirectory(orphanDir);
        await File.WriteAllTextAsync(Path.Combine(orphanDir, "stream.m3u8"), "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n#EXT-X-ENDLIST\n");
        await File.WriteAllBytesAsync(Path.Combine(orphanDir, "seg_0000.ts"), new byte[16]);

        // The CURRENT generation, resting in the transient root (the oversize t2 play).
        string transientDir = _cache.GetTransientHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(transientDir);
        await File.WriteAllTextAsync(Path.Combine(transientDir, "stream.m3u8"), "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n#EXT-X-ENDLIST\n");
        await File.WriteAllBytesAsync(Path.Combine(transientDir, "seg_0000.ts"), new byte[16]);

        DateTime now = DateTime.UtcNow;
        Directory.SetCreationTimeUtc(orphanDir, now.AddDays(-2));
        Directory.SetCreationTimeUtc(transientDir, now);

        // A restarted cache over the same cache path: the in-memory directory
        // lookup is empty, so segment resolution falls back to the scan.
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(_tempDir);
        var restarted = new VideoAudioCache(appPaths.Object, _loggerFactory.CreateLogger<VideoAudioCache>());
        var controller = CreateController(itemId, cache: restarted);

        ActionResult result = await controller.GetSegment(itemId, "seg_0000.ts");

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(transientDir, "seg_0000.ts"), file.FileName);
    }

    /// <summary>
    /// JF-774 finding 2 pin: while an oversize (transient-root) encode is LIVE,
    /// a stale prewrite left in the same-key CACHE-root directory must not
    /// shadow the live encode's fresh listing on the warm-cache prewrite probe
    /// (the stale file's expired JF-309 token would 401 every segment fetch it
    /// names for the whole encode window). The stale file's production origin is
    /// the undeletable class (it survived the whole-dir cleanups; the per-file
    /// debris backstop removes stream.m3u8 and segments but not
    /// playlist-full.m3u8), so the pin PLANTS it mid-window, after the encode's
    /// own cleanups ran, and asserts the probe serves the LIVE encode's
    /// registered directory (the reviewer's fix shape: the probe prefers the
    /// registered transient generation dir; the backstop-deletes-the-prewrite
    /// shape is untestable against this bar because every DELETABLE stale
    /// prewrite is already removed by an earlier whole-dir cleanup, and the
    /// surviving class defeats per-file deletes equally).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_LiveTransientEncode_StaleCacheRootPrewrite_DoesNotShadowFreshPrewrite()
    {
        (var episode, var mediaSourceManager) = CreateOversizeHevcEpisode("jf774-stale-prewrite");
        string itemId = episode.Id.ToString();

        string runCounterPath = Path.Combine(_tempDir, "jf774-prewrite-runs");
        string releaseFile = Path.Combine(_tempDir, "jf774-prewrite-release");
        string parkedFfmpeg = WriteBlockingFakeFfmpeg(runCounterPath, releaseFile);
        string transientDir = _cache.GetTransientHlsDirectoryPath(itemId, 0);
        string cacheDir = _cache.GetHlsDirectoryPath(itemId, 0);

        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 2;
        try
        {
            // First play: starts the oversize encode into the transient root and
            // returns inside the encode window (the fake parks, playlist live).
            var controller = CreateEpisodeController(mediaSourceManager, itemId, parkedFfmpeg);
            ActionResult first = await controller.StreamHlsEpisode(itemId);
            Assert.IsType<ContentResult>(first);
            Assert.Equal(1, CountFfmpegSpawns(runCounterPath));

            string freshPrewritePath = Path.Combine(transientDir, "playlist-full.m3u8");
            Assert.True(File.Exists(freshPrewritePath), "the live encode wrote its prewrite into the transient root");

            // The stale cache-root prewrite (mid-window plant: its production
            // origin survived every earlier cleanup), naming segments with an
            // expired-token stand-in.
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(
                Path.Combine(cacheDir, "playlist-full.m3u8"),
                "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.000,\n"
                    + "/alexaskill/api/video-audio/" + itemId + "/segments/seg_0000.ts?token=JF774-STALE-EXPIRED\n");

            // The replay INSIDE the encode window: the warm-cache own-live row
            // must serve the LIVE encode's prewrite, not the stale cache-root one.
            var replayController = CreateEpisodeController(mediaSourceManager, itemId, parkedFfmpeg);
            ActionResult replay = await replayController.StreamHlsEpisode(itemId);

            var replayContent = Assert.IsType<ContentResult>(replay);
            Assert.DoesNotContain("JF774-STALE-EXPIRED", replayContent.Content, StringComparison.Ordinal);
            string freshToken = ExtractPrewriteToken(freshPrewritePath);
            Assert.Contains(freshToken, replayContent.Content, StringComparison.Ordinal);
            Assert.Equal(1, CountFfmpegSpawns(runCounterPath));
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
            File.WriteAllText(releaseFile, "go");
            await ReleaseParkedEncodeFixturesAsync(itemId, new[] { transientDir, cacheDir });
            await FenceTempDirEncodesDeadAsync();
        }
    }

    /// <summary>
    /// The stale-vs-fresh discriminator of the finding 2 pin: the token value
    /// the LIVE encode embedded in its own prewrite's first token-suffixed
    /// segment URL (WriteEpisodePlaylist's JF-309 embedding).
    /// </summary>
    private static string ExtractPrewriteToken(string prewritePath)
    {
        string tokenLine = File.ReadAllLines(prewritePath)
            .First(l => l.Contains("token=", StringComparison.Ordinal));
        int tokenAt = tokenLine.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        return tokenLine[tokenAt..];
    }

    /// <summary>
    /// JF-774 finding 3 pin: an UNDELETABLE cache-root debris playlist must not
    /// mask the same-key VALID transient entry into a from-zero re-encode. The
    /// cache-root hit wins the ordered probe, its debris verdict's cleanup
    /// cannot remove it (the JF-499 W4 write-denied directory idiom: the Linux
    /// shape where recursive deletes and per-file deletes both fail while reads
    /// still succeed), and the fall-through re-encode's per-file debris sweep
    /// would wipe the transient entry the probe never reached. The fix probes
    /// the TRANSIENT root after the cache-root hit's verdict nulls, so the
    /// request serves the valid transient playlist with no encode at all (the
    /// reviewer's fix shape: probe the transient root when the cache-root hit
    /// fails its verdict cleanup).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_UndeletableCacheRootDebris_DoesNotMaskValidTransientEntry()
    {
        (var episode, var mediaSourceManager) = CreateOversizeHevcEpisode("jf774-debris-mask");
        string itemId = episode.Id.ToString();

        // Cache root: DEBRIS of a killed encode (no ENDLIST, nothing live) that
        // cannot be removed.
        string cacheDir = _cache.GetHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(Path.Combine(cacheDir, "stream.m3u8"), "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n");
        await File.WriteAllBytesAsync(Path.Combine(cacheDir, "seg_0000.ts"), new byte[16]);

        // Transient root: the VALID resting entry of a completed oversize encode
        // (distinctive 4.444 durations prove which playlist served).
        string transientDir = _cache.GetTransientHlsDirectoryPath(itemId, 0);
        Directory.CreateDirectory(transientDir);
        string transientSegment = Path.Combine(transientDir, "seg_0000.ts");
        await File.WriteAllTextAsync(Path.Combine(transientDir, "stream.m3u8"), "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.444,\nseg_0000.ts\n#EXT-X-ENDLIST\n");
        await File.WriteAllBytesAsync(transientSegment, new byte[16]);

        string runCounterPath = Path.Combine(_tempDir, "jf774-mask-runs");
        int originalCap = _config.VideoAudioCacheSizeMB;
        _config.VideoAudioCacheSizeMB = 2;
        try
        {
            var controller = CreateEpisodeController(
                mediaSourceManager,
                itemId,
                WriteRunCountingFakeFfmpeg("fake-ffmpeg-jf774-mask", runCounterPath, endlist: false));

            ActionResult result = await RunWithDeniedDirectoryAsync(
                cacheDir,
                () => controller.StreamHlsEpisode(itemId));

            var content = Assert.IsType<ContentResult>(result);
            Assert.Contains("#EXTINF:4.444,", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
            Assert.False(File.Exists(runCounterPath), "the valid transient entry must serve with no encode at all");
            Assert.True(File.Exists(transientSegment), "the valid transient entry's bytes must survive the request");
        }
        finally
        {
            _config.VideoAudioCacheSizeMB = originalCap;
        }
    }

    /// <summary>
    /// The JF-775 two-root shadow planter (the PlantLiveEncodeFixture
    /// convention, extracted at the second copy): an episode whose TRANSIENT
    /// generation dir is REGISTERED (the production encode-start ordering: the
    /// resolved dir registered before the mark), an UNDELETABLE cache-root
    /// shadow (a stale no-ENDLIST stream.m3u8, the seg_7777 marker, whose
    /// expired-token segment URLs would 401 every fetch for the whole encode
    /// window, the JF-774 finding 2 class), and the encode's live partial in
    /// the registered transient dir (seg_9999). NO playlist-full.m3u8
    /// anywhere (the no-prewrite row). Returns the wired controller and the
    /// read counters over the transient dir's two playlists.
    /// </summary>
    private (string ItemId, VideoAudioController Controller, (Func<int> Prewrites, Func<int> Lives) Reads) PlantTwoRootShadowFixture(
        string episodeName,
        string fakeFfmpegName)
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls(episodeName, "h264", TimeSpan.FromMinutes(45));
        string itemId = episode.Id.ToString();

        string cacheDir = _cache.GetHlsDirectoryPath(itemId, 0);
        string transientDir = _cache.GetTransientHlsDirectoryPath(itemId, 0);
        string livePath = Path.Combine(transientDir, "stream.m3u8");

        var controller = CreateController(
            itemId,
            loggerFactory: null,
            mediaSourceManager,
            WriteRecordingFakeFfmpeg(fakeFfmpegName));
        var reads = TrackPlaylistReads(controller, Path.Combine(transientDir, "playlist-full.m3u8"), livePath);

        // The encode-start ordering the production code performs: the resolved
        // transient dir is registered BEFORE the mark, so the mid-registration
        // gap already has the registration pointing at the incoming encode's
        // dir.
        _cache.RegisterHlsDirectoryPath(itemId, 0, transientDir);

        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "stream.m3u8"), "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_7777.ts\n");

        Directory.CreateDirectory(transientDir);
        File.WriteAllText(livePath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_9999.ts\n");

        return (itemId, controller, reads);
    }

    /// <summary>
    /// Plant a FOREIGN-ticks generation registration for the key (JF-782 legs
    /// 2/3 arrange, extracted at the second copy): the transient-root dir of
    /// ANOTHER generation of the same key, created and registered the way a
    /// real foreign-ticks encode does (register-before-mark writes both maps;
    /// the per-key slot is the one a later registration displaces). Returns
    /// the planted dir.
    /// </summary>
    private string PlantForeignTicksRegistration(string itemId, long ticks = 999)
    {
        string foreignDir = _cache.GetTransientHlsDirectoryPath(itemId, ticks);
        Directory.CreateDirectory(foreignDir);
        _cache.RegisterHlsDirectoryPath(itemId, ticks, foreignDir);
        return foreignDir;
    }

    /// <summary>
    /// JF-775 red proof (the JF-774 gate-marker addendum's mid-registration
    /// residual): a lock-free fast-path replay landing between the registry
    /// store and the first slot write inside MarkEncodeActive reads a
    /// REGISTERING verdict that accepts the ordered probe's first hit without
    /// reading, while the prewrite override and the registered-dir fallback
    /// both sit in the strict OwnTicksGenerationLive gate (false in the
    /// window), so the fall-through serve served the stale listing
    /// unredirected. The liveness-aware resolver closes it at the PROBE: while
    /// the caller's own generation is live-or-registering and the registration
    /// names one of its generation dirs, that dir is the ONLY probe source, so
    /// the shadow is never probed and the fall-through row serves the
    /// registered dir's live partial.
    /// RED PROOF: on the pre-JF-775 tree the probe's first hit IS the shadow,
    /// the registering verdict accepts it unread, and the strict gate's
    /// fall-through serves the shadow's stale bytes (seg_7777 present).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_MidRegistrationWindow_CacheRootShadowNeverProbed_ProbeFeedsRegisteredDir()
    {
        var (itemId, controller, reads) = PlantTwoRootShadowFixture(
            "JF-775 Mid-Registration Shadow S01E01",
            "fake-ffmpeg-jf775-midreg-shadow");

        // The MID-REGISTRATION marking (a stored holder with ZERO slots, the
        // window where the strict gate and the conservative verdict disagree).
        VideoAudioController.SetEncodeRegisteringForTest(itemId, registering: true);
        try
        {
            ActionResult result = await controller.StreamHlsEpisode(itemId);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_9999", StringComparison.Ordinal),
                $"the mid-registration replay must serve the REGISTERED dir's live partial, not the ordered probe's cache-root shadow (JF-775); shadow marker seg_7777 present: {content.Content.Contains("seg_7777", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_7777", content.Content, StringComparison.Ordinal);
            Assert.Equal(1, reads.Lives());
        }
        finally
        {
            VideoAudioController.SetEncodeRegisteringForTest(itemId, registering: false);
        }
    }

    /// <summary>
    /// JF-775 collapse guard (the JF-774 review F3 row through the resolver):
    /// on the own-live no-prewrite row the serve must read the LIVE encode's
    /// REGISTERED dir's stream.m3u8, never the ordered probe's first-hit
    /// cache-root shadow. JF-774 delivered this as a redirect block INSIDE
    /// ServeEpisodeWarmCacheAsync (registered contained ? registered dir :
    /// verdict file); JF-775 collapses the dir authority into the
    /// liveness-aware probe, which returns ONLY the registered dir's file on
    /// every liveness-accepted row, so the redirect's target equals the
    /// verdict file and the block is deleted. This pin holds GREEN on both
    /// shapes (it guards the collapse's equivalence, not a defect): a FULL own
    /// slot (SetEncodeActiveForTest, the ticks-0 sentinel this no-art fixture
    /// encodes at), so the strict gate reads true, unlike the mid-registration
    /// pin's zero-slot window.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_LiveTransientEncode_NoPrewrite_FallbackServesRegisteredDirNotCacheShadow()
    {
        var (itemId, controller, reads) = PlantTwoRootShadowFixture(
            "JF-775 Own-Live No-Prewrite S01E01",
            "fake-ffmpeg-jf775-ownlive-noprewrite");

        // A FULL own-ticks slot: the strict gate reads true (unlike the
        // mid-registration pin, whose zero-slot window it exists to contrast).
        VideoAudioController.SetEncodeActiveForTest(itemId, active: true);
        try
        {
            ActionResult result = await controller.StreamHlsEpisode(itemId);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_9999", StringComparison.Ordinal),
                $"the own-live no-prewrite row must serve the REGISTERED dir's live partial, not the cache-root shadow (JF-774 F3 through the JF-775 resolver); shadow marker seg_7777 present: {content.Content.Contains("seg_7777", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_7777", content.Content, StringComparison.Ordinal);
            Assert.Equal(1, reads.Lives());
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemId, active: false);
        }
    }

    // ========== JF-782: the JF-775 dir-authority timing residuals ==========

    /// <summary>
    /// JF-782 leg 1 pin (the probe-vs-verdict liveness STRADDLE, and the
    /// LEG-0 guard note's discriminating pin): a generation MARK landing
    /// between the live-aware probe's liveness read and the paired verdict's
    /// re-read is unseen by the probe (which resolved the static order, whose
    /// first hit is the undeletable cache-root shadow) but accepted UNREAD by
    /// the verdict (its loose read now true, Content null), so the serve row
    /// served the shadow's stale bytes. The ProbeLivenessReadForTest seam
    /// plants the mark inside exactly that gap (the only observer that fires
    /// between the two reads). The single-snapshot fix threads the probe's
    /// answer into the verdict: the disagreement cell READS and judges the
    /// hit instead of accepting unread, the judged no-ENDLIST shadow is NOT
    /// deleted (the own generation is live per the fresh read; the JF-676
    /// delete gate stays own-dead), and the request falls through to the lock
    /// scope, whose own probe now reads the mark and serves the REGISTERED
    /// dir's live partial.
    /// RED PROOF: on the pre-JF-782 tree the verdict accepts the shadow
    /// unread and the strict prewrite gate's fall-through serves the shadow's
    /// stale bytes (seg_7777 present, the shadow still on disk).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_ProbeToVerdictMarkStraddle_ShadowJudgedNotServedUnread()
    {
        var (itemId, controller, _) = PlantTwoRootShadowFixture(
            "JF-782 Straddle S01E01",
            "fake-ffmpeg-jf782-straddle");
        string shadowPath = Path.Combine(_cache.GetHlsDirectoryPath(itemId, 0), "stream.m3u8");

        bool markPlanted = false;
        controller.ProbeLivenessReadForTest = (key, answer) =>
        {
            if (!markPlanted && key == itemId && !answer)
            {
                // The straddle: the encode's mark lands AFTER the probe read
                // false, BEFORE the verdict re-reads.
                markPlanted = true;
                VideoAudioController.SetEncodeActiveForTest(itemId, active: true);
            }
        };
        try
        {
            ActionResult result = await controller.StreamHlsEpisode(itemId);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_9999", StringComparison.Ordinal),
                $"the straddled replay must fall through to the REGISTERED dir's live partial, not serve the static-order probe's cache-root shadow (JF-782 leg 1); shadow marker seg_7777 present: {content.Content.Contains("seg_7777", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_7777", content.Content, StringComparison.Ordinal);
            Assert.True(
                File.Exists(shadowPath),
                "the straddle cell must not delete: the own generation is live per the verdict's fresh read (the JF-676 delete gate stays own-dead)");
        }
        finally
        {
            controller.ProbeLivenessReadForTest = null;
            VideoAudioController.SetEncodeActiveForTest(itemId, active: false);
        }
    }

    /// <summary>
    /// JF-782 leg 2 pin (the FOREIGN-TICKS mid-registration sub-window): while
    /// a foreign-ticks encode of the same key sits in the zero-slot
    /// mid-registration window (the seam's registering holder), the per-key
    /// registration names ITS resolved dir (the register-before-mark ordering
    /// every encode path now performs), so the resolver's containment
    /// correctly refuses it and the probe's hit is the caller's cache-root
    /// shadow (its own generation's stale registration, the resting entry of
    /// the previous same-ticks encode, is what the generation-scoped arm
    /// reads); the registering verdict accepted that hit UNREAD (the
    /// zero-slot arm of the loose read), serving the shadow's stale bytes.
    /// The narrowing: the verdict's Registering arm accepts only while the
    /// per-key registration is not provably foreign for the caller's ticks,
    /// so the hit is READ and judged (the shadow is no-ENDLIST debris; not
    /// deleted, the entry is mid-registration) and the request falls through
    /// to the lock scope's re-encode, whose debris sweep removes the
    /// deletable shadow and serves fresh bytes.
    /// RED PROOF: on the pre-JF-782 tree the registering verdict serves the
    /// shadow unread (seg_7777 present).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_RegisteringForeignTicksRegistration_ShadowJudgedNotServedUnread()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("JF-782 Foreign Mid-Reg S01E01", "h264", TimeSpan.FromMinutes(45));
        string itemId = episode.Id.ToString();

        string cacheDir = _cache.GetHlsDirectoryPath(itemId, 0);
        var controller = CreateController(itemId, loggerFactory: null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf782-foreign-midreg"));

        // The caller's own generation's STALE registration (the previous
        // same-ticks encode's resting entry), then the incoming foreign-ticks
        // encode's registration displacing the per-key slot: the
        // art-change-mid-flight shape.
        _cache.RegisterHlsDirectoryPath(itemId, 0, cacheDir);
        _ = PlantForeignTicksRegistration(itemId);

        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(
            Path.Combine(cacheDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.000,\nseg_7777.ts\n");

        VideoAudioController.SetEncodeRegisteringForTest(itemId, registering: true);
        try
        {
            ActionResult result = await controller.StreamHlsEpisode(itemId);

            // Post-fix the request re-encodes (the shadow judged debris) and
            // serves the fresh encode's listing; the bar is the shadow's
            // marker staying out of the served bytes.
            var content = Assert.IsType<ContentResult>(result);
            Assert.DoesNotContain("seg_7777", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeRegisteringForTest(itemId, registering: false);
        }
    }

    /// <summary>
    /// JF-782 leg 3 pin (the full-slot FOREIGN-REGISTRATION OVERWRITE, the
    /// pre-existing JF-774 containment boundary): while the caller's OWN-ticks
    /// generation is fully live and its registration names the registered
    /// (transient) dir, a concurrent foreign-ticks encode's registration
    /// displaces the PER-KEY slot; the per-key-reading resolver then refused
    /// the containment and the static order's first hit (the cache-root
    /// shadow) won the liveness-ACCEPTED row, serving the shadow's stale
    /// bytes unread. The generation-scoped registration closes it: the
    /// resolver's exclusive arm reads the (key, ticks) entry a foreign
    /// registration cannot touch, so the live own generation's registered
    /// dir still serves.
    /// RED PROOF: on the pre-JF-782 tree the own-live verdict accepts the
    /// shadow unread and the strict gate's fall-through serves seg_7777.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_ForeignRegistrationOverwrite_OwnLiveGenerationStillServes()
    {
        var (itemId, controller, reads) = PlantTwoRootShadowFixture(
            "JF-782 Foreign Overwrite S01E01",
            "fake-ffmpeg-jf782-foreign-overwrite");

        // The own generation FULLY live (a written own-ticks slot)...
        VideoAudioController.SetEncodeActiveForTest(itemId, active: true);

        // ...and the concurrent foreign-ticks encode's registration
        // displacing the per-key slot (the art-change-mid-flight shape).
        _ = PlantForeignTicksRegistration(itemId);
        try
        {
            ActionResult result = await controller.StreamHlsEpisode(itemId);

            var content = Assert.IsType<ContentResult>(result);
            Assert.True(
                content.Content.Contains("seg_9999", StringComparison.Ordinal),
                $"the own-live generation's registered dir must survive a foreign per-key registration overwrite (JF-782 leg 3); shadow marker seg_7777 present: {content.Content.Contains("seg_7777", StringComparison.Ordinal)}");
            Assert.DoesNotContain("seg_7777", content.Content, StringComparison.Ordinal);
            Assert.Equal(1, reads.Lives());
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemId, active: false);
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

    /// <summary>A bare-GUID audio-variant playlist request with no token must be rejected (401).</summary>
    [Fact]
    public async Task StreamHlsEpisodeAudio_NoToken_Returns401()
    {
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

    /// <summary>A bare-GUID speed playlist request with no token must be rejected (401).</summary>
    [Fact]
    public async Task StreamHlsAudioSpeed_NoToken_Returns401()
    {
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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

            // Fence the monitor's stall kill to OBSERVED death (the sibling
            // JF-665/668 tests' idiom; added in the JF-537.1 cycle after two
            // full-suite net10.0 runs observed the still-dying fake here and
            // the Dispose backstop counted it as this test's leak).
            await FenceTempDirEncodesDeadAsync();
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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
            // serves the PRE-WRITE (JF-778: windowed to the encoded region -
            // the parked fake's head is seg_0000, so the 2-entry floor applies
            // and its second entry seg_0001 is the pre-write-only marker; the
            // live partial carries seg_0000 alone), with no ENDLIST.
            ActionResult midEncode = await controller.StreamHlsEpisode(itemIdStr);
            var midContent = Assert.IsType<ContentResult>(midEncode);
            Assert.Contains("seg_0001.ts", midContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0674", midContent.Content, StringComparison.Ordinal);
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
            // serves the PRE-WRITE (JF-819: windowed to the encoded region -
            // the parked fake's head is seg_000, so the 2-entry floor applies
            // and its second entry seg_001 is the pre-write-only marker; the
            // live partial carries seg_000 alone), with no ENDLIST.
            ActionResult midEncode = await controller.StreamHlsVideoAudio(itemIdStr);
            var midContent = Assert.IsType<ContentResult>(midEncode);
            Assert.Contains("seg_001.ts", midContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", midContent.Content, StringComparison.Ordinal);
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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
            // stale partial. JF-778 note: the fresh pre-write serve is WINDOWED
            // (the fake's head is seg_0000, so the 2-entry floor applies); its
            // second entry seg_0001 is the pre-write-only marker, since the
            // dead partial carries seg_0000 alone.
            ActionResult third = await controller.StreamHlsEpisode(itemIdStr);
            var content = Assert.IsType<ContentResult>(third);
            Assert.True(
                content.Content.Contains("seg_0001.ts", StringComparison.Ordinal),
                $"a killed own-ticks encode's stale partial must be cleaned and re-encoded, not served, while a foreign-ticks generation runs (JF-676); only the dead partial's single entry was served: {content.Content.Contains("seg_0001.ts", StringComparison.Ordinal) == false}");
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
    /// concurrent-encode guard (deliberately any-generation, JF-669/JF-675),
    /// which since JF-820 resolves the prewrite through the LIVE generation's
    /// directory and serves gen B's windowed prewrite (2 entries, no ENDLIST)
    /// to this foreign-ticks caller instead of the pre-JF-820 503 (the
    /// degradation the JF-820 design decision traded away: the audio concat
    /// timeline is generation-independent, so serving the running encode's
    /// listing is correct and the lock-wait/503 bought nothing). The verdict
    /// and cleanup asserts below are UNCHANGED by JF-820: the ticks-A debris
    /// is still invalidated and cleaned, gen B's directory still survives.
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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
            // JF-820: the guard serves gen B's live prewrite (window 2: head
            // seg_0000 only, floor 2) to this foreign-ticks caller instead of
            // the pre-JF-820 503.
            var liveServe = Assert.IsType<ContentResult>(second);
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(liveServe.Content));
            Assert.Contains("seg_0000.ts?token=", liveServe.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", liveServe.Content, StringComparison.Ordinal);

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
    /// JF-531 endpoint level, re-shaped by JF-778: on a cache miss the FIRST
    /// serve returns the pre-write listing WINDOWED to the encoded region (the
    /// floor of 2 entries when only seg_0000 exists), not ffmpeg's growing
    /// stream.m3u8 (1 entry) and not the full listing (whose no-ENDLIST shape
    /// puts the player's live-edge default start on the un-encoded tail: the
    /// 2026-10-05 device death). The entry beyond the head is deliberate: it is
    /// inside the JF-503 hold's +2 lookahead, which real encodes satisfy in
    /// under a second per segment.
    /// ffmpeg still writes its own stream.m3u8: it remains the recorded target,
    /// and the pre-write FILE on disk stays the full listing (the post-encode
    /// and resume-slice consumers).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_CacheMiss_ServesWindowedPrewriteNotLiveEdge()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Adolescence S01E02", "h264", TimeSpan.FromMinutes(45));

        string fakeFfmpegPath = WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-remux");

        var controller = CreateController(episode.Id.ToString(), null, mediaSourceManager, fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);

        // The WINDOWED listing: the 2-entry floor (head at seg_0000, prewrite age
        // ~0), event shape, token on the segment lines, no full-runtime tail.
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0002", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0674", content.Content, StringComparison.Ordinal);
        Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));

        // The pre-written FULL file exists next to ffmpeg's own target (the
        // windowing is serve-side only), and ffmpeg was still pointed at
        // stream.m3u8 (the prewrite never feeds ffmpeg's file).
        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        string prewritePath = Path.Combine(hlsDir, "playlist-full.m3u8");
        Assert.True(File.Exists(prewritePath), "pre-written listing must exist");
        Assert.Equal(675, VideoAudioController.CountSegmentsInPlaylist(File.ReadAllText(prewritePath)));
        string[] recordedArgs = File.ReadAllLines(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Equal("stream.m3u8", Path.GetFileName(recordedArgs[^1]));
    }

    /// <summary>
    /// JF-531 AC#1, re-shaped by JF-778: the TRANSCODE tier gets the same
    /// WINDOWED pre-write listing at first serve (the 2-entry floor with the
    /// fake ffmpeg's head at seg_0000). The live-edge mechanism is
    /// tier-independent (any no-ENDLIST partial playlist is treated as live),
    /// and the JF-778 incident episode itself was transcode-tier mpeg4.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_TranscodeTier_FirstServeIsTheWindowedPrewrite()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Adolescence S01E02", "hevc", TimeSpan.FromMinutes(51));

        var controller = CreateController(
            episode.Id.ToString(), null, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-hevc"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        // The 2-entry windowed prefix of the 765-entry listing, no ENDLIST.
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));
        Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0764", content.Content, StringComparison.Ordinal);
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
    /// JF-531 mid-encode fetch, re-shaped by JF-778: while the encode flag is
    /// up, playlist re-fetches (the player polls an event playlist) return the
    /// pre-write listing WINDOWED to the encoded region, and the window is
    /// APPEND-ONLY across fetches as the encode advances: a segment listed by
    /// one fetch is never dropped by the next (the JF-531 append contract,
    /// restated for the window; an ExoPlayer media-sequence regression would
    /// reset the player). The full-runtime tail stays OUT of every mid-encode
    /// serve (the JF-778 live-edge death shape).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_ActiveEncode_MidEncodeFetchServesGrowingWindowedListing()
    {
        (string itemIdStr, string hlsDir, string prewritePath) = PlantLiveEncodeFixture(
            "Adolescence S01E02", headSegmentCount: 2, runtime: TimeSpan.FromMinutes(45));

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            // Same ambient-ffmpeg note as the completed-encode test above.
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf531-midencode"));

            ActionResult firstFetch = await controller.StreamHlsEpisode(itemIdStr);

            var firstContent = Assert.IsType<ContentResult>(firstFetch);
            Assert.Contains("seg_0000.ts?token=", firstContent.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0001.ts?token=", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0674", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", firstContent.Content, StringComparison.Ordinal);

            // The encode advances (four more segments) and 16s pass (the window's
            // growth anchor: prewrite age backdated to simulate elapsed time).
            for (int i = 2; i <= 5; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(hlsDir, $"seg_{i:D4}.ts"), new byte[16]);
            }

            File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-16));

            ActionResult secondFetch = await controller.StreamHlsEpisode(itemIdStr);

            var secondContent = Assert.IsType<ContentResult>(secondFetch);
            // Append-only: the first fetch's entries survive; the window grew to
            // the new head; no ENDLIST and no full-runtime tail.
            Assert.Contains("seg_0000.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0001.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0005.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0006", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0674", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", secondContent.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// Shared arrange of a LIVE episode encode over a planted cache directory
    /// (the JF-778 device shape, parameterized): ffmpeg's own two-entry live
    /// partial on disk, segment files seg_0000..seg_00{headSegmentCount-1} (the
    /// encoded head), and the full prewrite for the given runtime. The JF-778
    /// incident itself is head=7 at 1420.25s runtime (Sailor Moon R E054,
    /// 2026-10-05 12:27: the "highest existing segment 7" log line and the
    /// 356 x 3.989469s listing). Returns the item ID (for the controller and
    /// the active-encode seam), the HLS dir (to advance the head mid-test), and
    /// the prewrite path (to age the mtime, the window's growth anchor).
    /// </summary>
    private (string ItemIdStr, string HlsDir, string PrewritePath) PlantLiveEncodeFixture(
        string episodeName, int headSegmentCount, TimeSpan runtime)
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = episodeName,
            Id = itemId,
            RunTimeTicks = runtime.Ticks
        };
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(episode);

        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, 0);
        Directory.CreateDirectory(hlsDir);

        File.WriteAllText(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:4.000,\nseg_0000.ts\n#EXTINF:4.000,\nseg_0001.ts\n");
        for (int i = 0; i < headSegmentCount; i++)
        {
            File.WriteAllBytes(Path.Combine(hlsDir, $"seg_{i:D4}.ts"), new byte[16]);
        }

        string prewritePath = Path.Combine(hlsDir, "playlist-full.m3u8");
        VideoAudioController.WriteEpisodePlaylist(
            prewritePath,
            $"/alexaskill/api/video-audio/{itemIdStr}/segments/",
            runtime.Ticks,
            token: null);
        return (itemIdStr, hlsDir, prewritePath);
    }

    /// <summary>
    /// The shared item/mock arrange of the JF-817 audiobook pins: a Folder
    /// parent plus two even-half Audio chapters of <paramref name="totalRuntime"/>
    /// (the even split over a whole 10s-segment count keeps the prewrite's
    /// uniform durations exactly 10s, the EXTINF arithmetic the pins resolve
    /// segments by), with the GetItemById and GetItemList mocks set. The
    /// planting fixture below adds the cache directory, the segment files,
    /// and the prewrite; the first-fetch and completed-encode pins start from
    /// this bare arrange (no planted cache).
    /// </summary>
    private (Guid ParentId, List<MediaBrowser.Controller.Entities.BaseItem> Chapters) SetupAudiobookParentAndChapters(
        string bookName, TimeSpan totalRuntime)
    {
        Guid parentId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = bookName,
            Id = parentId
        };
        var chapters = new List<MediaBrowser.Controller.Entities.BaseItem>
        {
            new MediaBrowser.Controller.Entities.Audio.Audio
            {
                Name = $"{bookName} Chapter 1",
                Id = Guid.NewGuid(),
                RunTimeTicks = totalRuntime.Ticks / 2
            },
            new MediaBrowser.Controller.Entities.Audio.Audio
            {
                Name = $"{bookName} Chapter 2",
                Id = Guid.NewGuid(),
                RunTimeTicks = totalRuntime.Ticks / 2
            }
        };

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(chapters);
        return (parentId, chapters);
    }

    /// <summary>
    /// Shared arrange of a LIVE audiobook encode over a planted cache directory
    /// (the JF-817 device shape, parameterized): the full prewrite for the
    /// given total runtime (<see cref="VideoAudioController.WriteAudiobookPlaylist"/>,
    /// the production emitter, so the EXTINF walk and the token rewrite see
    /// real shapes) plus segment files seg_0000..seg_00{headSegmentCount-1}
    /// (the encoded head; head = headSegmentCount - 1). The JF-817 incident
    /// itself is a 100-chapter book resumed at 210s (segment 21) while the
    /// encode head sat at 21-80 (2026-10-08 20:35, the seg_3055 tail death).
    /// No stream.m3u8 is planted: the request must take the concurrent-encode
    /// guard row, the during-encode prewrite serve under test. Returns the
    /// parent ID string (for the controller and the active-encode seam), the
    /// HLS dir (to advance the head mid-test), and the prewrite path (to age
    /// the mtime, the window's growth anchor).
    /// </summary>
    private (string ParentIdStr, string HlsDir, string PrewritePath) PlantLiveAudiobookEncodeFixture(
        string bookName, int headSegmentCount, TimeSpan totalRuntime)
    {
        (Guid parentId, var chapters) = SetupAudiobookParentAndChapters(bookName, totalRuntime);
        string parentIdStr = parentId.ToString("D");

        (string hlsDir, string prewritePath) = PlantLiveAudiobookEncodeCore(parentIdStr, 0, chapters, headSegmentCount);
        return (parentIdStr, hlsDir, prewritePath);
    }

    /// <summary>
    /// The planting core of the JF-817/JF-820 audiobook fixtures (the
    /// single-item planter's audiobook twin, extracted at JF-820 when the
    /// foreign-generation fixture needed the SAME shape at a non-zero
    /// generation's ticks): the cache directory at the given generation's
    /// ticks, the head segment files seg_0000..seg_{headSegmentCount-1}, and
    /// the full prewrite via the production emitter
    /// (<see cref="VideoAudioController.WriteAudiobookPlaylist"/>).
    /// </summary>
    private (string HlsDir, string PrewritePath) PlantLiveAudiobookEncodeCore(
        string cacheKeyId, long artTicks, List<MediaBrowser.Controller.Entities.BaseItem> chapters, int headSegmentCount)
    {
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKeyId, artTicks);
        Directory.CreateDirectory(hlsDir);
        for (int i = 0; i < headSegmentCount; i++)
        {
            File.WriteAllBytes(Path.Combine(hlsDir, $"seg_{i:D4}.ts"), new byte[16]);
        }

        string prewritePath = Path.Combine(hlsDir, "playlist-full.m3u8");
        VideoAudioController.WriteAudiobookPlaylist(
            prewritePath,
            $"/alexaskill/api/video-audio/{cacheKeyId}/segments/",
            chapters,
            token: null);
        return (hlsDir, prewritePath);
    }

    /// <summary>
    /// The shared planting core of the JF-819 single-item fixtures (the
    /// JF-778/JF-817 planters' song twin): ffmpeg's own live partial on
    /// disk listing exactly the planted segment files
    /// seg_000..seg_{headSegmentCount-1} (3-digit single-item names; the
    /// encoded head, head = headSegmentCount - 1), and the full prewrite
    /// for the given runtime via the production single-item writer
    /// (<see cref="VideoAudioController.WriteVideoAudioPlaylist"/>, 4s
    /// segments at the song path's own TARGETDURATION), so the EXTINF walk,
    /// the truncation, and the token rewrite see real shapes. Returns the
    /// HLS dir (to advance the head mid-test) and the prewrite path (to age
    /// the mtime, the window's growth anchor).
    /// </summary>
    private (string HlsDir, string PrewritePath) PlantLiveSingleItemEncodeCore(
        string cacheKeyId, TimeSpan runtime, int headSegmentCount)
    {
        string hlsDir = _cache.GetHlsDirectoryPath(cacheKeyId, 0);
        Directory.CreateDirectory(hlsDir);

        // The live partial lists every planted segment (the encoded head is
        // real): a fixture whose live playlist disagrees with its segment
        // files cannot satisfy the JF-503 hold's listing check should a
        // future pin built on this planter touch the hold path (code-review
        // F2; the episode planter's fixed 2-entry shape predates this).
        var livePartial = new System.Text.StringBuilder(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < headSegmentCount; i++)
        {
            livePartial.Append("#EXTINF:4.000,\nseg_").Append(i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).Append(".ts\n");
            File.WriteAllBytes(Path.Combine(hlsDir, $"seg_{i:D3}.ts"), new byte[16]);
        }

        File.WriteAllText(Path.Combine(hlsDir, "stream.m3u8"), livePartial.ToString());

        string prewritePath = Path.Combine(hlsDir, "playlist-full.m3u8");
        VideoAudioController.WriteVideoAudioPlaylist(
            prewritePath,
            $"/alexaskill/api/video-audio/{cacheKeyId}/segments/",
            runtime.Ticks,
            token: null);
        return (hlsDir, prewritePath);
    }

    /// <summary>
    /// Shared arrange of a LIVE single-item (song-family) encode over a
    /// planted cache directory: the Audio item with its runtime (ABOVE the
    /// 10-minute prewrite threshold, the only runtime band where a prewrite
    /// exists), the GetItemById mock, and the planting core above. Returns
    /// the item ID string (for the controller and the song-registry seam),
    /// the HLS dir, and the prewrite path.
    /// </summary>
    private (string ItemIdStr, string HlsDir, string PrewritePath) PlantLiveVideoAudioEncodeFixture(
        string songName, int headSegmentCount, TimeSpan runtime)
    {
        Guid itemId = Guid.NewGuid();
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = songName,
            Id = itemId,
            RunTimeTicks = runtime.Ticks
        };
        _libraryManagerMock.Setup(m => m.GetItemById(itemId)).Returns(audioItem);

        (string hlsDir, string prewritePath) = PlantLiveSingleItemEncodeCore(itemId.ToString("D"), runtime, headSegmentCount);
        return (itemId.ToString("D"), hlsDir, prewritePath);
    }

    /// <summary>
    /// Shared arrange of a LIVE encode over the ONE-chapter book's chapter
    /// cache directory (the JF-819 incident shape): the parent folder plus
    /// ONE long chapter (the single-file book the JF-794 census found
    /// common, the shape <see cref="VideoAudioController.StreamHlsAudiobook"/>
    /// redirects into the single-item core), the parent/chapter/children-query
    /// mocks, and the planting core keyed on the CHAPTER id (the cache key
    /// the redirect threads; the song-registry flag also keys on it).
    /// Returns the parent ID string (the endpoint drive), the chapter ID
    /// string, the chapter's HLS dir, and the prewrite path.
    /// </summary>
    private (string ParentIdStr, string ChapterIdStr, string HlsDir, string PrewritePath) PlantLiveSingleFileBookEncodeFixture(
        string bookName, int headSegmentCount, TimeSpan chapterRuntime)
    {
        Guid parentId = Guid.NewGuid();
        Guid chapterId = Guid.NewGuid();
        var parentItem = new MediaBrowser.Controller.Entities.Folder
        {
            Name = bookName,
            Id = parentId
        };
        var chapter = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = $"{bookName} Single File",
            Id = chapterId,
            RunTimeTicks = chapterRuntime.Ticks
        };

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(parentItem);
        _libraryManagerMock.Setup(m => m.GetItemById(chapterId)).Returns(chapter);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter });

        (string hlsDir, string prewritePath) = PlantLiveSingleItemEncodeCore(chapterId.ToString("D"), chapterRuntime, headSegmentCount);
        return (parentId.ToString("D"), chapterId.ToString("D"), hlsDir, prewritePath);
    }

    /// <summary>
    /// JF-778 core pin (the device failure, red on the unmodified tree): while the
    /// episode encode is LIVE with its head at seg_0007, the prewrite serve must
    /// NOT hand the player the full 356-entry listing. The Echo's ExoPlayer
    /// resolves a no-ENDLIST playlist's default start at playlist end minus 3x
    /// TARGETDURATION (media3 HlsMediaSource fallback; the incident's request was
    /// seg_0353 = 1420.25s - 12s exactly), so the full listing makes the player's
    /// FIRST fetch land on the un-encoded tail: 404 x3 within ~1-2s, playback dead
    /// before the first frame. The windowed prefix (3 entries at prewrite age ~0:
    /// the lead) puts the default start at segment 0 and every listed entry
    /// inside the encoded head. RED PROOF (run before the fix landed, output in
    /// the task notes): the unmodified serve returns all 356 entries
    /// (seg_0353.ts?token= present), flipping the count assert to
    /// expected 3 / actual 356.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_ColdEdgeEncode_ServesWindowedPrewritePrefix()
    {
        (string itemIdStr, _, string prewritePath) = PlantLiveEncodeFixture("JF-778 Cold Edge S01E01", headSegmentCount: 8, runtime: TimeSpan.FromSeconds(1420.25));

        // Prewrite age ~0 (the mtime the production prewrite just wrote): the
        // window is the 3-entry lead, well inside the head.
        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf778-cold-edge"));

            ActionResult result = await controller.StreamHlsEpisode(itemIdStr);

            var content = Assert.IsType<ContentResult>(result);
            // The windowed prefix: exactly the 3-entry lead, event shape intact.
            Assert.Equal(3, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0002.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0003", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0353", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-TARGETDURATION:4", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// JF-778 growth + head-cap pin: the window grows with the prewrite's age
    /// (one entry per 4s segment, the 1x playback pace that keeps the player
    /// trailing the edge) but NEVER promises beyond the encoded head: at prewrite
    /// age 40s the elapsed term (13) would list 13 entries while only 8 exist
    /// (seg_0000..seg_0007), so the head cap holds the listing at 8 entries.
    /// Asserting seg_0008's ABSENCE is the cap's red discriminator (an uncapped
    /// window would promise the missing segment and reintroduce the tail 404).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_WindowedPrewrite_GrowsWithElapsedAndCapsAtHead()
    {
        (string itemIdStr, _, string prewritePath) = PlantLiveEncodeFixture("JF-778 Window Growth S01E01", headSegmentCount: 8, runtime: TimeSpan.FromSeconds(1420.25));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-40));

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf778-growth"));

            ActionResult result = await controller.StreamHlsEpisode(itemIdStr);

            var content = Assert.IsType<ContentResult>(result);
            Assert.Equal(8, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_0007.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0008", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0353", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// JF-778 resume-beyond-window pin: a cold-cache resume whose position lies
    /// past the grown window cannot be honored by a still-growing listing (the
    /// JF-686 rule: slicing past the listed total yields a dead header-only
    /// playlist), so the windowed serve DROPS the offset and serves from the
    /// beginning, with the same Information-level drop log the song path's
    /// cold-serve carries.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_WindowedPrewrite_ResumeBeyondWindow_DropsOffset()
    {
        (string itemIdStr, _, string prewritePath) = PlantLiveEncodeFixture("JF-778 Resume Beyond S01E01", headSegmentCount: 8, runtime: TimeSpan.FromSeconds(1420.25));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow);

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            var controller = CreateController(itemIdStr, loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf778-resume-beyond"));

            ActionResult result = await controller.StreamHlsEpisode(itemIdStr, TimeSpan.FromMinutes(20).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            // Unsliced from the beginning: segment 0 leads, no MEDIA-SEQUENCE shift.
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("dropping the resume", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// JF-778 resume-inside-window pin: a resume position INSIDE the grown window
    /// still slices (the JF-499 W2 contract the prewrite serve carries). 1s into
    /// the 1420.25s listing resolves to segment 1 (the builder's EXTINF walk:
    /// cumulative 3.989s after segment 0 covers a 1s offset), so a 3-entry window
    /// serves entries seg_0001..seg_0002 with MEDIA-SEQUENCE rebased to 1, and
    /// the slice's ~8s duration keeps the live-edge default start at 0, i.e.
    /// exactly the resume point.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_WindowedPrewrite_ResumeInsideWindow_SlicesWithinWindow()
    {
        (string itemIdStr, _, string prewritePath) = PlantLiveEncodeFixture("JF-778 Resume Inside S01E01", headSegmentCount: 8, runtime: TimeSpan.FromSeconds(1420.25));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf778-resume-inside"));

            ActionResult result = await controller.StreamHlsEpisode(itemIdStr, TimeSpan.FromSeconds(1).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            Assert.DoesNotContain("seg_0000", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0002.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0003", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:1", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    /// <summary>
    /// JF-778 mid-window resume pin (code-review F1's discriminator): a resume
    /// position INSIDE the window but far from its edge is NOT honored, because a
    /// sliced no-ENDLIST listing joins at its own live-edge default start (slice
    /// end minus 3x TARGETDURATION), which would land minutes past the requested
    /// position. The honor band is edge-within-LEAD; outside it the offset drops
    /// (JF-686 rule) and playback starts at 0. Shape: elapsed 40s grows the window
    /// to 8 entries; 8s resolves to segment 3; 8 - 3 = 5 entries from the edge,
    /// beyond the 3-entry lead, so the serve must be unsliced from segment 0 with
    /// the drop log.
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_WindowedPrewrite_MidWindowResume_OutsideHonorBand_DropsOffset()
    {
        (string itemIdStr, _, string prewritePath) = PlantLiveEncodeFixture("JF-778 Mid Window S01E01", headSegmentCount: 8, runtime: TimeSpan.FromSeconds(1420.25));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-40));

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true);
        try
        {
            var controller = CreateController(itemIdStr, loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf778-mid-window"));

            ActionResult result = await controller.StreamHlsEpisode(itemIdStr, TimeSpan.FromSeconds(8).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            // Unsliced from the beginning: the resume band did not cover segment 3
            // of an 8-entry window.
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0007.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("outside the encode window's honor band", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false);
        }
    }

    // ========== JF-817: windowed audiobook prewrite serve (live-encode resume) ==========

    /// <summary>
    /// JF-817 core pin (the device failure, red on the unmodified tree): the
    /// audiobook RESUME while the encode runs must not hand the player the
    /// full remaining prewrite. The incident (2026-10-08 20:35): the resume
    /// slice began at segment 21 but served all 3035 remaining entries (no
    /// ENDLIST), ExoPlayer resolved the no-ENDLIST listing's default start at
    /// playlist end minus 3x TARGETDURATION (the media3 live-edge formula,
    /// the JF-778 mechanism), and the player probed the un-encoded tail
    /// (seg_3055 with the head at 21) until playback died. The windowed serve
    /// BEGINS at the resume segment AND CAPS at the encoded-region edge: head
    /// at seg_0022 with the prewrite aged 200s grows the window to 23 entries
    /// (elapsed 20 + lead 3, head-capped at 23), and 23 - 21 = 2 entries from
    /// the edge is inside the 3-entry honor band, so the serve is exactly the
    /// slice seg_0021..seg_0022. RED PROOF (run before the fix landed): the
    /// unmodified serve returns the full remaining listing (3015 entries,
    /// seg_3035 present), so the count assert fails expected 2 / actual 3015.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_LiveEncodeResume_ServesWindowedSliceNotFullPrewrite()
    {
        (string parentIdStr, _, string prewritePath) = PlantLiveAudiobookEncodeFixture(
            "JF-817 Resume Book", headSegmentCount: 23, totalRuntime: TimeSpan.FromSeconds(30360));

        // Elapsed 20 segments (200s): the window is min(head + 1 = 23, 23) = 23
        // entries, head-capped so test latency cannot move it; its edge sits 2
        // entries past the resume segment, inside the honor band.
        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-200));

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: true, audiobook: true);
        try
        {
            var controller = CreateController(parentIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf817-resume"));

            ActionResult result = await controller.StreamHlsAudiobook(parentIdStr, TimeSpan.FromSeconds(210).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            // BEGINS at the resume segment, CAPS at the encoded-region edge.
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_0021.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0022.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0020", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0023", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_3035", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:21", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: false, audiobook: true);
        }
    }

    /// <summary>
    /// JF-817 mid-window resume pin (the JF-778 honor-band decision mirrored
    /// unchanged; the JF-780 residuals stand as filed): a resume position far
    /// behind the grown window's edge is NOT honored, because a sliced
    /// no-ENDLIST listing joins at its own live-edge default start (slice end
    /// minus 3x TARGETDURATION), minutes past the requested position. Shape:
    /// head at seg_0062 with the prewrite aged 600s grows the window to 63
    /// entries (head-capped, stable against test latency); 210s resolves to
    /// segment 21; 63 - 21 = 42 entries from the edge, far beyond the 3-entry
    /// lead, so the serve drops the offset and serves the windowed listing
    /// unsliced from segment 0 with the drop log.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_WindowedPrewrite_MidWindowResume_OutsideHonorBand_DropsOffset()
    {
        (string parentIdStr, _, string prewritePath) = PlantLiveAudiobookEncodeFixture(
            "JF-817 Mid Window Book", headSegmentCount: 63, totalRuntime: TimeSpan.FromSeconds(30360));

        // Elapsed 60 segments (600s): the window is min(head + 1 = 63, 63) = 63,
        // head-capped so test latency cannot move it.
        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-600));

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: true, audiobook: true);
        try
        {
            var controller = CreateController(parentIdStr, loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf817-mid-window"));

            ActionResult result = await controller.StreamHlsAudiobook(parentIdStr, TimeSpan.FromSeconds(210).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            // Unsliced from the beginning, capped at the window edge.
            Assert.Equal(63, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0062.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0063", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_3035", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("outside the encode window's honor band", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: false, audiobook: true);
        }
    }

    /// <summary>
    /// JF-817 growth + head-cap pin: the audiobook window grows with the
    /// prewrite's age (one entry per 10s segment, the 1x playback pace) and
    /// NEVER promises beyond the encoded head. First fetch at prewrite age ~0
    /// with the head at seg_0001: the window is head + 1 = 2 entries (below
    /// the lead term; head-capped, stable). The encode then advances to head
    /// seg_0027 and the prewrite ages 600s: the window grows to the new head
    /// cap of 28 entries (the elapsed term 63 would list more), append-only.
    /// Both fetches assert the full-runtime tail (seg_3035) stays absent, the
    /// red discriminator (today's serve lists every prewrite entry).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_WindowedPrewrite_GrowsWithElapsedAndCapsAtHead()
    {
        (string parentIdStr, string hlsDir, string prewritePath) = PlantLiveAudiobookEncodeFixture(
            "JF-817 Window Growth Book", headSegmentCount: 2, totalRuntime: TimeSpan.FromSeconds(30360));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow);

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: true, audiobook: true);
        try
        {
            var controller = CreateController(parentIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf817-growth"));

            ActionResult firstFetch = await controller.StreamHlsAudiobook(parentIdStr);

            var firstContent = Assert.IsType<ContentResult>(firstFetch);
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(firstContent.Content));
            Assert.Contains("seg_0001.ts?token=", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0002", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_3035", firstContent.Content, StringComparison.Ordinal);

            // The encode advances (head to seg_0027) and 600s pass: the window
            // grows to the new head cap (28 entries).
            for (int i = 2; i <= 27; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(hlsDir, $"seg_{i:D4}.ts"), new byte[16]);
            }

            File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-600));

            ActionResult secondFetch = await controller.StreamHlsAudiobook(parentIdStr);

            var secondContent = Assert.IsType<ContentResult>(secondFetch);
            Assert.Equal(28, VideoAudioController.CountSegmentsInPlaylist(secondContent.Content));
            Assert.Contains("seg_0027.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0028", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_3035", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", secondContent.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: false, audiobook: true);
        }
    }

    /// <summary>
    /// JF-817 first-fetch pin (the incident's own site: the 2026-10-08 log
    /// line "serving pre-written playlist for parent e9720b84 (100 chapters)"
    /// is THIS row): the request that STARTS the encode must not be handed
    /// the full prewrite either. The fake ffmpeg writes the first two
    /// segments; the freshly-written prewrite (age ~0) windows to head + 1 =
    /// 2 entries of the 3036-entry book (head-capped at either 1 or 2
    /// segments on disk, so a mid-write race in the fake cannot move the
    /// count). RED on the unmodified tree (3036 entries, seg_3035 present).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_FirstFetch_ServesWindowedPrewriteNotFullListing()
    {
        (Guid parentId, _) = SetupAudiobookParentAndChapters("JF-817 First Fetch Book", TimeSpan.FromSeconds(30360));

        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf817-first-fetch",
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0001.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n#EXTINF:10.000,\\nseg_0001.ts\\n' > \"$playlist_path\"\n" +
            "exit 0\n");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_0001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0002", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_3035", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-817 companion pin: once the encode COMPLETED (ENDLIST cache), the
    /// serve is today's full listing with the exact resume slice; the
    /// live-encode windowing must not leak into the completed-encode row.
    /// 210s resolves to segment 21 of the 3036-segment book, so the slice
    /// spans seg_0021..seg_3035 with the ENDLIST preserved.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CompletedEncode_Resume_ServesFullListingSliced()
    {
        (Guid parentId, _) = SetupAudiobookParentAndChapters("JF-817 Completed Book", TimeSpan.FromSeconds(30360));

        await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 3036, encodedChapterCount: 2, encodedDurationTicks: TimeSpan.FromSeconds(30360).Ticks);

        var controller = CreateController(parentId.ToString(), ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf817-completed"));

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString(), TimeSpan.FromSeconds(210).Ticks);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0021.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_3035.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0020", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:21", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
    }

    // ========== JF-820: foreign-generation stale prewrite under a live encode ==========

    /// <summary>
    /// Shared JF-820 arrange: ONE book (two even-half chapters over
    /// <paramref name="totalRuntime"/>, so a 10s-segment prewrite's entries
    /// are uniform) with TWO generations on disk. The STALE generation sits in
    /// the REQUEST ticks dir (0, the mock's art ticks): a completed prior
    /// encode's leftovers, the documented undeletable debris class (full
    /// playlist-full.m3u8 + all its segments; a fully-encoded dir defeats the
    /// window cap, head + 1 >= total). The LIVE generation sits at
    /// <paramref name="liveHeadSegmentCount"/> head segments plus a fresh
    /// prewrite in a FOREIGN ticks dir (12345), registered the way the
    /// production encode start does (<see cref="VideoAudioCache.RegisterHlsDirectoryPath"/>;
    /// the caller marks the registry live at those ticks through the seam's
    /// explicit-ticks arm). Returns the ids, both dirs, both prewrite paths,
    /// and the live ticks.
    /// </summary>
    private (string ParentIdStr, string LiveHlsDir, string LivePrewritePath, string StaleHlsDir, long LiveTicks)
        PlantForeignGenerationAudiobookFixture(string bookName, int liveHeadSegmentCount, TimeSpan totalRuntime)
    {
        (Guid parentId, var chapters) = SetupAudiobookParentAndChapters(bookName, totalRuntime);
        string parentIdStr = parentId.ToString("D");
        const long liveTicks = 12345L;

        // The STALE generation through the shared core at the REQUEST ticks,
        // with the head = the FULL segment count (the fully-encoded shape) and
        // an OLD prewrite mtime: the aging is what makes the window's growth
        // term (elapsed + lead) reach the segment-count cap and the
        // fully-encoded dir's head cap (head + 1 >= total) defeat the
        // windowing entirely - the filed full-stale-listing serve shape.
        int totalSegments = (int)Math.Ceiling(totalRuntime.TotalSeconds / 10.0);
        (string staleHlsDir, string stalePrewritePath) =
            PlantLiveAudiobookEncodeCore(parentIdStr, 0, chapters, totalSegments);
        File.SetLastWriteTimeUtc(stalePrewritePath, DateTime.UtcNow.AddSeconds(-600));

        (string liveHlsDir, string livePrewritePath) =
            PlantLiveAudiobookEncodeCore(parentIdStr, liveTicks, chapters, liveHeadSegmentCount);
        _cache.RegisterHlsDirectoryPath(parentIdStr, liveTicks, liveHlsDir);

        return (parentIdStr, liveHlsDir, livePrewritePath, staleHlsDir, liveTicks);
    }

    /// <summary>
    /// JF-820 core pin (the filed defect, red on the unmodified guard): a
    /// FOREIGN-ticks request (its own art-ticks dir is dead) reaching the
    /// audiobook concurrent-encode guard while a live encode of the SAME key
    /// runs must be served the RUNNING generation's listing, not the stale
    /// foreign playlist-full.m3u8 sitting in its own dead dir. The stale dir
    /// here is the fully-encoded shape (all 60 segments + full prewrite), so
    /// the pre-fix guard's window cap reads head + 1 >= total and serves the
    /// FULL stale listing: the resume slice then spans seg_0021..seg_0059 (39
    /// entries) whose listing is a foreign generation's debris. The live
    /// generation (head at seg_0022, prewrite aged 200s: window 23 entries;
    /// 210s resolves to segment 21, inside the honor band) serves the 2-entry
    /// slice seg_0021..seg_0022, and the stale tail (seg_0059) must stay
    /// absent. RED PROOF (run before the fix, output in the task notes): the
    /// unmodified guard resolved the prewrite by the bare REQUEST ticks, found
    /// the stale listing, and served its full 39-entry tail
    /// (expected 2 / actual 39, seg_0059 present).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_ForeignTicksRequestUnderLiveEncode_ServesLiveGenerationListingNotStalePrewrite()
    {
        (string parentIdStr, _, string livePrewritePath, string staleHlsDir, long liveTicks) =
            PlantForeignGenerationAudiobookFixture(
                "JF-820 Foreign Ticks Book", liveHeadSegmentCount: 23, totalRuntime: TimeSpan.FromSeconds(600));

        File.SetLastWriteTimeUtc(livePrewritePath, DateTime.UtcNow.AddSeconds(-200));

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: true, audiobook: true, artModifiedTicks: liveTicks);
        try
        {
            var controller = CreateController(parentIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf820-foreign"));

            ActionResult result = await controller.StreamHlsAudiobook(parentIdStr, TimeSpan.FromSeconds(210).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_0021.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0022.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_0059", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
            Assert.True(!File.Exists(Path.Combine(staleHlsDir, "stream.m3u8")), "stale dir must not gain a live listing");
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: false, audiobook: true, artModifiedTicks: liveTicks);
        }
    }

    /// <summary>
    /// JF-820 counterpart pin (the resolution's PRIORITY, not its defect row):
    /// when the caller's OWN ticks generation IS the live one, a foreign
    /// generation's stale playlist-full.m3u8 in ANOTHER ticks dir must not
    /// steal the serve. The own live prewrite (head 23, aged 200s: window 23;
    /// resume 210s = segment 21, inside the honor band) serves the 2-entry
    /// slice seg_0021..seg_0022; the foreign stale dir (a full 60-entry
    /// listing, prewrite-only) would instead serve its own head-floored prefix
    /// from seg_0000. The existing JF-817 pins cover the same-ticks row with
    /// NO foreign dir present; this pin adds the foreign-debris-present
    /// discriminator the JF-820 resolution could regress.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_OwnTicksLiveUnderForeignStaleDir_ServesOwnWindowedPrewrite()
    {
        (string parentIdStr, _, string prewritePath) = PlantLiveAudiobookEncodeFixture(
            "JF-820 Own Ticks Book", headSegmentCount: 23, totalRuntime: TimeSpan.FromSeconds(600));

        // The foreign stale debris in ANOTHER ticks dir: full listing, no
        // segments (the undeletable-prewrite-only leftover shape). The writer
        // only reads RunTimeTicks, so the chapter stubs are local (no second
        // mock-parent arrange for the same book).
        string staleForeignDir = _cache.GetHlsDirectoryPath(parentIdStr, 12345L);
        Directory.CreateDirectory(staleForeignDir);
        var stubChapters = new List<MediaBrowser.Controller.Entities.BaseItem>
        {
            new MediaBrowser.Controller.Entities.Audio.Audio { RunTimeTicks = TimeSpan.FromSeconds(300).Ticks },
            new MediaBrowser.Controller.Entities.Audio.Audio { RunTimeTicks = TimeSpan.FromSeconds(300).Ticks }
        };
        VideoAudioController.WriteAudiobookPlaylist(
            Path.Combine(staleForeignDir, "playlist-full.m3u8"),
            $"/alexaskill/api/video-audio/{parentIdStr}/segments/",
            stubChapters,
            token: null);

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-200));

        VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: true, audiobook: true);
        try
        {
            var controller = CreateController(parentIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf820-own"));

            ActionResult result = await controller.StreamHlsAudiobook(parentIdStr, TimeSpan.FromSeconds(210).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_0021.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_0022.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(parentIdStr, active: false, audiobook: true);
        }
    }

    // ========== JF-819: windowed single-item (song-family) prewrite serve ==========

    /// <summary>
    /// JF-819 core pin (the JF-817 live-edge death on the OTHER common book
    /// shape, red on the unmodified tree): a ONE-chapter audiobook (the
    /// single-file book the JF-794 census found common) redirects into the
    /// single-item core, whose prewrite rows still served the FULL
    /// un-windowed no-ENDLIST listing resume-sliced at <c>?start=</c>: a deep
    /// resume joins at playlist end minus 3x TARGETDURATION on the un-encoded
    /// tail and playback dies (the 2026-10-08 20:35 incident's mechanism at
    /// the song path's own 4s segments). Arithmetic: the book spans 45min
    /// (675 x 4s entries, tail seg_674), the encode head sits at seg_0022
    /// with the prewrite aged 200s (elapsed 50 segments, head-capped: the
    /// window is 23 entries), and the resume at 84s resolves to segment 21,
    /// two entries behind the edge, inside the 3-entry honor band, so the
    /// serve must be exactly the slice seg_021..seg_022. RED PROOF (run
    /// before the fix landed): the unmodified serve returns the full
    /// remaining listing (654 entries, seg_674 present), so the count assert
    /// fails expected 2 / actual 654.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_SingleFileBook_LiveEncodeResume_ServesWindowedSliceNotFullPrewrite()
    {
        (string parentIdStr, string chapterIdStr, _, string prewritePath) = PlantLiveSingleFileBookEncodeFixture(
            "JF-819 Resume Single-File Book", headSegmentCount: 23, chapterRuntime: TimeSpan.FromMinutes(45));

        // Elapsed 50 segments (200s): the window is head + 1 = 23 entries,
        // head-capped so test latency cannot move it; its edge sits 2 entries
        // past the resume segment, inside the honor band.
        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-200));

        VideoAudioController.SetEncodeActiveForTest(chapterIdStr, active: true, song: true);
        try
        {
            var controller = CreateController(parentIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf819-resume"));

            ActionResult result = await controller.StreamHlsAudiobook(parentIdStr, TimeSpan.FromSeconds(84).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            // BEGINS at the resume segment, CAPS at the encoded-region edge.
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_021.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_022.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_020", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_023", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:21", content.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(chapterIdStr, active: false, song: true);
        }
    }

    /// <summary>
    /// JF-819 mid-window resume pin (the JF-778/JF-817 honor-band decision
    /// mirrored unchanged): a resume position far behind the grown window's
    /// edge is NOT honored, because a sliced no-ENDLIST listing joins at its
    /// own live-edge default start (slice end minus 3x TARGETDURATION),
    /// minutes past the requested position. Shape: head at seg_0022 with the
    /// prewrite aged 200s grows the window to 23 entries (head-capped,
    /// stable against test latency); 8s resolves to segment 2; 23 - 2 = 21
    /// entries from the edge, far beyond the 3-entry lead, so the serve
    /// drops the offset and serves the windowed listing unsliced from
    /// segment 0 with the drop log. Driven on the core seam with the song
    /// registry's own item (the row under test is the prewrite serve, not
    /// the redirect; the redirect's startTicks threading is pinned by the
    /// JF-686 single-chapter twins).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_WindowedPrewrite_MidWindowResume_OutsideHonorBand_DropsOffset()
    {
        (string itemIdStr, _, string prewritePath) = PlantLiveVideoAudioEncodeFixture(
            "JF-819 Mid Window Song", headSegmentCount: 23, runtime: TimeSpan.FromMinutes(45));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-200));

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true, song: true);
        try
        {
            var controller = CreateController(itemIdStr, loggerFactory, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf819-mid-window"));

            ActionResult result = await controller.StreamHlsVideoAudioCore(itemIdStr, overrideToken: null, startTicks: TimeSpan.FromSeconds(8).Ticks);

            var content = Assert.IsType<ContentResult>(result);
            // Unsliced from the beginning, capped at the window edge.
            Assert.Equal(23, VideoAudioController.CountSegmentsInPlaylist(content.Content));
            Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.Contains("seg_022.ts?token=", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_023", content.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", content.Content, StringComparison.Ordinal);
            Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
            Assert.Contains(
                TestCaptureLogger.Snapshot(logRecords),
                r => r.Message.Contains("outside the encode window's honor band", StringComparison.Ordinal));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false, song: true);
        }
    }

    /// <summary>
    /// JF-819 growth + head-cap pin (the JF-817 twin at the song path's 4s
    /// segments): the window grows with the prewrite's age (one entry per 4s,
    /// the 1x playback pace) and NEVER promises beyond the encoded head.
    /// First fetch at prewrite age ~0 with the head at seg_0001: the window
    /// is head + 1 = 2 entries. The encode then advances to head seg_0027
    /// and the prewrite ages 600s: the window grows to the new head cap of
    /// 28 entries (the elapsed term 150 would list more), append-only. Both
    /// fetches assert the full-runtime tail (seg_674) stays absent, the red
    /// discriminator (today's serve lists every prewrite entry).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_WindowedPrewrite_GrowsWithElapsedAndCapsAtHead()
    {
        (string itemIdStr, string hlsDir, string prewritePath) = PlantLiveVideoAudioEncodeFixture(
            "JF-819 Window Growth Song", headSegmentCount: 2, runtime: TimeSpan.FromMinutes(45));

        File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow);

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true, song: true);
        try
        {
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf819-growth"));

            ActionResult firstFetch = await controller.StreamHlsVideoAudio(itemIdStr);

            var firstContent = Assert.IsType<ContentResult>(firstFetch);
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(firstContent.Content));
            Assert.Contains("seg_001.ts?token=", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_002", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", firstContent.Content, StringComparison.Ordinal);

            // The encode advances (head to seg_0027) and 600s pass: the window
            // grows to the new head cap (28 entries).
            for (int i = 2; i <= 27; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(hlsDir, $"seg_{i:D3}.ts"), new byte[16]);
            }

            File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-600));

            ActionResult secondFetch = await controller.StreamHlsVideoAudio(itemIdStr);

            var secondContent = Assert.IsType<ContentResult>(secondFetch);
            Assert.Equal(28, VideoAudioController.CountSegmentsInPlaylist(secondContent.Content));
            Assert.Contains("seg_027.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_028", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", secondContent.Content, StringComparison.Ordinal);
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false, song: true);
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
    /// JF-536 scope (c) endpoint level, re-shaped by JF-819: on a cache miss
    /// the single-item path's FIRST serve returns the pre-write listing
    /// WINDOWED to the encoded region (the 2-entry floor when only seg_000
    /// exists), not ffmpeg's growing stream.m3u8 (1 entry, the live-edge
    /// shape the same VideoApp/ExoPlayer consumer showed on the episode
    /// path, 2026-09-09 corr=c0c21c6a) and not the full listing (whose
    /// no-ENDLIST shape puts the player's live-edge default start on the
    /// un-encoded tail: the JF-778 device death at this path's own 4s
    /// segments). The runtime here is the long tail this path exists to fix:
    /// single-chapter-audiobook length. The entry beyond the head is
    /// deliberate: it is inside the JF-503 hold's +2 lookahead, which real
    /// encodes satisfy in under a second per segment.
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CacheMiss_ServesWindowedPrewriteNotLiveEdge()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Long Single Chapter",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks
        };
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
        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString(), 0);
        var reads = TrackPlaylistReads(
            controller,
            Path.Combine(hlsDir, "playlist-full.m3u8"),
            Path.Combine(hlsDir, "stream.m3u8"));

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", content.ContentType);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("seg_001.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_002", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_674", content.Content, StringComparison.Ordinal);
        Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(content.Content));

        // Structural discriminator: at the 2-entry floor the windowed prewrite
        // and a 2-entry fake live partial would be byte-identical after token
        // rewrite, so the content pins alone cannot tell the rows apart (the
        // seg_001 marker is prewrite-only by accident of the fake's shape).
        // The read funnel can: the first-fetch row serves the prewrite and
        // never reads ffmpeg's live playlist (the pre-JF-536 live-partial
        // fallback row would flip Lives to 1).
        Assert.Equal(1, reads.Prewrites());
        Assert.Equal(0, reads.Lives());

        // The pre-written FULL file exists next to ffmpeg's own target (the
        // windowing is serve-side only), and ffmpeg keeps writing its own
        // playlist (the prewrite never feeds ffmpeg's file).
        string prewritePath = Path.Combine(hlsDir, "playlist-full.m3u8");
        Assert.True(File.Exists(prewritePath), "pre-written listing must exist");
        Assert.Equal(675, VideoAudioController.CountSegmentsInPlaylist(File.ReadAllText(prewritePath)));
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
    /// JF-536 mid-encode fetch, re-shaped by JF-819: while the encode flag is
    /// up, playlist re-fetches (the player polls an event playlist) return
    /// the pre-write listing WINDOWED to the encoded region, and the window
    /// is APPEND-ONLY across fetches as the encode advances: a segment
    /// listed by one fetch is never dropped by the next (the JF-531 append
    /// contract, restated for the window; an ExoPlayer media-sequence
    /// regression would reset the player). The full-runtime tail stays OUT
    /// of every mid-encode serve (the JF-778 live-edge death shape).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_ActiveEncode_MidEncodeFetchServesGrowingWindowedListing()
    {
        (string itemIdStr, string hlsDir, string prewritePath) = PlantLiveVideoAudioEncodeFixture(
            "Encoding Song", headSegmentCount: 2, runtime: TimeSpan.FromMinutes(45));

        VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: true, song: true);
        try
        {
            var controller = CreateController(itemIdStr, ffmpegPath: WriteRecordingFakeFfmpeg("fake-ffmpeg-jf536-midencode"));

            ActionResult firstFetch = await controller.StreamHlsVideoAudio(itemIdStr);

            var firstContent = Assert.IsType<ContentResult>(firstFetch);
            Assert.Contains("seg_000.ts?token=", firstContent.Content, StringComparison.Ordinal);
            Assert.Contains("seg_001.ts?token=", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", firstContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", firstContent.Content, StringComparison.Ordinal);
            Assert.Equal(2, VideoAudioController.CountSegmentsInPlaylist(firstContent.Content));

            // The encode advances (four more segments) and 16s pass (the window's
            // growth anchor: prewrite age backdated to simulate elapsed time).
            for (int i = 2; i <= 5; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(hlsDir, $"seg_{i:D3}.ts"), new byte[16]);
            }

            File.SetLastWriteTimeUtc(prewritePath, DateTime.UtcNow.AddSeconds(-16));

            ActionResult secondFetch = await controller.StreamHlsVideoAudio(itemIdStr);

            var secondContent = Assert.IsType<ContentResult>(secondFetch);
            // Append-only: the first fetch's entries survive; the window grew to
            // the new head cap (6 entries), and the tail stays out.
            Assert.Contains("seg_000.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.Contains("seg_005.ts?token=", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_006", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("seg_674", secondContent.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("#EXT-X-ENDLIST", secondContent.Content, StringComparison.Ordinal);
            Assert.Equal(6, VideoAudioController.CountSegmentsInPlaylist(secondContent.Content));
        }
        finally
        {
            VideoAudioController.SetEncodeActiveForTest(itemIdStr, active: false, song: true);
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
            Id = parentId,
            // JF-794 blocker 2: the verdict-aware key needs the folder's Path to
            // verify the chapter sits directly inside (the live server always
            // carries one; the pre-blocker fixture's Path-less folder now routes to
            // the leaf-own key branch).
            Path = "/audiobooks/one-chapter-book"
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
    /// JF-794 gate-marker BLOCKER 2 (the cross-book bleed): the six collapsed census
    /// books share ONE ParentId (the "Audiobooks" container), so the raw
    /// GetAudiobookBookKey record gate keyed every book's segments under the SAME
    /// container key: book B's records were discarded below book A's high-water mark
    /// (RecordSegment drops segmentNumber <= previous), B could never accumulate a
    /// position, and B's resume read A's mark. The verdict-aware gate keys each
    /// book under its OWN leaf id, and the container key stays cold.
    /// RED (the raw-key gate restored): book A's fetch records under the container
    /// key, book B's fetch is discarded below A's mark, both leaf keys read 0, and
    /// B's read returns A's position.
    /// </summary>
    [Fact]
    public async Task GetSegment_TwoCollapsedBooksUnderSharedContainer_EachRecordsUnderOwnLeafKey()
    {
        Guid containerId = Guid.NewGuid();
        string containerIdStr = containerId.ToString("D");
        var container = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "Audiobooks",
            Id = containerId,
            Path = "/audiobooks"
        };
        _libraryManagerMock.Setup(m => m.GetItemById(containerId)).Returns(container);

        Guid bookAId = Guid.NewGuid();
        string bookAIdStr = bookAId.ToString("D");
        var bookA = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Managing Humans",
            Id = bookAId,
            ParentId = containerId,
            Path = "/audiobooks/Managing Humans/Managing Humans.m4b"
        };
        Guid bookBId = Guid.NewGuid();
        string bookBIdStr = bookBId.ToString("D");
        var bookB = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Radical Candor",
            Id = bookBId,
            ParentId = containerId,
            Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
        };
        _libraryManagerMock.Setup(m => m.GetItemById(bookAId)).Returns(bookA);
        _libraryManagerMock.Setup(m => m.GetItemById(bookBId)).Returns(bookB);

        string dirA = _cache.GetHlsDirectoryPath(bookAIdStr, 0);
        Directory.CreateDirectory(dirA);
        await File.WriteAllTextAsync(Path.Combine(dirA, "seg_0300.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(bookAIdStr, 0);
        string dirB = _cache.GetHlsDirectoryPath(bookBIdStr, 0);
        Directory.CreateDirectory(dirB);
        await File.WriteAllTextAsync(Path.Combine(dirB, "seg_0005.ts"), new string('x', 512));
        _cache.RegisterHlsDirectory(bookBIdStr, 0);

        var tracker = CreatePositionTracker("jf794-tracker-crossbook");
        using var trackerSwap = SwapPluginPositionTracker(tracker);

        // One controller per book (each request's token is item-scoped).
        var controllerA = CreateController(bookAIdStr);
        var controllerB = CreateController(bookBIdStr);

        // Book A listens 20 minutes (seg_0300 at 4s); book B listens 20 seconds.
        Assert.IsType<PhysicalFileResult>(await controllerA.GetSegment(bookAIdStr, "seg_0300.ts"));
        Assert.IsType<PhysicalFileResult>(await controllerB.GetSegment(bookBIdStr, "seg_0005.ts"));

        // Each book accumulates under its OWN leaf key (B's conservative position
        // (2-1)*10s; A's (120-1)*10s), and the shared container key stays cold: no
        // book's resume can read a sibling's position.
        Assert.Equal(10 * TimeSpan.TicksPerSecond, tracker.GetPositionTicks(bookBIdStr));
        Assert.Equal(119 * 10 * TimeSpan.TicksPerSecond, tracker.GetPositionTicks(bookAIdStr));
        Assert.Equal(0, tracker.GetPositionTicks(containerIdStr));
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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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
    /// JF-499 W2, the watch-item scenario, re-shaped by JF-778: the SELF-HEAL
    /// RE-ENCODE over interrupted debris carries ?start=16s, but the fresh
    /// encode's window (2-entry floor, head at seg_0000) cannot honor the
    /// position, and the pre-JF-778 behavior (slicing the FULL pre-write at
    /// 16s) was itself the live-edge death shape on device: the sliced listing
    /// is a no-ENDLIST playlist spanning [16s..45min], so the player's default
    /// start landed on its un-encoded END and 404'd. The serve now DROPS the
    /// offset with the JF-686 still-growing log and plays from 0; the position
    /// resumes on the next warm serve (the ENDLIST cache-hit slice, pinned by
    /// StreamHlsEpisode_CacheHit_WithStart_ServesSlicedPlaylist above).
    /// </summary>
    [Fact]
    public async Task StreamHlsEpisode_SelfHealReencode_WithStart_BeyondWindowDropsOffset()
    {
        var (episode, mediaSourceManager) = SetupEpisodeForHls("Interrupted S01E01", "h264", TimeSpan.FromMinutes(45));

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

        // Debris of an interrupted encode: live-looking playlist (no ENDLIST), no
        // active flag.
        string hlsDir = _cache.GetHlsDirectoryPath(episode.Id.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        await File.WriteAllTextAsync(
            Path.Combine(hlsDir, "stream.m3u8"),
            "#EXTM3U\n#EXTINF:4.000,\nseg_0000.ts\n");

        var controller = CreateController(
            episode.Id.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf499-selfheal"));

        ActionResult result = await controller.StreamHlsEpisode(episode.Id.ToString(), 16 * TimeSpan.TicksPerSecond);

        var content = Assert.IsType<ContentResult>(result);
        // The re-encode's windowed listing, UNSLICED from segment 0:
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:0", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("seg_0674", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("#EXT-X-ENDLIST", content.Content, StringComparison.Ordinal);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("dropping the resume", StringComparison.Ordinal));
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
    /// library mocks wired. Returns the parent id and the warm-cache
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
    /// item with the library lookups wired, returning the item id and
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

        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(album);
        _libraryManagerMock.Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { track1, track2 });

        // Writes the first SEGMENT only: the first-segment wait passes, the
        // playlist read one flush cycle later still misses.
        string fakeFfmpegPath = WriteFlushLagFakeFfmpeg("fake-ffmpeg-jf678-flushlag");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);

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

    /// <summary>
    /// JF-763 Decision 1 pin: the album-concat endpoint's TWO isMusicAlbum query
    /// arms route through the ONE album-tracks builder
    /// (<see cref="QueueContinuationFetcher.BuildAlbumTracksQueryUnpaged"/>), not a
    /// hand-kept initializer. AlbumPlayService sums the resume offset against
    /// AlbumTrackOrder and the concat timeline encodes this enumeration, so the rows
    /// encoded must be definitionally the rows the paged head/tail play through; a
    /// hand-kept copy here would drift from a builder evolution silently (the
    /// wrong-track-resume-slice class). Split-album server shape (JF-338 'Jazz
    /// Cafe') so BOTH arms issue: the ParentId page empty, the AlbumIds retry
    /// populated. Each arm asserts the builder's field set INCLUDING the unpaged
    /// invariants (User null, StartIndex/Limit null: fetch-all; null is the SDK's
    /// no-paging value, NOT 0, which is Take(0) per JF-443).
    /// SABOTAGE (verified red 2026-10-05): BuildAlbumTracksQueryCore's
    /// IncludeItemTypes flipped to AudioBook reds the kind assert on BOTH arms
    /// (the endpoint consumes the builder's field set, not a local copy).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AlbumParent_BothArms_RouteThroughTheAlbumTracksBuilderUnpaged()
    {
        Guid parentId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Name = "JF-763 Fold-In Album",
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

        var captured = new List<MediaBrowser.Controller.Entities.InternalItemsQuery>();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Callback<MediaBrowser.Controller.Entities.InternalItemsQuery>(q => captured.Add(q))
            .Returns((MediaBrowser.Controller.Entities.InternalItemsQuery q) =>
                q.AlbumIds != null && q.AlbumIds.Contains(parentId)
                    ? new List<MediaBrowser.Controller.Entities.BaseItem> { track1, track2 }
                    : new List<MediaBrowser.Controller.Entities.BaseItem>());

        string fakeFfmpegPath = WriteFlushLagFakeFfmpeg("fake-ffmpeg-jf763-foldin");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        // Both album arms issued in order (folder primary, AlbumIds retry); the
        // retry found the tracks, so the endpoint proceeded past resolution (any
        // 404/503 short-circuit would leave captured at 0 or 1).
        Assert.Equal(2, captured.Count);

        void AssertBuilderArm(MediaBrowser.Controller.Entities.InternalItemsQuery arm, bool byAlbumIds)
        {
            // The builder's shared field set (the paged twin of these asserts lives
            // in ProgressiveQueueTests' lockstep pin; these are the endpoint's OWN).
            Assert.True(arm.Recursive);
            Assert.Equal(new[] { Jellyfin.Data.Enums.BaseItemKind.Audio }, arm.IncludeItemTypes);
            Assert.Equal(QueueContinuationFetcher.AlbumTrackOrder, arm.OrderBy);
            Assert.Equal(new MediaBrowser.Controller.Dto.DtoOptions(true).Fields, arm.DtoOptions!.Fields);
            // The JF-358 discipline the builder's doc declares for this shape.
            AssertNoMediaTypesFilter(arm, "album-concat builder arm");

            // The unpaged invariants: no session user on the token-gated HTTP path,
            // no paging (null = fetch-all, NOT Limit=0 = Take(0), JF-443).
            Assert.Null(arm.User);
            Assert.Null(arm.StartIndex);
            Assert.Null(arm.Limit);

            // The ONE arm difference: the scoping field.
            if (byAlbumIds)
            {
                Assert.Equal(new[] { parentId }, arm.AlbumIds);
                Assert.Equal(Guid.Empty, arm.ParentId);
            }
            else
            {
                Assert.Equal(parentId, arm.ParentId);
                Assert.Empty(arm.AlbumIds ?? Array.Empty<Guid>());
            }
        }

        AssertBuilderArm(captured[0], byAlbumIds: false);
        AssertBuilderArm(captured[1], byAlbumIds: true);

        // The tracks were found, so the endpoint proceeded PAST resolution to the
        // encode and served its degrade row (the JF-678 flush-lag shape: the lazy
        // PhysicalFile), not a 404 (queries never issued) and not a 500 (a
        // post-resolution runtime failure this pin must also catch).
        Assert.IsType<PhysicalFileResult>(result);
    }

    /// <summary>
    /// JF-767 Finding B pin: the concat endpoint applies the library scope the JF-309
    /// token carries. A restricted user's launch mints the scoped token (raw
    /// AllowedLibraryIds ids, see PlaybackLaunchBuilder.GetAudiobookResumeUrl); the
    /// endpoint resolves them to TopParentIds with the SAME resolver the paged path
    /// uses and filters BOTH isMusicAlbum arms (folder primary + JF-338 AlbumIds
    /// retry), so the encoded concat timeline IS the scoped timeline
    /// AlbumPlayService.BuildAlbumPlayResponseAsync summed the seek-mode resume
    /// offset over (the JF-763 residual closed). SABOTAGE (verified red): removing
    /// the scope application at the endpoint reddens this pin (both arms enumerate
    /// with empty TopParentIds).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AlbumParent_ScopedToken_EnumeratesUnderTokenLibraryScope()
    {
        Guid musicLib = Guid.NewGuid();
        var (captured, result) = await ServeSplitAlbumConcatWithTokenScope(new[] { musicLib }, "jf767-scoped");

        // Both album arms issued (split shape) and BOTH carry the token's scope.
        Assert.Equal(2, captured.Count);
        Assert.Equal(new[] { musicLib }, captured[0].TopParentIds);
        Assert.Equal(new[] { musicLib }, captured[1].TopParentIds);
        Assert.IsType<PhysicalFileResult>(result);
    }

    /// <summary>
    /// The backward-compat row (JF-767 Finding B): a legacy two-field token (an
    /// unrestricted user, or any URL minted before the change within its 10h TTL)
    /// carries no scope, and the endpoint enumerates UNSCOPED exactly as before.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AlbumParent_LegacyToken_StaysUnscoped()
    {
        var (captured, result) = await ServeSplitAlbumConcatWithTokenScope(tokenScope: null, "jf767-legacy");

        // Both arms issued (the split shape resolves via the retry) and NEITHER
        // carries a scope.
        Assert.Equal(2, captured.Count);
        Assert.Empty(captured[0].TopParentIds);
        Assert.Empty(captured[1].TopParentIds);
        Assert.IsType<PhysicalFileResult>(result);
    }

    /// <summary>
    /// Shared fixture for the two JF-767 endpoint rows: a split-shape MusicAlbum
    /// concat request (empty folder pages, populated AlbumIds pages) under the given
    /// token scope, capturing every query the endpoint issues.
    /// </summary>
    private async Task<(List<MediaBrowser.Controller.Entities.InternalItemsQuery> Captured, ActionResult Result)> ServeSplitAlbumConcatWithTokenScope(
        Guid[]? tokenScope,
        string fakeFfmpegTag)
    {
        Guid parentId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Name = $"Concat Album ({fakeFfmpegTag})",
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
        // Scope resolution: GetItemById of any library id is unstubbed (returns null),
        // so ResolveTopParentIds unions nothing and TopParentIds == the token's ids.

        var captured = new List<MediaBrowser.Controller.Entities.InternalItemsQuery>();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Callback<MediaBrowser.Controller.Entities.InternalItemsQuery>(q => captured.Add(q))
            .Returns((MediaBrowser.Controller.Entities.InternalItemsQuery q) =>
                q.AlbumIds != null && q.AlbumIds.Contains(parentId)
                    ? new List<MediaBrowser.Controller.Entities.BaseItem> { track1, track2 }
                    : new List<MediaBrowser.Controller.Entities.BaseItem>());

        string fakeFfmpegPath = WriteFlushLagFakeFfmpeg($"fake-ffmpeg-{fakeFfmpegTag}");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath, tokenScope: tokenScope);
        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());
        return (captured, result);
    }

    /// <summary>
    /// The ternary's other leg, SUPERSEDED by JF-784 leg 3 (the JF-767
    /// gate-marker's kind-axis divergence): a NON-MusicAlbum parent (an
    /// audiobook folder) now routes through the ONE chapters builder's unpaged
    /// form (<see cref="QueueContinuationFetcher.BuildAudiobookChaptersQueryUnpaged"/>),
    /// the SAME MediaTypes=Audio axis the PlayBook head/confirm/tail queue
    /// enumerates, so the concat encodes the rows the queue queued. The JF-763
    /// shape kept a LOCAL IncludeItemTypes=AudioBook initializer: a book parent
    /// with Audio-typed children (a metadata remap, or a fully Audio-typed
    /// folder) was queued by MediaTypes=Audio but enumerated 0-or-subset rows at
    /// the endpoint (404 for a book the confirm just launched). NO AlbumTrackOrder
    /// and no explicit IncludeItemTypes; the JF-672 explicit chapter order is the
    /// shared AudiobookChapterOrder (asserted below). The chapters query is
    /// captured field-for-field.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AudiobookParent_RoutesThroughTheChaptersBuilderUnpaged()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-763 Book Folder",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);

        var captured = new List<MediaBrowser.Controller.Entities.InternalItemsQuery>();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Callback<MediaBrowser.Controller.Entities.InternalItemsQuery>(q => captured.Add(q))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        string fakeFfmpegPath = WriteFlushLagFakeFfmpeg("fake-ffmpeg-jf763-audiobook");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        await controller.StreamHlsAudiobook(parentId.ToString());

        MediaBrowser.Controller.Entities.InternalItemsQuery chaptersQuery = Assert.Single(captured);
        Assert.Equal(parentId, chaptersQuery.ParentId);
        // The builder's kind axis (the queue's axis), NOT the local AudioBook
        // kind filter the JF-763 shape kept.
        Assert.Equal(new[] { Jellyfin.Data.Enums.MediaType.Audio }, chaptersQuery.MediaTypes);
        Assert.True(
            chaptersQuery.IncludeItemTypes == null || chaptersQuery.IncludeItemTypes.Length == 0,
            "the chapters builder must not grow an IncludeItemTypes filter");
        Assert.True(chaptersQuery.Recursive);
        // JF-672 supersedes the old no-order assert: the endpoint carries the SAME
        // explicit chapter order the paged queue runs (constant equality, the exact
        // form of the MusicAlbum arm's AlbumTrackOrder assert above), NOT
        // AlbumTrackOrder itself (an album-style disc/track sort would reorder the
        // book; the probe-backed rationale lives on the constant's doc and in the
        // JF-672 task record).
        Assert.Equal(
            QueueContinuationFetcher.AudiobookChapterOrder,
            chaptersQuery.OrderBy);
        Assert.NotNull(chaptersQuery.DtoOptions);
        // The unpaged invariants (the endpoint twin of the album-arm asserts):
        // no session user on the token-gated HTTP path, no paging (null is the
        // SDK's fetch-all, NOT 0 which is Take(0), JF-443).
        Assert.Null(chaptersQuery.User);
        Assert.Null(chaptersQuery.StartIndex);
        Assert.Null(chaptersQuery.Limit);
    }

    /// <summary>
    /// JF-784 leg 3 RED pin, the filed 404 shape: a fully Audio-typed book
    /// folder (every chapter BaseItemKind.Audio, the metadata-remap shape) is
    /// queued and launched by the PlayBook path (MediaTypes=Audio), but the
    /// pre-fix endpoint's IncludeItemTypes=AudioBook query enumerated 0 rows and
    /// 404ed the very URL the launch minted. The mock answers CONDITIONED on the
    /// query's kind axis (rows only when MediaTypes constrains to Audio, the
    /// server's actual behavior for the two axes), so the pre-fix endpoint
    /// reproduces the 404 and the post-fix one concatenates. SABOTAGE (red on
    /// the unmodified tree): reverting the arm to the local AudioBook
    /// initializer flips this pin to NotFoundObjectResult.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AudioTypedBookFolder_EnumeratesViaMediaTypesNotAudioBookKind()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-784 Audio-Typed Book",
            Id = parentId
        };
        var chapters = new List<MediaBrowser.Controller.Entities.BaseItem>();
        for (int i = 1; i <= 3; i++)
        {
            chapters.Add(new MediaBrowser.Controller.Entities.Audio.Audio
            {
                Name = $"JF-784 Chapter {i}",
                Id = Guid.NewGuid(),
                Path = $"/book/jf784-{i:000}.mp3",
                RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
            });
        }

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);

        var captured = new List<MediaBrowser.Controller.Entities.InternalItemsQuery>();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Callback<MediaBrowser.Controller.Entities.InternalItemsQuery>(q => captured.Add(q))
            .Returns((MediaBrowser.Controller.Entities.InternalItemsQuery q) =>
                q.MediaTypes != null && q.MediaTypes.Contains(Jellyfin.Data.Enums.MediaType.Audio)
                    ? chapters
                    : new List<MediaBrowser.Controller.Entities.BaseItem>());

        string fakeFfmpegPath = WriteFlushLagFakeFfmpeg("fake-ffmpeg-jf784-audiotyped");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        // Post-fix: the MediaTypes=Audio query found the chapters and the
        // endpoint proceeded to the concat encode and served its pre-written
        // listing (the audiobook first-fetch row), never the 404 the
        // AudioBook-kind query produced.
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0000.ts?token=", content.Content, StringComparison.Ordinal);
        MediaBrowser.Controller.Entities.InternalItemsQuery chaptersQuery = Assert.Single(captured);
        Assert.Equal(new[] { Jellyfin.Data.Enums.MediaType.Audio }, chaptersQuery.MediaTypes);
    }

    /// <summary>
    /// JF-784 leg 3, the MIXED-children shape the filing names: a book parent
    /// with AudioBook chapters PLUS an Audio-typed sibling (a metadata remap)
    /// is queued in full by the PlayBook path (MediaTypes=Audio), so the concat
    /// input must list ALL of them; the pre-fix AudioBook-kind arm enumerated
    /// only the AudioBook subset and encoded a shorter timeline than the queue
    /// plays. The mock answers conditioned on the query's kind axis (the
    /// IncludeItemTypes=AudioBook arm sees only the AudioBook subset, the
    /// MediaTypes=Audio arm sees every child), so the pre-fix tree reproduces
    /// the subset concat. RED on the unmodified tree: the Audio sibling's
    /// stream URL is missing from chapters.txt.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_MixedKindChildren_ConcatListsAudioTypedSiblings()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-784 Mixed-Kind Book",
            Id = parentId
        };
        var audioBookChapter1 = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-mixed-001.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var audioBookChapter2 = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-mixed-002.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var audioSibling = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Remapped Chapter 3",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-mixed-003.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);

        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns((MediaBrowser.Controller.Entities.InternalItemsQuery q) =>
                q.MediaTypes != null && q.MediaTypes.Contains(Jellyfin.Data.Enums.MediaType.Audio)
                    ? new List<MediaBrowser.Controller.Entities.BaseItem> { audioBookChapter1, audioBookChapter2, audioSibling }
                    : new List<MediaBrowser.Controller.Entities.BaseItem> { audioBookChapter1, audioBookChapter2 });

        string fakeFfmpegPath = WriteFlushLagFakeFfmpeg("fake-ffmpeg-jf784-mixed");

        var controller = CreateController(parentId.ToString(), ffmpegPath: fakeFfmpegPath);

        await controller.StreamHlsAudiobook(parentId.ToString());

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        string concatList = await File.ReadAllTextAsync(Path.Combine(hlsDir, "chapters.txt"));
        Assert.Contains(audioBookChapter1.Id.ToString(), concatList, StringComparison.Ordinal);
        Assert.Contains(audioBookChapter2.Id.ToString(), concatList, StringComparison.Ordinal);
        // The discriminating row: the Audio-typed sibling the AudioBook-kind
        // arm dropped.
        Assert.Contains(audioSibling.Id.ToString(), concatList, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-784 review F1 pin: the copy-compatibility gate covers the AUDIOBOOK
    /// arm too. The arm used to hardcode <c>-c:a copy</c> on the assumption its
    /// AudioBook-kind chapters were codec-uniform; since leg 3 the arm
    /// enumerates the queue's MediaTypes=Audio rows, where a non-copy codec
    /// (an ALAC remap among MP3 chapters) must transcode the concat to AAC the
    /// way the album arm does, or the copy silently truncates the output at the
    /// first non-matching track (exit 0) and the undercount verdict re-encodes
    /// into the same truncation forever. RED on the pre-fix tree (the arm
    /// hardcodes copy): the recorded args carry <c>-c:a copy</c>.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_MixedCodecs_TranscodesInsteadOfCopy()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-784 Mixed-Codec Book",
            Id = parentId
        };
        var mp3Chapter = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "MP3 Chapter",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-codec-001.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var alacChapter = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "ALAC Remap Chapter",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-codec-002.m4a",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(mp3Chapter.Id))
            .Returns(new List<MediaStream> { new() { Type = MediaStreamType.Audio, Codec = "mp3" } });
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(alacChapter.Id))
            .Returns(new List<MediaStream> { new() { Type = MediaStreamType.Audio, Codec = "alac" } });

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { mp3Chapter, alacChapter });

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
        var controller = CreateController(
            parentId.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf784-mixedcodec"));

        await controller.StreamHlsAudiobook(parentId.ToString());

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        string[] argLines = await File.ReadAllLinesAsync(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Contains("aac", argLines);
        Assert.Contains("192k", argLines);
        Assert.DoesNotContain("copy", argLines);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("mixed or non-copy audio codecs", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-784 review F3 pin: a HEALTHY concat encode (3 playlist segments for
    /// 2 short chapters; the concat cuts ~one 10s segment per 10s of chapter)
    /// must log the monitor's complete row, never INCOMPLETE. The pre-fix
    /// monitor compared segment count to chapter count with EQUALITY, so every
    /// healthy book/album encode warned INCOMPLETE (3000 segments vs 92
    /// chapters), drowning the real incomplete signal the sidecar exists for.
    /// RED on the pre-fix tree: the INCOMPLETE warning fires for 3 != 2.
    /// The chapters are 12s each so the JF-784 tail's duration-derived floor
    /// (2 chapters / 24s of audio) also passes: the healthy row must hold on
    /// BOTH floors.
    /// </summary>
    [Fact]
    public async Task MonitorHls_HealthyConcatEncode_SegmentsAboveChapterCount_LogsCompleteNotIncomplete()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-784 Monitor Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-monitor-001.mp3",
            RunTimeTicks = TimeSpan.FromSeconds(12).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-monitor-002.mp3",
            RunTimeTicks = TimeSpan.FromSeconds(12).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        // Healthy encode: three segments cover both 12s chapters (floor 2),
        // ENDLIST, exit 0.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf784-monitor-healthy",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0001.ts\" 2>/dev/null\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0002.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n#EXTINF:10.000,\\nseg_0001.ts\\n#EXTINF:10.000,\\nseg_0002.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());
        Assert.IsType<ContentResult>(result);

        Assert.True(
            await WaitUntilAsync(
                () => TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("Audiobook HLS encoding complete", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)),
            "the monitor must report the healthy encode as complete");
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("HLS encoding INCOMPLETE", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-784 gate-marker tail F1 pin, the true-positive direction the worker's
    /// F3 fix unguarded: a concat encode that truncates at exit 0 keeps listing
    /// FAR more segments than it has chapters (two 10-minute chapters = 120
    /// segment-seconds of expected audio; the encode delivered 3), so the bare
    /// chapter-count floor sees only near-total failures and logs "complete".
    /// The duration-derived floor must fire INCOMPLETE here. RED on the
    /// pre-tail tree: the monitor logs the complete Debug row (3 >= 2
    /// chapters).
    /// </summary>
    [Fact]
    public async Task MonitorHls_TruncatedConcatEncode_SegmentsAboveChapterCountButBelowDuration_LogsIncomplete()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-784 Truncated Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-monitor-trunc-001.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-monitor-trunc-002.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        // Truncated encode: 3 segments cover only 30s of the 20min the sidecar
        // demands (floor 115), but 3 >= 2 chapters passes the old bar.
        string fakeFfmpegPath = WriteFakeFfmpeg("fake-ffmpeg-jf784-monitor-truncated",
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0001.ts\" 2>/dev/null\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0002.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:10.000,\\nseg_0000.ts\\n#EXTINF:10.000,\\nseg_0001.ts\\n#EXTINF:10.000,\\nseg_0002.ts\\n#EXT-X-ENDLIST\\n' > \"$last_arg\"\n" +
            "exit 0\n");

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
        var controller = CreateController(parentId.ToString(), loggerFactory, ffmpegPath: fakeFfmpegPath);

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());
        Assert.IsType<ContentResult>(result);

        Assert.True(
            await WaitUntilAsync(
                () => TestCaptureLogger.Snapshot(logRecords).Any(r => r.Message.Contains("HLS encoding INCOMPLETE", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)),
            "the monitor must flag the truncated-at-exit-0 encode as incomplete");
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("Audiobook HLS encoding complete", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-784 gate-marker tail F2 pin: when NO chapter's audio codec can be
    /// resolved (a transient media-streams failure, or a fresh scan with no
    /// stream rows), the copy-compatibility gate must keep <c>-c:a copy</c>
    /// (the pre-gate behavior) instead of fail-closing into a full-book AAC
    /// transcode: unknown is missing information, not confirmed incompatibility,
    /// and the transcode cost lands on the longest content the plugin serves.
    /// A RESOLVED non-copy codec still transcodes (the sibling pin above). RED
    /// on the pre-tail tree: the all-null shape transcodes (aac/192k args).
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_AllCodecsUnresolvable_KeepsCopyInsteadOfTranscode()
    {
        Guid parentId = Guid.NewGuid();
        var book = new MediaBrowser.Controller.Entities.Folder
        {
            Name = "JF-784 Unresolvable-Codec Book",
            Id = parentId
        };
        var chapter1 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-nullcodec-001.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };
        var chapter2 = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Chapter 2",
            Id = Guid.NewGuid(),
            Path = "/book/jf784-nullcodec-002.mp3",
            RunTimeTicks = TimeSpan.FromMinutes(10).Ticks
        };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetMediaStreams(It.IsAny<Guid>()))
            .Returns(new List<MediaStream>());

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(book);
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { chapter1, chapter2 });

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
        var controller = CreateController(
            parentId.ToString(), loggerFactory, mediaSourceManager, WriteRecordingFakeFfmpeg("fake-ffmpeg-jf784-nullcodec"));

        await controller.StreamHlsAudiobook(parentId.ToString());

        string hlsDir = _cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        string[] argLines = await File.ReadAllLinesAsync(Path.Combine(hlsDir, "episode-args.txt"));
        Assert.Contains("copy", argLines);
        Assert.DoesNotContain("192k", argLines);
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("mixed or non-copy audio codecs", StringComparison.Ordinal));
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("no chapter audio codec resolvable", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-784 gate-marker tail F1 pin, the SERVE-TIME twin: a completed cache
    /// entry whose playlist carries far fewer segments than the live
    /// enumeration's runtime sum demands (the monitor's truncated-at-exit-0
    /// shape persisted to disk: 30 segments for 36 minutes of chapters) is
    /// debris and must invalidate to a scoped re-encode, not serve the
    /// truncated timeline. The bare chapter-count floor passed it (30 >= 12).
    /// RED on the pre-tail tree: the cache-hit verdict validates and serves.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CacheHit_SegmentsBelowDurationFloor_IsInvalidatedAsDebris()
    {
        Guid parentId = Guid.NewGuid();
        long threeMinutes = TimeSpan.FromMinutes(3).Ticks;
        await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 30, encodedChapterCount: 12, encodedDurationTicks: 12 * threeMinutes);

        var (captured, logRecords, result) = await ServeAlbumOverSeededConcatCacheAsync(
            parentId, trackCount: 12, tokenScope: new[] { Guid.NewGuid() }, "jf784-duration-floor");

        AssertCacheInvalidatedAndReencoded(logRecords, result, "segments");
        Assert.Single(captured);
    }

    /// <summary>
    /// Write the JF-292 encode-metadata sidecar into a concat cache generation
    /// directory (the JF-784 timeline verdict's comparison input), synchronously
    /// for the plant lambdas that seed inside a lock callback.
    /// </summary>
    private static void WriteEncodeMetadata(string hlsDir, int chapterCount, long durationTicks)
        => File.WriteAllText(
            Path.Combine(hlsDir, "encode-metadata.json"),
            $$"""{"ExpectedChapterCount":{{chapterCount}},"ExpectedDurationTicks":{{durationTicks}}}""");

    /// <summary>
    /// Seed a COMPLETED concat cache generation (ENDLIST playlist with
    /// <paramref name="segmentCount"/> segments) plus the JF-292
    /// encode-metadata sidecar the JF-784 timeline verdict reads. A null
    /// <paramref name="encodedChapterCount"/> omits the sidecar entirely (the
    /// pre-sidecar / wiped-sidecar shape; <paramref name="encodedDurationTicks"/>
    /// is then ignored). Returns the playlist path.
    /// </summary>
    private static async Task<string> SeedCompletedConcatCacheAsync(
        VideoAudioCache cache,
        Guid parentId,
        int segmentCount,
        int? encodedChapterCount,
        long encodedDurationTicks)
    {
        string hlsDir = cache.GetHlsDirectoryPath(parentId.ToString(), 0);
        Directory.CreateDirectory(hlsDir);
        var playlist = new System.Text.StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
        for (int i = 0; i < segmentCount; i++)
        {
            playlist.Append(CultureInfo.InvariantCulture, $"#EXTINF:10.000,\nseg_{i:0000}.ts\n");
        }

        playlist.Append("#EXT-X-ENDLIST\n");
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(playlistPath, playlist.ToString());
        if (encodedChapterCount is { } chapterCount)
        {
            WriteEncodeMetadata(hlsDir, chapterCount, encodedDurationTicks);
        }

        return playlistPath;
    }

    /// <summary>
    /// The shared arrangement of the JF-784 leg 1 timeline pins: a MusicAlbum
    /// whose (scoped) enumeration is <paramref name="trackCount"/> Audio tracks
    /// of 3 minutes each, over a seeded completed concat cache. Captures every
    /// query; the request presents the given token scope.
    /// </summary>
    private async Task<(List<MediaBrowser.Controller.Entities.InternalItemsQuery> Captured, List<(LogLevel Level, string Message)> LogRecords, ActionResult Result)> ServeAlbumOverSeededConcatCacheAsync(
        Guid parentId,
        int trackCount,
        Guid[]? tokenScope,
        string fakeFfmpegTag)
    {
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Name = $"JF-784 Timeline Album ({fakeFfmpegTag})",
            Id = parentId
        };
        var tracks = new List<MediaBrowser.Controller.Entities.BaseItem>();
        for (int i = 1; i <= trackCount; i++)
        {
            tracks.Add(new MediaBrowser.Controller.Entities.Audio.Audio
            {
                Name = $"Track {i}",
                Id = Guid.NewGuid(),
                RunTimeTicks = TimeSpan.FromMinutes(3).Ticks
            });
        }

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(parentId)).Returns(album);

        var captured = new List<MediaBrowser.Controller.Entities.InternalItemsQuery>();
        _libraryManagerMock
            .Setup(m => m.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Callback<MediaBrowser.Controller.Entities.InternalItemsQuery>(q => captured.Add(q))
            .Returns(tracks);

        var logRecords = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(logRecords);
        var controller = CreateController(
            parentId.ToString(),
            loggerFactory,
            ffmpegPath: WriteFlushLagFakeFfmpeg($"fake-ffmpeg-{fakeFfmpegTag}"),
            tokenScope: tokenScope);
        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());
        return (captured, logRecords, result);
    }

    /// <summary>
    /// JF-784 leg 1 RED pin, the filed shape: a cache entry encoded under a
    /// DIFFERENT timeline (an unrestricted user's 15-track enumeration) serves
    /// unchanged to a scoped request enumerating 12 tracks. Pre-fix the
    /// cache-hit verdict validated (the seeded 270 segments clear every
    /// undercount bar) and served the foreign timeline's ContentResult; post-fix
    /// the verdict compares the encode-metadata sidecar against the CURRENT
    /// scoped enumeration and invalidates (re-encode, the flush-lag degrade
    /// row). SABOTAGE (red on the unmodified tree): the pin asserts the
    /// invalidation log and the re-encode result; the pre-fix tree serves the
    /// cached ContentResult with neither.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CacheHit_EncodedUnderDifferentTimeline_IsInvalidatedAndReencoded()
    {
        Guid parentId = Guid.NewGuid();
        long threeMinutes = TimeSpan.FromMinutes(3).Ticks;
        // 270 segments = the 45-minute sidecar timeline's own segment count (one
        // per 10s), so the duration floor passes and the TIMELINE verdict is the
        // row under test, not the undercount bar.
        await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 270, encodedChapterCount: 15, encodedDurationTicks: 15 * threeMinutes);

        var (captured, logRecords, result) = await ServeAlbumOverSeededConcatCacheAsync(
            parentId, trackCount: 12, tokenScope: new[] { Guid.NewGuid() }, "jf784-foreign-timeline");

        // The scoped enumeration issued (the album arm found the 12 tracks, no retry).
        Assert.Single(captured);
        AssertCacheInvalidatedAndReencoded(logRecords, result, "different timeline");
    }

    /// <summary>
    /// The three invalidation rows' shared terminal assert: the verdict fired
    /// with the discriminating reason fragment, and the request re-encoded
    /// (the flush-lag degrade row) instead of serving the cached entry.
    /// </summary>
    private static void AssertCacheInvalidatedAndReencoded(
        List<(LogLevel Level, string Message)> logRecords,
        ActionResult result,
        string reasonFragment)
    {
        Assert.IsType<PhysicalFileResult>(result);
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("Audiobook HLS cache invalidated", StringComparison.Ordinal)
                && r.Message.Contains(reasonFragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-784 leg 1, the SAME-timeline row: when the sidecar matches the
    /// current scoped enumeration exactly (count and duration), the completed
    /// entry serves unchanged (no re-encode, no invalidation). This is the
    /// no-thrash guarantee for the common single-scope household and for two
    /// scopes that select the SAME membership.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CacheHit_SameTimeline_ServesCachedEntry()
    {
        Guid parentId = Guid.NewGuid();
        long threeMinutes = TimeSpan.FromMinutes(3).Ticks;
        // 216 segments = the 36-minute timeline's own segment count (one per
        // 10s): coherent with the duration floor, so the verdict validates.
        await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 216, encodedChapterCount: 12, encodedDurationTicks: 12 * threeMinutes);

        var (captured, logRecords, result) = await ServeAlbumOverSeededConcatCacheAsync(
            parentId, trackCount: 12, tokenScope: new[] { Guid.NewGuid() }, "jf784-same-timeline");

        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("seg_0215.ts?token=", content.Content, StringComparison.Ordinal);
        Assert.Single(captured);
        // The cache-hit serve row fired ("generating concat stream" logs on every
        // multi-chapter request, cache hit included; "Audiobook chapter sort" is
        // the encode branch's first log and the honest no-re-encode signal).
        Assert.Contains(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("serving cached playlist for parent", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("HLS cache invalidated", StringComparison.Ordinal));
        Assert.DoesNotContain(
            TestCaptureLogger.Snapshot(logRecords),
            r => r.Message.Contains("Audiobook chapter sort", StringComparison.Ordinal));
    }

    /// <summary>
    /// JF-784 leg 1, the duration axis: an equal chapter COUNT with a different
    /// duration sum (two scopes selecting different memberships of equal size,
    /// or an in-place runtime edit between encode and serve) is still a
    /// different timeline and invalidates. Both compared numbers are DB
    /// RunTimeTicks sums, so the comparison needs no tolerance.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CacheHit_CountEqualDurationDifferent_IsInvalidated()
    {
        Guid parentId = Guid.NewGuid();
        long threeMinutes = TimeSpan.FromMinutes(3).Ticks;
        // 270 segments = the SIDEcar's 45-minute timeline's own count, so the
        // undercount bar (floored on the live 36-minute enumeration) passes and
        // the duration-axis comparison is the row under test.
        await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 270, encodedChapterCount: 12, encodedDurationTicks: 15 * threeMinutes);

        var (_, logRecords, result) = await ServeAlbumOverSeededConcatCacheAsync(
            parentId, trackCount: 12, tokenScope: new[] { Guid.NewGuid() }, "jf784-duration-axis");

        AssertCacheInvalidatedAndReencoded(logRecords, result, "different timeline");
    }

    /// <summary>
    /// JF-784 leg 1, the fail-closed row: a completed entry with NO
    /// encode-metadata sidecar (a pre-sidecar cache, or a wiped sidecar) cannot
    /// prove its timeline identity and is stale: one re-encode re-establishes
    /// the sidecar. Never a silent serve of an unverifiable timeline.
    /// </summary>
    [Fact]
    public async Task StreamHlsAudiobook_CacheHit_NoEncodeMetadata_IsInvalidatedFailClosed()
    {
        Guid parentId = Guid.NewGuid();
        // 216 segments = the live 36-minute enumeration's own segment count, so
        // the undercount bar passes and the MISSING SIDECAR is the row under
        // test.
        await SeedCompletedConcatCacheAsync(
            _cache, parentId, segmentCount: 216, encodedChapterCount: null, encodedDurationTicks: 0);

        var (_, logRecords, result) = await ServeAlbumOverSeededConcatCacheAsync(
            parentId, trackCount: 12, tokenScope: new[] { Guid.NewGuid() }, "jf784-no-metadata");

        AssertCacheInvalidatedAndReencoded(logRecords, result, "no encode metadata");
    }

    // ---- W4: permission-denied deletes must not surface as 500s ----

    /// <summary>
    /// The async, result-passing sibling of <see cref="AssertSurvivesDeniedDirectory"/>
    /// (JF-774): await the act inside the write-denied directory, always restoring
    /// the mode for the fixture's recursive temp cleanup, and hand the act's result
    /// back for the caller's content assertions. ASSERTS THE DENIAL HELD (the
    /// directory survived the act): a root runner is not restricted by file
    /// modes, the act's cleanups then succeed, and an undeletable-class pin would
    /// silently degrade to the deletable shape while staying green (JF-774 review
    /// finding 5); contract: the act must not legitimately remove the directory.
    /// </summary>
    private static async Task<T> RunWithDeniedDirectoryAsync<T>(string dir, Func<Task<T>> act)
    {
#pragma warning disable CA1416, CA3003 // Unix-only test; test-created path
        File.SetUnixFileMode(dir, ReadOnlyDirMode);
        try
        {
            T result = await act().ConfigureAwait(false);
            // Assert inside the try: the act's exception keeps precedence when both
            // fire, and the finally still restores writability either way (a failing
            // assert must not leave the dir read-only for the fixture cleanup).
            Assert.True(
                Directory.Exists(dir),
                $"the write-denied directory did not survive the act ({dir}): the denial never held (running as root?) and the undeletable-class pin is invalid on this runner");
            return result;
        }
        finally
        {
            File.SetUnixFileMode(dir, WritableDirMode);
        }
#pragma warning restore CA1416, CA3003
    }

    // Returned to the serialized Plugin collection (JF-792 gate-marker tail F2): the
    // 2-second liveness margin is a wall-clock assertion and the parallel phase can
    // preempt the test thread past the shell's exit on a contended runner.
    [Fact]
    public void SafeExitCode_LiveProcess_ReturnsMinusOne()
    {
        // 2s sleep: the process is deterministically alive at the check right after Start.
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 2\""))!;
        Assert.Equal(-1, VideoAudioController.SafeExitCode(process));
        process.Kill();
        process.WaitForExit();
    }


}
