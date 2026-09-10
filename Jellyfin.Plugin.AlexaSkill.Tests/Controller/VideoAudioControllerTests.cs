using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
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
        _tempDir = Path.Combine(Path.GetTempPath(), "va-ctrl-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

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
    }

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
            b.AddProvider(new CaptureLoggerProvider(logRecords));
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
            b.AddProvider(new CaptureLoggerProvider(logRecords));
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
    /// Verify that the HLS cache hit path returns a PhysicalFileResult with
    /// the correct HLS content type (application/vnd.apple.mpegurl).
    /// </summary>
    [Fact]
    public async Task StreamHlsVideoAudio_CacheHit_ReturnsPhysicalFileResult()
    {
        var audioItem = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(audioItem.Id)).Returns(audioItem);

        // Pre-populate HLS cache with a valid playlist (>= 10 KB)
        string hlsDir = _cache.GetHlsDirectoryPath(audioItem.Id.ToString("D"), 0);
        Directory.CreateDirectory(hlsDir);
        string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
        await File.WriteAllTextAsync(playlistPath, new string('x', 12 * 1024));

        var controller = CreateController(audioItem.Id.ToString());
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        // JF-309: when a token is present (as it is here via CreateController), the playlist is
        // rewritten with ?token= on each segment line and served as ContentResult (not PhysicalFile).
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", contentResult.ContentType);
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
            b.AddProvider(new CaptureLoggerProvider(logRecords));
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
        var missLogs = logRecords
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
    private class CaptureLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _records;

        public CaptureLoggerProvider(List<(LogLevel Level, string Message)> records)
        {
            _records = records;
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(_records);

        public void Dispose() { }
    }

    private class CaptureLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _records;

        public CaptureLogger(List<(LogLevel Level, string Message)> records)
        {
            _records = records;
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _records.Add((logLevel, formatter(state, exception)));
        }
    }

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
        File.WriteAllText(fakeFfmpegPath, "#!/bin/sh\n" + scriptBody);
#pragma warning disable CA3003, CA1416 // test-created path; Unix-only test
        File.SetUnixFileMode(fakeFfmpegPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA3003, CA1416
        return fakeFfmpegPath;
    }

    /// <summary>
    /// Fake ffmpeg for the tier-pick tests: records its arguments next to the
    /// output playlist, creates the first segment + playlist, exits 0 (the
    /// first-segment wait then succeeds immediately).
    /// </summary>
    private string WriteRecordingFakeFfmpeg(string name)
        => WriteFakeFfmpeg(name,
            "for last_arg in \"$@\"; do :; done\n" +
            "dir=$(dirname \"$last_arg\")\n" +
            "printf '%s\\n' \"$@\" > \"$dir/episode-args.txt\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$dir/seg_0000.ts\" 2>/dev/null\n" +
            "printf '#EXTM3U\\n#EXT-X-VERSION:3\\n#EXTINF:4.000,\\nseg_0000.ts\\n' > \"$last_arg\"\n" +
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
        string fakeFfmpegPath)
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        return CreateController(itemId, null, mediaSourceManager, fakeFfmpegPath);
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
            await WaitUntilAsync(() => VideoAudioController.EpisodeTranscodeSlotFree).ConfigureAwait(false),
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
    /// given codec (1080p video + eac3 audio streams), the encoder path, and the
    /// GetItemById wiring.
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

        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
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
}
