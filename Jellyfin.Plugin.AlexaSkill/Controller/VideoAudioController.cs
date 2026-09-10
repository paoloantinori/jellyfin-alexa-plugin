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
    /// Name of the episode path's pre-written full-listing playlist file (JF-531),
    /// the twin of the audiobook path's inline "playlist-full.m3u8": a SEPARATE file
    /// from ffmpeg's own stream.m3u8, listing every segment the encode will produce
    /// (no ENDLIST, event playlist) so first serve carries the true total runtime.
    /// </summary>
    private const string EpisodePrewrittenPlaylistFileName = "playlist-full.m3u8";

    /// <summary>
    /// The episode HLS <c>-hls_time</c> in seconds (JF-531). LOAD-BEARING COUPLING:
    /// <see cref="WriteEpisodePlaylist"/> derives the pre-written listing's segment
    /// count and TARGETDURATION from this value; retuning one without the other makes
    /// the listing claim the wrong segment count and the tail 404 until the completed
    /// ENDLIST playlist supersedes it. Audiobook (10s) and audio-only episode (10s)
    /// paths keep their own values; this one is episode-video only.
    /// </summary>
    private const int EpisodeHlsSegmentSeconds = 4;

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

        // JF-310: apply the configured encode-gate capacity. The controller is
        // constructed per-request, so this also picks up config changes on the next
        // request after a save (no explicit config-save hook needed).
        UpdateEncodeGateCapacity(Plugin.Instance?.Configuration.MaxConcurrentFfmpegEncodes ?? 2);
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
            var ffmpegProcess = await StartFfmpegProcessGatedAsync(
                validation.FfmpegPath,
                ffmpegArgs,
                EstimateEncodeBytes(validation.Item.RunTimeTicks ?? 0),
                cachePath).ConfigureAwait(false);

            FileStream stream;
            try
            {
                // Wait for ffmpeg to create the output file (it opens the file on startup).
                // This is nearly instantaneous in practice but guards against the race
                // between process.Start() and the file appearing on disk.
                // Also check if ffmpeg has already exited (e.g. invalid input) to avoid
                // a 1-second spin-wait when the process fails fast.
                // StartupWaitAttempts * StartupWaitDelayMs is the "~1s" the failure
                // diagnostic below names; keep them in sync when tuning.
                const int StartupWaitAttempts = 100;
                const int StartupWaitDelayMs = 10;
                bool fileAppeared = false;
                for (int i = 0; i < StartupWaitAttempts; i++)
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

                    await Task.Delay(StartupWaitDelayMs).ConfigureAwait(false);
                }

                if (!fileAppeared)
                {
                    // JF-518: ExitCode throws on a process that hasn't exited; the wait
                    // window above can expire while ffmpeg is still starting up.
                    // Deliberately not SafeExitCode: the else arm needs HasExited itself.
                    if (ffmpegProcess.HasExited)
                    {
                        _logger.LogWarning("VideoAudio: ffmpeg failed to create output for item {ItemId} (exit code {ExitCode})", itemId, ffmpegProcess.ExitCode);
                    }
                    else
                    {
                        _logger.LogWarning("VideoAudio: ffmpeg still running after ~1s without creating output for item {ItemId}", itemId);

                        // Kill before Dispose (matching the catch path below and the HLS
                        // sibling sites): Dispose alone orphans the process, which then
                        // finishes writing a cache file this endpoint has abandoned and
                        // can collide with a retry's fresh encode mid-write (JF-518 review).
                        // Bare catch like the siblings: on Unix a raced exit does not
                        // throw at all; the reachable shape is a non-ESRCH kill failure.
                        try { ffmpegProcess.Kill(); }
                        catch { /* raced to exit, or kill failed; Dispose follows either way */ }
                    }

                    ffmpegProcess.Dispose();
                    return StatusCode(500, new { error = "Video generation failed" });
                }

                stream = new FileStream(
                    cachePath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });

                // Kill ffmpeg if the client disconnects mid-stream. Registered BEFORE the
                // monitor handoff below; on a raced disposal the bare catch absorbs the
                // ObjectDisposedException, and the monitor's own timeout remains as backstop.
                HttpContext.RequestAborted.Register(static state =>
                {
                    var proc = (Process)state!;
                    try { proc.Kill(); }
                    catch { /* already exited */ }
                }, ffmpegProcess);
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process
                // (FileStream creation failed), so kill and clean up here.
                try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                ffmpegProcess.Dispose();
                throw;
            }

            // Monitor ffmpeg completion in the background: trigger remux + cleanup.
            // FileStreamResult owns the stream lifetime; the monitor must NOT dispose it.
            // CA2025: the guarded (disposing) scope ends above; the monitor start lives in
            // StartSongMonitor so no disposing code path can run after the handoff.
            StartSongMonitor(ffmpegProcess, validation.FfmpegPath, cachePath, itemId, artModifiedTicks);
