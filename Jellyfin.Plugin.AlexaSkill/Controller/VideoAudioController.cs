using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Controller that combines album art and audio into a streamable MP4 video
/// for Alexa Echo Show VideoApp playback. Uses ffmpeg to mux a static image
/// with the audio track on-the-fly with chunked streaming.
/// Results are cached so subsequent plays of the same item (with same album art)
/// are served instantly without re-encoding.
/// </summary>
[ApiController]
[Route("alexaskill/api/video-audio")]
public class VideoAudioController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager? _mediaSourceManager;
    private readonly VideoAudioCache _cache;
    private readonly ILogger<VideoAudioController> _logger;

    /// <summary>
    /// Source audio codecs that are stream-copy-compatible with the MP4/HLS muxer used
    /// by the single-item video-audio endpoint. When the item's first audio stream uses
    /// one of these codecs, ffmpeg remuxes with <c>-c:a copy</c> (instant, no quality
    /// loss) instead of re-encoding to AAC (~3-10s per song). Any other codec falls back
    /// to AAC re-encode for muxer compatibility.
    /// </summary>
    private static readonly HashSet<string> CopyCompatibleAudioCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3",
        "aac"
    };

    /// <summary>
    /// Tracks audiobook HLS encode operations currently in progress by parentId.
    /// Prevents concurrent ffmpeg processes for the same audiobook (Echo Show
    /// sends multiple rapid requests for stream.m3u8).
    /// Key: parentId string, Value: dummy bool (presence = active).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _activeAudiobookEncodes = new();

    /// <summary>
    /// Compiled regex for extracting trailing chapter number from audiobook filenames
    /// (e.g. "The Upside of Irrationality 065.mp3" → 65).
    /// </summary>
    private static readonly Regex _chapterNumberRegex = new(@"(\d+)\s*$", RegexOptions.Compiled);

    private const string HlsExtInf = "#EXTINF:";
    private const string HlsEndList = "#EXT-X-ENDLIST";

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoAudioController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="cache">Instance of the <see cref="VideoAudioCache"/> service.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public VideoAudioController(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        VideoAudioCache cache,
        ILoggerFactory loggerFactory)
        : this(libraryManager, mediaEncoder, cache, loggerFactory, mediaSourceManager: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoAudioController"/> class with
    /// a media source manager for resolving the source audio codec (enables -c:a copy).
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="cache">Instance of the <see cref="VideoAudioCache"/> service.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface (optional — null falls back to AAC transcode).</param>
    [ActivatorUtilitiesConstructor]
    public VideoAudioController(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        VideoAudioCache cache,
        ILoggerFactory loggerFactory,
        IMediaSourceManager? mediaSourceManager)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _cache = cache;
        _logger = loggerFactory.CreateLogger<VideoAudioController>();
    }

    /// <summary>
    /// Gets or sets the path to the ffmpeg binary.
    /// Resolved from Jellyfin's <see cref="IMediaEncoder"/> service.
    /// Overridden in tests to inject a mock path.
    /// </summary>
    internal string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Stream an MP4 video combining album art and audio for the given item.
    /// If a cached version exists (same item + same album art), serves it directly.
    /// Otherwise, generates on-the-fly via ffmpeg and caches the result.
    /// </summary>
    /// <param name="itemId">The Jellyfin audio item ID.</param>
    /// <returns>An MP4 video file.</returns>
    [HttpGet("{itemId}")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamVideoAudio([FromRoute] string itemId)
    {
        if (Guid.TryParse(itemId, out _))
        {
            ActionResult? tokenError = ValidateStreamToken(itemId);
            if (tokenError != null)
            {
                return tokenError;
            }
        }

        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        // Determine cache key: itemId + album art modification time
        long artModifiedTicks = GetArtModifiedTicks(validation.Item);

        // Check cache first (fast path — no lock needed)
        FileInfo? cached = await _cache.GetCachedFile(itemId, artModifiedTicks).ConfigureAwait(false);
        if (cached != null)
        {
            _logger.LogDebug("VideoAudio: serving cached file for item {ItemId}", itemId);
            return PhysicalFile(cached.FullName, "video/mp4", enableRangeProcessing: true);
        }

        // Cache miss — acquire per-item lock to prevent concurrent ffmpeg processes
        // for the same item. Different items proceed in parallel.
        using (await _cache.LockItemAsync(itemId, artModifiedTicks).ConfigureAwait(false))
        {
            // Clean up any corrupt stub from a previous failed generation.
            // Safe to delete here because we hold the per-item lock.
            _cache.DeleteStubIfPresent(itemId, artModifiedTicks);

            // Double-check cache: another request may have generated the file
            // while we were waiting for the lock
            cached = await _cache.GetCachedFile(itemId, artModifiedTicks).ConfigureAwait(false);
            if (cached != null)
            {
                _logger.LogDebug("VideoAudio: serving file generated by concurrent request for item {ItemId}", itemId);
                return PhysicalFile(cached.FullName, "video/mp4", enableRangeProcessing: true);
            }

            // Build audio URL (ffmpeg fetches it directly via HTTP).
            // Jellyfin's /Audio/{id}/stream endpoint works without auth for static streams.
            string audioUrl = $"{validation.ServerUrl}/Audio/{itemId}/stream?static=true";

            // Determine album art URL — prefer item image, then parent, then black frame.
            // Image endpoints also work without auth.
            string? artUrl = ResolveArtUrl(validation.Item, validation.ServerUrl);
            bool useBlackFrame = artUrl == null;

            // Resolve the source audio codec to decide -c:a copy vs AAC transcode.
            // mp3/aac sources are remuxed (instant); anything else is re-encoded to AAC.
            string? sourceAudioCodec = ResolveSourceAudioCodec(validation.Item);
            bool useAudioCopy = sourceAudioCodec != null
                && CopyCompatibleAudioCodecs.Contains(sourceAudioCodec);

            _logger.LogDebug(
                "VideoAudio: itemId={ItemId}, artUrl={ArtUrl}, useBlackFrame={UseBlackFrame}, sourceAudioCodec={SourceAudioCodec}, audioMode={AudioMode}",
                itemId,
                artUrl ?? "(black frame)",
                useBlackFrame,
                sourceAudioCodec ?? "(unknown)",
                useAudioCopy ? "copy" : "transcode");

            // Prepare cache file path
            string cachePath = _cache.GetCacheFilePath(itemId, artModifiedTicks);
            string? cacheDir = Path.GetDirectoryName(cachePath);
            if (cacheDir != null)
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Build ffmpeg arguments (includes output file path directly)
            var ffmpegArgs = BuildFfmpegArguments(artUrl, audioUrl, useBlackFrame, cachePath, sourceAudioCodec);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Stream-while-writing: start ffmpeg, open the output file for reading
            // while ffmpeg writes, and return a FileStreamResult immediately.
            // The client receives data as ffmpeg produces it instead of waiting
            // for the entire file to be generated first.
#pragma warning disable CA3003 // cachePath is built from GUID-validated itemId and numeric ticks
            var ffmpegProcess = StartFfmpegProcess(validation.FfmpegPath, ffmpegArgs);

            try
            {
                // Wait for ffmpeg to create the output file (it opens the file on startup).
                // This is nearly instantaneous in practice but guards against the race
                // between process.Start() and the file appearing on disk.
                // Also check if ffmpeg has already exited (e.g. invalid input) to avoid
                // a 1-second spin-wait when the process fails fast.
                bool fileAppeared = false;
                for (int i = 0; i < 100; i++)
                {
                    if (System.IO.File.Exists(cachePath))
                    {
                        fileAppeared = true;
                        break;
                    }

                    if (ffmpegProcess.HasExited)
                    {
                        break;
                    }

                    await Task.Delay(10).ConfigureAwait(false);
                }

                if (!fileAppeared)
                {
                    _logger.LogWarning("VideoAudio: ffmpeg failed to create output for item {ItemId} (exit code {ExitCode})", itemId, ffmpegProcess.ExitCode);
                    ffmpegProcess.Dispose();
                    return StatusCode(500, new { error = "Video generation failed" });
                }

                var stream = new FileStream(
                    cachePath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });

                // Kill ffmpeg if the client disconnects mid-stream
                HttpContext.RequestAborted.Register(static state =>
                {
                    var proc = (Process)state!;
                    try { proc.Kill(); }
                    catch { /* already exited */ }
                }, ffmpegProcess);

                // Monitor ffmpeg completion in the background: trigger remux + cleanup
                // FileStreamResult owns the stream lifetime — monitor must NOT dispose it.
                _ = MonitorFfmpegAndRemuxAsync(ffmpegProcess, validation.FfmpegPath, cachePath, itemId, artModifiedTicks);
#pragma warning restore CA3003

                _logger.LogDebug("VideoAudio: streaming generated file for item {ItemId}", itemId);
                return new FileStreamResult(stream, "video/mp4");
            }
            catch
            {
                // FileStream creation failed — kill and clean up the ffmpeg process
                try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                ffmpegProcess.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Stream an HLS playlist combining album art and audio for the given item.
    /// HLS provides native seek support and correct duration display on Echo Show from first play.
    /// If a cached HLS directory exists (same item + same album art), serves the playlist directly.
    /// Otherwise, starts ffmpeg generating HLS segments and serves the playlist as soon as the
    /// first segment is ready — without waiting for the entire file to be encoded. This avoids
    /// timeout issues with long content (audiobooks) where full encoding could take minutes.
    /// The Echo Show re-fetches the playlist periodically and discovers new segments as they appear.
    /// </summary>
    /// <param name="itemId">The Jellyfin audio item ID.</param>
    /// <returns>An HLS playlist (.m3u8) file.</returns>
    [HttpGet("{itemId}/stream.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsVideoAudio([FromRoute] string itemId)
    {
        if (Guid.TryParse(itemId, out _))
        {
            ActionResult? tokenError = ValidateStreamToken(itemId);
            if (tokenError != null)
            {
                return tokenError;
            }
        }

        return await StreamHlsVideoAudioCore(itemId).ConfigureAwait(false);
    }

    /// <summary>
    /// Build and serve the single-item HLS playlist without token validation. Token validation is
    /// performed by the public <see cref="StreamHlsVideoAudio"/> entry point, or by the audiobook
    /// endpoint (which validates against parentId before redirecting single-chapter books here).
    /// </summary>
    private async Task<ActionResult> StreamHlsVideoAudioCore(string itemId, string? overrideToken = null)
    {
        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        long artModifiedTicks = GetArtModifiedTicks(validation.Item);

        // Check cache first (fast path — no lock needed).
        // HLS playlists are served even when small (partial/in-progress) because
        // ffmpeg writes atomically via .tmp rename — the file is always consistent.
        FileInfo? cached = await _cache.GetCachedHlsPlaylist(itemId, artModifiedTicks).ConfigureAwait(false);
        if (cached != null)
        {
            _logger.LogDebug("VideoAudio HLS: serving cached playlist for item {ItemId}", itemId);
#pragma warning disable CA3003 // path derived from GUID-validated itemId
            return ServePlaylistWithToken(cached.FullName, overrideToken);
#pragma warning restore CA3003
        }

        // Cache miss — acquire per-item lock
        using (await _cache.LockItemAsync(itemId, artModifiedTicks).ConfigureAwait(false))
        {
            // Clean up any corrupt/partial HLS directory from a previous failed generation
            _cache.CleanupHlsStub(itemId, artModifiedTicks);

            // Double-check cache after acquiring lock
            cached = await _cache.GetCachedHlsPlaylist(itemId, artModifiedTicks).ConfigureAwait(false);
            if (cached != null)
            {
                _logger.LogDebug("VideoAudio HLS: serving playlist generated by concurrent request for item {ItemId}", itemId);
#pragma warning disable CA3003
                return ServePlaylistWithToken(cached.FullName, overrideToken);
#pragma warning restore CA3003
            }

            string audioUrl = $"{validation.ServerUrl}/Audio/{itemId}/stream?static=true";
            string? artUrl = ResolveArtUrl(validation.Item, validation.ServerUrl);
            bool useBlackFrame = artUrl == null;

            // Resolve source audio codec: mp3/aac → -c:a copy, else AAC transcode
            string? sourceAudioCodec = ResolveSourceAudioCodec(validation.Item);
            bool useAudioCopy = sourceAudioCodec != null
                && CopyCompatibleAudioCodecs.Contains(sourceAudioCodec);

            _logger.LogDebug(
                "VideoAudio HLS: itemId={ItemId}, sourceAudioCodec={SourceAudioCodec}, audioMode={AudioMode}",
                itemId,
                sourceAudioCodec ?? "(unknown)",
                useAudioCopy ? "copy" : "transcode");

#pragma warning disable CA3003 // paths derived from GUID-validated itemId
            string hlsDir = _cache.GetHlsDirectoryPath(itemId, artModifiedTicks);
            Directory.CreateDirectory(hlsDir);

            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
            string segmentPath = Path.Combine(hlsDir, "seg_%03d.ts");
            string hlsBaseUrl = $"/alexaskill/api/video-audio/{itemId}/segments/";

            var ffmpegArgs = BuildHlsFfmpegArguments(artUrl, audioUrl, useBlackFrame, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio HLS: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Start ffmpeg without waiting — generate segments in the background.
            // Serve the playlist as soon as the first segment is ready so the Echo Show
            // can start playback immediately, even for long content like audiobooks.
            var ffmpegProcess = StartFfmpegProcess(validation.FfmpegPath, ffmpegArgs);

            try
            {
                // Wait for the first segment file to appear on disk.
                // ffmpeg creates the playlist atomically (.tmp rename), so once the
                // segment file exists, the playlist references are valid.
                string firstSegmentPath = Path.Combine(hlsDir, "seg_000.ts");
                bool segmentAppeared = false;
                for (int i = 0; i < 200; i++) // up to ~20 seconds
                {
                    if (System.IO.File.Exists(firstSegmentPath) && System.IO.File.Exists(playlistPath))
                    {
                        segmentAppeared = true;
                        break;
                    }

                    if (ffmpegProcess.HasExited)
                    {
                        break;
                    }

                    await Task.Delay(100).ConfigureAwait(false);
                }

                if (!segmentAppeared)
                {
                    _logger.LogWarning("VideoAudio HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, ffmpegProcess.HasExited ? ffmpegProcess.ExitCode : -1);
                    try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                    ffmpegProcess.Dispose();
                    return StatusCode(500, new { error = "HLS generation failed" });
                }

                // Register the HLS directory for fast segment lookups (avoids filesystem scan per segment)
                _cache.RegisterHlsDirectory(itemId, artModifiedTicks);

                // Monitor ffmpeg in the background: wait for completion, log errors, trigger eviction.
                _ = MonitorFfmpegHlsAsync(ffmpegProcess, hlsDir, itemId, artModifiedTicks);

                // Serve the partial playlist immediately — the Echo Show will start
                // fetching segments and re-request the playlist for updates.
                _logger.LogDebug("VideoAudio HLS: serving partial playlist for item {ItemId}", itemId);
                return ServePlaylistWithToken(playlistPath, overrideToken);
            }
            catch
            {
                try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                ffmpegProcess.Dispose();
                throw;
            }
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Stream an HLS playlist that concatenates all chapters of an audiobook into
    /// one continuous stream. Gives the full book duration in the Echo Show seek bar
    /// and allows seeking across the entire book via VideoApp.Launch.
    /// Uses ffmpeg's concat demuxer to join chapter audio URLs sequentially.
    /// Segments are served by the existing <see cref="GetSegment"/> endpoint using
    /// the parent GUID as the cache key — no collision with single-item entries.
    /// </summary>
    /// <param name="parentId">The audiobook parent folder ID.</param>
    /// <returns>An HLS playlist (.m3u8) file spanning all chapters.</returns>
    [HttpGet("audiobook/{parentId}/stream.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsAudiobook(
        [FromRoute] string parentId,
        [FromQuery(Name = "start")] long? startTicks = null)
    {
        if (string.IsNullOrWhiteSpace(parentId) || !Guid.TryParse(parentId, out Guid parentGuid))
        {
            return BadRequest(new { error = "Invalid parentId format" });
        }

        ActionResult? tokenError = ValidateStreamToken(parentId);
        if (tokenError != null)
        {
            return tokenError;
        }

        string ffmpeg = ResolveFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("ffmpeg not available for audiobook HLS request");
            return StatusCode(503, new { error = "ffmpeg is not available on this server" });
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.ServerAddress))
        {
            return StatusCode(503, new { error = "Plugin not configured" });
        }

        string serverUrl = config.ServerAddress.TrimEnd('/');

        // Find the parent item (audiobook folder)
        MediaBrowser.Controller.Entities.BaseItem? parent = _libraryManager.GetItemById(parentGuid);
        if (parent == null)
        {
            _logger.LogWarning("VideoAudio audiobook HLS: parent {ParentId} not found", parentId);
            return NotFound(new { error = "Parent item not found" });
        }

        // Get all AudioBook children.
        var childrenQuery = new InternalItemsQuery
        {
            ParentId = parentGuid,
            IncludeItemTypes = new[] { BaseItemKind.AudioBook },
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> chapters =
            _libraryManager.GetItemList(childrenQuery);

        if (chapters.Count == 0)
        {
            _logger.LogWarning("VideoAudio audiobook HLS: no AudioBook chapters found under parent {ParentId}", parentId);
            return NotFound(new { error = "No audiobook chapters found" });
        }

        // Single chapter — use regular single-item HLS (no concat needed)
        if (chapters.Count == 1)
        {
            _logger.LogDebug("VideoAudio audiobook HLS: single chapter, serving single-item HLS inline for {ItemId} (token already validated against parentId)", chapters[0].Id);
            // Re-mint a chapter-scoped token: the playlist references segments by chapterId, not
            // parentId, so the Echo needs a token GetSegment will accept against chapterId.
            string? secret = Plugin.Instance?.Configuration?.StreamTokenSecret;
            string chapterToken = string.IsNullOrEmpty(secret)
                ? string.Empty
                : StreamTokenHelper.Mint(chapters[0].Id.ToString(), secret);
            return await StreamHlsVideoAudioCore(chapters[0].Id.ToString(), chapterToken).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "VideoAudio audiobook HLS: generating concat stream for '{BookName}' ({ParentId}) with {ChapterCount} chapters",
            parent.Name, parentId, chapters.Count);

        // Resolve art from the parent (book cover) for cache key only.
        // Always use black frame for audiobook HLS — the 1fps black frame video is the
        // proven approach for seeking, and VideoApp.Launch doesn't display album art anyway.
        // Using album art with -loop requires a video filter to ensure even dimensions for
        // libx264, and the art image may have arbitrary dimensions.
        long artModifiedTicks = GetArtModifiedTicks(parent);

        // Check cache first (keyed by parent ID — different GUID than any chapter)
        // For audiobooks with 10s segments, we cannot serve a pre-written playlist because
        // it would reference thousands of segments that don't exist yet. Instead:
        // - During encoding: serve ffmpeg's live stream.m3u8 (only lists existing segments)
        // - After encoding: serve the completed cached stream.m3u8 (has ENDLIST)
        // We validate completeness by checking for ENDLIST + segment count >= chapter count.
        FileInfo? cached = await _cache.GetCachedHlsPlaylist(parentId, artModifiedTicks).ConfigureAwait(false);
        if (cached != null)
        {
            cached = await ValidateAudiobookCacheAsync(cached, chapters.Count, parentId).ConfigureAwait(false);

            if (cached != null)
            {
                _logger.LogDebug("VideoAudio audiobook HLS: serving cached playlist for parent {ParentId}", parentId);
                return await ServeAudiobookPlaylistAsync(cached.FullName, startTicks).ConfigureAwait(false);
            }
        }

        // Guard: if an encode is already running for this audiobook (from a concurrent
        // Echo Show request), serve the pre-written event playlist instead of starting
        // another ffmpeg. The pre-written playlist has correct total duration and no
        // ENDLIST, so the player plays available segments.
        if (_activeAudiobookEncodes.TryGetValue(parentId, out _))
        {
            _logger.LogDebug("VideoAudio audiobook HLS: encode already in progress for {ParentId}, serving pre-written playlist", parentId);
            string hlsDir = _cache.GetHlsDirectoryPath(parentId, artModifiedTicks);
#pragma warning disable CA3003
            string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");
            if (System.IO.File.Exists(prewrittenPath))
            {
                return await ServeAudiobookPlaylistAsync(prewrittenPath, startTicks).ConfigureAwait(false);
            }

            _logger.LogWarning("VideoAudio audiobook HLS: pre-written playlist not available for {ParentId}, returning 503", parentId);
            return StatusCode(503, "Encode in progress");
        }

        // Cache miss — acquire per-parent lock
        using (await _cache.LockItemAsync(parentId, artModifiedTicks).ConfigureAwait(false))
        {
            _cache.CleanupHlsStub(parentId, artModifiedTicks);

            // Double-check cache after acquiring lock (another request's encode may have completed)
            cached = await _cache.GetCachedHlsPlaylist(parentId, artModifiedTicks).ConfigureAwait(false);
            if (cached != null)
            {
                cached = await ValidateAudiobookCacheAsync(cached, chapters.Count, parentId).ConfigureAwait(false);

                if (cached != null)
                {
                    _logger.LogDebug("VideoAudio audiobook HLS: serving playlist generated by concurrent request for parent {ParentId}", parentId);
                    return await ServeAudiobookPlaylistAsync(cached.FullName, startTicks).ConfigureAwait(false);
                }
            }

#pragma warning disable CA3003 // paths derived from GUID-validated parentId
            string hlsDir = _cache.GetHlsDirectoryPath(parentId, artModifiedTicks);
            Directory.CreateDirectory(hlsDir);

            // Sort chapters by file path number for correct playback order.
            // Audiobook chapters are typically named "... 001.mp3", "... 002.mp3" etc.
            // Jellyfin doesn't always parse these into IndexNumber, and SortName/Name
            // may be identical across all chapters. Extract the trailing number from
            // the filename for natural chapter order.
            _logger.LogInformation(
                "Audiobook chapter sort: first item Name={Name}, Path={Path}, Id={Id}",
                chapters[0].Name, chapters[0].Path, chapters[0].Id);

            var sortedChapters = chapters
                .OrderBy(c =>
                {
                    string? path = c.Path;
                    if (string.IsNullOrEmpty(path))
                    {
                        return int.MaxValue;
                    }

                    string filename = System.IO.Path.GetFileNameWithoutExtension(path);
                    var match = _chapterNumberRegex.Match(filename);
                    return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
                })
                .ToList();

            _logger.LogInformation(
                "Audiobook chapter sort result: first={FirstPath}, last={LastPath}",
                sortedChapters[0].Path, sortedChapters[^1].Path);

            // Write ffmpeg concat input file listing all chapter audio URLs in sorted order.
            // The concat demuxer reads each chapter sequentially — it doesn't preload
            // all URLs upfront, so the first segment appears in ~5 seconds regardless
            // of total chapter count.
            string concatListPath = Path.Combine(hlsDir, "chapters.txt");
            var writer = new StreamWriter(concatListPath);
            try
            {
                foreach (var chapter in sortedChapters)
                {
                    string audioUrl = $"{serverUrl}/Audio/{chapter.Id}/stream?static=true";
                    await writer.WriteLineAsync($"file '{audioUrl}'").ConfigureAwait(false);
                }
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            // Write metadata for post-encode validation in MonitorFfmpegHlsAsync.
            // Records the expected chapter count at encode time so the monitor can
            // detect incomplete encodes without re-querying the Jellyfin library.
            var encodeMetadata = new
            {
                ExpectedChapterCount = chapters.Count,
                ExpectedDurationTicks = chapters.Sum(c => c.RunTimeTicks ?? 0),
                ParentId = parentId,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            string metadataPath = Path.Combine(hlsDir, "encode-metadata.json");
#pragma warning disable CA3003
            using (var metadataStream = System.IO.File.Create(metadataPath))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(
                    metadataStream,
                    encodeMetadata,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning restore CA3003

            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
            // 10-second segments: 8.3h audiobook → ~3001 segments. Use %04d (max 9999).
            // IsValidSegmentName only accepts 3-4 digit names (seg_NNN.ts or seg_NNNN.ts).
            string segmentPath = Path.Combine(hlsDir, "seg_%04d.ts");
            // Segments served by existing GetSegment endpoint using parentId as key
            string hlsBaseUrl = $"/alexaskill/api/video-audio/{parentId}/segments/";

            // Pre-write a complete HLS playlist to a SEPARATE file from what ffmpeg uses.
            // This gives the Echo Show the correct total book duration immediately.
            // ffmpeg writes to stream.m3u8 (its own file).
            // After ffmpeg completes, stream.m3u8 becomes the cached playlist.
            // The pre-written file has NO ENDLIST — treated as an event playlist so the
            // player plays available segments without failing on missing ones.
            string prewrittenPath = Path.Combine(hlsDir, "playlist-full.m3u8");
            string? token = HttpContext.Request.Query["token"];
            WriteAudiobookPlaylist(prewrittenPath, hlsBaseUrl, sortedChapters, token);

            var ffmpegArgs = BuildHlsAudiobookFfmpegArguments(
                concatListPath, null, true, playlistPath, segmentPath, hlsBaseUrl);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio audiobook HLS: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Start ffmpeg in the background — writes to stream.m3u8, not our pre-written file.
            var ffmpegProcess = StartFfmpegProcess(ffmpeg, ffmpegArgs);

            // Register the HLS directory for segment lookups immediately.
            _cache.RegisterHlsDirectory(parentId, artModifiedTicks);

            // Mark this audiobook as actively encoding to prevent concurrent ffmpeg launches.
            _activeAudiobookEncodes.TryAdd(parentId, true);

            // Monitor ffmpeg in background — logs errors, triggers eviction when done.
            _ = MonitorFfmpegHlsAsync(ffmpegProcess, hlsDir, parentId, artModifiedTicks);

            // Wait briefly for the first segment to appear so we don't serve a playlist
            // that references zero actual segment files (Echo Show would fail immediately).
            string firstSegmentPath = Path.Combine(hlsDir, "seg_0000.ts");
            for (int i = 0; i < 100; i++) // up to ~10 seconds
            {
#pragma warning disable CA3003
                if (System.IO.File.Exists(firstSegmentPath))
                {
                    break;
                }
#pragma warning restore CA3003

                try
                {
                    if (ffmpegProcess.HasExited)
                    {
                        _logger.LogWarning(
                            "VideoAudio audiobook HLS: ffmpeg exited early for parent {ParentId} (exit code {ExitCode})",
                            parentId, ffmpegProcess.ExitCode);
                        break;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process already disposed by MonitorFfmpegHlsAsync — encoding failed
                    break;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            _logger.LogDebug(
                "VideoAudio audiobook HLS: serving pre-written playlist for parent {ParentId} ({ChapterCount} chapters)",
                parentId, sortedChapters.Count);

            // Serve the pre-written event playlist (no ENDLIST) so the Echo Show gets
            // the correct total book duration. The player treats it as an event playlist
            // and plays available segments without failing on missing ones. ffmpeg generates
            // segments in the background, staying ahead of real-time playback.
            return await ServeAudiobookPlaylistAsync(prewrittenPath, startTicks).ConfigureAwait(false);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Serve an individual HLS segment (.ts) file for a given item.
    /// The segment name is validated against a strict pattern (seg_NNN.ts) to prevent
    /// directory traversal attacks. The segment directory is resolved via the cache service.
    /// </summary>
    /// <param name="itemId">The Jellyfin audio item ID.</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_0000.ts").</param>
    /// <returns>The segment file.</returns>
    [HttpGet("{itemId}/segments/{segmentName}")]
    [AllowAnonymous]
    public ActionResult GetSegment([FromRoute] string itemId, [FromRoute] string segmentName)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out _))
        {
            return BadRequest(new { error = "Invalid itemId format" });
        }

        ActionResult? tokenError = ValidateStreamToken(itemId);
        if (tokenError != null)
        {
            return tokenError;
        }

        // Validate segment name to prevent directory traversal
        if (!VideoAudioCache.IsValidSegmentName(segmentName))
        {
            _logger.LogWarning("VideoAudio: rejected invalid segment name '{SegmentName}' for item {ItemId}", segmentName, itemId);
            return BadRequest(new { error = "Invalid segment name" });
        }

        // Record audiobook playback progress via the segment request. Anonymous endpoint,
        // so keyed by itemId (the book parent-folder ID for audiobook concat streams).
        // Best-effort: never fail the segment request over tracking.
        RecordSegmentForTracking(itemId, segmentName);

        string? segmentPath = _cache.FindSegmentPath(itemId, segmentName);
        if (segmentPath == null)
        {
            return NotFound(new { error = "Segment not found" });
        }

#pragma warning disable CA3003 // segmentPath validated via GUID itemId + strict segment name pattern
        return PhysicalFile(segmentPath, "video/mp2t", enableRangeProcessing: true);
#pragma warning restore CA3003
    }

    /// <summary>
    /// Best-effort audiobook position tracking: parse the segment number from a
    /// <c>seg_NNN[N].ts</c> name and record it. For audiobook concat streams, itemId is
    /// the book parent-folder ID (matches the resume-time lookup key). Single-item
    /// segment requests are keyed by their own itemId and simply never read at resume.
    /// </summary>
    private void RecordSegmentForTracking(string itemId, string segmentName)
    {
        // Format: "seg_" + digits + ".ts"  → digits span [4, Length-3)
        if (segmentName.Length < 8 || !segmentName.StartsWith("seg_", StringComparison.Ordinal) || !segmentName.EndsWith(".ts", StringComparison.Ordinal))
        {
            return;
        }

        string digits = segmentName.Substring(4, segmentName.Length - 7);
        if (int.TryParse(digits, out int segmentNumber))
        {
            Plugin.Instance?.AudiobookPositionTracker?.RecordSegment(itemId, segmentNumber);
        }
    }

    /// <summary>
    /// Serve an HLS playlist file, injecting <c>?token=</c> into every segment URI line so the
    /// Echo carries the stream token when fetching segments (JF-309). ffmpeg-written playlists
    /// (<c>stream.m3u8</c>) don't carry the token (ffmpeg's <c>-hls_base_url</c> can't place it
    /// correctly), so this post-processes the file before serving. If no token is in the request
    /// query, the file is served raw (no rewriting).
    /// </summary>
    private ActionResult ServePlaylistWithToken(string playlistPath, string? overrideToken = null)
    {
        string? token = overrideToken ?? HttpContext.Request.Query["token"];
        if (string.IsNullOrEmpty(token))
        {
            return PhysicalFile(playlistPath, "application/vnd.apple.mpegurl");
        }

#pragma warning disable CA3003 // path derived from GUID-validated itemId + strict segment-name validation upstream
        string content = System.IO.File.ReadAllText(playlistPath);
#pragma warning restore CA3003
        string rewritten = RewritePlaylistWithToken(content, token);
        return Content(rewritten, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Append <c>?token={token}</c> to every segment URI line in an HLS playlist. Idempotent:
    /// lines that already carry a <c>?token=</c> param are not double-appended.
    /// </summary>
    internal static string RewritePlaylistWithToken(string playlistContent, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return playlistContent;
        }

        var lines = playlistContent.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            // Segment URI lines (non-tag, contain seg_, end with .ts). Handles both bare filenames
            // (seg_NNNN.ts) and full URLs (/alexaskill/.../segments/seg_NNNN.ts) — ffmpeg writes
            // the latter via -hls_base_url. Mirrors AudiobookPlaylistBuilder's line-matching shape.
            if (!line.StartsWith('#') && line.Length > 0
                && line.Contains("seg_", StringComparison.Ordinal)
                && line.EndsWith(".ts", StringComparison.Ordinal)
                && !line.Contains("?token="))
            {
                lines[i] = line + "?token=" + token;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Serve an audiobook playlist, injecting the resume hint (#EXT-X-START) when a start
    /// position is requested, otherwise serve the raw file. Centralizes resume injection so
    /// every playlist return path (cache hit, encode-in-progress, post-encode) honors ?start=.
    /// </summary>
    private async Task<ActionResult> ServeAudiobookPlaylistAsync(string playlistPath, long? startTicks)
    {
        string? token = HttpContext.Request.Query["token"];

        if (startTicks.HasValue && startTicks.Value > 0)
        {
            return await ServeResumePlaylistAsync(playlistPath, startTicks.Value, token).ConfigureAwait(false);
        }

        // Non-resume: inject token into segment lines (JF-309). The cached playlist may be
        // ffmpeg-written (stream.m3u8, no token) or plugin-written (playlist-full.m3u8, has token).
        // RewritePlaylistWithToken is idempotent — skips lines already carrying ?token=.
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
#pragma warning disable CA3003 // playlistPath is an internal cache file (parentId GUID-validated upstream)
                string content = await System.IO.File.ReadAllTextAsync(playlistPath).ConfigureAwait(false);
#pragma warning restore CA3003
                return Content(RewritePlaylistWithToken(content, token), "application/vnd.apple.mpegurl");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rewrite audiobook playlist with token from {Path}", playlistPath);
            }
        }

#pragma warning disable CA3003 // playlistPath is an internal cache file (parentId GUID-validated upstream)
        return PhysicalFile(playlistPath, "application/vnd.apple.mpegurl");
#pragma warning restore CA3003
    }

    /// <summary>
    /// Read a base audiobook playlist, inject a resume hint for the given start position,
    /// and return it as content. Falls back to serving the base playlist unchanged on error.
    /// Also injects the stream token into segment lines (JF-309).
    /// </summary>
    private async Task<ActionResult> ServeResumePlaylistAsync(string basePlaylistPath, long startTicks, string? token)
    {
        try
        {
#pragma warning disable CA3003 // basePlaylistPath is a validated internal cache file (parentId is GUID-validated upstream)
            string content = await System.IO.File.ReadAllTextAsync(basePlaylistPath).ConfigureAwait(false);
#pragma warning restore CA3003
            string resumeContent = Alexa.Playback.AudiobookPlaylistBuilder.BuildResumePlaylist(content, startTicks);
            if (!string.IsNullOrEmpty(token))
            {
                resumeContent = RewritePlaylistWithToken(resumeContent, token);
            }

            return Content(resumeContent, "application/vnd.apple.mpegurl");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to serve resume playlist from {Path}, serving base", basePlaylistPath);
#pragma warning disable CA3003 // path is an internal cache file resolved by the controller
            return PhysicalFile(basePlaylistPath, "application/vnd.apple.mpegurl");
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Validate the signed item-scoped stream token (JF-309). The token is carried in the
    /// <c>?token=</c> query parameter and binds the request's itemId to an HMAC signature so a
    /// bare item GUID can no longer stream an item. Returns a 401 result on any failure, or null
    /// when the token is valid. Call after the GUID-format check (the token binds to the GUID).
    /// </summary>
    private ActionResult? ValidateStreamToken(string itemId)
    {
        string? secret = Plugin.Instance?.Configuration?.StreamTokenSecret;
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("VideoAudio: stream token secret not configured");
            return StatusCode(503, new { error = "Stream token secret not configured" });
        }

        string? token = HttpContext.Request.Query["token"];
        if (!StreamTokenHelper.TryValidate(token, itemId, secret))
        {
            _logger.LogWarning("VideoAudio: rejected stream request for {ItemId} (missing/invalid/expired token)", itemId);
            return Unauthorized(new { error = "Invalid or expired stream token" });
        }

        return null;
    }

    /// <summary>
    /// Validate a video-audio request: parse itemId, resolve ffmpeg, look up the item,
    /// check it's a streamable media type, and verify plugin configuration.
    /// Shared by both MP4 and HLS endpoints to avoid duplicating validation logic.
    /// </summary>
    /// <param name="itemId">The raw itemId string from the route.</param>
    /// <returns>A validated result, or a result with <see cref="ValidatedRequest.Error"/> set.</returns>
    private ValidatedRequest ValidateVideoAudioRequest(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out Guid itemGuid))
        {
            return new ValidatedRequest { Error = BadRequest(new { error = "Invalid itemId format" }) };
        }

        string ffmpeg = ResolveFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("ffmpeg not available for VideoAudio request");
            return new ValidatedRequest { Error = StatusCode(503, new { error = "ffmpeg is not available on this server" }) };
        }

        MediaBrowser.Controller.Entities.BaseItem? item = _libraryManager.GetItemById(itemGuid);
        if (item == null)
        {
            _logger.LogWarning("VideoAudio: item {ItemId} not found", itemId);
            return new ValidatedRequest { Error = NotFound(new { error = "Item not found" }) };
        }

        if (item is not MediaBrowser.Controller.Entities.IHasMediaSources)
        {
            _logger.LogWarning("VideoAudio: item {ItemId} ({ItemType}) is not a streamable media type", itemId, item.GetType().Name);
            return new ValidatedRequest { Error = BadRequest(new { error = "Item is not a streamable media type" }) };
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.ServerAddress))
        {
            return new ValidatedRequest { Error = StatusCode(503, new { error = "Plugin not configured" }) };
        }

        return new ValidatedRequest
        {
            FfmpegPath = ffmpeg,
            Item = item,
            ServerUrl = config.ServerAddress.TrimEnd('/')
        };
    }

    /// <summary>
    /// Holds the result of <see cref="ValidateVideoAudioRequest"/>. If validation passes,
    /// <see cref="Error"/> is null and the other fields are populated.
    /// </summary>
    private sealed class ValidatedRequest
    {
        public ActionResult? Error { get; set; }
        public string FfmpegPath { get; set; } = string.Empty;
        public MediaBrowser.Controller.Entities.BaseItem Item { get; set; } = null!;
        public string ServerUrl { get; set; } = string.Empty;
    }

    /// <summary>
    /// Run ffmpeg to completion: start the process, await exit with a cancellation token,
    /// and throw on non-zero exit code. Used by HLS generation (which needs the complete
    /// output before serving) and by the faststart remux.
    /// </summary>
    /// <param name="ffmpegPath">Path to ffmpeg binary.</param>
    /// <param name="arguments">ffmpeg command-line arguments as individual tokens.</param>
    /// <param name="cancellationToken">Cancellation token for timeout/abort.</param>
    private async Task RunFfmpegToCompletionAsync(
        string ffmpegPath,
        List<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = StartFfmpegProcess(ffmpegPath, arguments);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg exited with code {ExitCode}", process.ExitCode);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
        }
    }

    /// <summary>
    /// Resolve the source audio codec for an item by reading its first audio media stream.
    /// Used to decide between <c>-c:a copy</c> (mp3/aac) and AAC transcode for the
    /// single-item video-audio path. Returns null when the codec cannot be determined
    /// (e.g. media source manager unavailable, no audio stream, or a DB read failure),
    /// in which case the caller falls back to AAC transcode.
    /// </summary>
    /// <param name="item">The Jellyfin audio item.</param>
    /// <returns>Lowercase audio codec string (e.g. "mp3", "aac", "flac"), or null.</returns>
    internal string? ResolveSourceAudioCodec(MediaBrowser.Controller.Entities.BaseItem item)
    {
        if (_mediaSourceManager == null)
        {
            return null;
        }

        try
        {
            foreach (var stream in _mediaSourceManager.GetMediaStreams(item.Id))
            {
                if (stream.Type == MediaStreamType.Audio && !string.IsNullOrWhiteSpace(stream.Codec))
                {
                    return stream.Codec.ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VideoAudio: could not resolve source audio codec for item {ItemId}", item.Id);
        }

        return null;
    }

    /// <summary>
    /// Resolve the album art URL for the given item. Returns null if no suitable image is available.
    /// Priority: item's own Primary image > parent (album) Primary image > parent ID fallback > null.
    /// </summary>
    /// <param name="item">The Jellyfin audio item.</param>
    /// <param name="serverUrl">The base server URL (no trailing slash).</param>
    /// <returns>Art URL string or null.</returns>
    internal static string? ResolveArtUrl(
        MediaBrowser.Controller.Entities.BaseItem item,
        string serverUrl)
    {
        // Check if the item itself has a primary image
        if (item.HasImage(ImageType.Primary, 0))
        {
            return $"{serverUrl}/Items/{item.Id}/Images/Primary";
        }

        // For audio items, try to find the album parent's primary image
        if (item is MediaBrowser.Controller.Entities.Audio.Audio)
        {
            var album = item.FindParent<MediaBrowser.Controller.Entities.Audio.MusicAlbum>();
            if (album != null && album.HasImage(ImageType.Primary, 0))
            {
                return $"{serverUrl}/Items/{album.Id}/Images/Primary";
            }
        }

        // No suitable image found — fall back to black frame
        return null;
    }

    /// <summary>
    /// Build ffmpeg argument list for combining album art + audio into MP4.
    /// Returns individual arguments for use with <see cref="ProcessStartInfo.ArgumentList"/>,
    /// which passes each token directly to the OS — no shell interpretation, no injection risk.
    /// </summary>
    /// <param name="artUrl">Album art URL (null for black frame fallback).</param>
    /// <param name="audioUrl">Audio stream URL.</param>
    /// <param name="useBlackFrame">Whether to generate a black frame instead of using art.</param>
    /// <param name="outputPath">File path for the output MP4.</param>
    /// <param name="sourceAudioCodec">Source audio codec (e.g. "mp3") for -c:a copy decision, or null to transcode.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildFfmpegArguments(string? artUrl, string audioUrl, bool useBlackFrame, string outputPath, string? sourceAudioCodec = null)
    {
        var args = new List<string>();

        if (useBlackFrame)
        {
            args.AddRange(BlackFrameInputArgs);
        }
        else
        {
            args.AddRange(ArtInputPrefixArgs);
            args.Add(artUrl!);
        }

        args.Add("-i");
        args.Add(audioUrl);
        args.AddRange(VideoCodecArgs);
        args.AddRange(BuildAudioCodecArgs(sourceAudioCodec));
        args.AddRange(PixelFormatArgs);
        args.AddRange(VideoFilterArgs);
        args.AddRange(OutputFormatArgs);
        args.Add("-shortest");
        args.Add(outputPath);

        return args;
    }

    private static readonly string[] BlackFrameInputArgs = ["-f", "lavfi", "-i", "color=c=black:s=1280x720:d=999"];
    private static readonly string[] AudiobookBlackFrameInputArgs = ["-f", "lavfi", "-i", "color=c=black:s=1280x720:d=999999"];
    private static readonly string[] ArtInputPrefixArgs = ["-loop", "1", "-framerate", "1", "-i"];
    // -g 1 forces a keyframe every 1s (1fps). Without it libx264 uses its default GOP
    // (250) → a keyframe only every ~4min at 1fps → the HLS muxer can't cut at -hls_time
    // boundaries, so segments span ~4min and the first segment takes ~18s of encode time
    // to appear (the "forever" delay on cache-miss plays). Mirrors the audiobook path.
    private static readonly string[] VideoCodecArgs = ["-c:v", "libx264", "-tune", "stillimage", "-preset", "ultrafast", "-crf", "28", "-g", "1"];
    private static readonly string[] AudioCodecArgs = ["-c:a", "aac", "-b:a", "128k"];
    private static readonly string[] AudioCopyArgs = ["-c:a", "copy"];

    /// <summary>
    /// Build the ffmpeg audio codec arguments for the single-item MP4/HLS path based on
    /// the resolved source audio codec. Returns <c>-c:a copy</c> for stream-copy-compatible
    /// codecs (mp3, aac) and the AAC re-encode args for everything else (or when the codec
    /// is unknown).
    /// </summary>
    /// <param name="sourceAudioCodec">Lowercase source audio codec (e.g. "mp3"), or null/empty if unknown.</param>
    /// <returns>ffmpeg audio codec argument tokens.</returns>
    internal static string[] BuildAudioCodecArgs(string? sourceAudioCodec)
        => !string.IsNullOrWhiteSpace(sourceAudioCodec) && CopyCompatibleAudioCodecs.Contains(sourceAudioCodec)
            ? AudioCopyArgs
            : AudioCodecArgs;
    private static readonly string[] PixelFormatArgs = ["-pix_fmt", "yuv420p", "-r", "1"];
    private static readonly string[] VideoFilterArgs = ["-vf", "scale=1280x720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black"];
    private static readonly string[] OutputFormatArgs = ["-f", "mp4", "-movflags", "frag_keyframe+empty_moov"];
    private static readonly string[] FaststartRemuxArgs = ["-f", "mp4", "-movflags", "+faststart"];

    /// <summary>
    /// Build ffmpeg argument list for generating HLS output (playlist + segments).
    /// Returns individual arguments for use with <see cref="ProcessStartInfo.ArgumentList"/>.
    /// HLS provides native seek support and correct duration display on Echo Show from first play.
    /// </summary>
    /// <param name="artUrl">Album art URL (null for black frame fallback).</param>
    /// <param name="audioUrl">Audio stream URL.</param>
    /// <param name="useBlackFrame">Whether to generate a black frame instead of using art.</param>
    /// <param name="playlistPath">File path for the output .m3u8 playlist.</param>
    /// <param name="segmentPath">File path template for segments (e.g. dir/seg_%03d.ts).</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment URLs in the playlist.</param>
    /// <param name="sourceAudioCodec">Source audio codec (e.g. "mp3") for -c:a copy decision, or null to transcode.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildHlsFfmpegArguments(
        string? artUrl,
        string audioUrl,
        bool useBlackFrame,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl,
        string? sourceAudioCodec = null)
    {
        var args = new List<string>();

        // Input: album art (looped) or black frame
        if (useBlackFrame)
        {
            args.AddRange(BlackFrameInputArgs);
        }
        else
        {
            args.AddRange(ArtInputPrefixArgs);
            args.Add(artUrl!);
        }

        args.Add("-i");
        args.Add(audioUrl);

        // Video codec
        args.AddRange(VideoCodecArgs);

        // Audio codec — copy when source is mp3/aac, else transcode to AAC
        args.AddRange(BuildAudioCodecArgs(sourceAudioCodec));

        // Pixel format + frame rate
        args.AddRange(PixelFormatArgs);

        // Video filter (scale + pad)
        args.AddRange(VideoFilterArgs);

        // HLS-specific flags
        args.Add("-hls_time");
        args.Add("4");
        args.Add("-hls_list_size");
        args.Add("0");
        args.Add("-hls_flags");
        args.Add("append_list");

        // Segment file name template
        args.Add("-hls_segment_filename");
        args.Add(segmentPath);

        // Base URL for segment references in the playlist
        args.Add("-hls_base_url");
        args.Add(hlsBaseUrl);

        args.Add("-shortest");
        args.Add(playlistPath);

        return args;
    }

    /// <summary>
    /// Build ffmpeg argument list for generating HLS output from concatenated audiobook chapters.
    /// Uses the concat demuxer (<c>-f concat</c>) to join all chapter audio URLs sequentially
    /// into one continuous stream. The concat demuxer opens each chapter lazily (not upfront),
    /// so the first HLS segment appears in ~5 seconds regardless of total chapter count.
    /// The art input uses a very long duration (27+ hours) to cover any audiobook length
    /// when using the black frame fallback.
    /// </summary>
    /// <param name="concatListPath">Path to the ffmpeg concat input file listing chapter URLs.</param>
    /// <param name="artUrl">Album art URL (null for black frame fallback).</param>
    /// <param name="useBlackFrame">Whether to generate a black frame instead of using art.</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildHlsAudiobookFfmpegArguments(
        string concatListPath,
        string? artUrl,
        bool useBlackFrame,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl)
    {
        var args = new List<string>();

        // Input 0: concat demuxer for all chapters (read sequentially, not preloaded)
        // -protocol_whitelist is required because the concat demuxer restricts which
        // protocols can be used in the input file. Without it, HTTPS URLs fail with
        // "Protocol 'https' not on whitelist 'file,crypto,data'!"
        args.Add("-protocol_whitelist");
        args.Add("file,http,https,tcp,tls,crypto,data");
        args.Add("-f");
        args.Add("concat");
        args.Add("-safe");
        args.Add("0");
        args.Add("-i");
        args.Add(concatListPath);

        // Input 1: album art (looped) or black frame with long duration for audiobooks
        if (useBlackFrame)
        {
            // 999999 seconds ≈ 11.5 days — covers any audiobook length
            args.AddRange(AudiobookBlackFrameInputArgs);
        }
        else
        {
            args.AddRange(ArtInputPrefixArgs);
            args.Add(artUrl!);
        }

        // Explicit stream mapping: take video from art input (input 1), audio from
        // concat input (input 0, first audio stream only). This ignores any embedded
        // cover art in chapter MP3 files, which would otherwise cause stream layout
        // mismatches between chapters and break the HLS muxer.
        args.Add("-map");
        args.Add("1:0");    // art video → output video
        args.Add("-map");
        args.Add("0:a:0");  // concat first audio stream → output audio

        // Video: 1fps black frame at minimum quality — fast to encode, provides keyframes every
        // second for accurate seeking. VideoApp.Launch requires a video track for the seek bar.
        args.AddRange([
            "-c:v", "libx264",
            "-tune", "stillimage",
            "-preset", "ultrafast",
            "-crf", "51",
            "-r", "1",
            "-g", "1",          // keyframe every 1s for accurate seeking
            "-pix_fmt", "yuv420p"
        ]);

        // Audio: copy without re-encoding (MP3 remux is instant, no quality loss)
        args.AddRange(["-c:a", "copy"]);

        // HLS-specific flags — 10-second segments required by ExoPlayer (Echo Show).
        // Longer segments (e.g. 250s) cause buffer stalls after seeking.
        args.Add("-hls_time");
        args.Add("10");
        args.Add("-hls_list_size");
        args.Add("0");

        // Segment file name template
        args.Add("-hls_segment_filename");
        args.Add(segmentPath);

        // Base URL for segment references in the playlist
        args.Add("-hls_base_url");
        args.Add(hlsBaseUrl);

        // Stop when the shorter input ends (audio = finite chapters, art = infinite loop)
        args.Add("-shortest");
        args.Add(playlistPath);

        return args;
    }

    /// <summary>
    /// Pre-write a complete HLS playlist for an audiobook with all segment durations.
    /// Written WITHOUT #EXT-X-ENDLIST so the player treats it as an event playlist —
    /// it plays available segments without failing on missing ones. This gives the Echo
    /// Show the correct total book duration immediately, while ffmpeg generates segments
    /// in the background. ffmpeg writes its own stream.m3u8 separately.
    /// </summary>
    /// <param name="playlistPath">File path for the .m3u8 playlist.</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references.</param>
    /// <param name="chapters">Sorted list of chapter items with duration info.</param>
    internal static void WriteAudiobookPlaylist(
        string playlistPath,
        string hlsBaseUrl,
        List<MediaBrowser.Controller.Entities.BaseItem> chapters,
        string? token)
    {
        string segmentSuffix = string.IsNullOrEmpty(token) ? string.Empty : $"?token={token}";
        using var writer = new StreamWriter(playlistPath);
        writer.WriteLine("#EXTM3U");
        writer.WriteLine("#EXT-X-VERSION:3");
        writer.WriteLine("#EXT-X-TARGETDURATION:10");
        writer.WriteLine("#EXT-X-MEDIA-SEQUENCE:0");

        int segmentIndex = 0;
        foreach (var chapter in chapters)
        {
            double durationSeconds = chapter.RunTimeTicks.HasValue && chapter.RunTimeTicks.Value > 0
                ? chapter.RunTimeTicks.Value / 10000000.0
                : 250.0;

            // Split chapter into 10-second segments to match ffmpeg's output
            int segmentsInChapter = Math.Max(1, (int)Math.Ceiling(durationSeconds / 10.0));
            double segmentDuration = durationSeconds / segmentsInChapter;

            for (int s = 0; s < segmentsInChapter; s++)
            {
                writer.WriteLine("#EXT-X-DISCONTINUITY");
                writer.WriteLine($"{HlsExtInf}{segmentDuration:F6},");
                writer.WriteLine($"{hlsBaseUrl}seg_{segmentIndex:D4}.ts{segmentSuffix}");
                segmentIndex++;
            }
        }

        // No #EXT-X-ENDLIST — event playlist so the player plays available segments
        // without failing on ones not yet generated by ffmpeg.
    }

    /// <summary>
    /// Count the number of segments in an HLS playlist by counting #EXTINF: lines.
    /// Used to validate that a cached playlist has the expected number of segments
    /// compared to the library's chapter count.
    /// </summary>
    /// <param name="playlistContent">The full text content of the .m3u8 file.</param>
    /// <returns>The number of #EXTINF entries (segments) in the playlist.</returns>
    internal static int CountSegmentsInPlaylist(string playlistContent)
    {
        int count = 0;
        int index = 0;
        while ((index = playlistContent.IndexOf(HlsExtInf, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += HlsExtInf.Length;
        }

        return count;
    }

    /// <summary>
    /// Validate a cached audiobook HLS playlist and invalidate if stale.
    /// Reads the playlist file, checks for ENDLIST (encoding complete), and validates
    /// that segment count >= chapter count. If the cache is stale (incomplete encode),
    /// cleans up and returns null. Used by both the fast-path and double-check-path
    /// to avoid duplicating validation logic.
    /// </summary>
    /// <param name="cached">The cached playlist file info (stream.m3u8).</param>
    /// <param name="chapterCount">Expected minimum segment count (from live library).</param>
    /// <param name="parentId">Parent audiobook ID for logging and cleanup.</param>
    /// <returns>The original <paramref name="cached"/> if valid, or null if invalidated.</returns>
    private async Task<FileInfo?> ValidateAudiobookCacheAsync(FileInfo cached, int chapterCount, string parentId)
    {
#pragma warning disable CA3003 // paths derived from GUID-validated parentId
        string content = await System.IO.File.ReadAllTextAsync(cached.FullName).ConfigureAwait(false);
        if (content.Contains(HlsEndList, StringComparison.Ordinal))
        {
            int cachedSegments = CountSegmentsInPlaylist(content);
            if (cachedSegments < chapterCount)
            {
                _logger.LogWarning(
                    "Audiobook HLS cache invalidated for {ParentId}: expected >= {Expected} segments, found only {Actual} — re-encoding",
                    parentId, chapterCount, cachedSegments);
                _cache.Cleanup(parentId);
                return null;
            }
        }
        else
        {
            _logger.LogDebug("VideoAudio audiobook HLS: serving live ffmpeg playlist (encoding in progress) for parent {ParentId}", parentId);
        }
#pragma warning restore CA3003

        return cached;
    }

    /// <summary>
    /// Get the album art DateModified ticks for use as a cache key component.
    /// When album art changes, the cache key changes and the old entry becomes stale.
    /// </summary>
    /// <param name="item">The Jellyfin item.</param>
    /// <returns>DateModified ticks from the primary image, or 0 if no image.</returns>
    internal static long GetArtModifiedTicks(MediaBrowser.Controller.Entities.BaseItem item)
    {
        var imageInfo = item.GetImageInfo(ImageType.Primary, 0);
        if (imageInfo != null && imageInfo.DateModified != default)
        {
            return imageInfo.DateModified.Ticks;
        }

        // For audio items, try album parent's image
        if (item is MediaBrowser.Controller.Entities.Audio.Audio)
        {
            var album = item.FindParent<MediaBrowser.Controller.Entities.Audio.MusicAlbum>();
            if (album != null)
            {
                var albumImageInfo = album.GetImageInfo(ImageType.Primary, 0);
                if (albumImageInfo != null && albumImageInfo.DateModified != default)
                {
                    return albumImageInfo.DateModified.Ticks;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Resolve the path to the ffmpeg binary.
    /// Tries IMediaEncoder first, then falls back to PATH lookup.
    /// </summary>
    /// <returns>Path to ffmpeg or empty string if not found.</returns>
    private string ResolveFfmpegPath()
    {
        if (!string.IsNullOrEmpty(FfmpegPath))
        {
            return FfmpegPath;
        }

        try
        {
            string? encoderPath = _mediaEncoder.EncoderPath;
            if (!string.IsNullOrEmpty(encoderPath) && System.IO.File.Exists(encoderPath))
            {
                return encoderPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve ffmpeg path from IMediaEncoder");
        }

        // Fallback: try to find ffmpeg on PATH
        try
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                string ffmpegName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir, ffmpegName);
                    if (System.IO.File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error searching PATH for ffmpeg");
        }

        return string.Empty;
    }

    /// <summary>
    /// Start an ffmpeg process writing to disk without awaiting its completion.
    /// Used by the stream-while-writing path so the response can start sending
    /// data while ffmpeg is still producing output.
    /// The caller is responsible for awaiting <see cref="Process.WaitForExitAsync"/>
    /// and disposing the process.
    /// </summary>
    /// <param name="ffmpegPath">Path to ffmpeg binary.</param>
    /// <param name="arguments">ffmpeg command-line arguments as individual tokens.</param>
    /// <returns>The started ffmpeg <see cref="Process"/> (not yet awaited).</returns>
    private Process StartFfmpegProcess(string ffmpegPath, List<string> arguments)
    {
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true
        };

        foreach (string arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        // Drain stderr asynchronously to prevent deadlock
        _ = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                _logger.LogDebug("ffmpeg stderr: {Line}", line);
            }
        });

        return process;
    }

    /// <summary>
    /// Monitor an ffmpeg process that was started by <see cref="StartFfmpegProcess"/>.
    /// Waits for the process to exit (with a 5-minute timeout), then triggers the
    /// background faststart remux. Disposes the process when done.
    /// The read stream is NOT disposed here — <see cref="FileStreamResult"/> owns it.
    /// </summary>
    /// <param name="process">The ffmpeg process (started, not yet awaited).</param>
    /// <param name="ffmpegPath">Path to ffmpeg binary (for remux).</param>
    /// <param name="cachePath">Path to the fragmented MP4 cache file.</param>
    /// <param name="itemId">Item ID for remux path computation.</param>
    /// <param name="artModifiedTicks">Art ticks for remux path computation.</param>
    /// <returns>A task representing the background monitoring operation.</returns>
    private async Task MonitorFfmpegAndRemuxAsync(
        Process process,
        string ffmpegPath,
        string cachePath,
        string itemId,
        long artModifiedTicks)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg exited with code {ExitCode} for item {ItemId}", process.ExitCode, itemId);
            }
            else
            {
                // Background: remux to a separate .fs.mp4 file with +faststart for seeking.
                // Writes to a NEW file — never overwrites the fragmented version.
                // Next play will prefer the seekable version via GetCachedFile.
                _ = RemuxToFaststartAsync(ffmpegPath, cachePath, itemId, artModifiedTicks);

                // Background eviction — don't block
                _ = Task.Run(() => _cache.EvictIfNeeded(), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ffmpeg timed out (5 min) for item {ItemId}", itemId);
            try { process.Kill(); } catch { /* already exited */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg monitoring failed for item {ItemId}", itemId);
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Monitor an HLS ffmpeg process running in the background. Waits for the process
    /// to exit (with a long timeout for audiobook-length content), logs the outcome,
    /// and triggers cache eviction. Disposes the process when done.
    /// Unlike <see cref="MonitorFfmpegAndRemuxAsync"/>, there is no remux step —
    /// HLS segments are already seekable individually.
    /// </summary>
    /// <param name="process">The ffmpeg process (started, not yet awaited).</param>
    /// <param name="hlsDir">The HLS directory containing playlist and segments.</param>
    /// <param name="itemId">Item ID for logging.</param>
    /// <param name="artModifiedTicks">Art ticks for logging.</param>
    /// <returns>A task representing the background monitoring operation.</returns>
    private async Task MonitorFfmpegHlsAsync(
        Process process,
        string hlsDir,
        string itemId,
        long artModifiedTicks)
    {
        try
        {
            // Long timeout for audiobook-length content (hours at 21x speed = minutes)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            // Post-completion diagnostics — gather metrics for structured logging
            int segmentFileCount = 0;
            int playlistSegmentCount = 0;
            int expectedChapterCount = 0;

            // Count actual .ts segment files on disk
            try
            {
#pragma warning disable CA3003
                segmentFileCount = Directory.GetFiles(hlsDir, "seg_*.ts").Length;
#pragma warning restore CA3003
            }
            catch (DirectoryNotFoundException)
            {
                // Directory already cleaned up
            }

            // Parse playlist for segment count
            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
#pragma warning disable CA3003
            if (System.IO.File.Exists(playlistPath))
            {
                try
                {
                    string content = await System.IO.File.ReadAllTextAsync(playlistPath).ConfigureAwait(false);
                    playlistSegmentCount = CountSegmentsInPlaylist(content);
                }
                catch (IOException)
                {
                    // Best effort
                }
            }

            // Read expected chapter count from metadata written at encode time
            string metadataPath = Path.Combine(hlsDir, "encode-metadata.json");
            if (System.IO.File.Exists(metadataPath))
            {
                try
                {
                    string json = await System.IO.File.ReadAllTextAsync(metadataPath).ConfigureAwait(false);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ExpectedChapterCount", out var countProp))
                    {
                        expectedChapterCount = countProp.GetInt32();
                    }
                }
                catch (Exception)
                {
                    // Best effort — metadata is optional
                }
            }
#pragma warning restore CA3003

            // Structured logging based on outcome
            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Audiobook HLS encoding FAILED for {ParentId}: ffmpeg exit code {ExitCode}, {SegmentFileCount} segment files, {PlaylistSegmentCount} playlist entries{MetaInfo}",
                    itemId, process.ExitCode, segmentFileCount, playlistSegmentCount,
                    expectedChapterCount > 0 ? $" (expected {expectedChapterCount})" : string.Empty);
            }
            else if (expectedChapterCount > 0 && playlistSegmentCount != expectedChapterCount)
            {
                _logger.LogWarning(
                    "Audiobook HLS encoding INCOMPLETE for {ParentId}: ffmpeg exited 0 but produced {PlaylistSegmentCount}/{ExpectedCount} playlist segments, {SegmentFileCount} segment files on disk",
                    itemId, playlistSegmentCount, expectedChapterCount, segmentFileCount);
            }
            else
            {
                _logger.LogDebug(
                    "Audiobook HLS encoding complete for {ParentId}: {SegmentFileCount} segments",
                    itemId, segmentFileCount);
            }

            // Background eviction — don't block
            _ = Task.Run(() => _cache.EvictIfNeeded(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Audiobook HLS encoding TIMED OUT (30 min) for {ParentId}", itemId);
            try { process.Kill(); } catch { /* already exited */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audiobook HLS: ffmpeg monitoring failed for {ParentId}", itemId);
        }
        finally
        {
            // Always clear the active encode flag so future requests can start a fresh encode
            _activeAudiobookEncodes.TryRemove(itemId, out _);
            process.Dispose();
        }
    }

    /// <summary>
    /// Background task: remux the fragmented MP4 to a separate seekable faststart file.
    /// Writes to <c>.fs.mp4</c> — a completely independent file that never overwrites the
    /// fragmented original. On next play, <see cref="VideoAudioCache.GetCachedFile"/> prefers
    /// the faststart version. Uses stream copy (-c copy) — no re-encoding, only I/O bound.
    /// </summary>
    /// <param name="ffmpegPath">Path to ffmpeg binary.</param>
    /// <param name="fragmentedPath">Path to the fragmented MP4 (input).</param>
    /// <param name="itemId">Item ID for computing the faststart output path.</param>
    /// <param name="artModifiedTicks">Art ticks for computing the faststart output path.</param>
    /// <returns>A task representing the background remux operation.</returns>
    private async Task RemuxToFaststartAsync(string ffmpegPath, string fragmentedPath, string itemId, long artModifiedTicks)
    {
        string faststartPath = _cache.GetFaststartCacheFilePath(itemId, artModifiedTicks);

        // Already have a faststart version? Skip.
#pragma warning disable CA3003
        if (System.IO.File.Exists(faststartPath))
#pragma warning restore CA3003
        {
            return;
        }

        try
        {
            var remuxArgs = new List<string> { "-i", fragmentedPath, "-c", "copy" };
            remuxArgs.AddRange(FaststartRemuxArgs);
            remuxArgs.Add(faststartPath);

            using var process = StartFfmpegProcess(ffmpegPath, remuxArgs);

            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg remux exited with code {ExitCode}", process.ExitCode);
#pragma warning disable CA3003
                TryDelete(faststartPath);
#pragma warning restore CA3003
                return;
            }

            _logger.LogDebug("VideoAudio: remuxed to faststart: {Path}", faststartPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VideoAudio: faststart remux failed for {Path}", fragmentedPath);
        }
    }

#pragma warning disable CA3003 // path derived from cachePath built from GUID-validated itemId
    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort
        }
    }
#pragma warning restore CA3003
}
