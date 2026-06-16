using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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

    /// <summary>
    /// Verify that the endpoint returns 404 when the item is not found.
    /// </summary>
    [Fact]
    public async Task StreamVideoAudio_ItemNotFound_Returns404()
    {
        _mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg");
        _libraryManagerMock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var controller = CreateController();
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(
            Guid.NewGuid().ToString());

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

        var controller = CreateController();
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
        string fakeFfmpegPath = Path.Combine(_tempDir, "fake-ffmpeg");
        string fakeFfmpegScript = "#!/bin/sh\n" +
            // POSIX sh (dash) has no "${@: -1}" — iterate to the last positional arg instead
            "for last_arg in \"$@\"; do :; done\n" +
            "dd if=/dev/zero bs=1024 count=12 of=\"$last_arg\" 2>/dev/null\n" +
            "exit 0\n";
        File.WriteAllText(fakeFfmpegPath, fakeFfmpegScript);
#pragma warning disable CA3003, CA1416 // test-created path; Unix-only test
        File.SetUnixFileMode(fakeFfmpegPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA3003, CA1416

        var controller = CreateController();
        controller.FfmpegPath = fakeFfmpegPath;

        // Set up HttpContext with RequestAborted so the controller can register
        // the client-disconnect callback
        var httpContext = new DefaultHttpContext
        {
            RequestAborted = CancellationToken.None
        };
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

        var controller = CreateController();
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamVideoAudio(audioItem.Id.ToString());

        // On cache hit, PhysicalFileResult is returned (not FileStreamResult)
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("video/mp4", physicalResult.ContentType);
        Assert.True(physicalResult.EnableRangeProcessing);
    }

    private VideoAudioController CreateController()
    {
        return new VideoAudioController(
            _libraryManagerMock.Object,
            _mediaEncoderMock.Object,
            _cache,
            _loggerFactory);
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

        var controller = CreateController();
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(Guid.NewGuid().ToString());

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

        var controller = CreateController();
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

        var controller = CreateController();
        controller.FfmpegPath = "/usr/bin/ffmpeg";

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", physicalResult.ContentType);
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
        string fakeFfmpegPath = Path.Combine(_tempDir, "fake-ffmpeg-hls");
        string fakeFfmpegScript = "#!/bin/sh\n" +
            // POSIX sh (dash) has no "${@: -1}" — iterate to the last positional arg instead
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
            "exit 0\n";
        File.WriteAllText(fakeFfmpegPath, fakeFfmpegScript);
#pragma warning disable CA3003, CA1416 // test-created path; Unix-only test
        File.SetUnixFileMode(fakeFfmpegPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA3003, CA1416

        var controller = CreateController();
        controller.FfmpegPath = fakeFfmpegPath;

        var httpContext = new DefaultHttpContext
        {
            RequestAborted = CancellationToken.None
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        ActionResult result = await controller.StreamHlsVideoAudio(audioItem.Id.ToString());

        // HLS serves the playlist via PhysicalFile as soon as the first segment appears
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", physicalResult.ContentType);
    }

    // ========== Segment Endpoint Tests ==========

    /// <summary>
    /// Verify that the segment endpoint returns 400 for an invalid itemId.
    /// </summary>
    [Fact]
    public void GetSegment_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = controller.GetSegment("not-a-guid", "seg_000.ts");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Verify that the segment endpoint returns 400 for an invalid segment name
    /// (directory traversal prevention).
    /// </summary>
    [Fact]
    public void GetSegment_InvalidSegmentName_Returns400()
    {
        var controller = CreateController();

        // Test various traversal and injection attempts
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), "../etc/passwd"));
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), "../../secret"));
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), "seg_000.ts/../../etc/passwd"));
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), ""));
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), "seg_00.ts"));
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), "seg_00000.ts"));   // 5 digits
        Assert.IsType<BadRequestObjectResult>(controller.GetSegment(Guid.NewGuid().ToString(), "segment.ts"));
    }

    /// <summary>
    /// Verify that the segment endpoint returns 404 when the segment file doesn't exist.
    /// </summary>
    [Fact]
    public void GetSegment_SegmentNotFound_Returns404()
    {
        var controller = CreateController();

        ActionResult result = controller.GetSegment(Guid.NewGuid().ToString(), "seg_000.ts");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Verify that the segment endpoint returns a .ts file with correct content type
    /// when the segment exists in the HLS cache directory.
    /// </summary>
    [Fact]
    public void GetSegment_ValidSegment_ReturnsFile()
    {
        Guid itemId = Guid.NewGuid();
        string itemIdStr = itemId.ToString("D");
        long artTicks = 0;

        // Create an HLS directory with a segment file
        string hlsDir = _cache.GetHlsDirectoryPath(itemIdStr, artTicks);
        Directory.CreateDirectory(hlsDir);
        string segmentPath = Path.Combine(hlsDir, "seg_000.ts");
        File.WriteAllText(segmentPath, new string('x', 1024));

        var controller = CreateController();

        ActionResult result = controller.GetSegment(itemIdStr, "seg_000.ts");

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("video/mp2t", physicalResult.ContentType);
        Assert.True(physicalResult.EnableRangeProcessing);
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

        var controller = CreateController();
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

        var controller = CreateController();
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

        var controller = CreateController();

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
        string fakeFfmpegPath = Path.Combine(_tempDir, "fake-ffmpeg-audiobook");
        string fakeFfmpegScript = "#!/bin/sh\n" +
            // POSIX sh (dash) has no "${@: -1}" — iterate to the last positional arg instead
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:10.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_0000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n";
        File.WriteAllText(fakeFfmpegPath, fakeFfmpegScript);
#pragma warning disable CA3003, CA1416
        File.SetUnixFileMode(fakeFfmpegPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA3003, CA1416

        var controller = CreateController();
        controller.FfmpegPath = fakeFfmpegPath;

        var httpContext = new DefaultHttpContext
        {
            RequestAborted = CancellationToken.None
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        ActionResult result = await controller.StreamHlsAudiobook(parentId.ToString());

        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/vnd.apple.mpegurl", physicalResult.ContentType);
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
        string fakeFfmpegPath = Path.Combine(_tempDir, "fake-ffmpeg-concat-check");
        string fakeFfmpegScript = "#!/bin/sh\n" +
            // POSIX sh (dash) has no "${@: -1}" — iterate to the last positional arg instead
            "for playlist_path in \"$@\"; do :; done\n" +
            "playlist_dir=\"$(dirname \"$playlist_path\")\"\n" +
            "mkdir -p \"$playlist_dir\"\n" +
            "dd if=/dev/zero bs=1024 count=4 of=\"$playlist_dir/seg_0000.ts\" 2>/dev/null\n" +
            "echo '#EXTM3U' > \"$playlist_path\"\n" +
            "echo '#EXT-X-VERSION:3' >> \"$playlist_path\"\n" +
            "echo '#EXTINF:10.000,' >> \"$playlist_path\"\n" +
            "echo 'seg_0000.ts' >> \"$playlist_path\"\n" +
            "exit 0\n";
        File.WriteAllText(fakeFfmpegPath, fakeFfmpegScript);
#pragma warning disable CA3003, CA1416
        File.SetUnixFileMode(fakeFfmpegPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA3003, CA1416

        var controller = CreateController();
        controller.FfmpegPath = fakeFfmpegPath;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = CancellationToken.None }
        };

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
}
