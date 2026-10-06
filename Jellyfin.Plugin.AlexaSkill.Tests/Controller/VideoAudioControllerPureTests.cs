using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// JF-792 split of <see cref="VideoAudioControllerTests"/>: the families that
/// need NO shared static state, moved out of the Plugin collection so they run
/// in the parallel phase. Membership rule (mechanically classified over the
/// transitive call closure, then hand-verified): a test moves here iff neither
/// it nor any helper it reaches touches Plugin.Instance / the shared config /
/// StreamTokenHelper minting / the encode gate / the live-encode registries /
/// the JF-731 teardown backstop / a CurrentCulture write / the ServeInLock IL
/// pins. Everything here exercises internal static controller surface (ffmpeg
/// argument builders, cache-key and path formats, playlist writers, encode-byte
/// estimators, ResolveSourceCodecs on a locally constructed controller,
/// SafeExitCode) or the per-instance VideoAudioCache, plus the no-encode
/// permission-denied deletes. Fixture: <see cref="VideoAudioControllerTestHarness"/>
/// (per-class temp dir and cache; no plugin instance is ever ensured, so the
/// controller ctor's Plugin.Instance read stays null and the gate stays idle
/// at its default capacity, which nothing in the parallel phase changes).
/// </summary>
public class VideoAudioControllerPureTests : VideoAudioControllerTestHarness
{
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

        FileInfo? result = await _cache.GetCachedHlsPlaylist(itemId, 0, ownGenerationLiveOrRegistering: false);

        Assert.NotNull(result);
        Assert.Equal(playlistPath, result!.FullName);
    }

    /// <summary>
    /// Verify that GetCachedHlsPlaylist returns null on cache miss.
    /// </summary>
    [Fact]
    public async Task HlsCacheMiss_ReturnsNull()
    {
        FileInfo? result = await _cache.GetCachedHlsPlaylist("nonexistent", 0, ownGenerationLiveOrRegistering: false);

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
    /// The REQUIRED RELATION between the JF-778 window floor and the JF-503 hold
    /// lookahead (the floor const's doc): the floor's promise beyond the encode
    /// head (floor - 1 entries) must stay within
    /// <see cref="VideoAudioController.SegmentHoldLookahead"/>, or the listing
    /// would promise entries that 404 past the hold's reach, the tail death the
    /// window exists to prevent. A constants-only pin: retuning either number
    /// without the other fails here instead of on a device.
    /// </summary>
    [Fact]
    public void EpisodePrewriteWindowFloor_RespectsSegmentHoldLookahead()
    {
        Assert.True(
            VideoAudioController.EpisodePrewriteWindowFloorSegments - 1 <= VideoAudioController.SegmentHoldLookahead,
            $"EpisodePrewriteWindowFloorSegments ({VideoAudioController.EpisodePrewriteWindowFloorSegments}) promises floor-1 entries beyond the encode head; SegmentHoldLookahead ({VideoAudioController.SegmentHoldLookahead}) must cover them or the windowed listing 404s past the JF-503 hold");
    }

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