#pragma warning restore CA3003

            _logger.LogDebug("VideoAudio: streaming generated file for item {ItemId}", itemId);
            return new FileStreamResult(stream, "video/mp4");
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
            var ffmpegProcess = await StartFfmpegProcessGatedAsync(
                validation.FfmpegPath,
                ffmpegArgs,
                EstimateEncodeBytes(validation.Item.RunTimeTicks ?? 0),
                hlsDir).ConfigureAwait(false);

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
                    _logger.LogWarning("VideoAudio HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, SafeExitCode(ffmpegProcess));
                    try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                    ffmpegProcess.Dispose();
                    return StatusCode(500, new { error = "HLS generation failed" });
                }

                // Register the HLS directory for fast segment lookups (avoids filesystem scan per segment)
                _cache.RegisterHlsDirectory(itemId, artModifiedTicks);
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process.
                try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                ffmpegProcess.Dispose();
                throw;
            }

            // Monitor ffmpeg in the background: wait for completion, log errors, trigger
            // eviction. CA2025: started via the boundary helper AFTER the disposing
            // scope above closed, so no later exception (e.g. the playlist re-read in
            // ServePlaylistWithToken racing ffmpeg's rewrite) can dispose the process
            // under the running monitor.
            StartHlsMonitor(ffmpegProcess, hlsDir, itemId, artModifiedTicks, "Song");

            // Serve the partial playlist immediately; the Echo Show will start
            // fetching segments and re-request the playlist for updates.
            _logger.LogDebug("VideoAudio HLS: serving partial playlist for item {ItemId}", itemId);
            return ServePlaylistWithToken(playlistPath, overrideToken);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Tracks episode/movie remux HLS encodes currently in progress by itemId (the
    /// JF-498 episode path). Doubles as the liveness signal for
    /// <see cref="ValidateEpisodeCacheAsync"/>: a cached playlist WITHOUT
    /// <c>#EXT-X-ENDLIST</c> is either a live encode (flag present) or the debris of
    /// an interrupted one (flag absent; cleaned up and re-encoded).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _activeEpisodeEncodes = new();

    /// <summary>
    /// Item IDs whose HLS directory was produced by the EPISODE remux path (JF-498).
    /// <see cref="GetSegment"/> consults it to skip audiobook position tracking: an
    /// episode's segment requests must not grow keys in
    /// <c>AudiobookPositionTracker</c> (never read there, but persisted to disk).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _episodeHlsItems = new();

    /// <summary>
    /// Attached-picture COVER codecs seen as VIDEO streams in tagged files (JF-500
    /// review R2): an embedded mjpeg/png cover can sit ahead of the real track in
    /// the stream list, and a cover that won the codec pick would misroute the
    /// episode tier decision below (mjpeg is "not h264" -> transcode of a static
    /// image). <see cref="ResolveSourceCodecs"/> skips these codecs on the video side, and the
    /// ffmpeg mapping (<c>-map 0:V:0</c>, capital V: ffmpeg video streams EXCLUDING
    /// attached pictures, verified on ffmpeg 8.1.2) drops them at the encode level.
    /// </summary>
    private static readonly HashSet<string> EpisodeCoverVideoCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mjpeg",
        "png"
    };

    /// <summary>
    /// Stream an HLS REMUX or video TRANSCODE of a movie/episode (JF-498/JF-500):
    /// sources whose audio codec has no decoder on the Echo Show (eac3 family) get a
    /// video stream copy + audio AAC transcode; sources whose VIDEO codec the Echo
    /// cannot decode (hevc/av1) get an H.264 re-encode instead of the copy. Both
    /// tiers land in MPEG-TS segments served stream-while-writing like the song
    /// path. Launch sites route here via <c>BaseHandler.GetVideoAppLaunchUrl</c>;
    /// the endpoint re-probes the codecs server-side to pick its own ffmpeg
    /// arguments (video copy vs transcode, audio copy vs AAC).
    /// </summary>
    /// <param name="itemId">The Jellyfin video item ID.</param>
    /// <returns>An HLS playlist (.m3u8) file.</returns>
    [HttpGet("episode/{itemId}/stream.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsEpisode([FromRoute] string itemId)
    {
        if (Guid.TryParse(itemId, out _))
        {
            ActionResult? tokenError = ValidateStreamToken(itemId);
            if (tokenError != null)
            {
                return tokenError;
            }
        }

        return await StreamHlsEpisodeCore(itemId).ConfigureAwait(false);
    }

    /// <summary>
    /// Build and serve the episode remux/transcode HLS playlist. Mirrors
    /// <see cref="StreamHlsVideoAudioCore"/> (per-item cache dir, per-item lock,
    /// first-segment wait, background monitor) with the video input
    /// and the full-framerate arguments of <see cref="BuildEpisodeHlsFfmpegArguments"/>
    /// (stream copy) or <see cref="BuildEpisodeHlsTranscodeFfmpegArguments"/> (H.264
    /// re-encode, JF-500). While an encode runs, the served playlist is the
    /// PRE-WRITTEN full listing of <see cref="WriteEpisodePlaylist"/> (JF-531; live-edge
    /// mechanism documented at the prewrite site); once the encode completes, the
    /// ENDLIST playlist ffmpeg wrote takes over.
    /// </summary>
    private async Task<ActionResult> StreamHlsEpisodeCore(string itemId)
    {
        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        long artModifiedTicks = GetArtModifiedTicks(validation.Item);

        // Fast path: a cached playlist is valid when the encode COMPLETED (ENDLIST)
        // or is still RUNNING (active flag); anything else is the debris of an
        // interrupted encode (server restart mid-encode) and is re-encoded.
        FileInfo? cached = await _cache.GetCachedHlsPlaylist(itemId, artModifiedTicks).ConfigureAwait(false);
        if (cached != null)
        {
            cached = await ValidateEpisodeCacheAsync(cached, itemId).ConfigureAwait(false);
            if (cached != null)
            {
                // JF-531: while the encode is RUNNING, serve the PRE-WRITTEN full
                // listing, not ffmpeg's growing stream.m3u8 (mechanism on the
                // prewrite site below). The flag gate matters: the pre-written file
                // survives on disk after completion, and a completed cache must
                // serve ffmpeg's ENDLIST playlist below.
                if (_activeEpisodeEncodes.ContainsKey(itemId))
                {
                    ActionResult? prewritten = TryServePrewrittenEpisodePlaylist(itemId, artModifiedTicks);
                    if (prewritten != null)
                    {
                        return prewritten;
                    }
                }

                _logger.LogDebug("VideoAudio episode HLS: serving cached playlist for item {ItemId}", itemId);
#pragma warning disable CA3003 // path derived from GUID-validated itemId
                return ServePlaylistWithToken(cached.FullName);
#pragma warning restore CA3003
            }
        }

        // Cache miss: acquire per-item lock
        using (await _cache.LockItemAsync(itemId, artModifiedTicks).ConfigureAwait(false))
        {
            _cache.CleanupHlsStub(itemId, artModifiedTicks);

            // Double-check cache after acquiring lock
            cached = await _cache.GetCachedHlsPlaylist(itemId, artModifiedTicks).ConfigureAwait(false);
            if (cached != null)
            {
                cached = await ValidateEpisodeCacheAsync(cached, itemId).ConfigureAwait(false);
                if (cached != null)
                {
                    // JF-531: the encode a concurrent request started is still running;
                    // serve its pre-written full listing, not ffmpeg's live one (flag
                    // gate as in the fast path: a completed cache serves the ENDLIST one).
                    if (_activeEpisodeEncodes.ContainsKey(itemId))
                    {
                        ActionResult? prewritten = TryServePrewrittenEpisodePlaylist(itemId, artModifiedTicks);
                        if (prewritten != null)
                        {
                            return prewritten;
                        }
                    }

                    _logger.LogDebug("VideoAudio episode HLS: serving playlist generated by concurrent request for item {ItemId}", itemId);
#pragma warning disable CA3003
                    return ServePlaylistWithToken(cached.FullName);
#pragma warning restore CA3003
                }
            }

            // Re-probe server-side to build the ffmpeg arguments (the handler-side
            // probe only picked the route). Video tier (JF-500): only a KNOWN h264
            // source is remuxed; every other video codec (hevc, av1, ...) is
            // re-encoded to H.264. An UNKNOWN codec (null) takes the TRANSCODE tier
            // too, inverting the handler-side route pick's fail-open on purpose: the
            // endpoint owns the cache, and a copy remux of undecodable video bytes
            // COMPLETES with ENDLIST, which ValidateEpisodeCacheAsync accepts, and
            // the cache-hit path never re-probes, so the broken copy would be served
            // forever. The wrong-tier cost is asymmetric the other way: transcoding
            // an actually-h264 source costs one extra encode, not a cached dud.
            var (sourceVideoCodec, sourceAudioCodec) = ResolveSourceCodecs(validation.Item);
            bool videoTranscodeTier = !VideoAppStreamPolicy.VideoSupportsRemux(sourceVideoCodec);
            if (videoTranscodeTier)
            {
                _logger.LogInformation(
                    "VideoAudio episode HLS: item {ItemId} video codec '{VideoCodec}' is not known h264; using the video transcode tier (libx264 ultrafast CRF 23, measured 4.40x realtime on the minix 2026-09-08, JF-500)",
                    itemId, sourceVideoCodec ?? "(unknown)");
            }

            _logger.LogDebug(
                "VideoAudio episode HLS: itemId={ItemId}, sourceVideoCodec={VideoCodec}, sourceAudioCodec={AudioCodec}",
                itemId,
                sourceVideoCodec ?? "(unknown)",
                sourceAudioCodec ?? "(unknown)");

#pragma warning disable CA3003 // paths derived from GUID-validated itemId
            string hlsDir = _cache.GetHlsDirectoryPath(itemId, artModifiedTicks);
            Directory.CreateDirectory(hlsDir);

            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
            // 4-second segments: a 45min episode is ~675 segments; %04d caps at 9999
            // (~11h at 4s, far above any episode/movie) and IsValidSegmentName
            // accepts 3-4 digit names.
            string segmentPath = Path.Combine(hlsDir, "seg_%04d.ts");
            string hlsBaseUrl = $"/alexaskill/api/video-audio/{itemId}/segments/";

            // The raw static stream is the ffmpeg input (the same no-auth shape the
            // song path uses for /Audio/{id}/stream; ffmpeg decodes what ExoPlayer
            // cannot).
            string videoUrl = $"{validation.ServerUrl}/Videos/{itemId}/stream?static=true";

            var ffmpegArgs = videoTranscodeTier
                ? BuildEpisodeHlsTranscodeFfmpegArguments(
                    videoUrl,
                    playlistPath,
                    segmentPath,
                    hlsBaseUrl,
                    sourceAudioCodec)
                : BuildEpisodeHlsFfmpegArguments(videoUrl, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio episode HLS: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Review I1 (JF-498): remove any debris that survived the cleanup attempts
            // above. The whole-directory deletes (ValidateEpisodeCacheAsync's Cleanup,
            // CleanupHlsStub) are all-or-nothing and swallow failures; a playlist that
            // survived them would make append_list append this encode's entries to the
            // stale ones, baking a doubled playlist into the cache. Per-file deletion
            // (best-effort, warning + proceed on failure) so ffmpeg always starts over
            // a clean target.
            _cache.DeleteHlsEncodeDebris(itemId, artModifiedTicks);

            // JF-531: pre-write the FULL segment listing (the audiobook pattern,
            // WriteAudiobookPlaylist/playlist-full.m3u8) BEFORE the active-encode
            // flag goes up, so any request that later sees the flag finds the file.
            // ffmpeg keeps writing its own stream.m3u8 (append_list growth, ENDLIST
            // at completion); what we SERVE while the encode runs is this full
            // listing. Why: ExoPlayer treats a no-ENDLIST playlist as LIVE, starts
            // playback at the live edge (the newest segment) and shows only the
            // encoded-so-far window on the seekbar (live evidence 2026-09-09,
            // corr=c0c21c6a: a 51-min HEVC episode showed a 6-min bar and started
            // near the end). A full listing with no ENDLIST is an EVENT playlist:
            // the seekbar shows the true runtime, playback starts at segment 0, and
            // the player plays available segments without failing on the
            // not-yet-written tail (the JF-503 near-ahead hold covers early fetches
            // there). When the encode completes, ffmpeg's own ENDLIST playlist takes
            // over via the cache-hit paths above.
            string prewrittenPath = Path.Combine(hlsDir, EpisodePrewrittenPlaylistFileName);
            long runtimeTicks = validation.Item.RunTimeTicks ?? 0;
            if (runtimeTicks > 0)
            {
                WriteEpisodePlaylist(prewrittenPath, hlsBaseUrl, runtimeTicks, HttpContext.Request.Query["token"]);
            }
            else
            {
                // No runtime: an honest full listing cannot be computed. Fall back
                // to serving ffmpeg's live playlist (the pre-JF-531 behavior) and
                // remove any stale listing so the active-encode guard below can
                // never serve one this encode did not write.
                TryDelete(prewrittenPath);
                _logger.LogWarning(
                    "VideoAudio episode HLS: item {ItemId} has no runtime, cannot pre-write the full listing (JF-531); serving ffmpeg's live playlist instead",
                    itemId);
            }

            // Mark the encode active BEFORE starting ffmpeg (inside the lock): a
            // concurrent fast-path request that sees a live (no-ENDLIST) playlist
            // can then rely on the flag being set, because no playlist file can
            // exist before ffmpeg starts. The monitor clears the flag on exit.
            _activeEpisodeEncodes.TryAdd(itemId, true);
            _episodeHlsItems.TryAdd(itemId, true);

            Process ffmpegProcess;
            try
            {
                ffmpegProcess = await StartFfmpegProcessGatedAsync(
                    validation.FfmpegPath,
                    ffmpegArgs,
                    videoTranscodeTier
                        ? EstimateEpisodeTranscodeEncodeBytes(validation.Item.RunTimeTicks ?? 0)
                        : EstimateEpisodeEncodeBytes(
                            validation.Item.RunTimeTicks ?? 0,
                            ResolveTotalMediaBitrateBps(validation.Item)),
                    hlsDir,
                    videoTranscodeTier ? _episodeTranscodeSlot : null).ConfigureAwait(false);
            }
            catch
            {
                _activeEpisodeEncodes.TryRemove(itemId, out _);
                throw;
            }

            try
            {
                // Wait for the first segment + playlist to appear. A remux is
                // I/O-bound (tens of x realtime), so the first 4s segment is on disk
                // in well under a second; a video-transcode encode (JF-500) at the
                // measured 4.40x realtime needs ~1s of encode for the first 4s
                // segment plus ffmpeg startup, still far inside the window. The
                // ~20s ceiling only guards pathological cases (network-attached
                // library, cold cache).
                string firstSegmentPath = Path.Combine(hlsDir, "seg_0000.ts");
                bool segmentAppeared = false;
                for (int i = 0; i < 200; i++)
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
                    _logger.LogWarning("VideoAudio episode HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, SafeExitCode(ffmpegProcess));
                    try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                    ffmpegProcess.Dispose();
                    _activeEpisodeEncodes.TryRemove(itemId, out _);
                    return StatusCode(500, new { error = "Episode HLS generation failed" });
                }

                // Register the HLS directory for fast segment lookups.
                _cache.RegisterHlsDirectory(itemId, artModifiedTicks);
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process.
                try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                ffmpegProcess.Dispose();
                _activeEpisodeEncodes.TryRemove(itemId, out _);
                throw;
            }

            // Monitor ffmpeg in the background: wait for completion, log errors,
            // trigger eviction, clear the active-encode flag (its finally owns both
            // the flag clear and the process disposal from here on). The transcode
            // tier carries the item runtime so the monitor kill scales with it (JF-500
            // review F1); the remux tier keeps the fixed ceiling. CA2025: started via
            // the boundary helper AFTER the disposing scope above closed.
            StartHlsMonitor(
                ffmpegProcess,
                hlsDir,
                itemId,
                artModifiedTicks,
                "Episode",
                _activeEpisodeEncodes,
                videoTranscodeTier,
                validation.Item.RunTimeTicks);

            // Serve the pre-written FULL listing immediately (JF-531). Null only
            // when the prewrite was skipped (no runtime): fall back to ffmpeg's
            // live partial playlist, the pre-JF-531 behavior.
            ActionResult? prewrittenServe = TryServePrewrittenEpisodePlaylist(itemId, artModifiedTicks);
            if (prewrittenServe != null)
            {
                return prewrittenServe;
            }

            _logger.LogDebug("VideoAudio episode HLS: serving partial playlist for item {ItemId}", itemId);
            return ServePlaylistWithToken(playlistPath);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Validate a cached episode remux playlist (JF-498): valid when the encode
    /// completed (<c>#EXT-X-ENDLIST</c> present) or an encode is currently running
    /// (the playlist is ffmpeg's live, growing one; checked first, in memory, so the
    /// periodic playlist re-fetches during a live encode cost no file read here).
    /// Anything else is the debris of an interrupted encode (e.g. server restart
    /// mid-encode): the directory is removed so the item re-encodes instead of
    /// stalling mid-episode at the point the old encode died.
    /// </summary>
    /// <param name="cached">The cached playlist file info (stream.m3u8).</param>
    /// <param name="itemId">Episode/movie item ID for logging and cleanup.</param>
    /// <returns>The original <paramref name="cached"/> if valid, or null when invalidated.</returns>
    private async Task<FileInfo?> ValidateEpisodeCacheAsync(FileInfo cached, string itemId)
    {
        if (_activeEpisodeEncodes.ContainsKey(itemId))
        {
            return cached;
        }

#pragma warning disable CA3003 // paths derived from GUID-validated itemId
        string content = await System.IO.File.ReadAllTextAsync(cached.FullName).ConfigureAwait(false);
        if (!content.Contains(HlsEndList, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Episode HLS cache invalidated for {ItemId}: playlist has no ENDLIST and no encode is running (interrupted encode?), re-encoding",
                itemId);
            _cache.Cleanup(itemId);
            return null;
        }
#pragma warning restore CA3003

        return cached;
    }

    /// <summary>
    /// Serve the episode encode's pre-written full listing (JF-531) when it exists.
    /// Called on every serve path whose active-encode flag is set (fast path, in-lock
    /// double check, first serve). Returns null when the file is absent (encode with
    /// unknown runtime, or a race before the prewrite landed) so the caller falls
    /// back to serving the live playlist, the pre-JF-531 behavior, rather than
    /// failing the play. Live-edge mechanism: the prewrite site in
    /// <see cref="StreamHlsEpisodeCore"/>.
    /// </summary>
    /// <param name="itemId">GUID-validated episode item ID.</param>
    /// <param name="artModifiedTicks">Art ticks of the item's HLS cache directory.</param>
    /// <returns>The playlist response, or null when no pre-written listing exists.</returns>
    private ActionResult? TryServePrewrittenEpisodePlaylist(string itemId, long artModifiedTicks)
    {
#pragma warning disable CA3003 // path derived from GUID-validated itemId
        string prewrittenPath = Path.Combine(
            _cache.GetHlsDirectoryPath(itemId, artModifiedTicks),
            EpisodePrewrittenPlaylistFileName);
        if (!System.IO.File.Exists(prewrittenPath))
        {
            return null;
        }
#pragma warning restore CA3003

        _logger.LogDebug(
            "VideoAudio episode HLS: serving pre-written full listing for item {ItemId} (encode in progress, JF-531)",
            itemId);
        return ServePlaylistWithToken(prewrittenPath);
    }

    /// <summary>
    /// Cache key of the AUDIO-ONLY episode variant (JF-507): deliberately distinct from
    /// the bare itemId the video remux keys on, so the two encodes of the same item can
    /// never collide (the in-memory directory lookup and the <c>{key}_*</c> filesystem
    /// scan both key on this string), and distinct per start position because a
    /// <c>-ss</c>-seeked encode serves a different timeline than a from-zero one.
    /// </summary>
    /// <param name="itemId">GUID-validated item ID.</param>
    /// <param name="startTicks">The encode's start position (0 for a from-zero encode).</param>
    /// <returns>The variant cache key string.</returns>
    internal static string EpisodeAudioCacheKey(string itemId, long startTicks)
        => startTicks > 0 ? $"{itemId}-audio-{startTicks}" : $"{itemId}-audio";

    /// <summary>
    /// Stream an AUDIO-ONLY HLS transcode of a movie/episode (JF-507): the item's first
    /// audio track mapped alone (-map 0:a:0, AAC stereo) for AudioPlayer launches on
    /// screenless devices, whose raw <c>/Audio/{id}/stream?static=true</c> URL serves the
    /// source bytes (an EAC3 track the Dot cannot decode; live incident 2026-09-06
    /// corr=f0240020: playback died at 1ms). Optional <c>?start=&lt;ticks&gt;</c> seeks the
    /// encode (ffmpeg -ss): the served timeline STARTS at that position, so the
    /// AudioPlayer.Play directive must carry offset 0 for a start-shifted URL (a from-zero
    /// encode runs at ~49x realtime, measured on the incident episode, so the segment a
    /// deep offset maps to does not exist on the player's first fetch).
    /// Mirrors <see cref="StreamHlsEpisodeCore"/>: per-item cache dir, per-item lock,
    /// first-segment wait, partial playlist, background monitor, encode gate.
    /// </summary>
    /// <param name="itemId">The Jellyfin video item ID.</param>
    /// <param name="startTicks">Optional resume position in .NET ticks (0 to play from the start).</param>
    /// <returns>An HLS playlist (.m3u8) file.</returns>
    [HttpGet("episode/{itemId}/audio.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsEpisodeAudio(
        [FromRoute] string itemId,
        [FromQuery(Name = "start")] long? startTicks = null)
    {
        if (Guid.TryParse(itemId, out _))
        {
            ActionResult? tokenError = ValidateStreamToken(itemId);
            if (tokenError != null)
            {
                return tokenError;
            }
        }

        return await StreamHlsEpisodeAudioCore(itemId, startTicks ?? 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Build and serve the audio-only episode HLS playlist: the same cache/lock/gate/
    /// monitor machinery as the episode remux, with the audio-only ffmpeg arguments and
    /// the variant cache key (so it can never collide with the video remux of the same
    /// item, and different start positions cache apart).
    /// </summary>
    /// <param name="itemId">GUID-validated item ID.</param>
    /// <param name="startTicks">Encode start position in ticks (0 = from the beginning).</param>
    /// <returns>The playlist, or an error result.</returns>
    private async Task<ActionResult> StreamHlsEpisodeAudioCore(string itemId, long startTicks)
    {
        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        long artModifiedTicks = GetArtModifiedTicks(validation.Item);
        string cacheKey = EpisodeAudioCacheKey(itemId, startTicks);

        // Same cache-validity rule as the remux: completed (ENDLIST) or actively encoding.
        FileInfo? cached = await _cache.GetCachedHlsPlaylist(cacheKey, artModifiedTicks).ConfigureAwait(false);
        if (cached != null)
        {
            cached = await ValidateEpisodeCacheAsync(cached, cacheKey).ConfigureAwait(false);
            if (cached != null)
            {
                _logger.LogDebug("VideoAudio episode AUDIO HLS: serving cached playlist for item {ItemId} (start={StartTicks})", itemId, startTicks);
#pragma warning disable CA3003 // path derived from GUID-validated itemId
                return ServePlaylistWithToken(cached.FullName);
#pragma warning restore CA3003
            }
        }

        using (await _cache.LockItemAsync(cacheKey, artModifiedTicks).ConfigureAwait(false))
        {
            _cache.CleanupHlsStub(cacheKey, artModifiedTicks);

            cached = await _cache.GetCachedHlsPlaylist(cacheKey, artModifiedTicks).ConfigureAwait(false);
            if (cached != null)
            {
                cached = await ValidateEpisodeCacheAsync(cached, cacheKey).ConfigureAwait(false);
                if (cached != null)
                {
                    _logger.LogDebug("VideoAudio episode AUDIO HLS: serving playlist generated by concurrent request for item {ItemId} (start={StartTicks})", itemId, startTicks);
#pragma warning disable CA3003
                    return ServePlaylistWithToken(cached.FullName);
#pragma warning restore CA3003
                }
            }

            // Re-probe server-side (the handler-side probe only picked the route): copy
            // for the muxer-compatible codecs (mp3/aac), AAC re-encode for everything
            // else, which is the family this endpoint exists for.
            string? sourceAudioCodec = ResolveSourceAudioCodec(validation.Item);
            _logger.LogDebug(
                "VideoAudio episode AUDIO HLS: itemId={ItemId}, startTicks={StartTicks}, sourceAudioCodec={AudioCodec}",
                itemId, startTicks, sourceAudioCodec ?? "(unknown)");

#pragma warning disable CA3003 // paths derived from GUID-validated itemId
            string hlsDir = _cache.GetHlsDirectoryPath(cacheKey, artModifiedTicks);
            Directory.CreateDirectory(hlsDir);

            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
            // 10-second segments (the audio-rate value; a 45min episode is ~270 segments
            // and %04d caps at 9999). Fewer, larger fetches through the public URL than
            // the remux's 4s, and audio-only has no keyframe constraint.
            string segmentPath = Path.Combine(hlsDir, "seg_%04d.ts");
            // Segment URLs point at the dedicated audio-segments route: it embeds the
            // start position (the directory key) in the path, and never touches the
            // generic segments route the video remux serves through.
            string hlsBaseUrl = $"/alexaskill/api/video-audio/episode/{itemId}/audio-segments/{startTicks}/";

            string videoUrl = $"{validation.ServerUrl}/Videos/{itemId}/stream?static=true";

            var ffmpegArgs = BuildEpisodeAudioHlsFfmpegArguments(videoUrl, startTicks, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio episode AUDIO HLS: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Per-file debris cleanup so ffmpeg always starts over a clean target (the
            // JF-498 review I1 concern, same as the remux path).
            _cache.DeleteHlsEncodeDebris(cacheKey, artModifiedTicks);

            _activeEpisodeEncodes.TryAdd(cacheKey, true);

            Process ffmpegProcess;
            try
            {
                ffmpegProcess = await StartFfmpegProcessGatedAsync(
                    validation.FfmpegPath,
                    ffmpegArgs,
                    EstimateEpisodeAudioEncodeBytes(validation.Item.RunTimeTicks ?? 0),
                    hlsDir).ConfigureAwait(false);
            }
            catch
            {
                _activeEpisodeEncodes.TryRemove(cacheKey, out _);
                throw;
            }

            try
            {
                // Audio-only AAC encodes at ~49x realtime (measured on the incident
                // episode, 2026-09-07), so the first 10s segment is on disk well under a
                // second; the ceiling only guards pathological cases.
                string firstSegmentPath = Path.Combine(hlsDir, "seg_0000.ts");
                bool segmentAppeared = false;
                for (int i = 0; i < 200; i++)
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
                    _logger.LogWarning("VideoAudio episode AUDIO HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, SafeExitCode(ffmpegProcess));
                    try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                    ffmpegProcess.Dispose();
                    _activeEpisodeEncodes.TryRemove(cacheKey, out _);
                    return StatusCode(500, new { error = "Episode audio HLS generation failed" });
                }

                _cache.RegisterHlsDirectory(cacheKey, artModifiedTicks);
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process.
                try { ffmpegProcess.Kill(); } catch { /* already exited */ }
                ffmpegProcess.Dispose();
                _activeEpisodeEncodes.TryRemove(cacheKey, out _);
                throw;
            }

            // CA2025: monitor started via the boundary helper AFTER the disposing scope
            // above closed; the monitor's finally owns the flag clear and the disposal.
            StartHlsMonitor(ffmpegProcess, hlsDir, cacheKey, artModifiedTicks, "EpisodeAudio", _activeEpisodeEncodes);

            _logger.LogDebug("VideoAudio episode AUDIO HLS: serving partial playlist for item {ItemId} (start={StartTicks})", itemId, startTicks);
            return ServePlaylistWithToken(playlistPath);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Serve an individual HLS segment of the audio-only episode variant (JF-507). A
    /// dedicated route (not the generic <c>{itemId}/segments</c> one) because the cache
    /// directory is keyed by the variant key, which embeds the start position: the path
    /// carries <paramref name="startTicks"/> so the key can be recomputed. Skips the
    /// audiobook position tracking the generic route performs (episode keys are never
    /// read there); keeps its token validation, segment-name validation, and the JF-503
    /// hold-for-near-ahead-segment behavior of a running encode.
    /// </summary>
    /// <param name="itemId">The Jellyfin video item ID (GUID-validated, token-scoped).</param>
    /// <param name="startTicks">The encode start position carried in the playlist's segment URLs.</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_0000.ts").</param>
    /// <returns>The segment file.</returns>
    [HttpGet("episode/{itemId}/audio-segments/{startTicks:long}/{segmentName}")]
    [AllowAnonymous]
    public async Task<ActionResult> GetEpisodeAudioSegment(
        [FromRoute] string itemId,
        [FromRoute] long startTicks,
        [FromRoute] string segmentName)
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

        if (!VideoAudioCache.IsValidSegmentName(segmentName))
        {
            _logger.LogWarning("VideoAudio episode AUDIO HLS: rejected invalid segment name '{SegmentName}' for item {ItemId}", segmentName, itemId);
            return BadRequest(new { error = "Invalid segment name" });
        }

        string cacheKey = EpisodeAudioCacheKey(itemId, startTicks);
        string? segmentPath = _cache.FindSegmentPath(cacheKey, segmentName);
        if (segmentPath == null)
        {
            segmentPath = await TryHoldForNearAheadSegmentAsync(cacheKey, segmentName, HttpContext.RequestAborted).ConfigureAwait(false);
            if (segmentPath == null)
            {
                return NotFound(new { error = "Segment not found" });
            }
        }

#pragma warning disable CA3003 // segmentPath validated via GUID itemId + strict segment name pattern upstream
        return PhysicalFile(segmentPath, "video/mp2t", enableRangeProcessing: true);
#pragma warning restore CA3003
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
            var ffmpegProcess = await StartFfmpegProcessGatedAsync(
                ffmpeg,
                ffmpegArgs,
                EstimateEncodeBytes(chapters.Sum(c => c.RunTimeTicks ?? 0)),
                hlsDir).ConfigureAwait(false);

            // Register the HLS directory for segment lookups immediately.
            _cache.RegisterHlsDirectory(parentId, artModifiedTicks);

            // Mark this audiobook as actively encoding to prevent concurrent ffmpeg launches.
            _activeAudiobookEncodes.TryAdd(parentId, true);

            // Monitor ffmpeg in background: logs errors, triggers eviction when done.
            // CA2025: started via the boundary helper (this method never disposes the
            // process). The HasExited/ExitCode reads below are best-effort; their
            // InvalidOperationException catch already tolerates the monitor's raced
            // disposal (the monitor's finally is the sole owner).
            StartHlsMonitor(ffmpegProcess, hlsDir, parentId, artModifiedTicks, "Audiobook");

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
    /// On a miss for an item with an ACTIVE encode, a NEAR-AHEAD segment request is held
    /// briefly for ffmpeg to write the file instead of 404-ing (JF-503; see
    /// <see cref="TryHoldForNearAheadSegmentAsync"/> for the boundary).
    /// </summary>
    /// <param name="itemId">The Jellyfin audio item ID.</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_0000.ts").</param>
    /// <returns>The segment file.</returns>
    [HttpGet("{itemId}/segments/{segmentName}")]
    [AllowAnonymous]
    public async Task<ActionResult> GetSegment([FromRoute] string itemId, [FromRoute] string segmentName)
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
        // Best-effort: never fail the segment request over tracking. Episode remux
        // segments (JF-498) are skipped: their keys are never read by the audiobook
        // resume path and would only grow the persisted positions file.
        if (!_episodeHlsItems.ContainsKey(itemId))
        {
            RecordSegmentForTracking(itemId, segmentName);
        }

        string? segmentPath = _cache.FindSegmentPath(itemId, segmentName);
        if (segmentPath == null)
        {
            segmentPath = await TryHoldForNearAheadSegmentAsync(itemId, segmentName, HttpContext.RequestAborted).ConfigureAwait(false);
            if (segmentPath == null)
            {
                return NotFound(new { error = "Segment not found" });
            }
        }

#pragma warning disable CA3003 // segmentPath validated via GUID itemId + strict segment name pattern
        return PhysicalFile(segmentPath, "video/mp2t", enableRangeProcessing: true);
#pragma warning restore CA3003
    }

    /// <summary>
    /// Parse the segment number out of a <c>seg_NNN[N].ts</c> name. Shared by the
    /// audiobook position tracker and the JF-503 hold-for-segment head computation.
    /// </summary>
    /// <param name="segmentName">The segment file name (e.g. "seg_0042.ts").</param>
    /// <param name="segmentNumber">The parsed number, or -1 when the name is not a segment.</param>
    /// <returns>True when the name parsed to a number.</returns>
    private static bool TryParseSegmentNumber(string segmentName, out int segmentNumber)
    {
        // Format: "seg_" + digits + ".ts"  → digits span [4, Length-3)
        if (segmentName.Length < 8 || !segmentName.StartsWith("seg_", StringComparison.Ordinal) || !segmentName.EndsWith(".ts", StringComparison.Ordinal))
        {
            segmentNumber = -1;
            return false;
        }

        if (int.TryParse(segmentName.AsSpan(4, segmentName.Length - 7), out segmentNumber))
        {
            return true;
        }

        segmentNumber = -1;
        return false;
    }

    /// <summary>
    /// Best-effort audiobook position tracking: parse the segment number from a
    /// <c>seg_NNN[N].ts</c> name and record it. For audiobook concat streams, itemId is
    /// the book parent-folder ID (matches the resume-time lookup key). Single-item
    /// segment requests are keyed by their own itemId and simply never read at resume.
    /// </summary>
    private void RecordSegmentForTracking(string itemId, string segmentName)
    {
        if (TryParseSegmentNumber(segmentName, out int segmentNumber))
        {
            Plugin.Instance?.AudiobookPositionTracker?.RecordSegment(itemId, segmentNumber);
        }
    }

    /// <summary>
    /// Total time a near-ahead segment request waits for a running encode to write the
    /// file (JF-503). Internal test hook (the InternalsVisibleTo seam, same shape as
    /// <see cref="VideoAudioCache.PlaybackEvictionExemptionTtl"/>): production ~3.5s,
    /// tests shrink it to milliseconds.
    /// </summary>
    internal TimeSpan SegmentHoldBudget { get; set; } = TimeSpan.FromMilliseconds(3500);

    /// <summary>
    /// Poll interval of the hold-for-segment loop (JF-503). Internal test hook: tests
    /// shrink it together with <see cref="SegmentHoldBudget"/>.
    /// </summary>
    internal TimeSpan SegmentHoldPollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How many segments beyond the highest existing one a GetSegment request may name
    /// and still be held for (JF-503). +2 covers a seek landing just past the running
    /// encode's head (~8s of 4s episode segments, ~20s of 10s audiobook segments);
    /// anything farther is a jump into unencoded minutes that no bounded wait can serve.
    /// </summary>
    internal const int SegmentHoldLookahead = 2;

    /// <summary>
    /// JF-503 hold-for-segment: when a GetSegment request names a segment that does not
    /// exist yet while an encode is ACTIVE for the item, and the requested segment is
    /// the next expected one (within <see cref="SegmentHoldLookahead"/> of the highest
    /// existing segment), block bounded for ffmpeg to write the file and serve it
    /// instead of 404-ing immediately. ExoPlayer errors hard on a missing segment, so
    /// during the first minutes of an encode (remux at ~20x realtime) a seek that lands
    /// just past the encoded head kills playback without this smoothing. Completion is
    /// gated on ffmpeg's live playlist LISTING the segment (the write-completion
    /// signal), never on the file merely existing: see
    /// <see cref="IsSegmentListedInLivePlaylistAsync"/>.
    ///
    /// USER-VISIBLE BOUNDARY (stated plainly):
    /// - Seeks WITHIN the encoded head, or roughly 3 seconds beyond it, are smoothed:
    ///   the request waits briefly and then serves the freshly written segment.
    /// - Seeks FAR beyond the head of a running encode (e.g. to minute 30 of an episode
    ///   that has only encoded 5 minutes) still fail: the endpoint answers 404 as
    ///   before and the player errors until the encode catches up. No bounded wait can
    ///   serve a jump into unencoded content.
    /// - After the encode completes (~2-3 minutes for a 45min episode at ~20x realtime,
    ///   the 2026-09-06 Silo measurement) every seek works.
    ///
    /// Scoping across the three encode families: the hold keys on the ACTIVE-encode
    /// flags, so it covers the EPISODE remux (<see cref="_activeEpisodeEncodes"/>, the
    /// JF-503 device failure) and the AUDIOBOOK concat
    /// (<see cref="_activeAudiobookEncodes"/>: its pre-written event playlist lists
    /// every segment from first play, so a seek during the first minutes hits the same
    /// listed-but-missing 404). The single-item SONG path is deliberately NOT covered:
    /// it sets no active-encode flag, its encode finishes within seconds, and it serves
    /// ffmpeg's live playlist which only lists existing segments, so the near-ahead
    /// window is sub-second and unreported.
    /// </summary>
    /// <param name="itemId">The GUID-validated item ID the segment URL carries.</param>
    /// <param name="segmentName">The validated segment name that was not found.</param>
    /// <param name="cancellationToken">Aborts the hold when the client goes away.</param>
    /// <returns>The segment path once it appears within the budget, or null to 404.</returns>
    private async Task<string?> TryHoldForNearAheadSegmentAsync(string itemId, string segmentName, CancellationToken cancellationToken)
    {
        // Resolve the item's HLS directory to compute the encode HEAD (the highest
        // segment number on disk). Same resolution order FindSegmentPath uses.
        string? hlsDir = _cache.FindHlsDirectory(itemId);
        int requested = TryParseSegmentNumber(segmentName, out int requestedNumber) ? requestedNumber : -1;
        int highest = GetHighestSegmentNumber(hlsDir);

        bool encodeActive = _activeEpisodeEncodes.ContainsKey(itemId)
            || _activeAudiobookEncodes.ContainsKey(itemId);

        // Hold only for the next expected segment(s) of a live encode. A miss with no
        // active encode is stale debris (or a wrong GUID), and a miss BEHIND the head
        // (requested <= highest) can never be backfilled: ffmpeg writes sequentially.
        bool holdEligible = encodeActive
            && hlsDir != null
            && requested > highest
            && requested <= highest + SegmentHoldLookahead;

        // JF-503 observability: every GetSegment miss logs the requested name and the
        // highest existing segment, so the next device session can confirm the
        // seek-head mechanism from the logs (this endpoint is the only segment 404 source).
        _logger.LogDebug(
            "GetSegment miss: item {ItemId} requested {SegmentName}, highest existing segment {Highest}, activeEncode {ActiveEncode}, holdEligible {HoldEligible}",
            itemId, segmentName, highest, encodeActive, holdEligible);

        if (!holdEligible)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < SegmentHoldBudget)
            {
                // Wait at most one poll interval, and never past the budget.
                TimeSpan remaining = SegmentHoldBudget - stopwatch.Elapsed;
                TimeSpan wait = remaining < SegmentHoldPollInterval ? remaining : SegmentHoldPollInterval;
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

                // Completion gate: the segment must be LISTED in ffmpeg's live
                // playlist, not merely present on disk. The HLS muxer rewrites
                // stream.m3u8 only after a segment file is fully written and closed,
                // while File.Exists alone can catch the segment mid-write and serve a
                // truncated .ts (review finding on the first JF-503 cut).
                if (await IsSegmentListedInLivePlaylistAsync(hlsDir!, segmentName).ConfigureAwait(false))
                {
                    string? path = _cache.FindSegmentPath(itemId, segmentName);
                    if (path != null)
                    {
                        _logger.LogDebug(
                            "GetSegment hold: {SegmentName} appeared after {HeldMs}ms for item {ItemId} (encode caught up)",
                            segmentName, (int)stopwatch.Elapsed.TotalMilliseconds, itemId);
                        return path;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away mid-hold; nobody will read the 404 either.
            _logger.LogDebug(
                "GetSegment hold aborted (client disconnected) after {HeldMs}ms for item {ItemId}: {SegmentName}",
                (int)stopwatch.Elapsed.TotalMilliseconds, itemId, segmentName);
            return null;
        }

        _logger.LogDebug(
            "GetSegment hold expired after {HeldMs}ms for item {ItemId}: {SegmentName} still not written (budget {BudgetMs}ms)",
            (int)stopwatch.Elapsed.TotalMilliseconds, itemId, segmentName, (int)SegmentHoldBudget.TotalMilliseconds);
        return null;
    }

    /// <summary>
    /// Whether ffmpeg's LIVE playlist (stream.m3u8 in the item's HLS directory) already
    /// lists the segment. The HLS muxer rewrites the playlist only AFTER a segment file
    /// is fully written and closed, so a playlist entry is the segment-completion
    /// signal; File.Exists alone can catch a segment mid-write. False when the playlist
    /// does not exist yet or is mid-rename (the caller keeps polling).
    /// </summary>
    /// <param name="hlsDir">The item's HLS cache directory.</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_0001.ts").</param>
    /// <returns>True when the live playlist lists the segment.</returns>
    private static async Task<bool> IsSegmentListedInLivePlaylistAsync(string hlsDir, string segmentName)
    {
        try
        {
#pragma warning disable CA3003 // hlsDir resolved by the cache from a GUID-validated itemId
            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
            if (!System.IO.File.Exists(playlistPath))
            {
                return false;
            }

            string content = await System.IO.File.ReadAllTextAsync(playlistPath).ConfigureAwait(false);
            return content.Contains(segmentName, StringComparison.Ordinal);
#pragma warning restore CA3003
        }
        catch (IOException)
        {
            // Playlist vanished (eviction) or is mid-rename; treat as not ready.
            return false;
        }
    }

    /// <summary>
    /// Highest segment number present in an HLS cache directory (-1 when the directory
    /// is missing or holds no segments). JF-503: defines the running encode's HEAD for
    /// the hold-for-segment near-ahead test. Called only on a GetSegment miss (never on
    /// the per-segment hot path), so a directory enumeration here is fine.
    /// </summary>
    /// <param name="hlsDir">The item's HLS cache directory, or null when none exists.</param>
    /// <returns>The highest segment number on disk, or -1.</returns>
    private static int GetHighestSegmentNumber(string? hlsDir)
    {
        if (hlsDir == null)
        {
            return -1;
        }

        int highest = -1;
        try
        {
#pragma warning disable CA3003 // hlsDir resolved by the cache from a GUID-validated itemId
            foreach (string file in Directory.EnumerateFiles(hlsDir, "seg_*.ts"))
#pragma warning restore CA3003
            {
                if (TryParseSegmentNumber(Path.GetFileName(file), out int number) && number > highest)
                {
                    highest = number;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Evicted between resolution and enumeration; treat as empty.
        }

        return highest;
    }

    /// <summary>
    /// Internal test seam (JF-503, InternalsVisibleTo): set or clear the active-encode
    /// flag the hold-for-segment path keys on, without a live ffmpeg process.
    /// <paramref name="audiobook"/> selects the audiobook registry instead of the
    /// episode one, mirroring which endpoint would have set it in production.
    /// </summary>
    /// <param name="itemId">The item ID (episode itemId or audiobook parentId).</param>
    /// <param name="active">True to mark an encode active, false to clear it.</param>
    /// <param name="audiobook">True to target the audiobook registry (default episode).</param>
    internal static void SetEncodeActiveForTest(string itemId, bool active, bool audiobook = false)
    {
        var registry = audiobook ? _activeAudiobookEncodes : _activeEpisodeEncodes;
        if (active)
        {
            registry.TryAdd(itemId, true);
        }
        else
        {
            registry.TryRemove(itemId, out _);
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
    /// Resolve the source audio codec for an item by reading its first audio media stream.
    /// Used to decide between <c>-c:a copy</c> (mp3/aac) and AAC transcode for the
    /// single-item video-audio path. Returns null when the codec cannot be determined
    /// (e.g. media source manager unavailable, no audio stream, or a DB read failure),
    /// in which case the caller falls back to AAC transcode.
    /// </summary>
    /// <param name="item">The Jellyfin audio item.</param>
    /// <returns>Lowercase audio codec string (e.g. "mp3", "aac", "flac"), or null.</returns>
    internal string? ResolveSourceAudioCodec(MediaBrowser.Controller.Entities.BaseItem item)
        => ResolveSourceCodecs(item).Audio;

    /// <summary>
    /// Shared body of the media-stream readers: fetch the item's media streams as a
    /// materialized list, or null when the media source manager is unavailable or the
    /// read fails (each caller degrades to its own fallback). Materialized so a
    /// deferred-enumeration failure surfaces inside the same try.
    /// </summary>
    /// <param name="item">The item whose streams to read.</param>
    /// <returns>The item's media streams, or null when unavailable.</returns>
    private List<MediaStream>? TryGetMediaStreams(MediaBrowser.Controller.Entities.BaseItem item)
    {
        if (_mediaSourceManager == null)
        {
            return null;
        }

        try
        {
            return _mediaSourceManager.GetMediaStreams(item.Id).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VideoAudio: could not read media streams for item {ItemId}", item.Id);
            return null;
        }
    }

    /// <summary>
    /// Combined source-codec probe (JF-525): the episode HLS tier decision needs
    /// BOTH the video and the audio codec, and resolving them through the
    /// individual resolvers cost one
    /// <see cref="IMediaSourceManager.GetMediaStreams(Guid)"/> read each. This resolves
    /// both sides from ONE stream read, each side keeping its individual
    /// resolver's semantics: the video side skips blank codecs and
    /// attached-picture covers (<see cref="EpisodeCoverVideoCodecs"/>), the audio
    /// side takes the first audio stream with a non-blank codec. Fail-open shapes
    /// are unchanged: null media source manager or a read failure returns
    /// (null, null), and a missing stream of either type leaves that side null.
    /// </summary>
    /// <param name="item">The item whose streams to read.</param>
    /// <returns>Lowercase (video codec, audio codec), either side null when unknown.</returns>
    internal (string? Video, string? Audio) ResolveSourceCodecs(MediaBrowser.Controller.Entities.BaseItem item)
    {
        var streams = TryGetMediaStreams(item);
        if (streams == null)
        {
            return (null, null);
        }

        string? videoCodec = null;
        string? audioCodec = null;
        foreach (MediaStream stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream.Codec))
            {
                continue;
            }

            if (stream.Type == MediaStreamType.Video && videoCodec == null && !EpisodeCoverVideoCodecs.Contains(stream.Codec))
            {
                videoCodec = stream.Codec.ToLowerInvariant();
            }
            else if (stream.Type == MediaStreamType.Audio && audioCodec == null)
            {
                audioCodec = stream.Codec.ToLowerInvariant();
            }

            if (videoCodec != null && audioCodec != null)
            {
                break;
            }
        }

        return (videoCodec, audioCodec);
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
    /// Build ffmpeg argument list for the EPISODE HLS REMUX (JF-498): the item's own
    /// video stream COPIED at full framerate (the sources routed here are H.264:
    /// remux, not transcode) plus the audio transcoded to AAC when the source audio
    /// has no Echo decoder (or copied when it is mp3/aac). NOT the 1fps
    /// black-frame trick of the audiobook path: the real video is the payload.
    /// Segments are 4 seconds; the playlist is served while ffmpeg is still writing
    /// (stream-while-writing, same as the song path).
    /// </summary>
    /// <param name="videoUrl">Static stream URL of the source item (ffmpeg input).</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    /// <param name="sourceAudioCodec">Source audio codec (e.g. "eac3") for the copy
    /// decision, or null/unknown to transcode to AAC.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildEpisodeHlsFfmpegArguments(
        string videoUrl,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl,
        string? sourceAudioCodec = null)
        => BuildEpisodeHlsArgumentsCore(videoUrl, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec, EpisodeVideoCopyArgs);

    /// <summary>
    /// Video codec arguments for the episode remux: stream copy plus the GOP hint.
    /// <c>-g 48</c> is INERT under <c>-c:v copy</c> (it is an encoder option): with
    /// copy, the HLS muxer cuts segments at the SOURCE's own keyframes, so segment
    /// boundaries land on the source GOP (typically 4-10s for scene-cut-encoded
    /// H.264 at <c>hls_time 4</c>; Jellyfin's MediaStream model exposes no keyframe
    /// interval to read, so the source cadence cannot be queried). It documents the
    /// cadence this path ASSUMES (at least one keyframe per ~2s window: 48 frames
    /// at the 24-30fps TV rates) and becomes live the day this path grows a video
    /// transcode. First-segment appearance stays far under the ~4s budget because a
    /// copy remux runs far ahead of realtime: measured on the exact argument set
    /// below (ffmpeg 8.1.2, 60s h264+eac3 MKV source), the first 4s segment was on
    /// disk in 0.31s and the whole 60s remux completed in 3.95s.
    /// </summary>
    private static readonly string[] EpisodeVideoCopyArgs = ["-c:v", "copy", "-g", "48"];

    /// <summary>
    /// AAC encode arguments for the episode remux audio. STEREO DOWNMIX (-ac 2):
    /// the Echo's ExoPlayer decodes stereo AAC only in skill video; a 5.1 EAC3
    /// source kept at six channels produced a 5.1 AAC track that black-screened
    /// on-device (2026-09-06 device test: the player opened, killed the TTS
    /// mid-sentence, and rendered nothing; ffprobe of the segment showed AAC-LC
    /// channels=6). 192k (vs the 128k music path) because it carries a 5.1
    /// mixdown.
    /// </summary>
    private static readonly string[] EpisodeAudioCodecArgs = ["-c:a", "aac", "-ac", "2", "-b:a", "192k"];

    /// <summary>
    /// Build the ffmpeg audio codec arguments for the episode remux, in the
    /// <see cref="BuildAudioCodecArgs"/> style: copy for the muxer-compatible
    /// codecs (mp3, aac), AAC re-encode for everything else (the EAC3/AC3/TrueHD/DTS
    /// sources this endpoint exists for) or an unknown codec.
    /// </summary>
    /// <param name="sourceAudioCodec">Lowercase source audio codec (e.g. "eac3"), or null/empty if unknown.</param>
    /// <returns>ffmpeg audio codec argument tokens.</returns>
    internal static string[] BuildEpisodeAudioCodecArgs(string? sourceAudioCodec)
        => !string.IsNullOrWhiteSpace(sourceAudioCodec) && CopyCompatibleAudioCodecs.Contains(sourceAudioCodec)
            ? AudioCopyArgs
            : EpisodeAudioCodecArgs;

    /// <summary>
    /// Shared body of the two episode HLS builders (the JF-498 remux and the
    /// JF-500 transcode tier): one static input, the explicit first-video +
    /// first-audio mapping, the per-tier VIDEO arguments, the shared audio
    /// selection (<see cref="BuildEpisodeAudioCodecArgs"/>), and the identical
    /// HLS/segment/playlist tails (4s MPEG-TS, append_list event growth, no
    /// -shortest). Each tier passes only its own video tokens; the transcode's
    /// conditional scale rides inside its video token list.
    /// </summary>
    /// <param name="videoUrl">Static stream URL of the source item (ffmpeg input).</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    /// <param name="sourceAudioCodec">Source audio codec for the copy decision, or
    /// null/unknown to transcode to AAC.</param>
    /// <param name="videoArgs">The tier's video codec arguments (copy set or the
    /// measured H.264 re-encode set, plus the transcode's scale filter when it fires).</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    private static List<string> BuildEpisodeHlsArgumentsCore(
        string videoUrl,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl,
        string? sourceAudioCodec,
        IReadOnlyList<string> videoArgs)
    {
        var args = new List<string>();

        // Input 0: the item's own static stream (raw container; ffmpeg's demuxers
        // handle mkv/mp4/webm regardless of what ExoPlayer supports).
        args.Add("-i");
        args.Add(videoUrl);

        // Explicit mapping: FIRST video + FIRST audio stream only. Drops subtitle
        // streams (PGS/SRT inside MKV cannot be muxed into MPEG-TS and would fail
        // the encode) and any attached-picture/attachment streams. Capital V
        // (JF-500 review R2): ffmpeg's 'V' specifier matches video streams
        // EXCLUDING attached pictures/thumbnails/cover art ('v' matches all video
        // streams, verified on ffmpeg 8.1.2), so an embedded cover cannot steal
        // the video slot from the real track; for normal files V:0 selects exactly
        // what v:0 did.
        args.Add("-map");
        args.Add("0:V:0");
        args.Add("-map");
        args.Add("0:a:0");

        // Video: the per-tier arguments (stream copy for the remux, the measured
        // H.264 re-encode set for the transcode, plus its conditional scale).
        args.AddRange(videoArgs);

        // Audio: copy when the source is mp3/aac, else transcode to AAC.
        args.AddRange(BuildEpisodeAudioCodecArgs(sourceAudioCodec));

        // HLS flags. 4-second segments (the JF-292 family value for full-framerate
        // content; 10s is the audiobook value and the ceiling ExoPlayer wants).
        // append_list keeps already-written segments listed while the playlist grows.
        args.Add("-hls_time");
        args.Add(EpisodeHlsSegmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        args.Add("-hls_list_size");
        args.Add("0");
        args.Add("-hls_flags");
        args.Add("append_list");
        args.Add("-hls_segment_type");
        args.Add("mpegts");

        // Segment file name template
        args.Add("-hls_segment_filename");
        args.Add(segmentPath);

        // Base URL for segment references in the playlist
        args.Add("-hls_base_url");
        args.Add(hlsBaseUrl);

        // No -shortest: both output streams come from ONE finite input (the
        // audiobook path needs -shortest only because its art input loops forever).
        args.Add(playlistPath);

        return args;
    }

    /// <summary>
    /// Build ffmpeg argument list for the EPISODE HLS VIDEO TRANSCODE tier (JF-500):
    /// the item's video re-encoded to H.264 (the sources routed here are
    /// hevc/av1/...: the Echo decodes H.264 only, and a remux would copy the
    /// undecodable bytes), with audio EXACTLY as the remux pipeline
    /// (<see cref="BuildEpisodeAudioCodecArgs"/>: AAC 192k stereo for the
    /// Echo-undecodable family, copy for mp3/aac).
    /// Measured parameter set (2026-09-08, minix, Adolescence E1 1080p HEVC):
    /// ultrafast CRF 23 sustains 4.40x realtime (veryfast measured 2.12x and was
    /// rejected on margin), so playback never catches the encode head and the
    /// first 4s segment lands in ~1s, far inside the first-segment wait.
    /// <c>-g 48</c> is LIVE here (an encoder option, unlike its inert copy-path
    /// twin): a keyframe at least every 48 frames (~2s at the 24-30fps TV rates)
    /// so the HLS muxer can cut 4-second segments at GOP boundaries. The scale
    /// clamp is UNCONDITIONAL (<c>scale=-2:min(ih\,1080)</c> in
    /// <see cref="EpisodeVideoTranscodeArgs"/>: a no-op at &lt;=1080p) so a
    /// taller-than-ceiling source is always clamped: the height probe that used
    /// to gate the filter could fail open on an unprobed 4K source, whose decode
    /// cost would break the realtime margin and hit the monitor kill.
    /// Segment/playlist/cache machinery is the remux's, unchanged (same 4s
    /// hls_time, append_list event growth, MPEG-TS segments, token rewriting).
    /// </summary>
    /// <param name="videoUrl">Static stream URL of the source item (ffmpeg input).</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    /// <param name="sourceAudioCodec">Source audio codec for the copy decision, or
    /// null/unknown to transcode to AAC.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildEpisodeHlsTranscodeFfmpegArguments(
        string videoUrl,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl,
        string? sourceAudioCodec = null)
        => BuildEpisodeHlsArgumentsCore(videoUrl, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec, EpisodeVideoTranscodeArgs);

    /// <summary>
    /// Video codec arguments for the episode transcode tier (JF-500): the measured
    /// set (libx264 ultrafast CRF 23, GOP 48) that sustains 4.40x realtime on the
    /// minix for a 1080p HEVC source. Not to be re-tuned without a new on-box
    /// measurement (veryfast was measured at 2.12x and rejected on margin).
    /// <c>-pix_fmt yuv420p</c> is a correctness guard, not a tuning knob: a no-op
    /// for 8-bit sources, it converts a 10-bit HEVC source to a profile the Echo
    /// decodes (without it libx264 keeps the 10-bit depth and emits High-10
    /// H.264; ffmpeg 8.1.2 probe). The file's other two libx264 sets
    /// (<see cref="PixelFormatArgs"/> and the audiobook path) carry it too.
    /// The trailing <c>-vf scale=-2:min(ih\,1080)</c> (JF-500 review R3) is the
    /// UNCONDITIONAL height clamp: a no-op for &lt;=1080p sources, it scales taller
    /// ones down to <see cref="MaxEpisodeTranscodeHeight"/> without needing a height
    /// probe. The comma inside <c>min()</c> is BACKSLASH-ESCAPED, not quoted:
    /// there is no shell between <c>ArgumentList</c> and ffmpeg, so quote
    /// characters would pass through as literals and the filtergraph parser
    /// rejects them ("Error initializing filters", live 2026-09-08); the escaped
    /// comma is ffmpeg's own graph-level escaping. Verified on the container's
    /// ffmpeg via the exact ArgumentList token: 12s HEVC encode, exit 0, no
    /// filter errors.
    /// </summary>
    private static readonly string[] EpisodeVideoTranscodeArgs =
        ["-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-g", "48", "-pix_fmt", "yuv420p", "-vf", $"scale=-2:min(ih\\,{MaxEpisodeTranscodeHeight})"];

    /// <summary>
    /// Height ceiling of the episode transcode tier (JF-500): the transcode args
    /// clamp every source to this height (<c>-vf scale=-2:min(ih\,1080)</c>, a
    /// no-op at &lt;=1080p) because a 4K HEVC decode would not sustain the measured
    /// realtime margin. 1080 matches the Echo Show's screen, so no visible quality
    /// is lost on the target device.
    /// </summary>
    internal const int MaxEpisodeTranscodeHeight = 1080;

    /// <summary>
    /// Shared body of the flat hourly encode-size estimates: reserve
    /// <paramref name="bytesPerHour"/> per rounded-UP hour of content, floored at
    /// one hour's worth.
    /// </summary>
    /// <param name="runtimeTicks">Content duration (item runtime).</param>
    /// <param name="bytesPerHour">Conservative bytes-per-hour rate.</param>
    /// <returns>Estimated bytes the encode writes.</returns>
    internal static long FlatHourlyEncodeBytes(long runtimeTicks, long bytesPerHour)
    {
        long hours = (runtimeTicks + TimeSpan.TicksPerHour - 1) / TimeSpan.TicksPerHour;
        return Math.Max(bytesPerHour, hours * bytesPerHour);
    }

    /// <summary>
    /// JF-500: conservative on-disk size estimate for the episode video transcode.
    /// The tier re-encodes at a fixed CRF, so the output size is driven by content
    /// complexity, NOT by the source bitrate: the remux's bitrate-aware scaling
    /// (<see cref="EstimateEpisodeEncodeBytes"/>) does not apply. H.264 CRF 23 at
    /// 1080p lands ~2-3GB/h, so the flat rate is 3GB/h with the same
    /// round-UP-to-hours/floor shape as <see cref="EstimateEncodeBytes"/>. The
    /// reserve drives the JF-428 pre-encode headroom: the eviction sweep must empty
    /// enough of the cache before a multi-GB transcode starts (the playback pin
    /// still protects the entry being watched). Default-cap interaction: the JF-534
    /// live measurement confirmed the rate (~3.2GB/h for a 51-min HEVC episode;
    /// why the default changed: the VideoAudioCacheSizeMB field doc). Content
    /// beyond ~80min still outgrows even the raised cap and relies on the
    /// playback-recency window plus oldest-first eviction.
    /// </summary>
    /// <param name="runtimeTicks">Content duration (item runtime).</param>
    /// <returns>Estimated bytes the encode writes.</returns>
    internal static long EstimateEpisodeTranscodeEncodeBytes(long runtimeTicks)
        => FlatHourlyEncodeBytes(runtimeTicks, 3072L * 1024 * 1024);

    /// <summary>
    /// Wall-clock minutes the HLS background monitor waits before killing ffmpeg
    /// (JF-500 review F1). The remux and audio tiers are I/O-bound and finish
    /// audiobook-length content in minutes (~21x realtime), so they keep the
    /// historical 30-minute ceiling unchanged. The video TRANSCODE tier runs at a
    /// MEASURED 4.40x realtime (minix, 2026-09-08): 30 wall minutes only covers
    /// ~132 minutes of content, so any longer HEVC movie was hard-killed
    /// mid-encode, its playlist left without ENDLIST (cache invalidation), and
    /// every retry churned a fresh multi-GB re-encode. Its timeout therefore
    /// scales from the item runtime: half the measured speed (2.0x, the
    /// conservative floor for concurrent-load slowdown) plus 10 minutes of
    /// ffmpeg startup/slack, floored at the 30-minute default for short content.
    /// A transcode tier with an UNKNOWN runtime (missing metadata) gets 120
    /// minutes (JF-500 review R4), not the 30-minute default: 30 would still
    /// hard-kill any movie longer than ~132 minutes, while 120 bounds how long a
    /// hung encode may hold its transcode slot before the monitor reclaims it.
    /// </summary>
    /// <param name="videoTranscodeTier">Whether the monitored encode is the
    /// episode video-transcode tier (HEVC re-encode).</param>
    /// <param name="runTimeTicks">The item's runtime, or null/&lt;=0 when unknown.</param>
    /// <returns>Monitor timeout in minutes.</returns>
    internal static int HlsMonitorTimeoutMinutes(bool videoTranscodeTier, long? runTimeTicks)
    {
        if (!videoTranscodeTier)
        {
            return 30;
        }

        if (runTimeTicks is not > 0)
        {
            return 120;
        }

        double runtimeMinutes = TimeSpan.FromTicks(runTimeTicks.Value).TotalMinutes;
        return Math.Max(30, (int)Math.Ceiling(runtimeMinutes / 2.0) + 10);
    }

    /// <summary>
    /// Container overhead margin applied on top of the source bitrate when estimating
    /// an episode remux's on-disk size (MPEG-TS per-segment headers, AAC encoder
    /// priming, playlist growth). 10% over the raw stream bytes.
    /// </summary>
    private const double EpisodeContainerOverheadMargin = 1.1;

    /// <summary>
    /// JF-498: conservative on-disk size estimate for the episode remux. When the
    /// item's combined video+audio stream <see cref="MediaStream.BitRate"/> is known
    /// (JF-498 review C1b), the estimate scales from it: the remux COPIES the source
    /// video bytes, so a 10Mbps Blu-ray remux writes ~4.5GB/h, not the flat
    /// bitrate-blind value (bytes/h = bits/s / 8 * 3600, +10% container overhead).
    /// Without a bitrate (manager unavailable, streams lack the field, DB read
    /// failure) the flat 1280MB/h applies: a typical ~2.5Mbps H.264 TV episode +
    /// 192k AAC + MPEG-TS overhead is ~1.2GB per hour, so a 45min episode reserves
    /// one hour's worth. The floor is one hour's worth of the flat rate either way;
    /// the flat path keeps the round-UP-to-hours shape of
    /// <see cref="EstimateEncodeBytes"/>. Cache-budget consequence (JF-310/421/428):
    /// the reserve drives the JF-428 pre-encode headroom, so a high-bitrate source
    /// evicts more before its encode starts.
    /// </summary>
    /// <param name="runtimeTicks">Content duration (item runtime).</param>
    /// <param name="totalBitRateBps">Combined video+audio source bitrate in bits per
    /// second (from <see cref="ResolveTotalMediaBitrateBps"/>), or null/0 when unknown.</param>
    internal static long EstimateEpisodeEncodeBytes(long runtimeTicks, long? totalBitRateBps = null)
    {
        const long flatBytesPerHour = 1280L * 1024 * 1024;

        if (totalBitRateBps is > 0)
        {
            double seconds = runtimeTicks / (double)TimeSpan.TicksPerSecond;
            long bitrateBytes = (long)Math.Round(totalBitRateBps.Value / 8.0 * seconds * EpisodeContainerOverheadMargin);
            return Math.Max(flatBytesPerHour, bitrateBytes);
        }

        long hours = (runtimeTicks + TimeSpan.TicksPerHour - 1) / TimeSpan.TicksPerHour;
        return Math.Max(flatBytesPerHour, hours * flatBytesPerHour);
    }

    /// <summary>
    /// Build ffmpeg argument list for the AUDIO-ONLY episode HLS variant (JF-507): the
    /// item's first audio stream ALONE mapped into MPEG-TS (-map 0:a:0, NO video track),
    /// for AudioPlayer launches of video items whose audio codec has no Echo decoder.
    /// Optional <paramref name="startTicks"/> becomes an input seek (-ss BEFORE -i:
    /// container-level, so the encode starts at the resume position instead of decoding
    /// through everything before it) and shifts the served timeline to start there.
    /// Audio codec selection mirrors <see cref="BuildEpisodeAudioCodecArgs"/>: copy for
    /// mp3/aac, AAC 192k stereo for everything else (the EAC3 family this endpoint
    /// exists for). 10-second segments, event-style growth (append_list), no -shortest.
    /// </summary>
    /// <param name="videoUrl">Static stream URL of the source item (ffmpeg input; ffmpeg
    /// maps its audio stream, so the video bytes are never decoded or emitted).</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 to encode from the start).</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    /// <param name="sourceAudioCodec">Source audio codec (e.g. "eac3") for the copy
    /// decision, or null/unknown to transcode to AAC.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildEpisodeAudioHlsFfmpegArguments(
        string videoUrl,
        long startTicks,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl,
        string? sourceAudioCodec = null)
    {
        var args = new List<string>();

        // Input seek BEFORE -i: starts reading at the resume position (the player
        // receives offset 0 with this stream, so the directive offset cannot do it and
        // a from-zero encode cannot serve a deep offset's segment on the first fetch).
        if (startTicks > 0)
        {
            args.Add("-ss");
            args.Add(FormatSecondsInvariant(startTicks));
        }

        args.Add("-i");
        args.Add(videoUrl);

        // Audio ONLY: no video mapping (AudioPlayer plays audio streams; the remux's
        // 0:V:0 copy would waste the encode budget and is what the OTHER endpoint is for).
        args.Add("-map");
        args.Add("0:a:0");

        // Audio: copy when the source is mp3/aac, else AAC 192k stereo (same selection
        // as the remux; the stereo downmix matters for the 5.1 EAC3 family).
        args.AddRange(BuildEpisodeAudioCodecArgs(sourceAudioCodec));

        // 10-second segments: the audio-rate value (a 45min episode is ~270 segments;
        // %04d caps at 9999 = ~27h). append_list keeps written segments listed while
        // the playlist grows (the event-playlist shape the Echo family tolerates).
        args.Add("-hls_time");
        args.Add("10");
        args.Add("-hls_list_size");
        args.Add("0");
        args.Add("-hls_flags");
        args.Add("append_list");
        args.Add("-hls_segment_type");
        args.Add("mpegts");

        args.Add("-hls_segment_filename");
        args.Add(segmentPath);

        args.Add("-hls_base_url");
        args.Add(hlsBaseUrl);

        // No -shortest: one finite input, one mapped stream.
        args.Add(playlistPath);

        return args;
    }

    /// <summary>
    /// Format a .NET ticks duration as an ffmpeg seconds argument, invariant-culture,
    /// millisecond precision (e.g. "83.25"). Shared by the episode audio variant's
    /// -ss seek.
    /// </summary>
    /// <param name="ticks">Duration in .NET ticks.</param>
    /// <returns>The seconds string.</returns>
    internal static string FormatSecondsInvariant(long ticks)
        => (ticks / (double)TimeSpan.TicksPerSecond).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// JF-507 on-disk size estimate for the audio-only episode encode: measured 60MB for
    /// the 39-minute incident episode (AAC 192k stereo + MPEG-TS overhead ≈ 92MB/h), so
    /// the flat rate is 96MB/h with the same round-UP-to-hours/floor shape as
    /// <see cref="EstimateEncodeBytes"/>. A resume encode (-ss) covers less content but
    /// reserves the full runtime: conservative is the right direction for the JF-428
    /// pre-encode headroom.
    /// </summary>
    /// <param name="runtimeTicks">Content duration (item runtime).</param>
    /// <returns>Estimated bytes the encode writes.</returns>
    internal static long EstimateEpisodeAudioEncodeBytes(long runtimeTicks)
        => FlatHourlyEncodeBytes(runtimeTicks, 96L * 1024 * 1024);

    /// <summary>
    /// Resolve the combined video+audio source bitrate (bits per second) of an item
    /// for <see cref="EstimateEpisodeEncodeBytes"/> (JF-498 review C1b): the FIRST
    /// video stream's <see cref="MediaStream.BitRate"/> plus the FIRST audio stream's.
    /// Summing the source audio bitrate slightly over-reserves when it is transcoded
    /// down to AAC 192k; conservative is the right direction for a headroom reserve.
    /// Returns null when the media source manager is unavailable, the read fails, or
    /// neither stream carries a BitRate, so the caller falls back to the flat estimate.
    /// </summary>
    /// <param name="item">The Jellyfin video item.</param>
    /// <returns>Total bitrate in bits per second, or null when unavailable.</returns>
    internal long? ResolveTotalMediaBitrateBps(MediaBrowser.Controller.Entities.BaseItem item)
    {
        var streams = TryGetMediaStreams(item);
        if (streams == null)
        {
            return null;
        }

        int? video = null;
        int? audio = null;
        foreach (var stream in streams)
        {
            if (stream.Type == MediaStreamType.Video)
            {
                video ??= stream.BitRate;
            }
            else if (stream.Type == MediaStreamType.Audio)
            {
                audio ??= stream.BitRate;
            }
        }

        // MediaStream.BitRate is int?; the sum widens to long? for the caller's
        // byte arithmetic. Null (no stream carried a BitRate) resolves to the
        // caller's flat-estimate fallback.
        long? total = (video ?? 0) + (audio ?? 0);
        return total > 0 ? total : null;
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
    /// Pre-write a complete HLS playlist for an episode encode (JF-531): the episode
    /// twin of <see cref="WriteAudiobookPlaylist"/>, same mechanism (a SEPARATE file
    /// from ffmpeg's own stream.m3u8, no #EXT-X-ENDLIST so the player treats it as an
    /// event playlist and plays available segments without failing on missing ones;
    /// WHY the episode path needs it: the prewrite site in
    /// <see cref="StreamHlsEpisodeCore"/>). Lists every segment the encode will
    /// produce, with uniform EXTINF durations summing to the item runtime. Coupled to
    /// <see cref="EpisodeHlsSegmentSeconds"/> (the ffmpeg <c>-hls_time</c>).
    /// Differences from the audiobook writer, both forced by the content shape:
    /// 4-second segments (not the audiobook 10s) and NO per-segment
    /// #EXT-X-DISCONTINUITY (a single continuous encode, not chapter-file
    /// boundaries). ffmpeg keeps appending to its own stream.m3u8; once it completes
    /// (ENDLIST), the cache-hit paths serve that instead. The per-segment durations
    /// are an approximation of where ffmpeg actually cuts (the remux tier cuts at
    /// source keyframes), but only their SUM is load-bearing for the seekbar, and the
    /// post-completion playlist carries the real durations.
    /// </summary>
    /// <param name="playlistPath">File path for the .m3u8 playlist.</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references.</param>
    /// <param name="runtimeTicks">The item's runtime, the duration the listing spans.</param>
    /// <param name="token">Stream token to embed in segment URLs (JF-309), or null.</param>
    internal static void WriteEpisodePlaylist(
        string playlistPath,
        string hlsBaseUrl,
        long runtimeTicks,
        string? token)
    {
        string segmentSuffix = string.IsNullOrEmpty(token) ? string.Empty : $"?token={token}";
        using var writer = new StreamWriter(playlistPath);
        writer.WriteLine("#EXTM3U");
        writer.WriteLine("#EXT-X-VERSION:3");
        writer.WriteLine($"#EXT-X-TARGETDURATION:{EpisodeHlsSegmentSeconds}");
        writer.WriteLine("#EXT-X-MEDIA-SEQUENCE:0");

        // Same split as the audiobook writer's per-chapter loop, over one "chapter"
        // (the whole runtime): ceil(runtime/hls_time) segments, each carrying an
        // equal share so the durations sum exactly to the runtime (the seekbar
        // total). Ceil is the safety margin: the listing never spans less than the
        // runtime, and any phantom tail beyond the actual segment count is only
        // reachable before the post-completion ENDLIST playlist supersedes this file.
        double durationSeconds = runtimeTicks / 10000000.0;
        int segmentCount = Math.Max(1, (int)Math.Ceiling(durationSeconds / EpisodeHlsSegmentSeconds));
        double segmentDuration = durationSeconds / segmentCount;

        for (int i = 0; i < segmentCount; i++)
        {
            // Invariant culture: a comma-decimal host culture would render "3,999346,"
            // and HLS parsers would read the duration as 3 (review nit on the new
            // writer; the audiobook twin carries the same latent pattern, JF-536).
            writer.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{HlsExtInf}{segmentDuration:F6},"));
            writer.WriteLine($"{hlsBaseUrl}seg_{i:D4}.ts{segmentSuffix}");
        }

        // No #EXT-X-ENDLIST: event playlist so the player plays available segments
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
    /// <summary>
    /// Global ffmpeg encode gate (JF-310, DoS bound): bounds the number of CONCURRENT
    /// ffmpeg processes across all video-audio endpoints. Without it, an anonymous
    /// caller who knows several item GUIDs can start unbounded parallel encodes and
    /// saturate CPU/disk before post-hoc cache eviction reacts. Callers exceeding the
    /// cap (MaxConcurrentFfmpegEncodes, default 2) WAIT here; the slot is released when
    /// the process exits.
    /// </summary>
    private static SemaphoreSlim _encodeGate = new(2, 2);

    /// <summary>
    /// The CONFIGURED capacity the current gate was built with (JF-421). Capacity
    /// changes are detected against THIS, never against <see cref="SemaphoreSlim.CurrentCount"/>
    /// (free slots): the old check compared free slots, so the per-request config sync
    /// rebuilt a fresh full semaphore whenever ANY encode was in flight, and the JF-310
    /// concurrency bound never bound.
    /// </summary>
    private static int _encodeGateCapacity = 2;

    /// <summary>
    /// Guards the capacity check-then-rebuild in <see cref="UpdateEncodeGateCapacity"/>
    /// (review round: an unsynchronized interleaving across a config save could leave
    /// the capacity field disagreeing with the live semaphore, and the early-return
    /// then froze the wrong bound for the process lifetime).
    /// </summary>
    private static readonly object _gateSwapLock = new();

    /// <summary>
    /// Serializes the episode video-TRANSCODE tier to ONE encode at a time (JF-500
    /// review F2). A full HEVC re-encode holds its shared <see cref="_encodeGate"/>
    /// slot for 15-30 minutes (measured 4.40x realtime): with the default gate
    /// capacity of 2, two concurrent transcodes (a household starting one on the
    /// Show while another begins on the Dot-fed Fire TV) would occupy BOTH slots
    /// and leave music/audiobook requests waiting on the gate, with no
    /// cancellation, for the whole encode window. The slot is acquired BEFORE the
    /// shared gate and released AFTER it, so a queued second transcode holds NO
    /// gate slot and the shorter paths always find one. The remux tier and every
    /// other gated path bypass it.
    /// </summary>
    private static readonly SemaphoreSlim _episodeTranscodeSlot = new(1, 1);

    /// <summary>
    /// Read-only probe of <see cref="_episodeTranscodeSlot"/> (internal test hook,
    /// the InternalsVisibleTo seam): true when no episode transcode holds the slot.
    /// Try-acquire based, so it reads correctly regardless of waiter queue depth.
    /// </summary>
    internal static bool EpisodeTranscodeSlotFree
    {
        get
        {
            if (!_episodeTranscodeSlot.Wait(0))
            {
                return false;
            }

            _episodeTranscodeSlot.Release();
            return true;
        }
    }

    /// <summary>
    /// Rebuilds the encode gate when the CONFIGURED cap changes (called from the
    /// per-request config sync; a no-op unless the config value differs from
    /// <see cref="_encodeGateCapacity"/>). Drain-safe: in-flight holders and waiting
    /// callers keep the old semaphore, new callers get the new cap.
    /// </summary>
    internal static void UpdateEncodeGateCapacity(int maxConcurrent)
    {
        int bounded = Math.Max(1, maxConcurrent);
        lock (_gateSwapLock)
        {
            if (bounded == _encodeGateCapacity)
            {
                return;
            }

            _encodeGateCapacity = bounded;
            _encodeGate = new SemaphoreSlim(bounded, bounded);
        }
    }

    /// <summary>
    /// Starts ffmpeg under the encode gate: the slot is held for the lifetime of the
    /// spawned process (all HLS/song encode paths; the lightweight faststart remux
    /// calls StartFfmpegProcess directly instead). An optional
    /// <paramref name="serializeSlot"/> (the episode transcode tier's
    /// <see cref="_episodeTranscodeSlot"/>, JF-500 review F2) is acquired BEFORE the
    /// gate and released AFTER it, in the same structures, so its ordering against
    /// the gate is fixed and inverse on release.
    /// </summary>
    private async Task<Process> StartFfmpegProcessGatedAsync(
        string ffmpegPath,
        List<string> arguments,
        long estimatedEncodeBytes,
        string pinPath,
        SemaphoreSlim? serializeSlot = null,
        CancellationToken cancellationToken = default)
    {
        // JF-500 review F2: acquire the serialize slot (when requested) FIRST, before
        // the pin, the budget sweep, and the shared gate. Before the gate so a queued
        // episode transcode holds NO gate slot while it waits and the other gated
        // paths always find one; before the sweep so a queued encode's multi-GB
        // headroom reservation (JF-428) is made immediately before ITS encode starts,
        // not up to one full encode-length earlier.
        if (serializeSlot != null)
        {
            await serializeSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // JF-310/JF-428 (+review round 2): PIN the entry being written BEFORE the
        // eviction sweep runs, not after the gate is acquired: the creation-to-pin
        // window let a concurrent request's sweep delete another request's
        // already-created but not-yet-pinned HLS directory. Every failure path from
        // here to process start (sweep throw, cancelled gate wait, process start
        // failure) unpins in its own catch, so no pin leaks.
        _cache.Pin(pinPath);
        try
        {
            await _cache.EnsureDiskBudgetBeforeEncodeAsync(estimatedEncodeBytes).ConfigureAwait(false);
        }
        catch
        {
            // The sweep threw before any of the downstream Unpin sites could run;
            // without this the leaked pin would keep the half-created entry
            // undeletable for the process lifetime.
            serializeSlot?.Release();
            _cache.Unpin(pinPath);
            throw;
        }

        // Capture the gate instance at acquire time so the release always goes to
        // the SAME semaphore, even if UpdateEncodeGateCapacity swaps the field while
        // this encode is in flight (review 2026-08-29: releasing the new field after
        // a swap would grant a phantom slot and strand waiters on the dead semaphore).
        var gate = _encodeGate;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            serializeSlot?.Release();
            _cache.Unpin(pinPath);
            throw;
        }

        Process process;
        try
        {
            process = StartFfmpegProcess(ffmpegPath, arguments);
        }
        catch
        {
            gate.Release();
            serializeSlot?.Release();
            _cache.Unpin(pinPath);
            throw;
        }

        // Release the gate slot when the process exits or is disposed. Polling
        // HasExited instead of WaitForExitAsync: the .NET runtime makes
        // WaitForExitAsync on a disposed-but-still-running process NEVER complete
        // (probed on net9.0, review 2026-08-29), which would permanently consume
        // the slot. HasExited returns true for both exited and already-disposed
        // processes. The 500ms poll interval is negligible vs. encode times.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process disposed (ObjectDisposedException derives from
                    // InvalidOperationException); release below regardless.
                }
                finally
                {
                    try { gate.Release(); }
                    catch (SemaphoreFullException) { /* defensive: double-release */ }

                    // Inverse order of acquisition (JF-500 review F2): the serialize
                    // slot frees only after the gate slot does.
                    if (serializeSlot != null)
                    {
                        try { serializeSlot.Release(); }
                        catch (SemaphoreFullException) { /* defensive: double-release */ }
                    }

                    // Encode finished: the entry becomes an ordinary LRU citizen.
                    _cache.Unpin(pinPath);
                }
            },
            CancellationToken.None);
        return process;
    }

    /// <summary>
    /// JF-428: conservative on-disk size estimate for a video-audio encode, scaled by
    /// content duration. Measured: an 8.3h audiobook HLS is ~472MB (~57MB/h: audio
    /// copy at ~128kbps plus 1fps CRF51 black-frame video), so 64MB/h leaves margin.
    /// Fractional hours round UP (ceiling): a 1.9h book predicts ~108MB and must
    /// reserve 128MB, not fall back to the 1h floor. The floor is one hour's worth:
    /// the previous flat 64MB reservation was implicitly the 1h case and under-reserved
    /// every longer encode.
    /// </summary>
    /// <param name="runtimeTicks">Total content duration (item runtime or chapters sum).</param>
    internal static long EstimateEncodeBytes(long runtimeTicks)
        => FlatHourlyEncodeBytes(runtimeTicks, 64L * 1024 * 1024);

    /// <summary>
    /// JF-519: the guarded first-segment-wait exit-code read. <see cref="Process.ExitCode"/>
    /// throws unless the process has exited, and those wait loops can give up while ffmpeg
    /// is still starting, so a live process reports -1 by contract. An exited process
    /// reports its real code; a never-started or disposed one still throws (the wait
    /// sites read this before their Dispose).
    /// </summary>
    /// <param name="process">The ffmpeg process being waited on.</param>
    internal static int SafeExitCode(Process process)
        => process.HasExited ? process.ExitCode : -1;

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

        // Drain stderr asynchronously to prevent deadlock. JF-515: the old
        // per-line Debug logging flooded the synchronous console sink (per-segment
        // "Opening ... for writing" churn at ~16 lines/s per encode) and correlated
        // with multi-second skill-request latency spikes. Errors are still logged
        // immediately (Warning); routine lines aggregate into one Debug summary per
        // 30s of draining plus a final total when the stream ends.
        _ = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            var summaryCadence = Stopwatch.StartNew();
            long suppressedRoutine = 0;
            long errorLines = 0;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (IsFfmpegErrorLine(line))
                {
                    errorLines++;
                    _logger.LogWarning("ffmpeg stderr: {Line}", line);
                    continue;
                }

                suppressedRoutine++;
                if (summaryCadence.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    _logger.LogDebug(
                        "ffmpeg stderr: {Suppressed} routine lines suppressed so far",
                        suppressedRoutine);
                    summaryCadence.Restart();
                }
            }

            _logger.LogDebug(
                "ffmpeg stderr: drained {Total} lines ({Suppressed} routine suppressed, {Errors} errors logged)",
                suppressedRoutine + errorLines,
                suppressedRoutine,
                errorLines);
        });

        return process;
    }

    /// <summary>
    /// JF-515: classifies an ffmpeg stderr line as a real failure (true) or routine
    /// progress noise (false). A leading ffmpeg component prefix ("[hls @ 0x7f...]")
    /// is skipped before the StartsWith check so prefixed failures
    /// ("[hls @ 0x7f] Error opening ...") classify like bare ones. Span-based: the
    /// drain calls this per line (~16 lines/s per encode), so it must not allocate.
    /// </summary>
    /// <param name="line">One stderr line from ffmpeg.</param>
    /// <returns>True when the line indicates a failure; false when routine.</returns>
    internal static bool IsFfmpegErrorLine(string line)
    {
        ReadOnlySpan<char> payload = line.AsSpan().TrimStart();
        if (payload.StartsWith('['))
        {
            int prefixEnd = payload.IndexOf(']');
            if (prefixEnd >= 0)
            {
                payload = payload[(prefixEnd + 1)..].TrimStart();
            }
        }

        return payload.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Conversion failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Failed to open", StringComparison.OrdinalIgnoreCase)
            || line.Contains("moov atom", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Header missing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("No space left on device", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CA2025 ownership boundary: the ONLY place the song-path ffmpeg Process is handed
    /// to an unawaited monitor task. Callers whose body disposes the process (their
    /// startup-wait failure paths and catches do) start the monitor through this
    /// wrapper, never inline: the analyzer flags any method that both disposes a
    /// disposable and passes one to an unawaited task. After this call the monitor
    /// owns the process (dispose in its finally); the caller must not touch it again.
    /// </summary>
    private void StartSongMonitor(Process process, string ffmpegPath, string cachePath, string itemId, long artModifiedTicks)
        => _ = MonitorFfmpegAndRemuxAsync(process, ffmpegPath, cachePath, itemId, artModifiedTicks);

    /// <summary>
    /// CA2025 ownership boundary for every HLS path: see <see cref="StartSongMonitor"/>.
    /// Optional parameters mirror <see cref="MonitorFfmpegHlsAsync"/> 1:1.
    /// </summary>
    private void StartHlsMonitor(
        Process process,
        string hlsDir,
        string itemId,
        long artModifiedTicks,
        string label,
        ConcurrentDictionary<string, bool>? activeEncodesTracker = null,
        bool videoTranscodeTier = false,
        long? runTimeTicks = null)
        => _ = MonitorFfmpegHlsAsync(process, hlsDir, itemId, artModifiedTicks, label, activeEncodesTracker, videoTranscodeTier, runTimeTicks);

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
    /// to exit (with a tier-sized timeout, see <see cref="HlsMonitorTimeoutMinutes"/>),
    /// logs the outcome, and triggers cache eviction. Disposes the process when done.
    /// Unlike <see cref="MonitorFfmpegAndRemuxAsync"/>, there is no remux step: HLS
    /// segments are already seekable individually.
    /// </summary>
    /// <param name="process">The ffmpeg process (started, not yet awaited).</param>
    /// <param name="hlsDir">The HLS directory containing playlist and segments.</param>
    /// <param name="itemId">Item ID for logging.</param>
    /// <param name="artModifiedTicks">Art ticks for logging.</param>
    /// <param name="label">Content label for log messages ("Song", "Audiobook", "Episode").</param>
    /// <param name="activeEncodesTracker">The active-encode registry to clear when
    /// this process exits (<see cref="_activeAudiobookEncodes"/> or
    /// <see cref="_activeEpisodeEncodes"/>); defaults to the audiobook registry so
    /// existing callers keep their behavior.</param>
    /// <param name="videoTranscodeTier">Whether this encode is the episode
    /// video-transcode tier (JF-500); only that tier scales its timeout from the
    /// runtime, the others keep the fixed 30-minute ceiling.</param>
    /// <param name="runTimeTicks">The item's runtime, used only by the transcode tier.</param>
    /// <returns>A task representing the background monitoring operation.</returns>
    private async Task MonitorFfmpegHlsAsync(
        Process process,
        string hlsDir,
        string itemId,
        long artModifiedTicks,
        string label,
        ConcurrentDictionary<string, bool>? activeEncodesTracker = null,
        bool videoTranscodeTier = false,
        long? runTimeTicks = null)
    {
        var activeEncodes = activeEncodesTracker ?? _activeAudiobookEncodes;
        int timeoutMinutes = HlsMonitorTimeoutMinutes(videoTranscodeTier, runTimeTicks);
        try
        {
            // Tier-sized ceiling: see HlsMonitorTimeoutMinutes (JF-500 review F1)
            // for the sizing rationale and arithmetic.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
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
                    "{Label} HLS encoding FAILED for {ParentId}: ffmpeg exit code {ExitCode}, {SegmentFileCount} segment files, {PlaylistSegmentCount} playlist entries{MetaInfo}",
                    label, itemId, process.ExitCode, segmentFileCount, playlistSegmentCount,
                    expectedChapterCount > 0 ? $" (expected {expectedChapterCount})" : string.Empty);
            }
            else if (expectedChapterCount > 0 && playlistSegmentCount != expectedChapterCount)
            {
                _logger.LogWarning(
                    "{Label} HLS encoding INCOMPLETE for {ParentId}: ffmpeg exited 0 but produced {PlaylistSegmentCount}/{ExpectedCount} playlist segments, {SegmentFileCount} segment files on disk",
                    label, itemId, playlistSegmentCount, expectedChapterCount, segmentFileCount);
            }
            else
            {
                _logger.LogDebug(
                    "{Label} HLS encoding complete for {ParentId}: {SegmentFileCount} segments",
                    label, itemId, segmentFileCount);
            }

            // Background eviction — don't block
            _ = Task.Run(() => _cache.EvictIfNeeded(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Label} HLS encoding TIMED OUT ({TimeoutMinutes} min) for {ParentId}", label, timeoutMinutes, itemId);
            try { process.Kill(); } catch { /* already exited */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Label} HLS: ffmpeg monitoring failed for {ParentId}", label, itemId);
        }
        finally
        {
            // Always clear the active encode flag so future requests can start a fresh encode
            activeEncodes.TryRemove(itemId, out _);
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

            // JF-428: the partial remux is a cache entry a sweep could delete mid-write
            _cache.Pin(faststartPath);
            try
            {
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
            }
            finally
            {
                _cache.Unpin(faststartPath);
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
