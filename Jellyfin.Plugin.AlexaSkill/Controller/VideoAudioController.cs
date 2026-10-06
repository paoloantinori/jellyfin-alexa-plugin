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
    /// Key: parentId string, Value: the live generation slots (presence = at
    /// least one art-tick generation active; see <see cref="MarkEncodeActive"/>
    /// for the generation rule, JF-665/JF-669).
    /// </summary>
    private static readonly ConcurrentDictionary<string, ActiveEncodeGenerations> _activeAudiobookEncodes = new();

    /// <summary>
    /// JF-636: live ffmpeg processes of the audio-speed variant, keyed by its
    /// cache key, with the Alexa device id whose launch minted them (the
    /// <c>?d=</c> hint BuildAudioPlayerResponse appends to speed URLs). A speed
    /// change re-launches the item at a NEW (rate, start) key while the previous
    /// variant's encode is still running; the launching Echo stops fetching its
    /// segments but nothing else would stop it, and an audio-only encode at ~49x
    /// realtime holds its encode-gate slot for up to minutes (three quick "faster"
    /// asks starve the gate the third stream start waits on). A new speed encode
    /// therefore kills the OTHER speed encodes of the same item minted by the SAME
    /// device (the review finding: tokens are item-scoped and queues per-device,
    /// so another Echo may be actively consuming a different variant of the same
    /// item; an ownerless entry is never killed - conservative). Entries are
    /// removed by an exit watcher when the process ends, so the registry only ever
    /// names live encodes.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (Process Process, string? OwnerDeviceId)> _activeAudioSpeedEncodeProcesses = new();

    /// <summary>
    /// Compiled regex for extracting trailing chapter number from audiobook filenames
    /// (e.g. "The Upside of Irrationality 065.mp3" → 65).
    /// </summary>
    private static readonly Regex _chapterNumberRegex = new(@"(\d+)\s*$", RegexOptions.Compiled);

    private const string HlsExtInf = "#EXTINF:";
    private const string HlsEndList = "#EXT-X-ENDLIST";

    /// <summary>
    /// Name of the pre-written full-listing playlist file shared by every prewriting
    /// HLS path (audiobook original, episode JF-531, single-item JF-536): a SEPARATE
    /// file from ffmpeg's own stream.m3u8, listing every segment the encode will
    /// produce (no ENDLIST, event playlist) so first serve carries the true total
    /// runtime. One constant because the name must agree across each path's
    /// prewrite/serve/stale-delete sites within the same cache directory.
    /// </summary>
    private const string PrewrittenPlaylistFileName = "playlist-full.m3u8";

    /// <summary>
    /// The encode-metadata sidecar every concat encode writes into its cache
    /// directory at encode start (JF-292: the post-exit monitor's completeness
    /// logging; JF-784 leg 1: the serve-time timeline verdict reads it too, so
    /// the name must agree across the write site and the ONE reader,
    /// <see cref="TryReadEncodeTimelineMetadata"/>).
    /// </summary>
    private const string EncodeMetadataFileName = "encode-metadata.json";

        /// <summary>JF-625: below this runtime the pre-written full listing is SKIPPED (the
        /// encode finishes before the device's first fetch; see ShouldPrewriteFullListing
        /// for the phantom-tail failure it prevents).</summary>
        private static readonly long PrewriteListingMinRuntimeTicks = TimeSpan.FromMinutes(10).Ticks;

        /// <summary>
        /// JF-625: the ONE pre-write decision, shared by every prewrite site (the song
        /// path and the episode path). A pre-written full listing CEIL-estimates the
        /// segment count; ffmpeg can produce one fewer, and short content's whole
        /// stream prefetches in ~1s, reaches the promised-but-missing tail segment,
        /// 404s, and kills the player before playback starts. Below the threshold the
        /// encode completes to ENDLIST before the device's first fetch, so the correct
        /// VOD playlist is served directly.
        /// </summary>
        private static bool ShouldPrewriteFullListing(long runtimeTicks)
            => runtimeTicks > PrewriteListingMinRuntimeTicks;

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
    /// The single-item HLS <c>-hls_time</c> in seconds (songs + single-chapter
    /// audiobooks, the path <see cref="StreamHlsVideoAudioCore"/> serves; JF-536).
    /// LOAD-BEARING COUPLING with the same shape as
    /// <see cref="EpisodeHlsSegmentSeconds"/>: the <c>-hls_time</c> argument in
    /// <see cref="BuildHlsFfmpegArguments"/> is the length ffmpeg actually cuts
    /// segments at, and <see cref="WriteVideoAudioPlaylist"/> derives the
    /// pre-written listing's segment count and TARGETDURATION from this value;
    /// retuning one without the other makes the listing claim the wrong segment
    /// count and the tail 404 until the completed ENDLIST playlist supersedes it.
    /// </summary>
    private const int SongHlsSegmentSeconds = 4;

    /// <summary>
    /// The audiobook HLS segment length in seconds. LOAD-BEARING COUPLING, three
    /// sites that must move together: (1) this const, the flat-divisor fallback
    /// passed to <see cref="Alexa.Playback.AudiobookPlaylistBuilder.BuildResumePlaylist"/>
    /// at the audiobook serve path; (2) the bare <c>-hls_time 10</c> literal in
    /// <see cref="BuildHlsAudiobookFfmpegArguments"/> (the length ffmpeg actually
    /// cuts audiobook segments at); (3) the tracker's
    /// <c>AudiobookPositionTracker.SegmentDurationSeconds</c> (the resume-position
    /// arithmetic). The audio-only EPISODE path keeps its own separate 10s literal
    /// in <see cref="BuildEpisodeAudioHlsFfmpegArguments"/> (its timelines never
    /// intermix with audiobook resume).
    /// </summary>
    private const int AudiobookHlsSegmentSeconds = 10;

    /// <summary>
    /// JF-778 windowed prewrite serve, the FLOOR: the minimum number of entries a
    /// live encode's windowed listing carries (and the minimum it keeps when the
    /// head cap would shrink below it). 2 entries = 8s at the episode 4s
    /// segments, which puts the player's live default start position at segment
    /// 0 for every player offset constant observed on device (1-3 segments; the
    /// device evidence lives on <see cref="TryServePrewrittenEpisodePlaylist"/>).
    /// REQUIRED RELATION, pinned by
    /// EpisodePrewriteWindowFloor_RespectsSegmentHoldLookahead: the floor's
    /// promise beyond the encode head (floor - 1 entries) must stay within
    /// <see cref="SegmentHoldLookahead"/>, or the entries the listing promises
    /// would 404 past the JF-503 hold's reach and reintroduce the tail death
    /// this window exists to prevent.
    /// </summary>
    internal const int EpisodePrewriteWindowFloorSegments = 2;

    /// <summary>
    /// JF-778 windowed prewrite serve, the LEAD: how many entries the window may
    /// run ahead of the encode's ELAPSED time (the prewrite file's mtime), i.e.
    /// how far the served listing's edge leads the 1x playback position. 3
    /// entries = 12s of playable headroom over the growing window, the standard
    /// live-playlist shape; the lead (not the floor) must stay &lt;= the player's
    /// live offset constant or the default start drifts past 0 as the window
    /// grows. The window grows one entry per segment-seconds of elapsed (1x
    /// playback), so a player that started at 0 never catches the edge while any
    /// encode (>= 4.4x realtime) outruns it.
    /// </summary>
    internal const int EpisodePrewriteWindowLeadSegments = 3;

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
    /// Gets or sets an optional explicit ffmpeg path that takes precedence over
    /// every other resolution source. When empty, <see cref="ResolveFfmpegPath"/>
    /// falls back to <see cref="IMediaEncoder"/> (gated on the file existing)
    /// and then a PATH scan. Tests inject their fake ffmpeg here.
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
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
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
    /// If a cached HLS directory exists (same item + same album art), serves the
    /// playlist after the ticks-scoped debris verdict accepts it (a live encode's
    /// growing playlist or a completed encode's ENDLIST one, JF-676).
    /// Otherwise, starts ffmpeg generating HLS segments and serves the playlist as soon as the
    /// first segment is ready, without waiting for the entire file to be encoded. This avoids
    /// timeout issues with long content (audiobooks) where full encoding could take minutes.
    /// The Echo Show re-fetches the playlist periodically and discovers new segments as they appear.
    /// </summary>
    /// <param name="itemId">The Jellyfin audio item ID.</param>
    /// <returns>An HLS playlist (.m3u8) file.</returns>
    [HttpGet("{itemId}/stream.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsVideoAudio([FromRoute] string itemId)
    {
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
        }

        return await StreamHlsVideoAudioCore(itemId).ConfigureAwait(false);
    }

    /// <summary>
    /// Build and serve the single-item HLS playlist without token validation. Token validation is
    /// performed by the public <see cref="StreamHlsVideoAudio"/> entry point, or by the audiobook
    /// endpoint (which validates against parentId before redirecting single-chapter books here).
    /// Internal test seam (JF-685, InternalsVisibleTo; visibility only): the ONE driver left that
    /// reaches <see cref="ServePlaylistWithTokenAsync"/>'s no-token branch through a real
    /// construction (every public entry is gate-gated; JF-682 closed the redirect shape). The full
    /// why lives on the JF-685 pin in VideoAudioControllerTests. INVARIANT the old private
    /// visibility enforced for free, now doc-only: this core performs NO token validation, so
    /// every PRODUCTION caller must arrive through a validated gate (the JF-309 rule); do not
    /// call it from new production code.
    /// </summary>
    /// <param name="itemId">The Jellyfin audio item ID.</param>
    /// <param name="overrideToken">Chapter-scoped token overriding the request's own (single-chapter audiobooks).</param>
    /// <param name="startTicks">Resume position in .NET ticks the playlist is SLICED at
    /// (JF-686: the single-chapter audiobook redirect threads the audiobook endpoint's
    /// <c>?start=</c> here, the same mechanism the episode core uses via
    /// <see cref="ServeEpisodePlaylistAsync"/>; ExoPlayer ignores <c>#EXT-X-START</c>, so
    /// slicing is the only resume that actually resumes). 0/absent serves unsliced: the
    /// song route has no <c>?start=</c>, so its serves stay byte-identical to pre-JF-686.</param>
    internal async Task<ActionResult> StreamHlsVideoAudioCore(string itemId, string? overrideToken = null, long startTicks = 0)
    {
        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        long artModifiedTicks = GetArtModifiedTicks(validation.Item);

        // Check cache first (fast path: no lock needed).
        // A small playlist is still consistent (ffmpeg writes atomically via
        // .tmp rename), but since JF-676 a cached playlist is only SERVED after
        // the ticks-scoped debris verdict accepts it: while the caller's own
        // art-tick generation runs (the pre-write listing below takes over), or
        // once it completed (ENDLIST); a KILLED generation's no-ENDLIST partial
        // is debris, cleaned and re-encoded, never served.
        HlsCacheProbe fastProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeVideoAudioEncodes, itemId, artModifiedTicks).ConfigureAwait(false);
        if (fastProbe.Playlist != null)
        {
            FileInfo cached = fastProbe.Playlist;
            // JF-536/JF-675 own-live prewrite gate, ONE copy on
            // TryServeOwnLiveVideoAudioPrewriteAsync (JF-679): while the
            // caller's OWN art-tick generation runs, the pre-written full
            // listing serves; the own-dead fall-through validates the cached
            // playlist below.
            ActionResult? prewritten = await TryServeOwnLiveVideoAudioPrewriteAsync(itemId, artModifiedTicks, overrideToken, startTicks).ConfigureAwait(false);
            if (prewritten != null)
            {
                return prewritten;
            }

            // JF-676: the own-dead fall-through VALIDATES the cached playlist
            // (the ticks-scoped debris verdict this path never had): a KILLED
            // own-ticks generation's stale no-ENDLIST partial is cleaned and
            // re-encoded, never served (ExoPlayer would join at the dead live
            // edge and poll a playlist that never grows), while a completed
            // encode's ENDLIST playlist serves here (the JF-675 fall-through).
            // JF-677: the verdict's read is threaded to the serve (one full
            // read per validated serve; ResolveServeContentAsync owns the
            // vanish re-probe).
            ActionResult? fastServed = await TryServeValidatedHlsCacheAsync(
                itemId,
                "VideoAudio HLS",
                () => ValidateVideoAudioCacheAsync(cached, itemId, artModifiedTicks, fastProbe.OwnGenerationLiveOrRegistering),
                valid =>
                {
                    _logger.LogDebug("VideoAudio HLS: serving cached playlist for item {ItemId}", itemId);
#pragma warning disable CA3003 // path derived from GUID-validated itemId
                    // JF-686 review F1: a null threaded Content is the verdict's OWN-LIVE
                    // row (ffmpeg's still-growing playlist); a resume slice past the
                    // partial's total would serve zero segments and kill the player, so
                    // the growing shape serves full (the benign pre-JF-686 degradation).
                    return ServeVideoAudioPlaylistAsync(valid.Playlist.FullName, overrideToken, valid.Content is null ? 0 : startTicks, valid.Content);
#pragma warning restore CA3003
                }).ConfigureAwait(false);
            if (fastServed != null)
            {
                return fastServed;
            }

            // Debris (the verdict cleaned it above) or a vanished playlist:
            // fall through to the per-item lock and re-encode.
        }

        // Cache miss: acquire per-item lock
        using (await LockHlsItemAsync(itemId, artModifiedTicks).ConfigureAwait(false))
        {
            // Clean up any corrupt/partial HLS directory from a previous failed generation
            _cache.CleanupHlsStub(itemId, artModifiedTicks);

            // Double-check cache after acquiring lock
            HlsCacheProbe inLockProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeVideoAudioEncodes, itemId, artModifiedTicks).ConfigureAwait(false);
            if (inLockProbe.Playlist != null)
            {
                FileInfo cached = inLockProbe.Playlist;
                // JF-536: the encode a concurrent request started is still running;
                // its pre-written full listing serves (the own-live row), the same
                // gate helper as the fast path (JF-675, JF-679).
                ActionResult? prewritten = await TryServeOwnLiveVideoAudioPrewriteAsync(itemId, artModifiedTicks, overrideToken, startTicks).ConfigureAwait(false);
                if (prewritten != null)
                {
                    return prewritten;
                }

                // JF-676: the own-dead fall-through validates (same ticks-scoped
                // debris verdict as the fast path; the validator core already
                // translates a vanished read into the null that re-encodes).
                // JF-678: the row rides the shared verdict+serve composite, so a
                // vanish between the verdict and the serve ALSO falls through to
                // this scope's own encode branch below instead of a bare 500.
#pragma warning disable CA3003
                ActionResult? concurrentServed = await TryServeValidatedHlsCacheAsync(
                    itemId,
                    "VideoAudio HLS",
                    () => ValidateVideoAudioCacheAsync(cached, itemId, artModifiedTicks, inLockProbe.OwnGenerationLiveOrRegistering),
                    valid =>
                    {
                        _logger.LogDebug("VideoAudio HLS: serving playlist generated by concurrent request for item {ItemId}", itemId);
                        // JF-686 review F1: same own-live guard as the fast path (a null
                        // threaded Content is the growing playlist; serve it full).
                        return ServeVideoAudioPlaylistAsync(valid.Playlist.FullName, overrideToken, valid.Content is null ? 0 : startTicks, valid.Content);
                    }).ConfigureAwait(false);
#pragma warning restore CA3003
                if (concurrentServed != null)
                {
                    return concurrentServed;
                }

                // JF-678 rework F1: the vanish fall-through's breach-vs-legitimate
                // liveness guard (the rationale lives on GuardInLockVanishFallThrough).
                GuardInLockVanishFallThrough(_activeVideoAudioEncodes, itemId, artModifiedTicks, cached.FullName, "VideoAudio HLS");
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

            // Register (encode start, before the mark: the JF-782 leg 2
            // one-wrapper owns the ordering rationale) and mark the encode
            // active BEFORE starting ffmpeg (inside the lock):
            // the mark-before-start invariant all four HLS paths share (the
            // episode/variant paths have carried it since JF-498; moved here in
            // the JF-676 rework). A concurrent fast-path request can only see a
            // playlist file once ffmpeg writes it, and by then the caller's own
            // art-tick generation is already live, so the ticks-scoped debris
            // verdict holds instead of deleting the running encode's directory
            // (the gate review's race: this mark used to sit after the process
            // start, and a verdict landing between ffmpeg's first playlist
            // write and the mark classified the genuinely-live no-ENDLIST
            // playlist as debris). The monitor clears the flag on exit
            // (generation-aware, JF-665/JF-669); the song path's former
            // pre-mark sentinel is gone with the pre-mark window it existed
            // for (the JF-665 semantics survive structurally: every failure
            // path from here on clears only THIS generation's slot).
            ActiveEncodeHandle activeEncode = RegisterAndMarkEncodeActive(_activeVideoAudioEncodes, itemId, artModifiedTicks, hlsDir);

            Process ffmpegProcess;
            try
            {
                // Start ffmpeg without waiting: generate segments in the
                // background. Serve the playlist as soon as the first segment
                // is ready so the Echo Show can start playback immediately,
                // even for long content like audiobooks.
                ffmpegProcess = await StartFfmpegProcessGatedAsync(
                    validation.FfmpegPath,
                    ffmpegArgs,
                    useBlackFrame ? EstimateEncodeBytes(validation.Item.RunTimeTicks ?? 0) : EstimateArtEncodeBytes(validation.Item.RunTimeTicks ?? 0),
                    hlsDir).ConfigureAwait(false);
            }
            catch
            {
                activeEncode.Clear();
                throw;
            }

            string prewrittenPath = Path.Combine(hlsDir, PrewrittenPlaylistFileName);

            try
            {
                // JF-536: pre-write the FULL segment listing (the episode path's
                // JF-531 mechanism, via the shared writer core) only AFTER the pin
                // inside StartFfmpegProcessGatedAsync succeeded (the JF-428 rule: a
                // listing written before the pin can be deleted by a concurrent
                // eviction sweep in the creation-to-pin window, silently reverting
                // the serve to the live playlist). ffmpeg keeps writing its own
                // stream.m3u8 (append_list growth, ENDLIST at completion); what we
                // SERVE while the encode runs is this full listing. Why this path
                // needs it: same VideoApp/ExoPlayer consumer the episode path
                // verified live (2026-09-09, corr=c0c21c6a: a no-ENDLIST growing
                // playlist is treated as LIVE, playback starts at the live edge and
                // the seekbar shows only the encoded-so-far window), and the
                // single-chapter audiobooks this path also serves are hours long,
                // so the encode window is minutes, not seconds. When the encode
                // completes, ffmpeg's own ENDLIST playlist takes over via the
                // cache-hit paths above.
                // JF-625 (live 2026-09-24, Magnolia): the pre-written listing is only
                // safe when the encode outlasts the device's first playlist fetch. Its
                // segment count is a CEIL estimate; ffmpeg can produce one fewer, and a
                // song's whole stream prefetched in ~1s reaches the promised-but-missing
                // tail segment, 404s, and kills the player before playback starts (green
                // first frame, no audio, no controls). Long encodes need the listing
                // (JF-536/JF-531: a no-ENDLIST growing playlist joins at the live edge);
                // short content encodes to ENDLIST before the device's first fetch, so
                // skipping the listing serves the correct VOD playlist directly.
                // Like the episode twin, the listing lands AFTER the process starts,
                // so a concurrent request can briefly see the flag with no listing
                // yet: TryServePrewrittenVideoAudioPlaylist covers that race by
                // falling back to ffmpeg's live playlist (the pre-JF-536 serve).
                if (validation.Item.RunTimeTicks is > 0 && ShouldPrewriteFullListing(validation.Item.RunTimeTicks.Value))
                {
                    WriteVideoAudioPlaylist(
                        prewrittenPath,
                        hlsBaseUrl,
                        validation.Item.RunTimeTicks.Value,
                        overrideToken ?? HttpContext.Request.Query["token"]);
                }
                else if (validation.Item.RunTimeTicks > 0)
                {
                    // Short content (JF-625): runtime known but below the prewrite
                    // threshold - a deliberate skip, not the missing-runtime case below.
                    TryDelete(prewrittenPath);
                    _logger.LogDebug(
                        "VideoAudio HLS: item {ItemId} is short content ({RuntimeMin:F1} min), skipping the pre-written listing; the ENDLIST playlist completes before the device's first fetch",
                        itemId, TimeSpan.FromTicks(validation.Item.RunTimeTicks.Value).TotalMinutes);
                }
                else
                {
                    SkipPrewrittenListing(prewrittenPath, itemId, "VideoAudio HLS");
                }

                // Wait for the first segment file to appear on disk.
                ActionResult? firstSegmentFailure = await WaitForFirstSegmentOrKillAsync(
                    ffmpegProcess,
                    hlsDir,
                    playlistPath,
                    "seg_000.ts",
                    activeEncode,
                    exitCode => _logger.LogWarning("VideoAudio HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, exitCode),
                    "HLS generation failed").ConfigureAwait(false);
                if (firstSegmentFailure != null)
                {
                    return firstSegmentFailure;
                }
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process.
                activeEncode.KillAndClear(ffmpegProcess);
                throw;
            }

            // Monitor ffmpeg in the background: wait for completion, log errors,
            // trigger eviction, clear the active-encode flag (its finally owns both
            // the flag clear and the process disposal from here on). CA2025:
            // started via the boundary helper AFTER the disposing scope above
            // closed, so no later exception (e.g. the playlist re-read in
            // ServePlaylistWithTokenAsync racing ffmpeg's rewrite) can dispose the
            // process under the running monitor.
            StartHlsMonitor(ffmpegProcess, hlsDir, itemId, artModifiedTicks, "Song", activeEncode);

            // Serve the pre-written FULL listing immediately (JF-536). Null only when
            // the prewrite was skipped (no runtime): fall back to ffmpeg's live
            // partial playlist, the pre-JF-536 behavior (the episode tail's shape).
            ActionResult? prewrittenServe = await TryServePrewrittenVideoAudioPlaylist(itemId, artModifiedTicks, overrideToken, startTicks).ConfigureAwait(false);
            if (prewrittenServe != null)
            {
                return prewrittenServe;
            }

            // Serve the partial playlist immediately; the Echo Show will start
            // fetching segments and re-request the playlist for updates.
            // JF-686 review F1: this is the still-growing live listing (the prewrite was
            // skipped: no runtime or a below-threshold item). A resume slice past the
            // partial's encoded-so-far total would keep ZERO segments (flat divisor past
            // the last written segment) and hand the player a dead header-only playlist,
            // where pre-JF-686 the same request served the full live playlist and merely
            // restarted at 0:00. The still-growing shape cannot honor a position: drop
            // the offset here exactly like the album twin's cold-serve (the JF-625
            // isMusicAlbum row above); the common resume case replays an already encoded
            // item and takes the cache-hit paths with the correct slice.
            if (startTicks > 0)
            {
                _logger.LogInformation(
                    "VideoAudio HLS: cold-cache resume for item {ItemId} (startTicks={StartTicks}) dropped; serving the live playlist from the beginning",
                    itemId, startTicks);
            }

            _logger.LogDebug("VideoAudio HLS: serving partial playlist for item {ItemId}", itemId);
            return await ServeVideoAudioPlaylistAsync(playlistPath, overrideToken, 0).ConfigureAwait(false);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Tracks episode/movie remux HLS encodes currently in progress by itemId (the
    /// JF-498 episode path). VARIANT-GENERIC since JF-507/JF-636 (kept under this
    /// historical name): the audio-only episode variant and the audio-speed
    /// (rate, start) variant key their encodes by their own variant cache keys
    /// (<see cref="EpisodeAudioCacheKey"/>, <see cref="AudioSpeedCacheKey"/>) in
    /// this SAME registry, via <see cref="ServeVariantHlsAsync"/>. Doubles as the
    /// liveness signal for <see cref="ValidateEpisodeCacheAsync"/>: a cached
    /// playlist WITHOUT <c>#EXT-X-ENDLIST</c> is either a live encode (the
    /// caller's own art-tick generation live or mid-registration) or the debris
    /// of a killed one (own generation dead; cleaned up, ticks-scoped since
    /// JF-676, and re-encoded). The value is the key's live generation slots, one per
    /// (key, artModifiedTicks) generation (JF-665/JF-669, see
    /// <see cref="MarkEncodeActive"/>): every clear is compare-and-remove on the
    /// handle's own (ticks, token) pair, so a displaced generation's late exit
    /// cannot drop a newer encode's flag, and the entry survives while ANY
    /// art-tick generation of the key is still writing.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ActiveEncodeGenerations> _activeEpisodeEncodes = new();

    /// <summary>
    /// Tracks single-item video-audio HLS encodes currently in progress by itemId
    /// (the song path, which also serves the single-chapter audiobooks
    /// <see cref="StreamHlsAudiobook"/> redirects to
    /// <see cref="StreamHlsVideoAudioCore"/>). Doubles as the gate for serving the
    /// pre-written full listing while the encode runs (JF-536): the monitor clears
    /// the flag on exit, after which the completed ENDLIST playlist is served.
    /// The value is the key's live generation slots (JF-665/JF-669, see
    /// <see cref="MarkEncodeActive"/>), like every sibling registry.
    /// </summary>
    private static readonly ConcurrentDictionary<string, ActiveEncodeGenerations> _activeVideoAudioEncodes = new();

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
    /// path. Launch sites route here via <c>PlaybackLaunchBuilder.GetVideoAppLaunchUrl</c>;
    /// the endpoint re-probes the codecs server-side to pick its own ffmpeg
    /// arguments (video copy vs transcode, audio copy vs AAC). Optional
    /// <c>?start=&lt;ticks&gt;</c> serves a resume-sliced playlist (JF-499 W2; see
    /// <see cref="ServeEpisodePlaylistAsync"/>).
    /// </summary>
    /// <param name="itemId">The Jellyfin video item ID.</param>
    /// <param name="startTicks">Optional resume position in .NET ticks (0/absent plays from the start).</param>
    /// <returns>An HLS playlist (.m3u8) file.</returns>
    [HttpGet("episode/{itemId}/stream.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsEpisode(
        [FromRoute] string itemId,
        [FromQuery(Name = "start")] long? startTicks = null)
    {
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
        }

        return await StreamHlsEpisodeCore(itemId, startTicks ?? 0).ConfigureAwait(false);
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
    /// ENDLIST playlist ffmpeg wrote takes over. A positive
    /// <paramref name="startTicks"/> slices every served playlist at the resume
    /// position (JF-499 W2, all serve paths honor it like the audiobook endpoint's
    /// <c>?start=</c>).
    /// </summary>
    /// <param name="itemId">GUID-validated video item ID.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 plays from the start).</param>
    private async Task<ActionResult> StreamHlsEpisodeCore(string itemId, long startTicks)
    {
        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        long artModifiedTicks = GetArtModifiedTicks(validation.Item);

        // The method's ONE warm-cache serve row (JF-679 shape, re-extracted at the
        // third copy by JF-774): verdict one playlist candidate through the
        // episode pairing and serve it via ServeEpisodeWarmCacheAsync. Only the
        // probed FileInfo, the resolving probe's liveness answer (JF-782: the
        // verdict conjuncts it into its unread acceptance), and the per-site
        // serve log line (the JF-677 in-lock pins discriminate serving branches
        // by that exact wording) vary per call site.
        Task<ActionResult?> TryServeEpisodeCacheAsync(FileInfo hit, bool probeOwnLiveOrRegistering, Action logServe)
            => TryServeValidatedHlsCacheAsync(
                itemId,
                "VideoAudio episode HLS",
                () => ValidateEpisodeCacheAsync(hit, itemId, artModifiedTicks, probeOwnLiveOrRegistering),
                valid => ServeEpisodeWarmCacheAsync(valid, itemId, artModifiedTicks, startTicks, logServe));

        // JF-774 finding 3 (widened to the in-lock row by the review): a
        // cache-root hit whose verdict nulled (debris, or a vanished file) must
        // not mask the same-key TRANSIENT generation the ordered first-hit
        // probe never reached. Probe that root before the caller falls through
        // to its encode branch: the re-encode path's per-file debris sweep
        // (DeleteHlsEncodeDebris) would otherwise wipe a VALID transient entry
        // hidden behind an UNDELETABLE cache-root playlist (the locked or
        // permission-denied class), a from-zero multi-GB re-encode whose
        // emptied dir also feeds the scan fallback's cross-root recency
        // (finding 1). Serving a valid transient entry here is strictly better
        // than re-encoding whether the cache-root debris was removed or merely
        // survived. Skipped when the probe's hit already came from the
        // transient root (the ordered probe's second leg covered it; there is
        // no third root to reach). The verdict over the transient hit threads
        // a FALSE probe answer (JF-782 review F1): this leg's hit was
        // resolved by the UNCONDITIONAL transient probe, not by a
        // liveness-gated one, so there is no probe-time liveness the unread
        // acceptance could ride; the verdict therefore always READS and
        // judges the transient hit (an ENDLIST resting entry serves, the
        // JF-774 F3 purpose; a no-ENDLIST stale shadow of a killed oversize
        // encode is debris, never served unread).
        string transientDir = _cache.GetTransientHlsDirectoryPath(itemId, artModifiedTicks);
        async Task<ActionResult?> TryServeTransientGenerationAsync(Action logServe)
        {
            FileInfo? transientHit = _cache.ProbeTransientHlsPlaylist(itemId, artModifiedTicks);
            return transientHit == null
                ? null
                : await TryServeEpisodeCacheAsync(transientHit, probeOwnLiveOrRegistering: false, logServe).ConfigureAwait(false);
        }

        // The ONE transient-root fallback for both warm rows (JF-774 gate-marker:
        // the block was duplicated verbatim; a drift at one row would silently
        // leave the other row's masking behavior in place).
        async Task<ActionResult?> TryServeTransientUnlessCachedAsync(FileInfo cached)
        {
            if (Path.GetDirectoryName(cached.FullName)!.Equals(transientDir, StringComparison.Ordinal))
            {
                return null;
            }

            return await TryServeTransientGenerationAsync(TransientServeLog()).ConfigureAwait(false);
        }

        Action TransientServeLog()
            => () => _logger.LogDebug("VideoAudio episode HLS: serving transient-root playlist for item {ItemId} after a cache-root debris miss (JF-774)", itemId);

        // Fast path: a cached playlist is valid when the encode COMPLETED (ENDLIST)
        // or the caller's OWN art-tick generation is still RUNNING; anything else
        // is the debris of a killed encode (server restart mid-encode, or a
        // killed own-ticks generation under a live foreign-ticks sibling, JF-676)
        // and is re-encoded.
        HlsCacheProbe fastProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeEpisodeEncodes, itemId, artModifiedTicks).ConfigureAwait(false);
        if (fastProbe.Playlist != null)
        {
            FileInfo cached = fastProbe.Playlist;
            // JF-499 W3: a concurrent lock-holder can invalidate this cache between
            // the FileInfo read above and the content reads below (a debris verdict
            // in ValidateEpisodeCacheAsync deletes the caller's own art-tick
            // generation directory under the per-item lock, JF-676). A vanished file
            // must fall through to the re-encode path instead of surfacing a 500
            // that only self-heals on the Echo's playlist retry.
            ActionResult? fastServed = await TryServeEpisodeCacheAsync(
                cached,
                fastProbe.OwnGenerationLiveOrRegistering,
                () => _logger.LogDebug("VideoAudio episode HLS: serving cached playlist for item {ItemId}", itemId)).ConfigureAwait(false);
            if (fastServed != null)
            {
                return fastServed;
            }

            ActionResult? transientFallback = await TryServeTransientUnlessCachedAsync(cached).ConfigureAwait(false);
            if (transientFallback != null)
            {
                return transientFallback;
            }
        }

        // Cache miss: acquire per-item lock
        using (await LockHlsItemAsync(itemId, artModifiedTicks).ConfigureAwait(false))
        {
            _cache.CleanupHlsStub(itemId, artModifiedTicks);

            // Double-check cache after acquiring lock
            HlsCacheProbe inLockProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeEpisodeEncodes, itemId, artModifiedTicks).ConfigureAwait(false);
            if (inLockProbe.Playlist != null)
            {
                FileInfo cached = inLockProbe.Playlist;
                // JF-531: the encode a concurrent request started is still running;
                // its pre-written full listing serves (the own-live row). The whole
                // serve row (JF-531/JF-675/JF-677 rationale and the JF-499 W3 log
                // ordering contract) lives once on ServeEpisodeWarmCacheAsync
                // (JF-679), shared with the fast-path closure above. JF-678: the
                // row rides the shared verdict+serve composite, so a playlist
                // deleted between the verdict and the serve falls through to this
                // scope's own encode branch below instead of a bare 500.
                ActionResult? concurrentServed = await TryServeEpisodeCacheAsync(
                    cached,
                    inLockProbe.OwnGenerationLiveOrRegistering,
                    () => _logger.LogDebug("VideoAudio episode HLS: serving playlist generated by concurrent request for item {ItemId}", itemId)).ConfigureAwait(false);
                if (concurrentServed != null)
                {
                    return concurrentServed;
                }

                // JF-678 rework F1: the vanish fall-through's breach-vs-legitimate
                // liveness guard (the rationale lives on GuardInLockVanishFallThrough);
                // BEFORE the transient leg below so a pin breach stays loud instead
                // of being served around.
                GuardInLockVanishFallThrough(_activeEpisodeEncodes, itemId, artModifiedTicks, cached.FullName, "VideoAudio episode HLS");

                // The transient leg runs HERE too (JF-774 review finding 2): a
                // transient entry minted by the concurrent lock-holder AFTER this
                // request's fast path probed is newly visible at this
                // double-check, still hidden behind the cache-first hit, and the
                // encode branch below would per-file-wipe it.
                ActionResult? transientRetry = await TryServeTransientUnlessCachedAsync(cached).ConfigureAwait(false);
                if (transientRetry != null)
                {
                    return transientRetry;
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
            var sourceMedia = ResolveSourceCodecs(validation.Item);
            bool videoTranscodeTier = !VideoAppStreamPolicy.VideoSupportsRemux(sourceMedia.Video);
            long runtimeTicks = validation.Item.RunTimeTicks ?? 0;
            long transcodeEstimateBytes = EstimateEpisodeTranscodeEncodeBytes(runtimeTicks);
            int cacheCapMB = _cache.EffectiveCacheCapMB;

            // JF-537.1 transient mode: an oversize transcode estimate (above the
            // cap the sweep enforces) encodes into the TRANSIENT root instead of
            // the capped cache root, so its bytes never count against the cap,
            // never evict the real cache, and survive for replay until the idle
            // reaper judges them unwatched (segment-fetch recency; VideoApp emits
            // no playback-stop event). Replaces the JF-537 announced-churn
            // resting point (the Warning about unavoidable per-replay re-encodes)
            // with the mode that actually avoids it. Remux tier excluded (the
            // JF-537 scope pin: copy speed bounds its churn).
            bool oversizeTransient = videoTranscodeTier
                && TranscodeEstimateExceedsCacheCap(transcodeEstimateBytes, cacheCapMB);
            if (videoTranscodeTier)
            {
                _logger.LogInformation(
                    "VideoAudio episode HLS: item {ItemId} video codec '{VideoCodec}' is not known h264; using the video transcode tier (libx264 ultrafast CRF 23, measured 4.40x realtime on the minix 2026-09-08, JF-500)",
                    itemId, sourceMedia.Video ?? "(unknown)");

                if (oversizeTransient)
                {
                    _logger.LogInformation(
                        "VideoAudio episode transcode for item {ItemId} is estimated at {EstimateMB:F0}MB, above the configured cache cap of {CapMB}MB (VideoAudioCacheSizeMB): encoding into the transient root instead (JF-537.1), so the capped cache is not evicted for it and the entry survives replay until idle",
                        itemId,
                        transcodeEstimateBytes / (1024.0 * 1024.0),
                        cacheCapMB);
                }
                else
                {
                    _logger.LogDebug(
                        "VideoAudio episode transcode for item {ItemId} is estimated at {EstimateMB:F0}MB, under the configured cache cap of {CapMB}MB (VideoAudioCacheSizeMB): the completed encode is cacheable (JF-537)",
                        itemId,
                        transcodeEstimateBytes / (1024.0 * 1024.0),
                        cacheCapMB);
                }
            }

            _logger.LogDebug(
                "VideoAudio episode HLS: itemId={ItemId}, sourceVideoCodec={VideoCodec}, sourceAudioCodec={AudioCodec}",
                itemId,
                sourceMedia.Video ?? "(unknown)",
                sourceMedia.Audio ?? "(unknown)");

#pragma warning disable CA3003 // paths derived from GUID-validated itemId
            string hlsDir = oversizeTransient
                ? _cache.GetTransientHlsDirectoryPath(itemId, artModifiedTicks)
                : _cache.GetHlsDirectoryPath(itemId, artModifiedTicks);
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
                    sourceMedia.Audio)
                : BuildEpisodeHlsFfmpegArguments(videoUrl, playlistPath, segmentPath, hlsBaseUrl, sourceMedia.Audio);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio episode HLS: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Review I1 (JF-498): remove any debris that survived the cleanup attempts
            // above. The whole-directory deletes (ValidateEpisodeCacheAsync's
            // ticks-scoped CleanupHlsGenerationAt, CleanupHlsStub) are all-or-nothing
            // and swallow failures; a playlist that
            // survived them would make append_list append this encode's entries to the
            // stale ones, baking a doubled playlist into the cache. Per-file deletion
            // (best-effort, warning + proceed on failure) so ffmpeg always starts over
            // a clean target. DIR-SCOPED (JF-537.1): exactly this encode's resolved
            // directory (cache root or transient root), never a same-key valid
            // entry in the other root.
            _cache.DeleteHlsEncodeDebris(hlsDir);
            if (oversizeTransient)
            {
                // The transient leg ALSO per-file-cleans the same-key CACHE-root
                // directory (code-review round, JF-537.1): if that dir holds a
                // debris playlist whose whole-dir delete failed above (the locked
                // or permission-denied class), it would shadow the fast path's
                // cache-first probe on EVERY replay and force a full transient
                // re-encode each time, resurrecting the churn this mode removes.
                // Safe against valid entries: the in-lock double-check above
                // already served any valid cache-root playlist, so whatever
                // remains here under the same key is debris or nothing.
                _cache.DeleteHlsEncodeDebris(_cache.GetHlsDirectoryPath(itemId, artModifiedTicks));
            }

            // Register the RESOLVED HLS directory (transient root's path on
            // the oversize leg, JF-537.1; cache root's otherwise; the lookup
            // holds opaque paths, so segment resolution stays root-agnostic)
            // and mark the encode active BEFORE starting ffmpeg (inside the
            // lock), through the JF-782 register-before-mark one-wrapper: the
            // mark-before-start invariant all four HLS paths share (this
            // path and the variants have carried it since JF-498; the song and
            // audiobook paths joined in the JF-676 rework). A concurrent
            // fast-path request that sees a live (no-ENDLIST) playlist can then
            // rely on the flag being set, because no playlist file can exist
            // before ffmpeg starts: the ticks-scoped debris verdict never sees
            // a playlist whose own-ticks generation is not already live. The
            // monitor clears the flag on exit (generation-aware, JF-665/JF-669).
            // Since JF-536 the pre-written listing lands AFTER the process starts
            // (it must sit inside the JF-428 pin window), so a concurrent request
            // can briefly see the flag with no listing yet;
            // <see cref="TryServePrewrittenEpisodePlaylist"/> covers that race by
            // falling back to the live playlist.
            ActiveEncodeHandle activeEncode = RegisterAndMarkEncodeActive(_activeEpisodeEncodes, itemId, artModifiedTicks, hlsDir);

            Process ffmpegProcess;
            try
            {
                // Oversize headroom is 0 on purpose (JF-537.1): the JF-428
                // pre-encode reservation exists to make room IN THE CACHE for the
                // incoming encode, and a transient encode writes nothing there;
                // reserving multi-GB headroom would evict real cache entries for
                // nothing (the exact churn this mode removes). Headroom 0 is the
                // sweep's post-encode shape, so the cap is still enforced.
                long cacheFootprintBytes = oversizeTransient
                    ? 0
                    : videoTranscodeTier
                        ? transcodeEstimateBytes
                        : EstimateEpisodeEncodeBytes(runtimeTicks, sourceMedia.TotalBitrateBps);
                ffmpegProcess = await StartFfmpegProcessGatedAsync(
                    validation.FfmpegPath,
                    ffmpegArgs,
                    cacheFootprintBytes,
                    hlsDir,
                    videoTranscodeTier ? _episodeTranscodeSlot : null).ConfigureAwait(false);
            }
            catch
            {
                activeEncode.Clear();
                throw;
            }

            try
            {
                // JF-536: pre-write the FULL segment listing (the audiobook pattern,
                // playlist-full.m3u8) only AFTER the pin inside
                // StartFfmpegProcessGatedAsync succeeded (the JF-428 rule: a listing
                // written before the pin can be deleted in the creation-to-pin
                // window by a concurrent eviction sweep, or on the transient leg by
                // its idle reaper, silently reverting the serve to the live
                // playlist; both honor pins, so the ordering invariant is
                // root-independent). ffmpeg keeps writing its own stream.m3u8
                // (append_list growth, ENDLIST at completion); what we SERVE while
                // the encode runs derives from this full listing, WINDOWED to the
                // encoded region (JF-778, see TryServePrewrittenEpisodePlaylist).
                // Why the file spans the full runtime: ExoPlayer treats a
                // no-ENDLIST playlist as LIVE and resolves its default start at
                // playlist end minus 3x TARGETDURATION (live evidence 2026-09-09,
                // corr=c0c21c6a, re-verified 2026-10-05: the player's first fetch
                // landed on the un-encoded tail of a full listing and playback
                // died), so the full-runtime span is only ever served as the
                // post-encode ENDLIST playlist or sliced for resume; the
                // growing-window serve keeps the default start at segment 0 while
                // the encode catches up. When the encode completes, ffmpeg's own
                // ENDLIST playlist takes over via the cache-hit paths above.
                string prewrittenPath = Path.Combine(hlsDir, PrewrittenPlaylistFileName);
                // JF-625 review: episodes ALWAYS pre-write when the runtime is known.
                // The 10-minute threshold is calibrated on the song path's audio-copy
                // encode (faster than the device's first fetch); an episode's remux/
                // transcode outlasts the first fetch even for short episodes, and
                // serving the live listing there is the documented JF-531 live-edge
                // failure (playback joins mid-content, resume slices a growing listing
                // into an empty playlist). The song path keeps the threshold gate.
                if (runtimeTicks > 0)
                {
                    WriteEpisodePlaylist(prewrittenPath, hlsBaseUrl, runtimeTicks, HttpContext.Request.Query["token"]);
                }
                else
                {
                    SkipPrewrittenListing(prewrittenPath, itemId, "VideoAudio episode HLS");
                }

                // Wait for the first segment + playlist to appear. A remux is
                // I/O-bound (tens of x realtime), so the first 4s segment is on disk
                // in well under a second; a video-transcode encode (JF-500) at the
                // measured 4.40x realtime needs ~1s of encode for the first 4s
                // segment plus ffmpeg startup, still far inside the window. The
                // ~20s ceiling only guards pathological cases (network-attached
                // library, cold cache).
                ActionResult? firstSegmentFailure = await WaitForFirstSegmentOrKillAsync(
                    ffmpegProcess,
                    hlsDir,
                    playlistPath,
                    "seg_0000.ts",
                    activeEncode,
                    exitCode => _logger.LogWarning("VideoAudio episode HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, exitCode),
                    "Episode HLS generation failed").ConfigureAwait(false);
                if (firstSegmentFailure != null)
                {
                    return firstSegmentFailure;
                }
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process.
                activeEncode.KillAndClear(ffmpegProcess);
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
                activeEncode,
                videoTranscodeTier,
                validation.Item.RunTimeTicks);

            // Serve the pre-written listing immediately, windowed to the encoded
            // region (JF-531 mechanism; JF-778 serve shape). Null only when the
            // prewrite was skipped (no runtime): fall back to ffmpeg's live
            // partial playlist, the pre-JF-531 behavior. Both honor the resume
            // slice within the window's honor band (JF-499 W2, JF-778).
            ActionResult? prewrittenServe = await TryServePrewrittenEpisodePlaylist(itemId, artModifiedTicks, startTicks).ConfigureAwait(false);
            if (prewrittenServe != null)
            {
                return prewrittenServe;
            }

            _logger.LogDebug("VideoAudio episode HLS: serving partial playlist for item {ItemId}", itemId);
            return await ServeEpisodePlaylistAsync(playlistPath, startTicks).ConfigureAwait(false);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Validate a cached episode remux playlist (JF-498; the episode registry's
    /// pairing over the ticks-scoped debris verdict core
    /// <see cref="ValidateHlsCacheAsync"/>, whose doc holds the decision
    /// table). VARIANT-GENERIC since JF-507/JF-636 (name kept for the remux
    /// history): the audio-only episode and audio-speed variants validate
    /// through it too, keyed by their variant cache keys against the same
    /// <see cref="_activeEpisodeEncodes"/> registry.
    /// REGISTRY-FAMILY BOUNDARY (JF-675, narrowed by JF-676): the verdict's
    /// liveness read covers ONLY this episode registry while sibling registries
    /// key some directories in the same cache root (the song path's
    /// <see cref="StreamHlsVideoAudioCore"/> names its directories by the same
    /// bare GUID), so a live same-GUID same-ticks encode in a sibling registry
    /// would not hold this verdict back, and its directory has the same name
    /// this verdict's scoped cleanup deletes. JF-676 narrowed the blast radius
    /// (one generation directory instead of every <c>{guid}_*</c> directory of
    /// the key) but the boundary itself stands;
    /// <see cref="TryHoldForNearAheadSegmentAsync"/> reads any-of-three
    /// registries for the same key family. The routing overlap is theoretical
    /// today (handlers route audio items to the song endpoint, episodes here),
    /// so the asymmetry is documented rather than widened; a real overlap would
    /// argue for an any-of-three read at this gate.
    /// </summary>
    /// <param name="cached">The cached playlist file info (stream.m3u8).</param>
    /// <param name="itemId">The encode's cache key (remux itemId or a variant cache key) for logging and cleanup.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    /// <returns>The valid playlist (with the verdict's read content on the read
    /// row), or null when invalidated or vanished.</returns>
    private Task<ValidatedHlsCache?> ValidateEpisodeCacheAsync(FileInfo cached, string itemId, long artModifiedTicks, bool probeOwnGenerationLiveOrRegistering)
        => ValidateHlsCacheAsync(cached, itemId, artModifiedTicks, _activeEpisodeEncodes, "Episode", endlistDebrisReason: null, probeOwnGenerationLiveOrRegistering);

    /// <summary>
    /// The SONG path's debris verdict (JF-676): this path never had one, so a
    /// killed encode's stale no-ENDLIST partial was served forever from the
    /// warm-cache paths. Same rule as <see cref="ValidateEpisodeCacheAsync"/>
    /// (whose doc carries the variant-generic history) against this path's own
    /// registry. Reached from the two prewrite serve gates' own-dead
    /// fall-through (the own-live gate above them serves the pre-write).
    /// </summary>
    /// <param name="cached">The cached playlist file info (stream.m3u8).</param>
    /// <param name="itemId">The song's item ID.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    /// <returns>The valid playlist (with the verdict's read content on the read
    /// row), or null when invalidated or vanished.</returns>
    private Task<ValidatedHlsCache?> ValidateVideoAudioCacheAsync(FileInfo cached, string itemId, long artModifiedTicks, bool probeOwnGenerationLiveOrRegistering)
        => ValidateHlsCacheAsync(cached, itemId, artModifiedTicks, _activeVideoAudioEncodes, "VideoAudio", endlistDebrisReason: null, probeOwnGenerationLiveOrRegistering);

    /// <summary>
    /// The verdict's output (JF-677 shape over the JF-676 core): the VALID
    /// playlist (always the caller's original <c>cached</c> FileInfo instance)
    /// plus, exactly when the verdict READ the file (the own-dead ENDLIST
    /// row), the content it read, threaded to the serve so a validated
    /// warm-cache serve performs ONE full read of the playlist instead of two
    /// (JF-677). <paramref name="Content"/> is null on the own-live row: the
    /// verdict answered liveness without reading, and the serve must read the
    /// growing playlist fresh. The serve re-checks existence before reusing
    /// threaded content (<see cref="ResolveServeContentAsync"/>, the JF-499 W3
    /// vanish guard).
    /// </summary>
    /// <param name="Playlist">The valid cached playlist file (the caller's original instance).</param>
    /// <param name="Content">The verdict's read of the playlist when it read one; null on the own-live row.</param>
    internal sealed record ValidatedHlsCache(FileInfo Playlist, string? Content);

    /// <summary>
    /// The live-aware warm-cache probe's output (JF-782 leg 1): the probed
    /// playlist (null on a miss) plus the liveness answer that resolved it.
    /// The answer is the SINGLE EVALUATION the request's probe made; the
    /// paired debris verdict threads it back in so ONE decision shape governs
    /// both the directory authority (the resolver consumed the answer at
    /// probe time) and the unread acceptance (the verdict conjuncts it at
    /// verdict time), which is what closes the probe-vs-verdict straddle: a
    /// mark landing between the two reads can no longer turn the verdict's
    /// fresh read into an unread acceptance of a hit the static-order probe
    /// resolved. Consumers must treat the answer as the PROBE-TIME snapshot
    /// it is (the verdict's own fresh read stays deliberate).
    /// </summary>
    /// <param name="Playlist">The probed playlist file, null on a miss.</param>
    /// <param name="OwnGenerationLiveOrRegistering">The wrapper's liveness
    /// answer at probe time (the same predicate the debris verdict family
    /// reads).</param>
    internal sealed record HlsCacheProbe(FileInfo? Playlist, bool OwnGenerationLiveOrRegistering);

    /// <summary>
    /// The ONE ticks-scoped debris verdict core (JF-676), reached only through
    /// the three registry+label pairings (the two episode/song ones above and
    /// the audiobook one below) so a verdict can never read one path's
    /// registry while deleting another's directory (the JF-668
    /// mispairing-unrepresentable move). Decision table: caller's own
    /// (key, art-tick) generation live or mid-registration AND the paired
    /// probe's liveness answer agrees, valid (ffmpeg's growing playlist; the
    /// mid-registration entry may be this very generation, and the
    /// mid-registration acceptance itself is NARROWED to registrations not
    /// provably foreign for the caller's ticks, JF-782 leg 2: the canonical
    /// account moved to <see cref="ActiveEncodeGenerations.ReadTickLiveness"/>
    /// and <see cref="VideoAudioCache.HasForeignHlsDirectoryRegistration"/>,
    /// with <see cref="OwnTicksGenerationLiveOrRegistering"/> still holding
    /// the probe-side read's account); the DISAGREEMENT cell (probe read
    /// false, verdict fresh read true: a mark landed between the two reads,
    /// JF-782 leg 1) and the narrowing cell READ the hit and judge it instead
    /// of accepting unread, so the static order's first hit (possibly the
    /// other root's undeletable stale shadow) can never serve unread off a
    /// liveness the probe never saw; otherwise ENDLIST present and not judged
    /// stale by the optional <paramref name="endlistDebrisReason"/> hook,
    /// valid (a completed encode, the JF-675 fall-through serve); otherwise
    /// DEBRIS of a killed or incomplete generation: warn, delete ONLY the
    /// caller's own generation directory, the one this playlist was served
    /// from
    /// (<see cref="VideoAudioCache.CleanupHlsGenerationAt"/> since JF-537.1, so a
    /// transient-root debris verdict cannot destroy a same-key cache-root
    /// generation; safe to fire while
    /// a foreign-ticks sibling writes, whose directory this delete cannot
    /// name; since JF-782 the delete fires only on the NotLive state, so the
    /// two judged-not-accepted cells never delete), return null so the caller
    /// re-encodes. A playlist that vanishes
    /// mid-validation (a concurrent verdict or cleanup deleted the directory
    /// between the caller's cache read and the read here) is ALSO null: there
    /// is nothing left to validate and the lock path re-encodes, which closes
    /// the in-lock read race the key-wide-Cleanup era left as a bare 500.
    /// BOUNDED WINDOW on the own-live row (the deliberate trade): between
    /// ffmpeg's final write and the monitor's clear (one poll interval), a
    /// completed-but-STALE ENDLIST playlist (the audiobook undercount shape)
    /// reads own-live and serves once; the next request after the clear
    /// re-encodes it. The pre-JF-676 ungated validator cleaned that playlist
    /// immediately but could wipe LIVE sibling directories mid-write; one
    /// degraded serve of a bounded window is the price of never deleting a
    /// live generation's directory. Since JF-677 the valid rows return the
    /// read content with the file (see <see cref="ValidatedHlsCache"/>) so the
    /// serve does not re-read it; the verdict's read stays the ONE read, and
    /// it is still the read that detects a vanish (the vanished-read-invalid
    /// semantics are unchanged).
    /// </summary>
    /// <param name="cached">The cached playlist file info (stream.m3u8).</param>
    /// <param name="cacheKey">The encode's cache key for logging and cleanup.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    /// <param name="activeEncodes">The path's active-encode registry (the verdict reads own-ticks liveness from it).</param>
    /// <param name="logLabel">Path label for the verdict's logs ("Episode" / "VideoAudio" / "Audiobook").</param>
    /// <param name="endlistDebrisReason">Optional ENDLIST-content hook (the
    /// audiobook path's undercount and, since JF-784, timeline-identity
    /// checks): given the playlist content, returns the
    /// invalidation reason when a COMPLETED-looking playlist is still debris,
    /// null when valid. Null hook (episode/song/variants): every ENDLIST
    /// playlist is valid.</param>
    /// <param name="probeOwnGenerationLiveOrRegistering">The liveness answer
    /// the wrapper's probe ALREADY computed when it resolved this hit
    /// (REQUIRED, the JF-686 no-default shape: a future call site must decide
    /// it, never silently probe behind a default). The own-live acceptance is
    /// the conjunction of this answer and the verdict's own narrowed read, so
    /// one unread acceptance can never ride a liveness the resolving probe
    /// never saw.</param>
    /// <returns>The valid playlist (with the verdict's read content on the
    /// read row, <see cref="ValidatedHlsCache"/>), or null when invalidated or vanished.</returns>
    private async Task<ValidatedHlsCache?> ValidateHlsCacheAsync(
        FileInfo cached,
        string cacheKey,
        long artModifiedTicks,
        ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes,
        string logLabel,
        Func<string, string?>? endlistDebrisReason,
        bool probeOwnGenerationLiveOrRegistering)
    {
        // JF-782 legs 1+2, the acceptance gate: the own-live row now needs the
        // PROBE's liveness answer AND the verdict's own NARROWED read to agree.
        // The probe answer (threaded from the wrapper that resolved this hit)
        // closes the straddle: a mark landing between the two reads flips the
        // fresh read true while the probe already resolved the static order,
        // whose first hit can be the undeletable other-root shadow, so that
        // cell must READ and judge the hit, never accept it unread. The
        // narrowing closes the foreign-ticks mid-registration sub-window: the
        // Registering arm accepts only while the per-key registration is not
        // provably foreign for the caller's ticks (sound because every encode
        // path registers its resolved dir before marking, so the zero-slot
        // window's per-key registration names the marking encode's dir:
        // contained means same-ticks, foreign means an art change
        // mid-flight).
        TickLiveness ownTicks = activeEncodes.TryGetValue(cacheKey, out ActiveEncodeGenerations? generations)
            ? generations.ReadTickLiveness(artModifiedTicks)
            : TickLiveness.NotLive;
        bool ownLiveOrRegistering = ownTicks == TickLiveness.Live
            || (ownTicks == TickLiveness.Registering
                && !_cache.HasForeignHlsDirectoryRegistration(cacheKey, artModifiedTicks));
        if (ownLiveOrRegistering && probeOwnGenerationLiveOrRegistering)
        {
            _logger.LogDebug("{LogLabel} HLS: serving live ffmpeg playlist (encoding in progress) for item {ItemId}", logLabel, cacheKey);
            return new ValidatedHlsCache(cached, Content: null);
        }

        // The JF-676 delete gate, unchanged in reach: the debris cleanup fires
        // only when the entry's own-ticks state is NotLive. The two cells that
        // read instead of accepting (the straddle and the foreign-registration
        // narrowing) both have a live-or-registering entry behind them, and
        // the just-marked encode's target dir is exactly what an own-live
        // cleanup could destroy (the JF-676 race this gate exists to prevent);
        // those cells judge and fall through, leaving the debris for the lock
        // scope's sweeps.
        bool cleanupAllowed = ownTicks == TickLiveness.NotLive;

#pragma warning disable CA3003 // paths derived from GUID-validated cacheKey
        string content;
        try
        {
            content = await ReadPlaylistContentAsync(cached.FullName).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHlsVanishException(ex))
        {
            _logger.LogDebug(
                "{LogLabel} HLS: cached playlist vanished for item {ItemId} before validation (invalidated by a concurrent re-encode?), re-encoding",
                logLabel, cacheKey);
            return null;
        }

        if (!content.Contains(HlsEndList, StringComparison.Ordinal))
        {
            if (cleanupAllowed)
            {
                _logger.LogWarning(
                    "{LogLabel} HLS cache invalidated for {ItemId}: playlist has no ENDLIST and its own art-tick generation is not live (interrupted encode?), re-encoding",
                    logLabel, cacheKey);
                // Dir-scoped delete (JF-537.1): the generation directory this playlist
                // was SERVED FROM (cache root or transient root), never its same-key
                // sibling in the other root.
                _cache.CleanupHlsGenerationAt(Path.GetDirectoryName(cached.FullName)!);
            }
            else
            {
                _logger.LogWarning(
                    "{LogLabel} HLS cache judged invalid for {ItemId}: playlist has no ENDLIST and was NOT accepted unread (probe/verdict liveness disagreement, or a foreign-ticks registration in the mid-registration window); own generation live-or-registering, so the directory is not deleted and the request falls through (JF-782)",
                    logLabel, cacheKey);
            }

            return null;
        }

        if (endlistDebrisReason != null)
        {
            string? reason = endlistDebrisReason(content);
            if (reason != null)
            {
                if (cleanupAllowed)
                {
                    _logger.LogWarning(
                        "{LogLabel} HLS cache invalidated for {ItemId}: {Reason}, re-encoding",
                        logLabel, cacheKey, reason);
                    _cache.CleanupHlsGenerationAt(Path.GetDirectoryName(cached.FullName)!);
                }
                else
                {
                    // Review F5: the judged-but-not-deleted cell must be
                    // distinguishable from the cleaned one in the logs (the
                    // JF-680/JF-681 triage-by-log contract).
                    _logger.LogWarning(
                        "{LogLabel} HLS cache judged invalid for {ItemId}: {Reason}; own generation live-or-registering (probe/verdict liveness disagreement, or a foreign-ticks registration in the mid-registration window), so the directory is not deleted and the request falls through (JF-782)",
                        logLabel, cacheKey, reason);
                }

                return null;
            }
        }
#pragma warning restore CA3003

        return new ValidatedHlsCache(cached, content);
    }

    /// <summary>
    /// Shared wrapper of the verdict-following serve rows (JF-499 W3; the
    /// three fast paths since then, path-generic since JF-676 when the song
    /// path's own debris verdict made it the fourth caller, and since JF-678
    /// also the in-lock double-check rows and the audiobook verdict rows):
    /// validate a cached playlist through the path's own verdict
    /// pairing and serve it, translating the vanish race into a null return.
    /// A concurrent lock-holder can invalidate the cache between the caller's
    /// <c>GetCachedHlsPlaylist</c> FileInfo read and the content reads here (a
    /// debris verdict deletes the caller's own art-tick generation directory
    /// under the per-item lock, JF-676; on Windows hosts the dir-gone state
    /// maps to <see cref="DirectoryNotFoundException"/>, on Linux to
    /// <see cref="FileNotFoundException"/>). A vanished file must fall through
    /// to the re-encode path instead of surfacing a 500 that only self-heals
    /// on the Echo's playlist retry. Null also covers an INVALID cache
    /// (interrupted-encode debris, cleaned up by validation). Since JF-677 the
    /// vanish translation still fires when the serve reuses the verdict's
    /// threaded content: <see cref="ResolveServeContentAsync"/> re-probes the
    /// file's existence (the segments live in the same directory as the
    /// playlist, so a vanished playlist is a dead serve) and throws the same
    /// exception the fresh read would have. Since JF-678 this composite is
    /// also the vanish translation of the IN-LOCK verdict+serve rows and the
    /// two audiobook verdict rows; every caller must have a next step its
    /// null can fall through to, and the in-lock rows additionally pass
    /// through <see cref="GuardInLockVanishFallThrough"/> first. The ONE
    /// authoritative account of which rows translate, which stay loud, and
    /// why is the coverage boundary on
    /// <see cref="ResolveServeContentAsync"/>.
    /// </summary>
    /// <param name="cacheKey">The encode's cache key (vanish-race logging).</param>
    /// <param name="logLabel">Log prefix ("VideoAudio episode HLS" / its AUDIO
    /// and audio-speed twins / "VideoAudio HLS" / "VideoAudio audiobook HLS").</param>
    /// <param name="validate">The path's verdict pairing over
    /// <see cref="ValidateHlsCacheAsync"/> (a closure binding the cached
    /// FileInfo, key, and the caller's art ticks).</param>
    /// <param name="serve">Serves the validated cache (the verdict's FileInfo
    /// plus its read content when there was one): the remux keeps its
    /// pre-written branch + threaded serve on
    /// <see cref="ServeEpisodeWarmCacheAsync"/>, the variants and the song
    /// path serve via <see cref="ServePlaylistWithTokenAsync"/>, the audiobook
    /// rows via <see cref="ServeAudiobookPlaylistAsync"/>.</param>
    /// <returns>The served response, or null when the cache is invalid or vanished.</returns>
    private async Task<ActionResult?> TryServeValidatedHlsCacheAsync(
        string cacheKey,
        string logLabel,
        Func<Task<ValidatedHlsCache?>> validate,
        Func<ValidatedHlsCache, Task<ActionResult>> serve)
    {
        try
        {
            ValidatedHlsCache? valid = await validate().ConfigureAwait(false);
            if (valid == null)
            {
                return null;
            }

            return await serve(valid).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsHlsVanishException(ex))
        {
            // "or became unreadable": the probe's File.Exists cannot
            // distinguish an ACL revocation from a delete (the conflation
            // decision lives on <see cref="ProbePlaylistExists"/>), so this
            // wording must stay honest for both trigger shapes.
            _logger.LogDebug(
                "{LogLabel}: cached playlist vanished or became unreadable for item {ItemId} (invalidated by a concurrent re-encode?), re-encoding",
                logLabel, cacheKey);
            return null;
        }
    }

    /// <summary>
    /// The vanish family's ONE exception vocabulary (JF-678 named it): the
    /// playlist (or its directory) is gone, whether the fresh read threw it or
    /// <see cref="ProbePlaylistExists"/> translated it. The vanish
    /// translation catches exactly this set; the audiobook content branches'
    /// fallback catches exclude exactly this set (where the caller confirmed
    /// the path) so a vanish can propagate to the translation while every
    /// other failure keeps the PhysicalFile degrade.
    /// </summary>
    private static bool IsHlsVanishException(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException;

    /// <summary>
    /// The in-lock fall-through's liveness guard (JF-678 rework, review F1):
    /// called at the four in-lock verdict+serve rows after
    /// <see cref="TryServeValidatedHlsCacheAsync"/> answered null, BEFORE
    /// falling through to the encode branch. No live generation of this
    /// (key, ticks): the legitimate fall-through (debris verdict, or a
    /// completed cache evicted between verdict and serve), so return. STILL
    /// LIVE: the vanish is a pin breach, and falling through would start a
    /// SECOND ffmpeg against the live writer's directory (the episode/variant
    /// paths' per-file debris sweep would first delete the live writer's
    /// files under it), so fail LOUD: a breach-named warning, then the
    /// action-time <see cref="FileNotFoundException"/> (the policy account
    /// lives on the coverage boundary of <see cref="ResolveServeContentAsync"/>).
    /// LOAD-BEARING INVARIANT: a null VERDICT can never arrive here with the
    /// own-ticks slot LIVE, because the debris rule's acceptance needs the
    /// entry live-or-registering and nothing can register THIS (key, ticks)
    /// generation while this scope holds its per-item lock, so the guard
    /// reads a vanish, never a false breach. JF-782 widened what a null
    /// verdict can arrive WITH: the judged-not-accepted cells (the
    /// probe/verdict straddle and the foreign-registration narrowing) null
    /// while the entry is LIVE or MID-REGISTRATION; the mid-registration
    /// holder belongs to a FOREIGN-ticks mark under a different (key, ticks)
    /// lock this scope never held, and the strict reader correctly treats it
    /// as the legitimate fall-through it is (no own-ticks slot is live). That
    /// placement clause is enforced by CONVENTION, not machinery: it holds
    /// because every <see cref="MarkEncodeActive"/> site sits inside its
    /// (key, ticks) lock scope, and a future registration site outside the
    /// lock would make this guard misclassify legitimate vanishes as
    /// breaches, so keep the placement when adding encode paths.
    /// </summary>
    /// <param name="activeEncodes">The path's active-encode registry.</param>
    /// <param name="cacheKey">The encode's cache key.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own generation).</param>
    /// <param name="playlistPath">The vanished playlist's path (carried on the exception).</param>
    /// <param name="logLabel">Path label for the breach warning.</param>
    private void GuardInLockVanishFallThrough(
        ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes,
        string cacheKey,
        long artModifiedTicks,
        string playlistPath,
        string logLabel)
    {
        if (!OwnTicksGenerationLive(activeEncodes, cacheKey, artModifiedTicks))
        {
            return;
        }

        _logger.LogWarning(
            "{LogLabel}: playlist of a LIVE encode vanished between validation and serve for item {ItemId} (pin breach or external delete?); failing the request loud instead of starting a second encode against the live writer",
            logLabel, cacheKey);
        throw new FileNotFoundException("Playlist of a live pinned encode vanished between validation and serve", playlistPath);
    }

    /// <summary>
    /// Serve the episode encode's pre-written full listing (JF-531), WINDOWED to
    /// the encoded region while the encode runs (JF-778). Called on the two
    /// warm-cache serve paths, which gate on own-ticks generation liveness since
    /// JF-675 (<see cref="OwnTicksGenerationLive"/>), and on the first serve,
    /// which is ungated by construction (the caller just wrote the prewrite under
    /// the lock). Returns null when the file is absent (encode with unknown
    /// runtime, or a race before the prewrite landed) so the caller falls back to
    /// serving the live playlist, the pre-JF-531 behavior, rather than failing
    /// the play.
    /// WHY THE WINDOW (JF-778, live device evidence 2026-10-05): the Echo's
    /// ExoPlayer resolves a no-ENDLIST playlist's default start at playlist end
    /// minus 3x TARGETDURATION (media3's live fallback; the incident's first
    /// fetch was seg_0353 of a 356-entry listing, = 1420.25s - 12s exactly), so
    /// serving the FULL listing during the encode makes the player's first fetch
    /// land on the un-encoded tail: 404 x3 within ~1-2s, playback dead before
    /// the first frame (the JF-625 song prefetch death is the same shape). The
    /// served listing is therefore truncated to a time-anchored growing window:
    /// max(<see cref="EpisodePrewriteWindowFloorSegments"/>, min(head + 1,
    /// elapsedSegments + <see cref="EpisodePrewriteWindowLeadSegments"/>))
    /// entries, elapsed anchored on the prewrite file's LastWriteTimeUtc
    /// (written once per encode inside the JF-428 pin window, so the anchor is
    /// encode start to within milliseconds). The window starts at the floor (the
    /// default start lands on segment 0), grows one entry per segment-seconds
    /// (1x playback, so the player trails the edge), and the head cap keeps the
    /// listing inside the encoded region for stalled encodes. The FILE stays the
    /// full listing: the post-encode ENDLIST serve and the resume slicing below
    /// keep consuming it. DELIBERATE COST: the full-runtime seekbar during the
    /// encode window is given up (unattainable: any no-ENDLIST listing spanning
    /// the runtime puts the default start at its end); it returns when the encode
    /// completes and the cache-hit path serves ffmpeg's ENDLIST playlist.
    /// A positive <paramref name="startTicks"/> slices the WINDOWED listing at
    /// the resume position (JF-499 W2), but only inside the HONOR BAND: a
    /// sliced no-ENDLIST listing joins at its own live-edge default start
    /// (slice end minus 3x TARGETDURATION), so the position is honored only
    /// while the window's edge runs at most LEAD entries past it. Outside the
    /// band the offset is dropped with a log and playback starts at 0 (the
    /// JF-686 still-growing rule; the old sliced-full-listing serve had the
    /// same live-edge death shape on device); the next warm serve (ENDLIST
    /// cache hit) resumes at the position exactly.
    /// EXTRACTION TRIGGER (the JF-637/JF-679 rule: extract at the second copy,
    /// not before): the window block below is deliberately inline with
    /// episode-calibrated constants. If the song/audiobook prewrite twins adopt
    /// windowing (the device-round sentinel in the task file), extract the
    /// head/elapsed/floor/lead computation into one parameterized helper instead
    /// of copying it, and thread a real encode-start anchor through the
    /// registries in place of the mtime stat (which also closes the anchor's
    /// known hazard: an external forward-touch of the prewrite's mtime
    /// mid-encode collapses the window for one poll, and the player sees a
    /// playlist regression; the fs stat has no defense against a touch).
    /// PROBE ORDER (JF-774 finding 2, consolidated into the cache's
    /// liveness-aware resolver by JF-775): while the registration names one of
    /// this generation's own directories (the transient one for an oversize
    /// encode), that directory is the ONLY prewrite source: a same-key
    /// playlist-full.m3u8 in the other root is necessarily stale debris (the
    /// undeletable class: the per-file debris backstop removes stream.m3u8 and
    /// segments but not playlist-full.m3u8), and serving it would 401 every
    /// segment fetch it names for the whole encode window through its expired
    /// JF-309 token. CALLER PRECONDITION: both call sites run inside
    /// live-gated scopes (the warm row's strict OwnTicksGenerationLive gate
    /// and the first serve's just-marked encode), and the probe pins the
    /// resolver's liveness answer true instead of re-reading, so a monitor
    /// clear landing between the caller's gate and the probe cannot flip the
    /// source to the static order's stale cache-root leg (code-review F1).
    /// When the live prewrite is itself absent (the pre-prewrite race, or the
    /// no-runtime skip) the probe returns null so the caller falls back to the
    /// LIVE playlist, never to the other root's stale listing. The registration
    /// is the encode's own resolved directory (registered at encode start under
    /// the same lock) and the resolver's containment rule consumes it only when
    /// it IS one of this caller's generation dirs, so a foreign-ticks
    /// registration can never redirect the probe.
    /// </summary>
    /// <param name="itemId">GUID-validated episode item ID.</param>
    /// <param name="artModifiedTicks">Art ticks of the item's HLS cache directory.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 serves unsliced).</param>
    /// <returns>The playlist response, or null when no pre-written listing exists.</returns>
    private async Task<ActionResult?> TryServePrewrittenEpisodePlaylist(string itemId, long artModifiedTicks, long startTicks)
    {
#pragma warning disable CA3003 // path derived from GUID-validated itemId
        // Root-agnostic (JF-537.1): the prewrite lives in the CACHE root's
        // generation dir for a cacheable encode, the TRANSIENT root's for an
        // oversize one. The probe order is the cache's liveness-aware resolver
        // (JF-775 collapsing JF-774 finding 2's hand-rolled override): while
        // the registration names one of this generation's dirs, that dir is
        // the ONLY prewrite source; otherwise the static root-preference order
        // (cache root first). The liveness answer is pinned TRUE here, NOT
        // re-read: both call sites run inside live-gated scopes (the warm
        // row's strict gate, and the first serve's just-marked encode), and a
        // monitor CLEAR landing between the caller's gate and a fresh read
        // here would flip the source to the static order, whose cache-root
        // first leg is exactly the stale shadow the exclusive rule exists to
        // skip (code-review F1, the CLEAR-direction twin of the JF-782
        // straddle; the mark-direction straddle is pinned at the verdict
        // since JF-782, so this literal's protection family is under test
        // discipline). The caller owns the liveness question for this probe.
        string? prewrittenPath = null;
        foreach (string dirPath in _cache.ResolveHlsGenerationDirPaths(itemId, artModifiedTicks, ownGenerationLiveOrRegistering: true))
        {
            string candidate = Path.Combine(dirPath, PrewrittenPlaylistFileName);
            if (System.IO.File.Exists(candidate))
            {
                prewrittenPath = candidate;
                break;
            }
        }

        if (prewrittenPath == null)
        {
            return null;
        }

        // ONE read (JF-677/JF-680 read-count funnels): the window computation and
        // the serve both consume this content; the serve receives it as
        // preloadedContent and never re-reads. A vanish here propagates like the
        // serve's own read always did (the JF-499 W3 contract is unchanged).
        string content = await ReadPlaylistContentAsync(prewrittenPath).ConfigureAwait(false);

        // The window (JF-778): head from the prewrite's OWN directory (the
        // listing's segment URLs point there, root-agnostic), elapsed from the
        // prewrite's mtime (the per-encode anchor). Degenerate mtimes degrade
        // BOUNDED, never to the full listing: a future-dated mtime zeroes the
        // growth term (the lead plus the floor remains), and the vanished-file
        // 1601 sentinel clamps at int.MaxValue - lead so the growth term cannot
        // wrap negative (code-review F2: the accidental floor rescue of an
        // unchecked wrap is not a guarantee).
        int head = GetHighestSegmentNumber(Path.GetDirectoryName(prewrittenPath));
        TimeSpan prewriteAge = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(prewrittenPath);
        long elapsedSegmentsRaw = prewriteAge > TimeSpan.Zero
            ? (long)(prewriteAge.TotalSeconds / EpisodeHlsSegmentSeconds)
            : 0;
        int elapsedSegments = (int)Math.Min(elapsedSegmentsRaw, int.MaxValue - EpisodePrewriteWindowLeadSegments);
        int windowSegments = Math.Max(
            EpisodePrewriteWindowFloorSegments,
            Math.Min(head + 1, elapsedSegments + EpisodePrewriteWindowLeadSegments));

        int totalSegments = CountSegmentsInPlaylist(content);
        bool windowed = windowSegments < totalSegments;
        string serveContent = windowed
            ? Alexa.Playback.AudiobookPlaylistBuilder.TruncateToFirstSegments(content, windowSegments)
            : content;

        long effectiveStartTicks = startTicks;
        if (startTicks > 0)
        {
            int startSegment = Alexa.Playback.AudiobookPlaylistBuilder.ResolveStartSegment(
                content, startTicks, EpisodeHlsSegmentSeconds);
            // HONOR BAND (code-review F1): a sliced no-ENDLIST listing joins at
            // its live-edge default start (slice end minus 3x TARGETDURATION),
            // so the resume position is actually honored only while the window's
            // edge runs no more than LEAD entries past it; outside that band a
            // slice would join near the window edge, minutes off the requested
            // position, so the offset drops to 0 instead (the JF-686 rule).
            bool resumeHonored = startSegment < windowSegments
                && windowSegments - startSegment <= EpisodePrewriteWindowLeadSegments;
            if (!resumeHonored)
            {
                _logger.LogInformation(
                    "VideoAudio episode HLS: cold-cache resume for item {ItemId} (startTicks={StartTicks}, start segment {StartSegment}) is outside the encode window's honor band ({WindowSegments} of {TotalSegments} entries, edge within {Lead} of the position required); dropping the resume and serving from the beginning (JF-778, the JF-686 still-growing rule)",
                    itemId, startTicks, startSegment, windowSegments, totalSegments, EpisodePrewriteWindowLeadSegments);
                effectiveStartTicks = 0;
            }
        }

        _logger.LogDebug(
            "VideoAudio episode HLS: serving pre-written full listing for item {ItemId} (encode in progress, JF-531; windowed to the encoded region while the encode runs, JF-778)",
            itemId);
        if (windowed)
        {
            _logger.LogDebug(
                "VideoAudio episode HLS: pre-write listing WINDOWED to {WindowSegments} of {TotalSegments} entries for item {ItemId} (encode head segment {HeadSegment}, prewrite age {PrewriteAgeMs:F0}ms; the live-edge default start would land on the un-encoded tail, JF-778)",
                windowSegments, totalSegments, itemId, head, prewriteAge.TotalMilliseconds);
        }

        return await ServeEpisodePlaylistAsync(prewrittenPath, effectiveStartTicks, serveContent).ConfigureAwait(false);
#pragma warning restore CA3003
    }

    /// <summary>
    /// The ONE episode warm-cache serve row (JF-679), extracted from the two
    /// copies the JF-675 and JF-677 rounds each had to edit twice: serve the
    /// PRE-WRITTEN full listing when the caller's OWN art-tick generation is
    /// live (JF-531/JF-675, <see cref="OwnTicksGenerationLive"/>), else serve
    /// the validated playlist with the verdict's threaded read (JF-677).
    /// Called from both warm-cache sites of <see cref="StreamHlsEpisodeCore"/>
    /// (the fast path's serve closure inside
    /// <see cref="TryServeValidatedHlsCacheAsync"/> and the in-lock
    /// concurrent-serve double check); the first-serve site stays separate
    /// because it is ungated by construction (the caller just wrote the
    /// prewrite under the lock) and serves ffmpeg's partial, not a validated
    /// cache. On the own-live row the verdict read nothing
    /// (<see cref="ValidatedHlsCache.Content"/> is null), the prewrite serve
    /// reads the live listing fresh, and when no prewrite serves the row
    /// serves the verdict's file, which IS the live encode's REGISTERED
    /// directory's playlist: JF-774 review finding 3 delivered that as a
    /// redirect block here (the registered dir over the verdict's first-hit
    /// file, which under the two-root split can be the other root's stale
    /// file); JF-775 collapsed the dir authority into the liveness-aware
    /// probe both warm sites consumed the verdict's file FROM, so every
    /// liveness-accepted row's <see cref="ValidatedHlsCache.Playlist"/> is
    /// already the registered dir's file and the redirect's target always
    /// equals it (the block deleted; the resolver's containment rule carries
    /// the foreign-ticks protection). On the own-dead ENDLIST row the
    /// verdict's read is threaded through to
    /// <see cref="ServeEpisodePlaylistAsync"/> (one full read per validated
    /// serve). A third row exists on the fast path: MID-REGISTRATION, where
    /// the verdict answers valid on the conservative
    /// OwnTicksGenerationLiveOrRegistering read (Content null) while this
    /// helper's strict gate is false, so no prewrite serves and the
    /// fall-through reads the live playlist fresh, which since JF-775 is the
    /// REGISTERED dir's playlist for the same probe reason (the mid-registration
    /// residual the JF-774 gate marker filed, closed at the probe: the ordered
    /// first-hit shadow is never probed while the caller's own generation is
    /// live-or-registering); do not reconcile the two-row story by hoisting
    /// the gate behind the verdict, that would change the mid-registration
    /// window's serve. ORDER CONTRACT,
    /// load-bearing for the vanish pins (JF-499 W3):
    /// the per-site log fires BETWEEN the prewrite branch and the serve, so a
    /// file deleted at that log leaves exactly the vanish-at-serve state the
    /// <see cref="ResolveServeContentAsync"/> probe must catch; the per-site
    /// wording rides <paramref name="logServe"/> (the VariantHlsSpec delegate
    /// pattern, preserving each site's exact message) because the JF-677
    /// in-lock pins discriminate the serving branch by these exact log lines.
    /// </summary>
    /// <param name="valid">The verdict's output: the validated playlist file plus its read when it read one.</param>
    /// <param name="itemId">GUID-validated episode item ID.</param>
    /// <param name="artModifiedTicks">Art ticks of the item's HLS cache directory.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 serves unsliced).</param>
    /// <param name="logServe">The caller's serve log line (exact per-site wording; fired only on a row's fall-through serve, never on the prewrite row).</param>
    /// <returns>The playlist response: the pre-written listing on the own-live row, else the validated playlist (resume-sliced when <paramref name="startTicks"/> is positive).</returns>
    private async Task<ActionResult> ServeEpisodeWarmCacheAsync(
        ValidatedHlsCache valid,
        string itemId,
        long artModifiedTicks,
        long startTicks,
        Action logServe)
    {
        if (OwnTicksGenerationLive(_activeEpisodeEncodes, itemId, artModifiedTicks))
        {
            ActionResult? prewritten = await TryServePrewrittenEpisodePlaylist(itemId, artModifiedTicks, startTicks).ConfigureAwait(false);
            if (prewritten != null)
            {
                return prewritten;
            }
        }

        // Both fall-through rows serve the verdict's file; on liveness-accepted
        // rows the probe already resolved it to the live-or-registering
        // encode's REGISTERED dir (JF-775). On the mid-registration row
        // valid.Content is null, so the serve reads that file fresh; a vanish
        // there falls the request through to the lock-scope concurrent serve,
        // the hold the resolver chose over serving the other root's stale
        // listing.
        logServe();
#pragma warning disable CA3003 // path derived from GUID-validated itemId
        return await ServeEpisodePlaylistAsync(valid.Playlist.FullName, startTicks, valid.Content).ConfigureAwait(false);
#pragma warning restore CA3003
    }

    /// <summary>
    /// The no-runtime half of every prewrite site (JF-536): an item without a runtime
    /// cannot have an honest full listing, so fall back to serving ffmpeg's live
    /// playlist (the pre-prewrite behavior) and remove any stale listing so the
    /// active-encode guards can never serve one a previous encode wrote.
    /// </summary>
    /// <param name="prewrittenPath">The pre-written listing path inside this encode's directory.</param>
    /// <param name="itemId">Item ID for the log.</param>
    /// <param name="logLabel">Path label for the log (e.g. "VideoAudio episode HLS").</param>
    private void SkipPrewrittenListing(string prewrittenPath, string itemId, string logLabel)
    {
        TryDelete(prewrittenPath);
        _logger.LogWarning(
            "{LogLabel}: item {ItemId} has no runtime, cannot pre-write the full listing; serving ffmpeg's live playlist instead",
            logLabel, itemId);
    }

    /// <summary>
    /// Serve the single-item encode's pre-written full listing (JF-536), the song
    /// path's twin of <see cref="TryServePrewrittenEpisodePlaylist"/>: called on
    /// the two warm-cache serve paths (gated on own-ticks generation liveness
    /// since JF-675, like the twin) and the ungated first serve, returning null
    /// when the
    /// file is absent so the caller falls back to ffmpeg's live playlist (the
    /// pre-JF-536 behavior) instead of failing the play. The serve honors the
    /// caller's resume position (JF-686, the episode twin's shape): a positive
    /// <paramref name="startTicks"/> slices the listing via
    /// <see cref="ServeVideoAudioPlaylistAsync"/>, 0 serves it as-is.
    /// </summary>
    /// <param name="itemId">GUID-validated item ID.</param>
    /// <param name="artModifiedTicks">Art ticks of the item's HLS cache directory.</param>
    /// <param name="overrideToken">Chapter-scoped token when serving a single-chapter
    /// audiobook redirected from <see cref="StreamHlsAudiobook"/>; null uses the
    /// request's own token.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 serves unsliced). REQUIRED
    /// (JF-686 review F4): the core passes its own value explicitly, so a future call site
    /// must decide the position rather than silently dropping it behind a default.</param>
    /// <returns>The playlist response, or null when no pre-written listing exists.</returns>
    private async Task<ActionResult?> TryServePrewrittenVideoAudioPlaylist(string itemId, long artModifiedTicks, string? overrideToken, long startTicks)
    {
#pragma warning disable CA3003 // path derived from GUID-validated itemId
        string prewrittenPath = Path.Combine(
            _cache.GetHlsDirectoryPath(itemId, artModifiedTicks),
            PrewrittenPlaylistFileName);
        if (!System.IO.File.Exists(prewrittenPath))
        {
            return null;
        }
#pragma warning restore CA3003

        _logger.LogDebug(
            "VideoAudio HLS: serving pre-written full listing for item {ItemId} (encode in progress, JF-536)",
            itemId);
        return await ServeVideoAudioPlaylistAsync(prewrittenPath, overrideToken, startTicks).ConfigureAwait(false);
    }

    /// <summary>
    /// The song path's own-live prewrite serve gate (JF-679), the twin of the
    /// episode row on <see cref="ServeEpisodeWarmCacheAsync"/> and the ONE
    /// copy of the gate+prewrite pair that previously sat byte-identical on
    /// both <see cref="StreamHlsVideoAudioCore"/> warm-cache sites (the fast
    /// path and the in-lock double check; the JF-675 round had to edit both).
    /// When the caller's OWN art-tick generation is live
    /// (<see cref="OwnTicksGenerationLive"/>, JF-675), serve the PRE-WRITTEN
    /// full listing (<see cref="TryServePrewrittenVideoAudioPlaylist"/>,
    /// JF-536). Null on the own-dead row (the prewrite option is already
    /// closed by this gate returning null; the verdict below decides
    /// debris-vs-ENDLIST) and when the listing file is absent (the race
    /// before the prewrite lands; the caller falls through to the live
    /// playlist). Deliberately NOT the episode helper: this path's gate sits
    /// BEFORE its verdict at both sites, while the episode fast path's gate
    /// runs inside the <see cref="TryServeValidatedHlsCacheAsync"/> serve
    /// closure. That placement is load-bearing: this gate asks the STRICT
    /// predicate (the canonical strict-live vs mid-registration account lives
    /// on <see cref="OwnTicksGenerationLive"/> and
    /// <see cref="OwnTicksGenerationLiveOrRegistering"/>), and hoisting it
    /// behind the verdict would let the mid-registration window serve the
    /// prewrite where today it serves the live playlist. The twins share the
    /// predicate, not the row.
    /// </summary>
    /// <param name="itemId">GUID-validated item ID.</param>
    /// <param name="artModifiedTicks">Art ticks of the item's HLS cache directory.</param>
    /// <param name="overrideToken">Chapter-scoped token when serving a single-chapter
    /// audiobook redirected from <see cref="StreamHlsAudiobook"/>; null uses the
    /// request's own token.</param>
    /// <param name="startTicks">Resume position in .NET ticks passed to the serve (0 serves unsliced). REQUIRED
    /// (JF-686 review F4, same shape as the prewrite twin): the core passes its own value explicitly.</param>
    /// <returns>The pre-written listing response on the own-live row, else null.</returns>
    private async Task<ActionResult?> TryServeOwnLiveVideoAudioPrewriteAsync(string itemId, long artModifiedTicks, string? overrideToken, long startTicks)
    {
        if (!OwnTicksGenerationLive(_activeVideoAudioEncodes, itemId, artModifiedTicks))
        {
            return null;
        }

        return await TryServePrewrittenVideoAudioPlaylist(itemId, artModifiedTicks, overrideToken, startTicks).ConfigureAwait(false);
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
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
        }

        return await StreamHlsEpisodeAudioCore(itemId, startTicks ?? 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Build and serve the audio-only episode HLS playlist: constructs the
    /// variant's spec (variant cache key, audio-only ffmpeg arguments with the
    /// server-side codec re-probe, art-keyed cache) and delegates to
    /// <see cref="ServeVariantHlsAsync"/>, the shared
    /// cache/lock/gate/monitor machinery.
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

        return await ServeVariantHlsAsync(
            validation,
            new VariantHlsSpec
            {
                CacheKey = EpisodeAudioCacheKey(itemId, startTicks),
                ArtModifiedTicks = GetArtModifiedTicks(validation.Item),
                BuildFfmpegArgs = (playlistPath, segmentPath) =>
                {
                    // Re-probe server-side (the handler-side probe only picked the
                    // route): copy for the muxer-compatible codecs (mp3/aac), AAC
                    // re-encode for everything else, which is the family this
                    // endpoint exists for. Probing inside the builder keeps it
                    // inside the lock and after the cache fast paths, so a cache
                    // hit never pays the media-streams read.
                    string? sourceAudioCodec = ResolveSourceAudioCodec(validation.Item);
                    _logger.LogDebug(
                        "VideoAudio episode AUDIO HLS: itemId={ItemId}, startTicks={StartTicks}, sourceAudioCodec={AudioCodec}",
                        itemId, startTicks, sourceAudioCodec ?? "(unknown)");
                    // Segment URLs point at the dedicated audio-segments route: it
                    // embeds the start position (the directory key) in the path,
                    // and never touches the generic segments route the video remux
                    // serves through.
                    string hlsBaseUrl = $"/alexaskill/api/video-audio/episode/{itemId}/audio-segments/{startTicks}/";
                    string videoUrl = $"{validation.ServerUrl}/Videos/{itemId}/stream?static=true";
                    return BuildEpisodeAudioHlsFfmpegArguments(videoUrl, startTicks, playlistPath, segmentPath, hlsBaseUrl, sourceAudioCodec);
                },
                EstimateScalePerMille = 1000L,
                LogLabel = "VideoAudio episode AUDIO HLS",
                MonitorLabel = "EpisodeAudio",
                FailureErrorBody = "Episode audio HLS generation failed",
                LogCachedHit = () => _logger.LogDebug("VideoAudio episode AUDIO HLS: serving cached playlist for item {ItemId} (start={StartTicks})", itemId, startTicks),
                LogConcurrentHit = () => _logger.LogDebug("VideoAudio episode AUDIO HLS: serving playlist generated by concurrent request for item {ItemId} (start={StartTicks})", itemId, startTicks),
                // The wait ceiling calibration lives in WaitForFirstSegmentOrKillAsync's doc.
                LogFirstSegmentFailure = exitCode => _logger.LogWarning("VideoAudio episode AUDIO HLS: ffmpeg failed to create first segment for item {ItemId} (exit code {ExitCode})", itemId, exitCode),
                LogServingPartial = () => _logger.LogDebug("VideoAudio episode AUDIO HLS: serving partial playlist for item {ItemId} (start={StartTicks})", itemId, startTicks),
            }).ConfigureAwait(false);
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
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
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
    /// Cache key of the PLAYBACK-SPEED atempo variant (JF-636): deliberately
    /// distinct from every other variant of the same item (the bare itemId the
    /// video remux keys on, the <see cref="EpisodeAudioCacheKey"/> audio-only
    /// twin), and distinct per rate AND start position (each (rate, start)
    /// pair serves a different timeline). The art component is deliberately
    /// NOT part of the key: the encode renders no art, so an art change never
    /// invalidates a speed encode.
    /// </summary>
    /// <param name="itemId">GUID-validated item ID.</param>
    /// <param name="ratePerMille">Playback rate in per-mille form (750..2000).</param>
    /// <param name="startTicks">The encode's CONTENT start position (0 = from the beginning).</param>
    /// <returns>The variant cache key string.</returns>
    internal static string AudioSpeedCacheKey(string itemId, int ratePerMille, long startTicks)
    {
        string tail = startTicks > 0 ? $"-{startTicks}" : string.Empty;
        return $"{itemId}-speed-{ratePerMille}{tail}";
    }

    /// <summary>
    /// Stream a PLAYBACK-SPEED atempo HLS transcode of an item's audio (JF-636):
    /// the first audio stream alone (<c>-map 0:a:0</c>) time-stretched by
    /// <c>ffmpeg -af atempo</c> (pitch-preserving; 0.5-2.0 per instance, so every
    /// served 0.75x..2.0x step needs exactly one). Custom skills have no native
    /// rate control (the MSAPI-only platform limit), so speed changes re-launch
    /// the item at this endpoint on the AudioPlayer path. The optional
    /// <c>?start=&lt;ticks&gt;</c> is CONTENT-relative: it input-seeks the source
    /// (<c>-ss</c> before <c>-i</c>) and the atempo filter runs over the
    /// remainder, so the served output timeline starts at position 0 and the
    /// paired AudioPlayer.Play directive offset must be 0 (the JF-507 shape; the
    /// launch-side content position rides the launch base instead).
    /// Mirrors <see cref="StreamHlsEpisodeAudioCore"/>: per-key cache dir,
    /// per-key lock, first-segment wait, partial playlist, background monitor,
    /// encode gate. Rate 1000 is accepted and encodes transparently (atempo at
    /// identity): the launch side never mints it (rate 1000 launches keep the
    /// static/codec-routed URL), but the endpoint must not 404 a URL its own
    /// table calls valid.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID (GUID-validated, token-scoped).</param>
    /// <param name="ratePerMille">Playback rate in per-mille form; must be one of the six served steps.</param>
    /// <param name="startTicks">Optional CONTENT resume position in .NET ticks (0 to play from the start).</param>
    /// <returns>An HLS playlist (.m3u8) file.</returns>
    [HttpGet("~/alexaskill/api/audio-speed/{itemId}/{ratePerMille:int}/stream.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> StreamHlsAudioSpeed(
        [FromRoute] string itemId,
        [FromRoute] int ratePerMille,
        [FromQuery(Name = "start")] long? startTicks = null)
    {
        // ValidateSignedRoute (JF-664): for a doubly-invalid request (non-GUID id +
        // unserved rate) the id 400 wins here, matching GetAudioSpeedSegment and
        // every other route on that shape. JF-651 kept this route off the shared
        // preamble only because the Core checked the rate first; the decision is
        // now made deliberately. The token check still precedes the Core's rate
        // check, so a GUID with a bad rate and no token keeps its 401; the segment
        // sibling answers that triply-invalid shape with the rate 400 instead
        // (deliberate, JF-651): the split is ratified and pinned by JF-671
        // (AudioSpeedRoutes_TriplyInvalid_GuidPlusUnservedRatePlusNoToken_RatifiedSplit
        // carries the rationale).
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
        }

        return await StreamHlsAudioSpeedCore(itemId, ratePerMille, startTicks ?? 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Build and serve the atempo speed playlist: constructs the variant's spec
    /// ((rate, start)-keyed cache key, atempo ffmpeg arguments, the
    /// same-device-supersede hooks) and delegates to
    /// <see cref="ServeVariantHlsAsync"/>, the shared
    /// cache/lock/gate/monitor machinery.
    /// </summary>
    /// <param name="itemId">GUID-validated item ID.</param>
    /// <param name="ratePerMille">Playback rate in per-mille form (validated against the served steps).</param>
    /// <param name="startTicks">CONTENT encode start position in ticks (0 = from the beginning).</param>
    /// <returns>The playlist, or an error result.</returns>
    private async Task<ActionResult> StreamHlsAudioSpeedCore(string itemId, int ratePerMille, long startTicks)
    {
        if (!Alexa.Util.PlaybackSpeed.IsValidPerMille(ratePerMille))
        {
            _logger.LogWarning("AudioSpeed HLS: rejected unserved rate {RatePerMille}/1000 for item {ItemId}", ratePerMille, itemId);
            return BadRequest(new { error = "Unsupported playback rate" });
        }

        var validation = ValidateVideoAudioRequest(itemId);
        if (validation.Error != null)
        {
            return validation.Error;
        }

        if (startTicks < 0)
        {
            startTicks = 0;
        }

        // Read once: both the supersede hook and the live-registration hook below
        // need the owning device hint from the same request query.
        string? deviceIdHint = HttpContext.Request.Query["d"];

        string cacheKey = AudioSpeedCacheKey(itemId, ratePerMille, startTicks);

        return await ServeVariantHlsAsync(
            validation,
            new VariantHlsSpec
            {
                CacheKey = cacheKey,
                ArtModifiedTicks = 0, // art-irrelevant: the encode renders no art (JF-636)
                BuildFfmpegArgs = (playlistPath, segmentPath) =>
                {
                    _logger.LogDebug(
                        "VideoAudio audio-speed HLS: itemId={ItemId}, rate={RatePerMille}/1000, startTicks={StartTicks}",
                        itemId, ratePerMille, startTicks);
                    // Segment URLs point at the dedicated speed-segments route: it
                    // embeds the rate and start position (the directory key) in the
                    // path, never touching the generic or episode segments routes.
                    string hlsBaseUrl = $"/alexaskill/api/audio-speed/{itemId}/{ratePerMille}/segments/{startTicks}/";
                    // The atempo filter decodes whatever container the item lives in
                    // (an Audio item's MP3/FLAC or a series-shape Episode's MKV audio
                    // track), so the source URL follows the item's own kind: audio
                    // items via /Audio, video items via /Videos.
                    bool videoKind = validation.Item is MediaBrowser.Controller.Entities.Movies.Movie
                        or MediaBrowser.Controller.Entities.TV.Episode;
                    string sourceUrl = videoKind
                        ? $"{validation.ServerUrl}/Videos/{itemId}/stream?static=true"
                        : $"{validation.ServerUrl}/Audio/{itemId}/stream?static=true";
                    return BuildAudioSpeedHlsFfmpegArguments(sourceUrl, startTicks, ratePerMille, playlistPath, segmentPath, hlsBaseUrl);
                },
                // JF-636 review: the budget must estimate the OUTPUT, not the content:
                // atempo stretches (0.75x writes 1.33x the content duration of AAC) and
                // compresses (2x writes half), so the flat content-runtime estimate is
                // scaled by the inverse rate.
                EstimateScalePerMille = ratePerMille,
                LogLabel = "VideoAudio audio-speed HLS",
                MonitorLabel = "AudioSpeed",
                FailureErrorBody = "Audio speed HLS generation failed",
                LogCachedHit = () => _logger.LogDebug("VideoAudio audio-speed HLS: serving cached playlist for item {ItemId} (rate={RatePerMille}/1000, start={StartTicks})", itemId, ratePerMille, startTicks),
                LogConcurrentHit = () => _logger.LogDebug("VideoAudio audio-speed HLS: serving playlist generated by concurrent request for item {ItemId} (rate={RatePerMille}/1000, start={StartTicks})", itemId, ratePerMille, startTicks),
                // The wait ceiling calibration lives in WaitForFirstSegmentOrKillAsync's doc.
                LogFirstSegmentFailure = exitCode => _logger.LogWarning("VideoAudio audio-speed HLS: ffmpeg failed to create first segment for item {ItemId} (rate={RatePerMille}/1000, exit code {ExitCode})", itemId, ratePerMille, exitCode),
                LogServingPartial = () => _logger.LogDebug("VideoAudio audio-speed HLS: serving partial playlist for item {ItemId} (rate={RatePerMille}/1000, start={StartTicks})", itemId, ratePerMille, startTicks),
                // JF-636: supersede this DEVICE's other speed encodes of this item (see
                // _activeAudioSpeedEncodeProcesses). Runs INSIDE the per-key lock, after
                // the cache fast paths: a variant that had a valid cache entry returned
                // already, so anything still running here is abandoned by this launch.
                // JF-668: the SAME-KEY displaced entry dies here too, before the new
                // ffmpeg starts (the first-segment-wait window closer; see
                // KillDisplacedSpeedEncode).
                SupersedeStaleEncodes = () =>
                {
                    KillDisplacedSpeedEncode(cacheKey);
                    KillSupersededSpeedEncodes(itemId, cacheKey, deviceIdHint);
                },
                // Registered only once the encode is provably live (the first-segment
                // wait succeeded); the failure paths never enter the registry, so a
                // registry entry always names a running encode to supersede later.
                OnEncodeLive = process => RegisterLiveSpeedEncode(process, cacheKey, deviceIdHint),
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// JF-636: register a live speed encode for same-device superseding (see
    /// <see cref="_activeAudioSpeedEncodeProcesses"/>). The owner device hint is
    /// read by the CALLER (the same request-scope read
    /// <see cref="KillSupersededSpeedEncodes"/> gets), so this helper itself
    /// never touches HttpContext and stays callable from any context. A same-key
    /// registration first kills the LIVE prior encode it displaces (JF-665;
    /// rationale in the loop comment below); since JF-668 this is the BACKSTOP:
    /// <see cref="KillDisplacedSpeedEncode"/> already fired at the pre-start
    /// position inside the per-key lock, so the displaced entry here is normally
    /// already gone. The exit watcher keeps the registry
    /// to live encodes only (the monitor owns the process disposal and knows
    /// nothing of it). Internal
    /// test seam (JF-647, InternalsVisibleTo): the same-key re-register race the
    /// watcher's compare-and-remove guards against needs a mid-encode cache
    /// eviction the endpoint cannot reproduce without racing a live encode's
    /// directory, so the pin drives this seam directly.
    /// </summary>
    /// <param name="ffmpegProcess">The encode process the first-segment wait just proved live.</param>
    /// <param name="cacheKey">The speed variant's cache key.</param>
    /// <param name="ownerDeviceId">The <c>?d=</c> device hint of the launching request; null leaves the entry ownerless (never killed).</param>
    internal void RegisterLiveSpeedEncode(Process ffmpegProcess, string cacheKey, string? ownerDeviceId)
    {
        // JF-665: a same-key registration REPLACES whatever the registry names,
        // and a LIVE displaced encode would be orphaned by that swap: after the
        // JF-647 compare-and-remove its own watcher no longer cleans it up, the
        // registry no longer names it for the supersede kill, and the encode
        // gate releases only on the process's own exit, so it would run
        // abandoned to completion holding a gate slot. A same-key re-register
        // only ever happens after the prior encode's cache directory was evicted
        // mid-encode and a re-request restarted the variant, so the displaced
        // process is abandoned by construction: kill it, and its own exit paths
        // (gate release, unpin, flag clear, monitor disposal) react. The
        // TryAdd/TryUpdate loop keeps the kill atomic against a concurrent
        // same-key registration: each contender kills exactly the entry it
        // observes and displaces, so no registration can be silently orphaned
        // by the race.
        while (true)
        {
            if (_activeAudioSpeedEncodeProcesses.TryAdd(cacheKey, (ffmpegProcess, ownerDeviceId)))
            {
                break;
            }

            if (!_activeAudioSpeedEncodeProcesses.TryGetValue(cacheKey, out var displaced))
            {
                continue; // the holder exited and its watcher removed it; claim the free key
            }

            // A same-process double registration (no production caller does;
            // the seam is directly drivable) must not kill itself.
            if (!ReferenceEquals(displaced.Process, ffmpegProcess))
            {
                TryKillLiveProcess(
                    displaced.Process,
                    "VideoAudio audio-speed HLS: killing displaced speed encode {CacheKey} (a same-key re-register is rebuilding its evicted variant)",
                    cacheKey);
            }

            if (_activeAudioSpeedEncodeProcesses.TryUpdate(cacheKey, (ffmpegProcess, ownerDeviceId), displaced))
            {
                break;
            }

            // A concurrent registration or the displaced watcher won the swap;
            // retry against whatever the key names now.
        }

        // Capture the logger so the fire-and-forget watcher, which lives as long
        // as the encode, does not pin this transient controller instance for the
        // whole window.
        var logger = _logger;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ffmpegProcess.HasExited)
                {
                    await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "VideoAudio audio-speed HLS: exit watcher raced the disposal of {CacheKey}", cacheKey);
            }

            // Compare-and-remove (JF-647): a same-key re-register (the variant's
            // cache directory evicted mid-encode, then a re-request restarted it)
            // installs a NEWER process under this key before this watcher's 1s
            // poll notices the exit; a key-only TryRemove deleted that LIVE entry,
            // the supersede kill then degraded to its conservative no-kill, and
            // the abandoned encode held a gate slot to completion. The Process
            // reference is the generation token (each registration starts a
            // distinct instance), so this removes only the entry this watcher
            // registered.
            _activeAudioSpeedEncodeProcesses.TryRemove(new KeyValuePair<string, (Process Process, string? OwnerDeviceId)>(cacheKey, (ffmpegProcess, ownerDeviceId)));
        });
    }

    /// <summary>
    /// Internal test seam (JF-647, InternalsVisibleTo): the process the live
    /// speed-encode registry names for <paramref name="cacheKey"/>, or null when
    /// the key is absent. Read-only.
    /// </summary>
    internal static Process? LiveSpeedEncodeProcessForTest(string cacheKey)
        => _activeAudioSpeedEncodeProcesses.TryGetValue(cacheKey, out var entry) ? entry.Process : null;

    /// <summary>
    /// Internal test seam (JF-731, InternalsVisibleTo; sibling of <see
    /// cref="LiveSpeedEncodeProcessForTest"/>): a snapshot of the cache keys the
    /// live speed-encode registry currently holds. ConcurrentDictionary.Keys
    /// already builds a fresh snapshot collection per read (typed
    /// ICollection, so the ToList only adapts the return shape), which means
    /// the exit watchers' concurrent removals cannot mutate the caller's
    /// iteration. The test class's Dispose-level encode-gate backstop
    /// enumerates it to kill every leftover encode. Read-only.
    /// </summary>
    internal static IReadOnlyList<string> LiveSpeedEncodeCacheKeysForTest()
        => _activeAudioSpeedEncodeProcesses.Keys.ToList();

    /// <summary>
    /// Internal test seam (JF-731, InternalsVisibleTo): the CONFIGURED capacity
    /// of the current <see cref="_encodeGate"/> instance; this is the drain
    /// target the test-side backstop compares <see cref="SemaphoreSlim.CurrentCount"/>
    /// against (the BCL exposes no initial-count read, and the plugin
    /// configuration can diverge from the static after a capacity swap).
    /// Read-only.
    /// </summary>
    internal static int EncodeGateConfiguredCapacityForTest => _encodeGateCapacity;

    /// <summary>
    /// The ONE variant-HLS serve core (JF-637, collapsing the JF-507 episode-audio
    /// and JF-636 audio-speed twins, each ~120 lines of copied machinery): cache
    /// fast path with the JF-499 W3 vanish guard, per-key lock + stub cleanup +
    /// concurrent-generated serve, JF-498 per-file debris cleanup, the encode-flag
    /// bookkeeping with the double try/catch cleanup (flag marked via
    /// <see cref="MarkEncodeActive"/> before the gated start, generation-aware
    /// clear on every failure path), the shared first-segment wait, and
    /// the CA2025 monitor boundary (the monitor starts only after the disposing
    /// scope closes). Every historically bug-prone invariant lives HERE once,
    /// provably identical for every variant; a variant contributes only its real
    /// differences through <paramref name="spec"/>. The episode remux
    /// (<see cref="StreamHlsEpisodeCore"/>) and single-item song path
    /// (<see cref="StreamHlsVideoAudioCore"/>) are deliberately NOT on this core:
    /// their pre-written-listing machinery (JF-531/JF-536) interleaves with the
    /// shared steps in ways the variants do not (older, looser siblings).
    /// JF-536 scope-(c) decision, documented where the next reader looks: these
    /// variants deliberately have NO pre-written full listing. The prewrite exists
    /// to fix two VideoApp/ExoPlayer live-edge symptoms (seekbar showing only the
    /// encoded-so-far window; playback starting at the live edge), both verified
    /// on Echo Show VIDEOAPP (JF-531, corr=c0c21c6a). Both variants are consumed
    /// by AudioPlayer on screenless devices, where custom skills get NO seek bar
    /// or progress UI at all (platform limit; reserved for the Music/Radio/Podcast
    /// Skill API), so the seekbar half of that exposure has no surface here; no
    /// device evidence says AudioPlayer treats a no-ENDLIST playlist as live.
    /// Per the JF-536 bar, no prewrite without that evidence; the shared
    /// prewrite core (WritePrewrittenEventPlaylist) makes adding one later a
    /// constants-only call plus flag/serve wiring if it ever arrives.
    /// </summary>
    /// <param name="validation">The validated request (ffmpeg path; item already resolved).</param>
    /// <param name="spec">The variant's real differences.</param>
    /// <returns>The playlist, or an error result.</returns>
    private async Task<ActionResult> ServeVariantHlsAsync(ValidatedRequest validation, VariantHlsSpec spec)
    {
        // Same cache-validity rule as the remux: completed (ENDLIST) or actively
        // encoding (variant keys ride the episode registry).
        HlsCacheProbe fastProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeEpisodeEncodes, spec.CacheKey, spec.ArtModifiedTicks).ConfigureAwait(false);
        if (fastProbe.Playlist != null)
        {
            FileInfo cached = fastProbe.Playlist;
            // JF-499 W3 (same race and fix as the remux fast path): a lock-holder's
            // debris-verdict delete can remove the directory between the FileInfo
            // read and the content reads; fall through to the re-encode instead of
            // surfacing a 500.
            ActionResult? fastServed = await TryServeValidatedHlsCacheAsync(
                spec.CacheKey,
                spec.LogLabel,
                () => ValidateEpisodeCacheAsync(cached, spec.CacheKey, spec.ArtModifiedTicks, fastProbe.OwnGenerationLiveOrRegistering),
                valid =>
                {
                    spec.LogCachedHit();
#pragma warning disable CA3003
                    return ServePlaylistWithTokenAsync(valid.Playlist.FullName, preloadedContent: valid.Content);
#pragma warning restore CA3003
                }).ConfigureAwait(false);
            if (fastServed != null)
            {
                return fastServed;
            }
        }

        using (await LockHlsItemAsync(spec.CacheKey, spec.ArtModifiedTicks).ConfigureAwait(false))
        {
            _cache.CleanupHlsStub(spec.CacheKey, spec.ArtModifiedTicks);

            HlsCacheProbe inLockProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeEpisodeEncodes, spec.CacheKey, spec.ArtModifiedTicks).ConfigureAwait(false);
            if (inLockProbe.Playlist != null)
            {
                FileInfo cached = inLockProbe.Playlist;
                // JF-678: the row rides the shared verdict+serve composite, so a
                // playlist deleted between the verdict and the serve falls through
                // to this scope's own encode branch below instead of a bare 500.
#pragma warning disable CA3003
                ActionResult? concurrentServed = await TryServeValidatedHlsCacheAsync(
                    spec.CacheKey,
                    spec.LogLabel,
                    () => ValidateEpisodeCacheAsync(cached, spec.CacheKey, spec.ArtModifiedTicks, inLockProbe.OwnGenerationLiveOrRegistering),
                    valid =>
                    {
                        spec.LogConcurrentHit();
                        return ServePlaylistWithTokenAsync(valid.Playlist.FullName, preloadedContent: valid.Content);
                    }).ConfigureAwait(false);
#pragma warning restore CA3003
                if (concurrentServed != null)
                {
                    return concurrentServed;
                }

                // JF-678 rework F1: the vanish fall-through's breach-vs-legitimate
                // liveness guard (the rationale lives on GuardInLockVanishFallThrough).
                GuardInLockVanishFallThrough(_activeEpisodeEncodes, spec.CacheKey, spec.ArtModifiedTicks, cached.FullName, spec.LogLabel);
            }

            // JF-636/JF-668: both registry kills run INSIDE the per-key lock after
            // the cache fast paths (a variant with a valid cache entry returned
            // already, so anything still running here is abandoned by this launch)
            // and BEFORE this launch's directory recreation and debris sweep: the
            // abandoned prior writer must be dead before those mutations, or its
            // by-path segment opens and playlist rewrites land in the recreated
            // directory (see KillDisplacedSpeedEncode). The stub cleanup above the
            // hook also touches directories; it is kept from deleting under a live
            // writer only by its missing/empty-playlist guards plus the JF-428 pin
            // protocol (a registered encode holds a pin) - neither pinned for this
            // ordering, so do not hoist further mutations above the hook.
            spec.SupersedeStaleEncodes?.Invoke();

#pragma warning disable CA3003 // paths derived from GUID-validated itemId
            string hlsDir = _cache.GetHlsDirectoryPath(spec.CacheKey, spec.ArtModifiedTicks);
            Directory.CreateDirectory(hlsDir);

            string playlistPath = Path.Combine(hlsDir, "stream.m3u8");
            // 10-second segments (the audio-rate value shared by both variants; %04d
            // caps at 9999): fewer, larger fetches through the public URL than the
            // remux's 4s, and audio-only has no keyframe constraint.
            string segmentPath = Path.Combine(hlsDir, "seg_%04d.ts");

            var ffmpegArgs = spec.BuildFfmpegArgs(playlistPath, segmentPath);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("{LogLabel}: ffmpeg arguments: {Args}", spec.LogLabel, string.Join(" ", ffmpegArgs));
            }

            // Per-file debris cleanup so ffmpeg always starts over a clean target (the
            // JF-498 review I1 concern, same as every sibling path). Dir-scoped
            // since JF-537.1; the variant paths are always cache-rooted.
            _cache.DeleteHlsEncodeDebris(hlsDir);

            // Register the resolved (single-root: cache-root generation) HLS
            // directory and mark before the process start, through the JF-782
            // register-before-mark one-wrapper: the mark-before-start invariant
            // all four HLS paths share (see the remux mark site's comment).
            ActiveEncodeHandle activeEncode = RegisterAndMarkEncodeActive(_activeEpisodeEncodes, spec.CacheKey, spec.ArtModifiedTicks, hlsDir);

            Process ffmpegProcess;
            try
            {
                ffmpegProcess = await StartFfmpegProcessGatedAsync(
                    validation.FfmpegPath,
                    ffmpegArgs,
                    EstimateEpisodeAudioEncodeBytes(validation.Item.RunTimeTicks ?? 0) * 1000L / spec.EstimateScalePerMille,
                    hlsDir).ConfigureAwait(false);
            }
            catch
            {
                activeEncode.Clear();
                throw;
            }

            try
            {
                ActionResult? firstSegmentFailure = await WaitForFirstSegmentOrKillAsync(
                    ffmpegProcess,
                    hlsDir,
                    playlistPath,
                    "seg_0000.ts",
                    activeEncode,
                    spec.LogFirstSegmentFailure,
                    spec.FailureErrorBody).ConfigureAwait(false);
                if (firstSegmentFailure != null)
                {
                    return firstSegmentFailure;
                }

                spec.OnEncodeLive?.Invoke(ffmpegProcess);
            }
            catch
            {
                // Pre-handoff failure: this scope still owns the process.
                activeEncode.KillAndClear(ffmpegProcess);
                throw;
            }

            // CA2025: monitor started via the boundary helper AFTER the disposing scope
            // above closed; the monitor's finally owns the flag clear and the disposal.
            StartHlsMonitor(ffmpegProcess, hlsDir, spec.CacheKey, spec.ArtModifiedTicks, spec.MonitorLabel, activeEncode);

            spec.LogServingPartial();
            return await ServePlaylistWithTokenAsync(playlistPath).ConfigureAwait(false);
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// The per-variant inputs of <see cref="ServeVariantHlsAsync"/> (JF-637): a
    /// variant contributes ONLY its real differences (cache key, art ticks,
    /// ffmpeg arguments + context logging, size estimate, labels, log wording,
    /// and its optional extras); the serve machinery itself is shared. The log
    /// and serve delegates preserve each variant's exact message wording and
    /// arguments (the speed variant interleaves its rate into the lines).
    /// </summary>
    private sealed class VariantHlsSpec
    {
        /// <summary>The variant's cache key (distinct from every sibling variant of the same item).</summary>
        required public string CacheKey { get; init; }

        /// <summary>Art ticks of the cache directory; 0 for an art-irrelevant encode.</summary>
        required public long ArtModifiedTicks { get; init; }

        /// <summary>
        /// Builds the variant's ffmpeg arguments from the shared body's paths
        /// (playlist, segment template), logging the variant's own context line
        /// first (the episode variant's codec re-probe lives here, so a cache hit
        /// never pays the media-streams read). Runs inside the lock, only on a
        /// real encode.
        /// </summary>
        required public Func<string, string, List<string>> BuildFfmpegArgs { get; init; }

        /// <summary>
        /// The rate scale of the variant (1000 for unmodified playback, the
        /// per-mille rate for speed variants). The pre-encode cache-budget
        /// estimate (the JF-428 headroom input) scales inversely with it: a
        /// 1.5x stream spends content 1.5x faster, so the same runtime covers
        /// less wall time. A plain number, not a delegate: the estimate is
        /// pure arithmetic over data the core already holds, so laziness buys
        /// nothing (unlike <see cref="BuildFfmpegArgs"/>, whose laziness IS
        /// load-bearing: it hides a media-streams read).
        /// </summary>
        required public long EstimateScalePerMille { get; init; }

        /// <summary>Log prefix ("VideoAudio episode AUDIO HLS" / "VideoAudio audio-speed HLS").</summary>
        required public string LogLabel { get; init; }

        /// <summary>Monitor label ("EpisodeAudio" / "AudioSpeed").</summary>
        required public string MonitorLabel { get; init; }

        /// <summary>The 500 response's error string on a first-segment failure.</summary>
        required public string FailureErrorBody { get; init; }

        /// <summary>Logs the fast-path cache serve (with the variant's exact log line).</summary>
        required public Action LogCachedHit { get; init; }

        /// <summary>Logs the concurrent-generated serve (with the variant's exact log line).</summary>
        required public Action LogConcurrentHit { get; init; }

        /// <summary>Logs the first-segment failure; receives the exit code read before the kill.</summary>
        required public Action<int> LogFirstSegmentFailure { get; init; }

        /// <summary>Logs the final partial-playlist serve (with the variant's exact log line).</summary>
        required public Action LogServingPartial { get; init; }

        /// <summary>
        /// Optional extra inside the lock, after the cache fast paths and before
        /// the encode directory is recreated (the speed variant's pre-start
        /// same-key displacement kill and its same-device supersede kill: the
        /// abandoned prior writer must be dead before any directory mutation).
        /// </summary>
        public Action? SupersedeStaleEncodes { get; init; }

        /// <summary>
        /// Optional extra once the encode is provably live (first segment on disk,
        /// directory registered; the speed variant's live-encode registry).
        /// </summary>
        public Action<Process>? OnEncodeLive { get; init; }
    }

    /// <summary>
    /// The ONE first-segment wait (JF-637; the remux, song, episode-audio, and
    /// audio-speed paths carried four inline copies): poll up to 200 x 100ms
    /// (~20 seconds, the pathological-case ceiling) for ffmpeg's first segment
    /// file AND the playlist to appear on disk. ffmpeg creates the playlist
    /// atomically (.tmp rename), so once the segment file exists the playlist
    /// references are valid. The ceiling is calibrated generously: every caller
    /// encodes far above realtime (audio-only AAC measured ~49x, atempo similar,
    /// the remux I/O-bound; the per-path evidence sits at the callers' wait
    /// comments), so the window only guards pathological cases. On failure the
    /// caller's warning fires FIRST (the exit code must be read before the
    /// dispose: <see cref="SafeExitCode"/> throws on a disposed process), then
    /// the process is killed and disposed, the encode flag cleared, and the
    /// caller's own 500 body returned. Null means the segment appeared and the
    /// caller proceeds. The registry, warning wording, and error body stay per
    /// path (they genuinely differ). The audiobook/album path
    /// (<see cref="StreamHlsAudiobook"/>) deliberately keeps its OWN wait: its
    /// monitor is already running and owns the disposal when the wait polls, it
    /// clears no per-path flag, checks the segment file only, and uses a 10s
    /// ceiling; routing it through this helper would double-own the process. Do
    /// NOT fold it in.
    /// </summary>
    /// <param name="ffmpegProcess">The started ffmpeg process (caller-owned until this returns).</param>
    /// <param name="hlsDir">The encode's HLS cache directory.</param>
    /// <param name="playlistPath">ffmpeg's playlist path in that directory.</param>
    /// <param name="firstSegmentFileName">The first segment's file name ("seg_000.ts" for the song path's 3-digit template, "seg_0000.ts" for the 4-digit paths).</param>
    /// <param name="activeEncode">The encode's handle (<see cref="MarkEncodeActive"/>); its failure clear is the handle's generation-aware KillAndClear.</param>
    /// <param name="logFailure">Logs the path's exact failure warning; receives the exit code read before the kill.</param>
    /// <param name="errorBody">The 500 response's error string.</param>
    /// <returns>The failure response to answer with, or null when the first segment appeared.</returns>
    private async Task<ActionResult?> WaitForFirstSegmentOrKillAsync(
        Process ffmpegProcess,
        string hlsDir,
        string playlistPath,
        string firstSegmentFileName,
        ActiveEncodeHandle activeEncode,
        Action<int> logFailure,
        string errorBody)
    {
#pragma warning disable CA3003 // paths derived from GUID-validated itemId
        string firstSegmentPath = Path.Combine(hlsDir, firstSegmentFileName);
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
#pragma warning restore CA3003

        if (segmentAppeared)
        {
            return null;
        }

        logFailure(SafeExitCode(ffmpegProcess));
        activeEncode.KillAndClear(ffmpegProcess);
        return StatusCode(500, new { error = errorBody });
    }

    /// <summary>
    /// Mark a cache key as actively encoding under a fresh generation token in
    /// the encode's OWN art-tick slot and return the encode's handle (the
    /// JF-665/JF-669 generation rule, one bundled value since JF-668). Never a
    /// checked TryAdd: within one (key, artTicks) generation the flag passes to
    /// the NEWEST registration (JF-665: its directory was evicted mid-encode and
    /// a re-request is re-encoding, or its monitor's clear is still pending, so
    /// the prior monitor's generation-aware clear must not be able to drop the
    /// flag mid-encode, which turned the near-ahead segment hold off and let
    /// <see cref="ValidateEpisodeCacheAsync"/> delete the live directory). A
    /// DIFFERENT art-tick generation of the same key gets its OWN slot (JF-669:
    /// the locks and cache directories are keyed by (key, artModifiedTicks), so
    /// an art change mid-encode runs a genuinely separate encode whose clear
    /// must not drop the older generation's liveness either - the pre-JF-669
    /// single-token entry let the newest generation's correct clear drop the
    /// flag while the older-ticks encode still wrote, and the debris verdict
    /// deleted its live directory). The token is opaque: reference identity IS
    /// the generation. The entry as a whole (what every presence reader's
    /// ContainsKey sees) is present whenever at least one slot is live (plus
    /// the brief mid-registration window between the entry's creation and the
    /// slot write, where every PRESENCE reader errs in the conservative
    /// direction: serve pre-written instead of a second ffmpeg, hold a segment;
    /// the near-ahead hold and the audiobook guard are that family. The four
    /// prewrite serve gates LEFT the presence-reader family in JF-675: they
    /// read own-ticks liveness through
    /// <see cref="ActiveEncodeGenerations.IsTickLive"/> (whose doc holds the
    /// window account) and in that window err the OPPOSITE way, toward
    /// ffmpeg's own playlist. The DEBRIS VERDICTS left it in JF-676: they read
    /// own-ticks liveness-or-registering
    /// (<see cref="ActiveEncodeGenerations.IsTickLiveOrRegistering"/>) so they
    /// keep the conservative window direction while firing under a live
    /// FOREIGN-ticks generation, whose directory their ticks-scoped cleanup
    /// cannot name (canonical account on
    /// <see cref="OwnTicksGenerationLiveOrRegistering"/>, narrowed by JF-782
    /// leg 2 to registrations not provably foreign: the tri-state account
    /// lives on <see cref="ActiveEncodeGenerations.ReadTickLiveness"/>). The
    /// DIRECTORY
    /// RESOLVER joined the conservative family in JF-775: the liveness-aware
    /// probe consumes the same live-or-registering read, so in this window the
    /// registered dir (stored at encode start, BEFORE this mark on every path
    /// since JF-782, hence already naming the incoming encode's dir) is the
    /// only probe source and the other root's stale shadow is never probed.
    /// </summary>
    /// <param name="activeEncodes">The path's active-encode registry.</param>
    /// <param name="cacheKey">The encode's cache key.</param>
    /// <param name="artModifiedTicks">The encode's art ticks (the cache
    /// directory generation this encode writes into).</param>
    /// <returns>The handle every clear of THIS encode must go through.</returns>
    private static ActiveEncodeHandle MarkEncodeActive(ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes, string cacheKey, long artModifiedTicks)
        => ActiveEncodeHandle.MarkActive(activeEncodes, cacheKey, artModifiedTicks);

    /// <summary>
    /// The ONE register-before-mark pair (JF-782 leg 2, the LockHlsItemAsync
    /// one-wrapper idiom: call sites cannot drift): register the encode's
    /// resolved HLS directory, THEN mark the generation active. The ORDER is
    /// load-bearing for both consumers of the registration: the debris
    /// verdict's mid-registration narrowing reads the per-key registration as
    /// "the marking encode's directory" (contained means same-ticks, foreign
    /// means an art change mid-flight), and the liveness-aware resolver's
    /// exclusive arm reads the generation-scoped entry as the
    /// live-or-registering encode's own dir; a mark that precedes its
    /// registration opens the zero-slot window with a stale or absent
    /// registration naming it, unsoundening both. Every encode path runs this
    /// pair inside its (key, ticks) lock scope; a future fifth path must go
    /// through here, not hand-roll the pair. The registration leg:
    /// <paramref name="resolvedHlsDir"/> names the encode's RESOLVED
    /// directory, REQUIRED (the JF-686 no-default shape, review F2): a
    /// two-root path forgetting to pass its resolved transient dir would
    /// silently register the cache-root dir while ffmpeg writes the
    /// transient one, and the exclusive arm would serve the wrong root for
    /// the whole encode; the single-root paths pass
    /// <see cref="VideoAudioCache.GetHlsDirectoryPath"/> of their key
    /// explicitly. A failed start leaves a registration whose files never
    /// appear; every reader re-checks existence.
    /// </summary>
    /// <param name="activeEncodes">The path's active-encode registry.</param>
    /// <param name="cacheKey">The encode's cache key.</param>
    /// <param name="artModifiedTicks">The encode's art ticks (the cache
    /// directory generation this encode writes into).</param>
    /// <param name="resolvedHlsDir">The encode's already-resolved directory
    /// (the two-root episode path's transient-or-cache dir; the single-root
    /// paths' cache-root generation dir).</param>
    /// <returns>The handle every clear of THIS encode must go through.</returns>
    private ActiveEncodeHandle RegisterAndMarkEncodeActive(
        ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes,
        string cacheKey,
        long artModifiedTicks,
        string resolvedHlsDir)
    {
        _cache.RegisterHlsDirectoryPath(cacheKey, artModifiedTicks, resolvedHlsDir);
        return MarkEncodeActive(activeEncodes, cacheKey, artModifiedTicks);
    }

    /// <summary>
    /// Whether the CALLER'S OWN (cache key, art-tick) generation is the live one
    /// (JF-675, the prewrite serve gates' refinement of bare presence; THIS doc
    /// is the canonical account of the rule, and the four gates carry pointers).
    /// An entry found but without the caller's own-ticks slot means the own-ticks
    /// encode completed (or never ran) while a FOREIGN-ticks generation of the
    /// same key still writes, so the caller must fall through to the normal
    /// cache serve (ffmpeg's ENDLIST playlist for a completed encode) instead
    /// of the pre-written no-ENDLIST listing, which bare presence would serve
    /// for the sibling generation's whole remaining duration. The CONSERVATIVE
    /// readers keep bare any-generation presence, each for its own reason: the
    /// near-ahead hold asks whether ANY live writer of the key exists (its
    /// directory resolution is tick-blind by construction), and the audiobook
    /// concurrent-encode guard is a SERVE/bound decision; it serves the caller
    /// or 503s instead of starting a second ffmpeg, and own-ticks matching
    /// there would newly allow two concurrent encodes of one book under
    /// different ticks (JF-669). The debris verdicts LEFT this family in JF-676:
    /// they read <see cref="OwnTicksGenerationLiveOrRegistering"/> and scope
    /// their CLEANUP to the caller's own ticks directory, so directory
    /// protection no longer depends on holding the verdict back.
    /// </summary>
    /// <param name="activeEncodes">The path's active-encode registry.</param>
    /// <param name="cacheKey">The encode's cache key.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    private static bool OwnTicksGenerationLive(ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes, string cacheKey, long artModifiedTicks)
        => activeEncodes.TryGetValue(cacheKey, out ActiveEncodeGenerations? generations)
            && generations.IsTickLive(artModifiedTicks);

    /// <summary>
    /// The DEBRIS VERDICTS' own-ticks reader (JF-676; THIS doc is the canonical
    /// account of the ticks-scoped verdict rule, and both validators carry
    /// pointers). A cached playlist read from the caller's own
    /// (cache key, art-tick) directory can only be ffmpeg's live growing one
    /// while a writer of THOSE ticks exists, so own-live (or the entry being in
    /// the mid-registration window, where the incoming registration may be for
    /// this very ticks: <see cref="ActiveEncodeGenerations.IsTickLiveOrRegistering"/>)
    /// holds the verdict; own-dead means the verdict may fire EVEN WHILE a
    /// foreign-ticks generation of the same key runs, because the verdict's
    /// cleanup deletes only the caller's own generation directory
    /// (<see cref="VideoAudioCache.CleanupHlsGenerationAt"/>, never the key-wide
    /// <c>Cleanup</c>): directory protection comes from scoping the delete, not
    /// from holding the verdict (the JF-669 any-generation short-circuit's
    /// precision cost, dropped: a killed own-ticks encode's stale no-ENDLIST
    /// partial is cleaned and re-encoded instead of served until the foreign
    /// generation exits). In the mid-registration window this reader errs
    /// toward NOT deleting, the opposite of the serve gates' own-ticks reader
    /// (<see cref="OwnTicksGenerationLive"/>, the JF-675 split).
    /// JF-782 SPLIT of the consumers: the DEBRIS VERDICT no longer reads this
    /// bool directly; it composes the one-gate tri-state
    /// (<see cref="ActiveEncodeGenerations.ReadTickLiveness"/>) with the
    /// cache's foreign-registration evidence, accepting the mid-registration
    /// window only while the incoming registration is not provably foreign
    /// (leg 2) and conjuncting the PROBE's copy of this read (threaded via
    /// <see cref="HlsCacheProbe"/>) into the unread acceptance (leg 1). This
    /// helper remains the PROBE-side wrapper's answer: the one evaluation the
    /// resolver consumed and the verdict conjuncts.
    /// </summary>
    /// <param name="activeEncodes">The path's active-encode registry.</param>
    /// <param name="cacheKey">The encode's cache key.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    private static bool OwnTicksGenerationLiveOrRegistering(ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes, string cacheKey, long artModifiedTicks)
        => activeEncodes.TryGetValue(cacheKey, out ActiveEncodeGenerations? generations)
            && generations.IsTickLiveOrRegistering(artModifiedTicks);

    /// <summary>
    /// The ONE liveness-aware warm-cache probe wrapper (JF-775, the
    /// LockHlsItemAsync call-sites-cannot-drift idiom): the cache's
    /// <see cref="VideoAudioCache.GetCachedHlsPlaylist"/> paired with the
    /// verdict-family liveness read over the caller's OWN registry, so every
    /// warm site (episode/song/variants/audiobook fast path and in-lock
    /// double-check) spells only its registry choice and the pairing can never
    /// drift from the verdict it feeds. The liveness read is the conservative
    /// read of the debris verdict FAMILY (<see cref="OwnTicksGenerationLiveOrRegistering"/>,
    /// evaluated at its own instant and RETURNED WITH THE HIT
    /// (<see cref="HlsCacheProbe"/>,
    /// JF-782 leg 1); the VERDICT's own fresh read is the JF-782 leg 2
    /// NARROWED form of the same family, so the two sides are same-family but
    /// not same-predicate by design: the verdict re-reads after the probe's
    /// file I/O, and a
    /// generation marked between the two evaluations is seen by the fresh
    /// read even though the probe resolved the static order, so the verdict
    /// CONJUNCTS the threaded probe answer into its unread acceptance and
    /// judges the hit instead when the two disagree (the straddle's closure;
    /// the verdict's later read stays the deliberate freshness, this is not a
    /// hoisted snapshot). While the read is true and the GENERATION-scoped
    /// registration names one of the (key, ticks) generation dirs, the probe
    /// returns ONLY that dir's file, so a liveness-accepted verdict row's
    /// FileInfo is the live-or-registering encode's own registered file,
    /// never the other root's stale shadow (the mid-registration residual the
    /// JF-774 gate marker filed, closed at the probe). The two JF-782
    /// residuals of that "never" are closed beside it: a concurrent
    /// FOREIGN-ticks encode of the same key can no longer displace the
    /// generation-scoped registration the exclusive arm reads (leg 3; the
    /// per-key slot it still overwrites feeds only the tick-blind segment
    /// resolution and the verdict's foreign-evidence discriminator), and the
    /// mark-direction straddle above is closed at the verdict by the
    /// conjunction (leg 1).
    /// </summary>
    /// <param name="activeEncodes">The path's active-encode registry.</param>
    /// <param name="cacheKey">The encode's cache key.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    /// <returns>The probe's hit with the liveness answer that resolved it (a
    /// null Playlist on a miss still carries the answer, so a caller that
    /// falls through on the miss logs honestly).</returns>
    private async Task<HlsCacheProbe> GetCachedHlsPlaylistLiveAwareAsync(ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes, string cacheKey, long artModifiedTicks)
    {
        bool ownGenerationLiveOrRegistering = OwnTicksGenerationLiveOrRegistering(activeEncodes, cacheKey, artModifiedTicks);
        // JF-782 test seam: the only deterministic firing point between this
        // read and the probe's consumption of it (the rationale lives on
        // <see cref="ProbeLivenessReadForTest"/>).
        ProbeLivenessReadForTest?.Invoke(cacheKey, ownGenerationLiveOrRegistering);
        FileInfo? playlist = await _cache.GetCachedHlsPlaylist(cacheKey, artModifiedTicks, ownGenerationLiveOrRegistering).ConfigureAwait(false);
        return new HlsCacheProbe(playlist, ownGenerationLiveOrRegistering);
    }

    /// <summary>
    /// The debris verdict's tri-state own-ticks read (JF-782 legs 1+2): the
    /// entry's state at ONE gate instant, distinguishing the full own-ticks
    /// slot (Live) and the zero-slot mid-registration window (Registering)
    /// from everything else (NotLive: no entry, or slots that all belong to
    /// other ticks). The verdict composes this with the cache's
    /// foreign-registration evidence OUTSIDE the gate (the holder's gate must
    /// stay I/O-free), which is why it is a tri-state and not a second bool:
    /// the acceptance needs Live OR (Registering AND not-foreign) while the
    /// cleanup gate needs exactly NotLive.
    /// </summary>
    private enum TickLiveness
    {
        /// <summary>No entry for the key, or no slot at the caller's ticks
        /// while other-ticks slots exist.</summary>
        NotLive,

        /// <summary>A stored entry with ZERO slots: the brief window between
        /// the registry store and the first slot write.</summary>
        Registering,

        /// <summary>The caller's own (key, ticks) slot is written.</summary>
        Live,
    }

    /// <summary>
    /// The live generation slots of ONE cache key's active-encode flag (JF-669):
    /// one slot per (key, artModifiedTicks) generation actually running. The
    /// holder carries its own registry location, so a handle over it never
    /// re-threads the registry and key as parallel state. The slot mutations
    /// and the empty-check + registry removal all run under the holder's
    /// private gate so a registration can never land in a holder whose
    /// registry entry a concurrent clear just emptied and removed (which would
    /// drop the flag while that encode is live: the JF-669 exposure reborn as
    /// a race). The gate guards only a few in-memory dictionary operations -
    /// no I/O, no awaits - and the per-item cache locks are untouched.
    /// </summary>
    private sealed class ActiveEncodeGenerations
    {
        private readonly ConcurrentDictionary<string, ActiveEncodeGenerations> _registry;
        private readonly string _cacheKey;

        /// <summary>Serializes the slot mutations with this entry's registry removal.</summary>
        private readonly object _gate = new();

        private readonly Dictionary<long, object> _slotsByTicks = new();

        public ActiveEncodeGenerations(ConcurrentDictionary<string, ActiveEncodeGenerations> registry, string cacheKey)
        {
            _registry = registry;
            _cacheKey = cacheKey;
        }

        /// <summary>
        /// Register a generation in its art-tick slot (displacing any prior
        /// token of the SAME tick: the JF-665 newest-owns rule within one
        /// generation) only while this holder is still the registry's stored
        /// entry. False when a concurrent clear emptied and removed this holder
        /// between the caller's GetOrAdd and the gate: the caller retries
        /// against the holder the registry stores now, so a registration can
        /// never be stranded in an unreachable holder.
        /// </summary>
        internal bool RegisterIfStored(long artModifiedTicks, object generation)
        {
            lock (_gate)
            {
                if (!_registry.TryGetValue(_cacheKey, out ActiveEncodeGenerations? stored) || !ReferenceEquals(stored, this))
                {
                    return false;
                }

                _slotsByTicks[artModifiedTicks] = generation;
                return true;
            }
        }

        /// <summary>
        /// Compare-and-remove of one (art-tick, token) pair (the JF-647/JF-665
        /// shape: only the generation that set the slot may clear it), dropping
        /// the whole registry entry only when the LAST live generation cleared
        /// its own (JF-669). The empty check and the removal share the gate
        /// with the slot mutation so a concurrent registration cannot refill a
        /// holder this clear is about to drop (and vice versa).
        /// </summary>
        internal void ClearGeneration(long artModifiedTicks, object generation)
        {
            lock (_gate)
            {
                if (_slotsByTicks.TryGetValue(artModifiedTicks, out object? current) && ReferenceEquals(current, generation))
                {
                    _slotsByTicks.Remove(artModifiedTicks);
                }

                if (_slotsByTicks.Count == 0)
                {
                    _registry.TryRemove(new KeyValuePair<string, ActiveEncodeGenerations>(_cacheKey, this));
                }
            }
        }

        /// <summary>Live generation count, under the gate (the test seam's probe).</summary>
        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    return _slotsByTicks.Count;
                }
            }
        }

        /// <summary>
        /// Whether the key's OWN generation slot at <paramref name="artModifiedTicks"/>
        /// is live, under the gate (JF-675). The prewrite serve gates read this
        /// through <see cref="OwnTicksGenerationLive"/>, which owns the canonical
        /// account of the own-ticks rule. Also false inside the brief
        /// mid-registration window before the first slot write (see
        /// <see cref="MarkEncodeActive"/>), where bare presence already reads
        /// true: the serve gate errs toward ffmpeg's own playlist there, the
        /// same file the prewrite-missing fallback would serve.
        /// </summary>
        internal bool IsTickLive(long artModifiedTicks)
        {
            lock (_gate)
            {
                return _slotsByTicks.ContainsKey(artModifiedTicks);
            }
        }

        /// <summary>
        /// <see cref="IsTickLive"/> OR the entry is in the brief mid-registration
        /// window (JF-676): a stored entry with ZERO slots exists only between the
        /// registry store and the first slot write (every clear removes the entry
        /// under the same gate the moment its last slot empties), and the
        /// registration landing in that window may be for THIS very ticks, so the
        /// DEBRIS VERDICTS read this form and err toward not deleting a directory a
        /// starting encode may be about to write into. One gate read: composing
        /// <see cref="IsTickLive"/> with <see cref="Count"/> at the caller would
        /// race a registration landing between the two reads. The serve gates do
        /// NOT use this: they read bare <see cref="IsTickLive"/> and err toward
        /// ffmpeg's own playlist in the same window (the JF-675 split, canonical
        /// account on <see cref="OwnTicksGenerationLive"/>).
        /// JF-782 LEG 2 NARROWING, the canonical account of the mid-registration
        /// verdict rule: the DEBRIS VERDICT no longer consumes this bool directly
        /// but <see cref="ReadTickLiveness"/>'s tri-state plus the cache's
        /// foreign-registration evidence, so the window's conservative direction
        /// survives ONLY while the incoming registration is not provably foreign
        /// for the caller's ticks (an art change mid-flight registering its own
        /// dir before marking); sound because every encode path registers its
        /// resolved dir BEFORE the mark since JF-782, so the zero-slot window's
        /// per-key registration always names the marking encode's dir. The
        /// PROBE-side wrapper still consumes this bool as its probe-time answer.
        /// IMPLEMENTATION (JF-782 /simplify): this bool is the collapse of
        /// <see cref="ReadTickLiveness"/> (NotLive maps to false), delegating
        /// so the mid-registration truth table lives in ONE gate body the two
        /// readers can never drift apart.
        /// </summary>
        internal bool IsTickLiveOrRegistering(long artModifiedTicks)
            => ReadTickLiveness(artModifiedTicks) != TickLiveness.NotLive;

        /// <summary>
        /// The debris verdict's tri-state own-ticks read (JF-782 legs 1+2):
        /// <see cref="TickLiveness.Live"/> when the caller's own slot is
        /// written, <see cref="TickLiveness.Registering"/> in the brief
        /// zero-slot mid-registration window, <see cref="TickLiveness.NotLive"/>
        /// otherwise. THE ONE gate body of the family (a single lock read, so
        /// the state can never be observed torn between the slot check and
        /// the empty check): <see cref="IsTickLiveOrRegistering"/> is this
        /// tri-state collapsed to a bool. The verdict composes this with the
        /// cache's foreign-registration evidence OUTSIDE the gate (that
        /// evidence is a pure map read but the holder's gate contract stays
        /// no-I/O-no-callbacks), accepting the own-live row on Live or
        /// (Registering AND not foreign) and gating its debris cleanup on
        /// exactly NotLive.
        /// </summary>
        internal TickLiveness ReadTickLiveness(long artModifiedTicks)
        {
            lock (_gate)
            {
                if (_slotsByTicks.ContainsKey(artModifiedTicks))
                {
                    return TickLiveness.Live;
                }

                return _slotsByTicks.Count == 0 ? TickLiveness.Registering : TickLiveness.NotLive;
            }
        }
    }

    /// <summary>
    /// The active-encode flag's ownership bundle (JF-668): the generation
    /// slots (which carry their own registry location), the art-tick slot, and
    /// the generation token <see cref="MarkEncodeActive"/> minted, captured as
    /// ONE value so a clear can never pair the right key with the wrong
    /// registry or a stale token. Both mispairings were silent no-op clears
    /// leaving the flag stuck mid-encode (the failure family JF-665 exists to
    /// prevent), and until JF-668 they were representable because the triple
    /// travelled as parallel parameters through the wait/kill/monitor chain,
    /// guarded only by doc comments. The type is private to this controller,
    /// so construction happens only here, and the ONE intended shape is
    /// <see cref="MarkEncodeActive"/> (the real mint; every path marks before
    /// its process starts since the JF-676 rework, so the song path's former
    /// pre-mark sentinel shape is gone with the pre-mark window it existed
    /// for). <see cref="Clear"/> and <see cref="KillAndClear"/> are the ONLY
    /// clear paths.
    /// </summary>
    private readonly struct ActiveEncodeHandle
    {
        /// <summary>The live generation slots of this handle's key (carries the registry location).</summary>
        private readonly ActiveEncodeGenerations _generations;

        /// <summary>The encode's art ticks: the slot this handle's token lives in.</summary>
        private readonly long _artModifiedTicks;

        /// <summary>
        /// The generation token minted together with this handle; reference
        /// identity IS the generation, and every clear compares against it.
        /// </summary>
        private readonly object _generation;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActiveEncodeHandle"/> struct.
        /// PRIVATE (JF-668 gate-review F2): hand-built handles - the right key with
        /// the wrong registry or a stale token - are the silent no-op-clear failure
        /// family this type exists to prevent, so construction is confined to the
        /// struct's own factory (<see cref="MarkActive"/>);
        /// the enclosing controller cannot reach a private ctor's members but CAN
        /// call the public static factory.
        /// </summary>
        private ActiveEncodeHandle(ActiveEncodeGenerations generations, long artModifiedTicks, object generation)
        {
            _generations = generations;
            _artModifiedTicks = artModifiedTicks;
            _generation = generation;
        }

        /// <summary>
        /// The real mint: registers a fresh generation token in the key's
        /// <paramref name="artModifiedTicks"/> slot (the generation rule's
        /// canonical account lives on <see cref="MarkEncodeActive"/>) and
        /// returns the handle bound to it. The stored-holder verification
        /// inside <see cref="ActiveEncodeGenerations.RegisterIfStored"/> closes
        /// the register-vs-remove race (see the holder's doc): a holder emptied
        /// and removed between the GetOrAdd and the gate can never accept the
        /// token, and the retry loop re-registers against the holder the
        /// registry stores now. The loop terminates because every rejection
        /// implies a concurrent clear made progress.
        /// </summary>
        public static ActiveEncodeHandle MarkActive(ConcurrentDictionary<string, ActiveEncodeGenerations> activeEncodes, string cacheKey, long artModifiedTicks)
        {
            var generation = new object();
            while (true)
            {
                ActiveEncodeGenerations holder = activeEncodes.GetOrAdd(cacheKey, key => new ActiveEncodeGenerations(activeEncodes, key));
                if (holder.RegisterIfStored(artModifiedTicks, generation))
                {
                    return new(holder, artModifiedTicks, generation);
                }
            }
        }

        /// <summary>
        /// Generation-aware active-encode flag clear (JF-665/JF-669): removes
        /// this handle's (art-tick, token) slot only while it still belongs to
        /// this generation, so the late monitor or failure path of a displaced
        /// generation cannot clear a newer registration's slot, and the whole
        /// entry drops only when the LAST live generation cleared its own (a
        /// newer-ticks encode finishing first can no longer orphan an
        /// older-ticks encode's liveness).
        /// </summary>
        public void Clear()
            => _generations.ClearGeneration(_artModifiedTicks, _generation);

        /// <summary>
        /// The ONE failure cleanup triple shared by every HLS path's pre-handoff
        /// failure sites (JF-637): kill the encode (already-exited is fine; the
        /// tree-kill policy lives in <see cref="KillEncodeTree"/>), dispose the
        /// process this scope still owns, and clear the flag. Called from
        /// <see cref="WaitForFirstSegmentOrKillAsync"/>'s failure branch and
        /// every core's catch; a future cleanup addition (releasing a pin,
        /// clearing a second registry) lands here once instead of being
        /// mirrored per site. The caller owns the failure LOG: it must read the
        /// exit code through <see cref="SafeExitCode"/> BEFORE this kills.
        /// </summary>
        /// <param name="ffmpegProcess">The failed encode's process (never null).</param>
        public void KillAndClear(Process ffmpegProcess)
        {
            try { KillEncodeTree(ffmpegProcess); } catch { /* already exited */ }
            ffmpegProcess.Dispose();
            Clear();
        }
    }

    /// <summary>
    /// The KILL POLICY in one place (JF-668): every encode kill is a TREE kill
    /// (<c>entireProcessTree: true</c>). A plain Kill of a wrapper process
    /// leaves its CHILD ffmpeg writing; only the fact that production
    /// currently starts ffmpeg directly made the old plain Kills safe, and the
    /// stall kill already used the tree form. Callers add their own guard,
    /// log, and catch around this (see <see cref="TryKillLiveProcess"/> and
    /// <see cref="ActiveEncodeHandle.KillAndClear"/>).
    /// </summary>
    private static void KillEncodeTree(Process process)
        => process.Kill(entireProcessTree: true);

    /// <summary>
    /// The ONE kill-a-maybe-live-process idiom (JF-668; the supersede kill, the
    /// displacement kills, and the monitor's stall kill were three drifted
    /// copies): guard on HasExited, log at Information WHY, tree-kill
    /// (<see cref="KillEncodeTree"/>'s policy), and swallow only the exit race
    /// at Debug. The reason is a structured MESSAGE TEMPLATE with its args, so
    /// callers keep their exact pre-JF-668 wording and the log keeps named
    /// fields. KNOWN LIMITATION (gate-review F3, probed on MEL 9.0.11):
    /// threading the template as a value means the analyzer checks it in only
    /// one direction through this wrapper (extra args surface via CA2017; a
    /// template naming a placeholder whose arg was forgotten compiles clean),
    /// so template edits must be re-read against their args by eye. Does NOT
    /// dispose: the process's own owner (the monitor, the
    /// gate's exit-poll) keeps its lifecycle.
    /// </summary>
    /// <param name="process">The encode process to kill; may have exited (guard) or
    /// get disposed concurrently (the catch-race is expected and Debug-only).</param>
    /// <param name="reasonTemplate">The kill's structured log template (the caller's exact wording).</param>
    /// <param name="args">The template's arguments.</param>
    private void TryKillLiveProcess(Process process, string reasonTemplate, params object?[] args)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

#pragma warning disable CA2254 // the template is the caller's constant log wording, threaded as a value
            _logger.LogInformation(reasonTemplate, args);
#pragma warning restore CA2254
            KillEncodeTree(process);
        }
        catch (Exception ex)
        {
            // Exited or disposed between the check and the kill: the encode
            // ends on its own and whatever registry named it is updated either way.
#pragma warning disable CA2254 // same: the caller's constant wording
            _logger.LogDebug(ex, "Kill raced the exit: " + reasonTemplate, args);
#pragma warning restore CA2254
        }
    }

    /// <summary>
    /// JF-636: kill the RUNNING speed encodes of <paramref name="itemId"/> that the
    /// SAME device minted, other than <paramref name="activeCacheKey"/> (the launch
    /// this request is about to start supersedes them; see
    /// <see cref="_activeAudioSpeedEncodeProcesses"/>). Device-scoped by the
    /// <c>?d=</c> hint: another Echo may be actively consuming a different variant
    /// of the same item (tokens are item-scoped, queues per-device), and an entry
    /// without owner evidence is never killed (conservative). The killed encodes'
    /// own finally paths (gate release, unpin, flag clear, the monitor's disposal)
    /// react to the exit.
    /// </summary>
    /// <param name="itemId">GUID-validated item ID whose variants to supersede.</param>
    /// <param name="activeCacheKey">The cache key about to run; never killed.</param>
    /// <param name="requestingDeviceId">The <c>?d=</c> device hint of THIS launch; null disables the kill entirely.</param>
    private void KillSupersededSpeedEncodes(string itemId, string activeCacheKey, string? requestingDeviceId)
    {
        if (string.IsNullOrEmpty(requestingDeviceId))
        {
            return;
        }

        string prefix = $"{itemId}-speed-";
        foreach (var entry in _activeAudioSpeedEncodeProcesses)
        {
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal)
                || entry.Key == activeCacheKey
                || string.IsNullOrEmpty(entry.Value.OwnerDeviceId)
                || !string.Equals(entry.Value.OwnerDeviceId, requestingDeviceId, StringComparison.Ordinal))
            {
                continue;
            }

            RemoveSpeedEncodeAndKill(
                entry,
                "VideoAudio audio-speed HLS: killing superseded speed encode {CacheKey} for item {ItemId} (device {DeviceId} launched a new rate/start)",
                entry.Key, itemId, requestingDeviceId);
        }
    }

    /// <summary>
    /// Drop a live speed-encode registry entry and kill its process (JF-668:
    /// the remove+kill composite both registry kills share). Compare-and-remove
    /// on the OBSERVED pair (the JF-647 shape): never delete a newer
    /// registration the caller never inspected; the kill targets the observed
    /// process either way (a kill of an already-dead process is a no-op).
    /// </summary>
    /// <param name="entry">The registry entry the caller observed.</param>
    /// <param name="reasonTemplate">The kill's structured log template (the caller's exact wording).</param>
    /// <param name="args">The template's arguments.</param>
    private void RemoveSpeedEncodeAndKill(
        KeyValuePair<string, (Process Process, string? OwnerDeviceId)> entry,
        string reasonTemplate,
        params object?[] args)
    {
        _activeAudioSpeedEncodeProcesses.TryRemove(entry);
        TryKillLiveProcess(entry.Value.Process, reasonTemplate, args);
    }

    /// <summary>
    /// JF-668 (the JF-665 code-review follow-up): kill a registry entry that
    /// still names a LIVE encode under the cache key this request is about to
    /// re-encode, at the pre-start position inside the per-key lock, BEFORE
    /// the encode directory is recreated and the new ffmpeg starts. The JF-665
    /// registration-time kill fires only after the NEW encode's first-segment
    /// wait succeeds, and in that window (typically sub-second, worst case the
    /// ~20s wait ceiling) the abandoned prior ffmpeg keeps writing BY PATH
    /// into the recreated directory: its post-eviction segment opens and
    /// stream.m3u8 rewrites resolve by path at open time, so both processes
    /// can truncate each other's seg_NNNN.ts, and the new encode's liveness
    /// proof can even be satisfied by the prior encode's seg_0000.ts.
    /// Abandoned by construction, the same argument as the registration-time
    /// kill: reaching the encode branch with a live same-key entry requires
    /// the mid-encode eviction of that entry's cache directory. The
    /// registration-time displacement kill in
    /// <see cref="RegisterLiveSpeedEncode"/> stays as the backstop for any
    /// registry write this position cannot see.
    /// </summary>
    /// <param name="cacheKey">The speed variant's cache key about to re-encode.</param>
    private void KillDisplacedSpeedEncode(string cacheKey)
    {
        if (!_activeAudioSpeedEncodeProcesses.TryGetValue(cacheKey, out var displaced))
        {
            return;
        }

        // The per-key lock serializes registrations, so the only racer is a
        // displaced watcher removing an already-exited entry; the
        // compare-and-remove + kill contract lives in RemoveSpeedEncodeAndKill.
        RemoveSpeedEncodeAndKill(
            new KeyValuePair<string, (Process Process, string? OwnerDeviceId)>(cacheKey, displaced),
            "VideoAudio audio-speed HLS: killing displaced speed encode {CacheKey} (a same-key re-encode is restarting its evicted variant)",
            cacheKey);
    }

    /// <summary>
    /// Serve an individual HLS segment of the atempo speed variant (JF-636). A
    /// dedicated route because the cache directory is keyed by the variant key,
    /// which embeds the rate and start position: the path carries both so the
    /// key can be recomputed. Mirrors <see cref="GetEpisodeAudioSegment"/>
    /// (token validation, segment-name validation, hold-for-near-ahead-segment
    /// of a running encode).
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID (GUID-validated, token-scoped).</param>
    /// <param name="ratePerMille">The playback rate carried in the playlist's segment URLs.</param>
    /// <param name="startTicks">The encode start position carried in the playlist's segment URLs.</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_0000.ts").</param>
    /// <returns>The segment file.</returns>
    [HttpGet("~/alexaskill/api/audio-speed/{itemId}/{ratePerMille:int}/segments/{startTicks:long}/{segmentName}")]
    [AllowAnonymous]
    public async Task<ActionResult> GetAudioSpeedSegment(
        [FromRoute] string itemId,
        [FromRoute] int ratePerMille,
        [FromRoute] long startTicks,
        [FromRoute] string segmentName)
    {
        // Not ValidateSignedRoute (JF-651): the unserved-rate 400 sits between the
        // itemId 400 and the token 401; a combined preamble would move it to one
        // side, changing which error a bad-rate + bad-token request gets. The
        // resulting triply-invalid split vs the playlist sibling is ratified and
        // pinned by JF-671 (AudioSpeedRoutes_TriplyInvalid_GuidPlusUnservedRatePlusNoToken_RatifiedSplit
        // carries the rationale).
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out _))
        {
            return BadRequest(new { error = "Invalid itemId format" });
        }

        if (!Alexa.Util.PlaybackSpeed.IsValidPerMille(ratePerMille))
        {
            return BadRequest(new { error = "Unsupported playback rate" });
        }

        ActionResult? tokenError = ValidateStreamToken(itemId, out _);
        if (tokenError != null)
        {
            return tokenError;
        }

        if (!VideoAudioCache.IsValidSegmentName(segmentName))
        {
            _logger.LogWarning("VideoAudio audio-speed HLS: rejected invalid segment name '{SegmentName}' for item {ItemId}", segmentName, itemId);
            return BadRequest(new { error = "Invalid segment name" });
        }

        string cacheKey = AudioSpeedCacheKey(itemId, ratePerMille, startTicks);
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
        // Not ValidateSignedRoute (JF-651): the id is a parentId (the 400 body says
        // "Invalid parentId format") and this parse's GUID feeds the children query
        // below, which the itemId-shaped helper would neither produce nor preserve.
        if (string.IsNullOrWhiteSpace(parentId) || !Guid.TryParse(parentId, out Guid parentGuid))
        {
            return BadRequest(new { error = "Invalid parentId format" });
        }

        ActionResult? tokenError = ValidateStreamToken(parentId, out Guid[]? tokenLibraryScope);
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

        // JF-625: the concat endpoint also serves MUSIC ALBUMS (queue-as-concat in seek
        // mode): a MusicAlbum parent resolves its Audio children in disc/track order and
        // encodes with the album cover as the video track. Audiobook folders keep the
        // AudioBook children query and the black-frame encode.
        bool isMusicAlbum = parent is MediaBrowser.Controller.Entities.Audio.MusicAlbum;

        // JF-763: both isMusicAlbum arms route through the ONE album-tracks builder's
        // unpaged form (BuildAlbumTracksQueryUnpaged; the row-set rationale
        // lives on the builder's doc). JF-784 leg 3 closed the kind-axis
        // divergence on the other arm: the audiobook arm now routes through the
        // ONE chapters builder's unpaged form (BuildAudiobookChaptersQueryUnpaged)
        // instead of its local IncludeItemTypes=AudioBook initializer, so the
        // endpoint enumerates the SAME MediaTypes=Audio rows the PlayBook
        // head/confirm/tail queue (an Audio-typed chapter set, the metadata-remap
        // shape, launches instead of 404ing; a mixed folder concats all audio
        // children instead of the AudioBook subset). Still NO AlbumTrackOrder
        // here: the chapters leg carries the shared AudiobookChapterOrder
        // (JF-672; the probe-refuted successor of the old "DB order IS the
        // chapter order" claim, which held only for the tagged class), and this
        // path additionally applies its own filename-number chapter sort below
        // (the in-repo remedy for the untagged class, JF-790's precedent).
        var childrenQuery = isMusicAlbum
            ? Alexa.QueueContinuationFetcher.BuildAlbumTracksQueryUnpaged(jellyfinUser: null, parentGuid, byAlbumIds: false)
            : Alexa.QueueContinuationFetcher.BuildAudiobookChaptersQueryUnpaged(jellyfinUser: null, parentGuid);

        // JF-767 Finding B: the enumeration runs under the library scope the token
        // carries (both shapes: album tracks and audiobook chapters; row-neutral for
        // legitimately launched content since the parent was found under the same
        // scope, and the audiobook tail already scopes the same way). The scope rides
        // as raw config ids and is resolved ONCE per request here, with the SAME
        // resolver every paged query uses (one cache; serve-time resolution matches
        // what any current query would resolve), then applied to both arms through
        // the pre-resolved overload. A legacy or unrestricted token carries no scope
        // and enumerates unscoped, byte-identical to the pre-JF-767 behavior.
        // JF-784 leg 1 closed the residual JF-767 filed on this seam: a COMPLETED
        // cache entry encoded under a different timeline no longer serves
        // unchanged to a scoped request (the serve-time verdict compares the
        // encode-metadata sidecar against THIS enumeration's count and duration
        // sum, see ValidateAudiobookCacheAsync). RESIDUAL (accepted): the
        // DURING-ENCODE windows (the live-aware probe's own-live unread row and
        // the concurrent-encode prewrite/live guard below) still serve whatever
        // encode is running, with no timeline read: bounded by the encode
        // duration, self-healing at the first completed-cache verdict after the
        // encode exits, EXCEPT the tracker write that window records (the
        // foreign serve's segment fetches write positions under the shared book
        // key against the foreign timeline; filed as JF-787).
        Guid[]? tokenTopParents = tokenLibraryScope is null
            ? null
            : Alexa.Util.LibraryFilter.ResolveTopParentIds(tokenLibraryScope, _libraryManager, _logger);

        _logger.LogDebug("VideoAudio concat enumeration: token scope resolved ({LibCount} libraries) vs legacy unscoped", tokenTopParents?.Length ?? 0);
        Alexa.Util.LibraryFilter.ApplyLibraryFilter(childrenQuery, tokenTopParents);

        IReadOnlyList<MediaBrowser.Controller.Entities.BaseItem> chapters =
            _libraryManager.GetItemList(childrenQuery);

        // JF-625 review: split/malformed-folder albums (the JF-338 'Jazz Cafe' shape)
        // resolve in AlbumPlayService via an AlbumIds fallback; the endpoint's
        // ParentId-only query would 404 the very URL that service launched. Mirror
        // the fallback so both sides resolve a split album through the SAME arm
        // pair, under the SAME token scope as the primary arm.
        if (chapters.Count == 0 && isMusicAlbum)
        {
            var albumIdsRetryQuery = Alexa.QueueContinuationFetcher.BuildAlbumTracksQueryUnpaged(jellyfinUser: null, parentGuid, byAlbumIds: true);
            Alexa.Util.LibraryFilter.ApplyLibraryFilter(albumIdsRetryQuery, tokenTopParents);
            chapters = _libraryManager.GetItemList(albumIdsRetryQuery);
        }

        if (chapters.Count == 0)
        {
            // JF-784 leg 3: the arm enumerates the queue's MediaTypes=Audio
            // rows, so the empty row set means no audio children under the
            // (scoped) parent, not an AudioBook-kind filter miss.
            _logger.LogWarning("VideoAudio audiobook HLS: no audio chapters found under parent {ParentId}", parentId);
            return NotFound(new { error = "No audio chapters found" });
        }

        // JF-784 leg 1: the timeline identity the serve-time verdict compares
        // against the encode-metadata sidecar. Both numbers are exactly what the
        // encode writes into the sidecar over ITS enumeration, so a matching
        // scope/membership compares bit-identical (no tolerance) and any real
        // membership difference fails on count, duration, or both.
        long chapterDurationTicks = chapters.Sum(c => c.RunTimeTicks ?? 0);

        // Single chapter — use regular single-item HLS (no concat needed)
        if (chapters.Count == 1)
        {
            _logger.LogDebug("VideoAudio audiobook HLS: single chapter, serving single-item HLS inline for {ItemId} (token already validated against parentId)", chapters[0].Id);
            // Re-mint a chapter-scoped token: the playlist references segments by chapterId, not
            // parentId, so the Echo needs a token GetSegment will accept against chapterId.
            // JF-682: the gate above validated against a secret this re-read can find empty
            // (a config save between the two reads). Serving the gate's own 503 beats minting
            // an empty chapter token: that token flips every serve in the core into the
            // no-token branch, whose playlist carries token-less segment lines GetSegment
            // 401s, and since JF-678 the branch kicks a full re-encode before handing out
            // that dead playlist. The no-token branch itself stays as the last-resort
            // safety net for any future no-token shape. RESIDUAL (accepted): a secret
            // emptied AFTER this re-read yields a STALE-secret chapter token (the mint
            // consumes the local snapshot), which fails at GetSegment per segment: the
            // general mid-stream rotate/empty shape every token consumer shares, identical
            // on the multi-chapter path; this check closes only the gate-to-re-read
            // window because that is the one where the wasted re-encode kicked in.
            string? secret = Plugin.Instance?.Configuration?.StreamTokenSecret;
            if (string.IsNullOrEmpty(secret))
            {
                return StreamTokenSecretNotConfigured();
            }

            string chapterToken = StreamTokenHelper.Mint(chapters[0].Id.ToString(), secret);
            // JF-686: thread the resume position through (the core slices at the segment
            // length THIS path cuts; dropping it made one-chapter books restart at 0:00).
            return await StreamHlsVideoAudioCore(chapters[0].Id.ToString(), chapterToken, startTicks ?? 0).ConfigureAwait(false);
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
        HlsCacheProbe fastProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeAudiobookEncodes, parentId, artModifiedTicks).ConfigureAwait(false);
        if (fastProbe.Playlist != null)
        {
            FileInfo cached = fastProbe.Playlist;
            // JF-678: the vanish translation the fast paths have had since
            // JF-499 W3 (this row previously fell into
            // ServeAudiobookPlaylistAsync's generic catch, whose PhysicalFile
            // fallback 500s at result execution over the dead path); null falls
            // through to the concurrent-encode guard and the lock path below,
            // which re-encodes.
            ActionResult? fastServed = await TryServeValidatedHlsCacheAsync(
                parentId,
                "VideoAudio audiobook HLS",
                () => ValidateAudiobookCacheAsync(cached, chapters.Count, chapterDurationTicks, parentId, artModifiedTicks, fastProbe.OwnGenerationLiveOrRegistering),
                valid =>
                {
                    _logger.LogDebug("VideoAudio audiobook HLS: serving cached playlist for parent {ParentId}", parentId);
                    return ServeAudiobookPlaylistAsync(valid.Playlist.FullName, startTicks, valid.Content);
                }).ConfigureAwait(false);
            if (fastServed != null)
            {
                return fastServed;
            }
        }

        // Guard: if an encode is already running for this audiobook (from a concurrent
        // Echo Show request), serve the pre-written event playlist instead of starting
        // another ffmpeg. The pre-written playlist has correct total duration and no
        // ENDLIST, so the player plays available segments.
        // JF-678: this block's serves and the first-fetch prewrite row below are the
        // UNTRANSLATED rows of the vanish family (coverage boundary on
        // ResolveServeContentAsync).
        if (_activeAudiobookEncodes.TryGetValue(parentId, out _))
        {
            _logger.LogDebug("VideoAudio audiobook HLS: encode already in progress for {ParentId}, serving pre-written playlist", parentId);
            string hlsDir = _cache.GetHlsDirectoryPath(parentId, artModifiedTicks);
#pragma warning disable CA3003
            string prewrittenPath = Path.Combine(hlsDir, PrewrittenPlaylistFileName);
            if (System.IO.File.Exists(prewrittenPath))
            {
                return await ServeAudiobookPlaylistAsync(prewrittenPath, startTicks).ConfigureAwait(false);
            }

            // JF-625: album encodes write NO pre-written listing (the live-edge rule
            // at the prewrite site); their concurrent path serves ffmpeg's live listing,
            // whose edge tracks the encode instead of sitting at the album's end.
            if (isMusicAlbum)
            {
                string livePath = Path.Combine(hlsDir, "stream.m3u8");
                if (System.IO.File.Exists(livePath))
                {
                    // Same cold-entry resume rule as the first fetch below: slicing a
                    // still-growing listing at a not-yet-encoded segment yields an
                    // empty playlist, so the offset drops on EVERY cold serve path.
                    if (startTicks is > 0)
                    {
                        _logger.LogInformation(
                            "VideoAudio album HLS: concurrent cold serve for parent {ParentId} drops startTicks={StartTicks}",
                            parentId, startTicks);
                        startTicks = null;
                    }

                    return await ServeAudiobookPlaylistAsync(livePath, startTicks).ConfigureAwait(false);
                }
            }

            _logger.LogWarning("VideoAudio audiobook HLS: pre-written playlist not available for {ParentId}, returning 503", parentId);
            return StatusCode(503, "Encode in progress");
        }
#pragma warning restore CA3003

        // Cache miss — acquire per-parent lock
        using (await LockHlsItemAsync(parentId, artModifiedTicks).ConfigureAwait(false))
        {
            _cache.CleanupHlsStub(parentId, artModifiedTicks);

            // Double-check cache after acquiring lock (another request's encode may
            // have completed)
            HlsCacheProbe inLockProbe = await GetCachedHlsPlaylistLiveAwareAsync(_activeAudiobookEncodes, parentId, artModifiedTicks).ConfigureAwait(false);
            if (inLockProbe.Playlist != null)
            {
                FileInfo cached = inLockProbe.Playlist;
                // JF-678: vanish translation, same as the fast-path row above;
                // null falls through to this scope's own concat+encode branch.
                ActionResult? concurrentServed = await TryServeValidatedHlsCacheAsync(
                    parentId,
                    "VideoAudio audiobook HLS",
                    () => ValidateAudiobookCacheAsync(cached, chapters.Count, chapterDurationTicks, parentId, artModifiedTicks, inLockProbe.OwnGenerationLiveOrRegistering),
                    valid =>
                    {
                        _logger.LogDebug("VideoAudio audiobook HLS: serving playlist generated by concurrent request for parent {ParentId}", parentId);
                        return ServeAudiobookPlaylistAsync(valid.Playlist.FullName, startTicks, valid.Content);
                    }).ConfigureAwait(false);
                if (concurrentServed != null)
                {
                    return concurrentServed;
                }

                // JF-678 rework F1: the vanish fall-through's breach-vs-legitimate
                // liveness guard (the rationale lives on GuardInLockVanishFallThrough).
                GuardInLockVanishFallThrough(_activeAudiobookEncodes, parentId, artModifiedTicks, cached.FullName, "VideoAudio audiobook HLS");
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

            var sortedChapters = isMusicAlbum
                ? chapters.ToList()
                : chapters
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
            // JF-784 leg 1: the count and duration sum are ALSO the timeline
            // identity the serve-time verdict compares against the requesting
            // scope's live enumeration (the sidecar is what makes the shared
            // cache scope-aware without a key change).
            var encodeMetadata = new
            {
                ExpectedChapterCount = chapters.Count,
                // The same local the verdict compares at serve time: the two
                // expressions must stay bit-identical for a matching timeline.
                ExpectedDurationTicks = chapterDurationTicks,
                ParentId = parentId,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            string metadataPath = Path.Combine(hlsDir, EncodeMetadataFileName);
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

            string? collectionArtUrl = isMusicAlbum ? ResolveArtUrl(parent, serverUrl) : null;
            // JF-625 review (empirically verified with ffmpeg 8.1.2): -c:a copy across a
            // concat of MIXED codecs declares the first input's codec in the PMT and
            // silently truncates the whole output at the first non-matching track (exit 0,
            // no error - a single iTunes M4A among MP3s ends the album early). Copy ONLY
            // when every child's codec is copy-compatible or NONE is resolvable (the
            // all-unknown row keeps the pre-gate copy per the JF-784 tail F2); any
            // resolved non-copy codec, or an unknown among resolved ones, transcodes
            // the whole concat to AAC (slower but complete).
            // JF-784 review F1: the gate covers BOTH arms. The audiobook arm
            // used to hardcode copy on the assumption its AudioBook-kind
            // chapters were codec-uniform; since JF-784 leg 3 the arm
            // enumerates the queue's MediaTypes=Audio rows, where a remapped
            // sibling can bring a foreign codec into the same concat, and a
            // copy over mixed codecs is the silent-truncation shape (exit 0,
            // partial audio, then the undercount verdict re-encodes into the
            // same truncation forever).
            bool allCodecsUnresolvable = sortedChapters.Count > 0
                && sortedChapters.All(c => ResolveSourceAudioCodec(c) is null);
            bool albumAudioCopy = sortedChapters.Count > 0
                && (allCodecsUnresolvable
                    || sortedChapters.All(c => ResolveSourceAudioCodec(c) is { } codec && CopyCompatibleAudioCodecs.Contains(codec)));
            if (!albumAudioCopy)
            {
                _logger.LogInformation(
                    "VideoAudio concat HLS: mixed or non-copy audio codecs in '{ItemName}', transcoding the concat to AAC ({TrackCount} tracks)",
                    parent.Name, sortedChapters.Count);
            }
            else if (allCodecsUnresolvable)
            {
                // JF-784 gate-marker tail F2: unknown is missing information,
                // not confirmed incompatibility. Fail-closing here burned a
                // full-book AAC transcode (tens of minutes for an 8-10h book)
                // on a transient codec-resolution failure; the pre-gate copy
                // behavior is kept, and a genuinely foreign codec still
                // transcodes through the resolved arm above.
                _logger.LogDebug(
                    "VideoAudio concat HLS: no chapter audio codec resolvable for '{ItemName}' ({TrackCount} tracks), keeping -c:a copy",
                    parent.Name, sortedChapters.Count);
            }

            var ffmpegArgs = BuildHlsAudiobookFfmpegArguments(
                concatListPath, collectionArtUrl, collectionArtUrl == null, playlistPath, segmentPath, hlsBaseUrl, albumAudioCopy);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("VideoAudio audiobook HLS: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
            }

            // Register the resolved (single-root: cache-root generation) HLS
            // directory and mark this audiobook as actively encoding BEFORE
            // starting ffmpeg, through the JF-782 register-before-mark
            // one-wrapper
            // (the mark-before-start invariant all four HLS paths share; moved
            // here in the JF-676 rework, from after the prewrite): the
            // ticks-scoped debris verdict can only see a playlist once ffmpeg
            // writes it, and by then this generation is already live, so the
            // verdict holds instead of deleting the running encode's directory
            // (the gate review's race: the mark used to sit after the process
            // start, and a verdict landing between ffmpeg's first playlist
            // write and the mark classified the genuinely-live no-ENDLIST
            // playlist as debris). Also prevents concurrent ffmpeg launches
            // (the guard reads bare presence). The monitor clears the flag on
            // exit (generation-aware, JF-665/JF-669).
            ActiveEncodeHandle activeEncode = RegisterAndMarkEncodeActive(_activeAudiobookEncodes, parentId, artModifiedTicks, hlsDir);

            Process ffmpegProcess;
            try
            {
                // Start ffmpeg in the background: it writes to stream.m3u8,
                // not our pre-written file.
                ffmpegProcess = await StartFfmpegProcessGatedAsync(
                    ffmpeg,
                    ffmpegArgs,
                    isMusicAlbum && collectionArtUrl != null
                        ? EstimateArtEncodeBytes(chapterDurationTicks)
                        : EstimateEncodeBytes(chapterDurationTicks),
                    hlsDir).ConfigureAwait(false);
            }
            catch
            {
                activeEncode.Clear();
                throw;
            }

            // Pre-write a complete HLS playlist to a SEPARATE file from what ffmpeg
            // uses. This gives the Echo Show the correct total book duration
            // immediately. ffmpeg writes to stream.m3u8 (its own file); after it
            // completes, stream.m3u8 becomes the cached playlist. The pre-written
            // file has NO ENDLIST, so the player treats it as an event playlist and
            // plays available segments without failing on missing ones.
            // JF-536: written only AFTER the pin inside StartFfmpegProcessGatedAsync
            // succeeded (the JF-428 rule: a listing written before the pin can be
            // deleted by a concurrent eviction sweep in the creation-to-pin window,
            // silently reverting the serve to the live playlist). Since the JF-676
            // rework moved the active-encode mark ABOVE the process start, the
            // prewrite now lands AFTER the flag: a concurrent guard fetch inside
            // the [mark, prewrite] window finds the flag set with no listing on
            // disk and takes its designed 503 ("Encode in progress") once; the
            // Echo's retry lands after the listing. That transient replaces the
            // arming race the old ordering left for the debris verdict.
            string prewrittenPath = Path.Combine(hlsDir, PrewrittenPlaylistFileName);
            string? token = HttpContext.Request.Query["token"];
            // JF-625: albums SKIP the pre-written full listing entirely. The Echo joins
            // a no-ENDLIST listing at its LIVE EDGE, so a full listing puts the edge at
            // the album's end (the dead-player shape, live 2026-09-24); albums serve the
            // ffmpeg live listing on every in-progress path instead (the first fetch and
            // the concurrent-encode guard). Audiobooks keep the pre-write.
            if (!isMusicAlbum)
            {
                try
                {
                    WriteAudiobookPlaylist(prewrittenPath, hlsBaseUrl, sortedChapters, token);
                }
                catch
                {
                    // Pre-handoff failure: this scope still owns the process and
                    // the flag (no monitor was started yet), so the JF-637
                    // triple: kill, dispose, clear this generation's slot.
                    activeEncode.KillAndClear(ffmpegProcess);
                    throw;
                }
            }

            // Monitor ffmpeg in background: logs errors, triggers eviction when done.
            // CA2025: started via the boundary helper (this method never disposes the
            // process). The HasExited/ExitCode reads below are best-effort; their
            // InvalidOperationException catch already tolerates the monitor's raced
            // disposal (the monitor's finally is the sole owner).
            StartHlsMonitor(ffmpegProcess, hlsDir, parentId, artModifiedTicks, "Audiobook", activeEncode);

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

            // JF-625 (live 2026-09-24, Temple of the Dog): for MUSIC ALBUMS serve
            // ffmpeg's LIVE playlist on the first fetch, NOT the pre-written full
            // listing. The Echo joins a no-ENDLIST playlist at its LIVE EDGE: a full
            // pre-write (every album segment listed) put the edge at the album's END,
            // and the player immediately requested the not-yet-encoded tail segment
            // (seg_0335 at encode segment 0, three 404s, dead player). ffmpeg's live
            // listing names only encoded-so-far segments, so the edge is ~0 and the
            // player starts at the album's beginning; event-playlist reloads extend
            // the listing forward while the encode runs. The seek bar shows the
            // encoded-so-far window during the encode (the JF-531 cosmetic cost) and
            // settles to the full album duration once the ENDLIST playlist takes over
            // via the cache-hit paths. Audiobooks keep the pre-write: their
            // verified-live flow depends on it.
            // JF-625 review: a failed first segment (unreadable art input, cold network
            // library, early ffmpeg death) must NOT fall through to serving a missing
            // playlist file (unhandled FileNotFoundException -> bare 500); mirror the
            // song path's controlled failure.
#pragma warning disable CA3003 // firstSegmentPath derives from the GUID-validated parentId
            if (!System.IO.File.Exists(firstSegmentPath))
            {
                _logger.LogWarning(
                    "VideoAudio album HLS: no first segment appeared for parent {ParentId}, failing the request cleanly",
                    parentId);
                try { if (!ffmpegProcess.HasExited) { ffmpegProcess.Kill(); } } catch { /* already exited */ }
                return StatusCode(500, new { error = "Album encode failed to start" });
            }
#pragma warning restore CA3003

            if (isMusicAlbum)
            {
                // Cold-cache resume guard: the resume slice applies to the COMPLETE
                // listing (the cache-hit paths); slicing the still-growing live listing
                // at a not-yet-encoded segment would yield an empty playlist and a dead
                // player. Dropping the offset here (start from the album's beginning)
                // is the benign degradation; the common resume case replays an already
                // encoded album and takes the cache-hit path with the correct slice.
                if (startTicks is > 0)
                {
                    _logger.LogInformation(
                        "VideoAudio album HLS: cold-cache resume for parent {ParentId} (startTicks={StartTicks}) dropped; serving the live playlist from the album's beginning",
                        parentId, startTicks);
                    startTicks = null;
                }

                _logger.LogDebug(
                    "VideoAudio album HLS: serving ffmpeg live playlist for parent {ParentId} ({TrackCount} tracks)",
                    parentId, sortedChapters.Count);

                // JF-678 rework F3: existenceConfirmed is FALSE here, the only
                // DEGRADED row inside the audiobook serve family: the
                // first-segment wait above polls the SEGMENT, not the
                // playlist, so this serve may legitimately run one flush cycle
                // before ffmpeg's first stream.m3u8 write; a read miss must
                // keep the PhysicalFile degrade, not surface as a vanish.
                // (The song/episode/variant first-fetch tails carry the same
                // lag class pre-existing and unhandled; the coverage boundary
                // on ResolveServeContentAsync records it.)
                return await ServeAudiobookPlaylistAsync(playlistPath, startTicks, existenceConfirmed: false).ConfigureAwait(false);
            }

            _logger.LogDebug(
                "VideoAudio audiobook HLS: serving pre-written playlist for parent {ParentId} ({ChapterCount} chapters)",
                parentId, sortedChapters.Count);

            // Serve the pre-written event playlist (no ENDLIST) so the Echo Show gets
            // the correct total book duration. The player treats it as an event playlist
            // and plays available segments without failing on missing ones. ffmpeg generates
            // segments in the background, staying ahead of real-time playback. The
            // prewrite is plugin-written synchronously above, so no flush-lag
            // boundary applies (the confirmed default).
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
        ActionResult? routeError = ValidateSignedRoute(itemId);
        if (routeError != null)
        {
            return routeError;
        }

        // Validate segment name to prevent directory traversal
        if (!VideoAudioCache.IsValidSegmentName(segmentName))
        {
            _logger.LogWarning("VideoAudio: rejected invalid segment name '{SegmentName}' for item {ItemId}", segmentName, itemId);
            return BadRequest(new { error = "Invalid segment name" });
        }

        // Record audiobook playback progress via the segment request. Best-effort:
        // never fail the segment request over tracking. The item-derived gate and the
        // read-key rationale live once, on RecordPositionProgress below.
        RecordPositionProgress(itemId, segmentName);

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
    /// Best-effort audiobook position tracking: parse the segment number out of a
    /// <c>seg_NNN[N].ts</c> name and record it under the key the resume path reads.
    /// JF-499 W1 (restart-safe gate, derived from the library item so the skip survives
    /// a restart; the old <c>_episodeHlsItems</c> flag was process-static and cached
    /// episodes resumed growing the file) plus the JF-694 read-key alignment:
    /// <list type="bullet">
    /// <item>Folders record under their own id: the multi-chapter audiobook concat and
    /// the JF-625 album concat both key segments by the parent Folder, whose 10s
    /// timeline is exactly the tracker's read arithmetic. Album entries are
    /// LOAD-BEARING: in seek mode AlbumPlayService reads the album key as the resume
    /// truth (JF-625 criterion 3), so this arm must stay untouched.</item>
    /// <item>An AudioBook LEAF with a ParentId that resolves to a Folder records
    /// under the ONE verdict-aware key (<see cref="AudiobookItems.ResolveTrackedBookKey"/>):
    /// the book FOLDER's id when the leaf sits directly inside it (the multi-chapter
    /// book and the one-chapter redirect shape, where the leaf's own timeline IS the
    /// book timeline), or the leaf's OWN id when the ParentId is a shared container
    /// of sibling books (JF-794 gate-marker blocker 2: the raw ParentId key blended
    /// the container's books, discarding one book's records below another's
    /// high-water mark). The leaf's 4s song-core index is translated onto the
    /// tracker's 10s concat timeline by
    /// <see cref="Alexa.Playback.AudiobookPositionTracker.RecordScaledSegment"/>.
    /// A root-level single-file book (EMPTY ParentId) and a leaf with a DANGLING
    /// ParentId are deliberately NOT recorded: their raw keys are write-only dead
    /// weight (the root key especially: the concat endpoint's children query under
    /// a leaf returns zero chapters, so that key must stay cold for resume to keep
    /// the working plain single-item launch).</item>
    /// <item>Every other leaf (episodes, songs) is skipped: its key is write-only dead
    /// weight in the persisted positions file. Deliberately NOT
    /// <c>AudiobookItems.IsAudioBookOrChapter</c>: that helper's plain-Audio arm would
    /// key song fetches under ancestor ids no mint site reads, and where the ancestor
    /// IS an album the write is worse than dead weight, because the album key IS read
    /// back (see the first bullet) and a track-local 4s index read on the 10s timeline
    /// would poison AlbumPlayService's resume mapping.</item>
    /// </list>
    /// Not memoized: the verdict is a durable fact, but <c>GetItemById</c> is already
    /// an in-memory lookup on the platform side (and
    /// <see cref="ValidateVideoAudioRequest"/> already calls it unmemoized per
    /// playlist request), so a plugin-side memo would only duplicate the platform's
    /// cache.
    /// </summary>
    /// <param name="itemId">Item ID from the segment URL (any GUID format).</param>
    /// <param name="segmentName">The segment file name (e.g. "seg_0042.ts").</param>
    private void RecordPositionProgress(string itemId, string segmentName)
    {
        if (!Guid.TryParse(itemId, out Guid itemGuid)
            || !TryParseSegmentNumber(segmentName, out int segmentNumber))
        {
            return;
        }

        try
        {
            BaseItem? item = _libraryManager.GetItemById(itemGuid);
            if (item is MediaBrowser.Controller.Entities.Folder)
            {
                Plugin.Instance?.AudiobookPositionTracker?.RecordSegment(itemId, segmentNumber);
            }
            else if (item != null
                && AudiobookItems.IsAudioBook(item)
                && item.ParentId != Guid.Empty
                && _libraryManager.GetItemById(item.ParentId) is Folder)
            {
                // JF-794 gate-marker blocker 2: the key is the ONE verdict-aware key
                // (AudiobookItems.ResolveTrackedBookKey), so a leaf under a SHARED
                // container records under its OWN id instead of the container key the
                // sibling books also write (the raw GetAudiobookBookKey blend: book
                // B's records were discarded below book A's high-water mark and B's
                // resume read A's position). The added Folder resolution is one
                // in-memory platform-cache lookup per segment fetch (the cost note on
                // the helper). A leaf whose ParentId resolves to NO Folder (dangling)
                // records nothing: the key would be write-only dead weight.
                Plugin.Instance?.AudiobookPositionTracker?.RecordScaledSegment(
                    AudiobookItems.ResolveTrackedBookKey(item, _libraryManager),
                    segmentNumber,
                    SongHlsSegmentSeconds);
            }
        }
        catch (Exception ex)
        {
            // Best-effort gate, same contract as the tracking itself: never fail the
            // segment request over it.
            _logger.LogDebug(ex, "Position-tracking gate: item lookup failed for {ItemId}", itemId);
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
    /// JF-503 device failure), the AUDIOBOOK concat
    /// (<see cref="_activeAudiobookEncodes"/>: its pre-written event playlist lists
    /// every segment from first play, so a seek during the first minutes hits the same
    /// listed-but-missing 404), and the SINGLE-ITEM path
    /// (<see cref="_activeVideoAudioEncodes"/>, JF-536: it now serves the same kind of
    /// pre-written event playlist, and the single-chapter audiobooks it also serves
    /// encode for minutes, not seconds). The pre-JF-536 song exclusion rationale (no
    /// flag, live playlist only, seconds-long encode) died with the prewrite.
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
            || _activeAudiobookEncodes.ContainsKey(itemId)
            || _activeVideoAudioEncodes.ContainsKey(itemId);

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
    /// the hold-for-segment near-ahead test. JF-778: also defines the head cap of the
    /// windowed prewrite serve (one enumeration per playlist fetch while an encode
    /// is live; the player polls every few seconds, never per segment). Never
    /// called on the per-segment hot path, so a directory enumeration here is
    /// fine.
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
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // Evicted between resolution and enumeration, or read-denied (a chmod'd
            // cache root must not 500 the serve path, JF-499 W4); treat as empty.
        }

        return highest;
    }

    /// <summary>
    /// The one registry-selector definition shared by the three test seams
    /// (JF-669): <paramref name="audiobook"/> selects the audiobook registry
    /// and <paramref name="song"/> the single-item registry instead of the
    /// episode one, mirroring which endpoint would have set the flag in
    /// production. One definition so a fourth seam or a registry rename can
    /// never leave the siblings drifting.
    /// </summary>
    private static ConcurrentDictionary<string, ActiveEncodeGenerations> EncodeRegistryFor(bool audiobook, bool song)
        => audiobook ? _activeAudiobookEncodes : song ? _activeVideoAudioEncodes : _activeEpisodeEncodes;

    /// <summary>
    /// Internal test seam (JF-503, InternalsVisibleTo): set or clear the active-encode
    /// flag the hold-for-segment path keys on, without a live ffmpeg process,
    /// with the registry chosen by <see cref="EncodeRegistryFor"/>. Setting uses
    /// the PRODUCTION idiom (<see cref="MarkEncodeActive"/>, JF-665/JF-669): a
    /// fresh generation takes over the key even when a prior flag is still set,
    /// so a test can simulate the newer same-key generation of the re-register
    /// race. The simulated generation lands in the ticks-0 slot (the no-art
    /// sentinel: every flag-test fixture's real generation also encodes an item
    /// without images, so this displaces same-ticks exactly like the production
    /// re-register).
    /// </summary>
    /// <param name="itemId">The item ID (episode itemId or audiobook parentId).</param>
    /// <param name="active">True to mark an encode active, false to clear it.</param>
    /// <param name="audiobook">True to target the audiobook registry (default episode).</param>
    /// <param name="song">True to target the single-item registry (default episode).</param>
    /// TEST-SEAM HAZARD: the clear arm hard-removes the whole registry entry,
    /// bypassing the holder gate that closes the register-vs-remove race; safe
    /// only on fresh-Guid keys or after the monitors already cleared (all current
    /// callers) - do not call it concurrent with a production-marked generation.
    internal static void SetEncodeActiveForTest(string itemId, bool active, bool audiobook = false, bool song = false)
    {
        var registry = EncodeRegistryFor(audiobook, song);
        if (active)
        {
            MarkEncodeActive(registry, itemId, artModifiedTicks: 0);
        }
        else
        {
            registry.TryRemove(itemId, out _);
        }
    }

    /// <summary>
    /// Internal test seam (JF-681, InternalsVisibleTo): force the MID-REGISTRATION
    /// window on <paramref name="itemId"/>'s active-encode flag, with the registry
    /// chosen by <see cref="EncodeRegistryFor"/>. The window is the brief production
    /// state between <see cref="ActiveEncodeHandle.MarkActive"/>'s registry store and
    /// its first slot write (<c>GetOrAdd</c> before <see cref="ActiveEncodeGenerations.RegisterIfStored"/>):
    /// a stored holder with ZERO slots, where the four prewrite serve gates'
    /// strict reader (<see cref="OwnTicksGenerationLive"/>) answers FALSE while the
    /// debris verdicts' conservative reader
    /// (<see cref="OwnTicksGenerationLiveOrRegistering"/>) answers TRUE - the
    /// strict-vs-conservative split the full-slot seam
    /// (<see cref="SetEncodeActiveForTest"/>) cannot produce, because a written slot
    /// makes both predicates agree. This seam is the only way a pin can hold that
    /// state deterministically. The registering arm REPLACES any stored holder with
    /// a fresh zero-slot one (the indexer set, so the guarantee holds regardless of
    /// prior state); the clear arm delegates to
    /// <see cref="SetEncodeActiveForTest"/>'s clear arm (whose TEST-SEAM HAZARD
    /// doc is the canonical one: fresh-Guid keys only, which is every current
    /// caller). Test-only: production never calls this.
    /// </summary>
    /// <param name="itemId">The item ID (episode itemId, song itemId, or audiobook parentId).</param>
    /// <param name="registering">True to store the zero-slot mid-registration holder, false to clear it.</param>
    /// <param name="audiobook">True to target the audiobook registry (default episode).</param>
    /// <param name="song">True to target the single-item registry (default episode).</param>
    internal static void SetEncodeRegisteringForTest(string itemId, bool registering, bool audiobook = false, bool song = false)
    {
        if (registering)
        {
            var registry = EncodeRegistryFor(audiobook, song);
            registry[itemId] = new ActiveEncodeGenerations(registry, itemId);
        }
        else
        {
            SetEncodeActiveForTest(itemId, active: false, audiobook, song);
        }
    }

    /// <summary>
    /// Internal test seam (JF-665, InternalsVisibleTo): whether an active-encode
    /// flag is currently set for <paramref name="itemId"/>, with the same
    /// registry selectors as <see cref="SetEncodeActiveForTest"/>. Read-only.
    /// </summary>
    /// <param name="itemId">The item ID (episode itemId or audiobook parentId).</param>
    /// <param name="audiobook">True to target the audiobook registry (default episode).</param>
    /// <param name="song">True to target the single-item registry (default episode).</param>
    internal static bool EncodeActiveForTest(string itemId, bool audiobook = false, bool song = false)
        => EncodeRegistryFor(audiobook, song).ContainsKey(itemId);

    /// <summary>
    /// Internal test seam (JF-669, InternalsVisibleTo): how many art-tick
    /// generations of <paramref name="itemId"/>'s active-encode flag are
    /// currently live, with the same registry selectors as
    /// <see cref="SetEncodeActiveForTest"/>. Read-only; 0 when no flag entry
    /// exists. Lets a pin wait for ONE generation's clear to land without
    /// conflating it with another generation's liveness (plain
    /// <see cref="EncodeActiveForTest"/> cannot distinguish them). Can read 0
    /// while <see cref="EncodeActiveForTest"/> reads true in the brief
    /// mid-registration window before the first slot write.
    /// </summary>
    /// <param name="itemId">The item ID (episode itemId or audiobook parentId).</param>
    /// <param name="audiobook">True to target the audiobook registry (default episode).</param>
    /// <param name="song">True to target the single-item registry (default episode).</param>
    internal static int EncodeGenerationCountForTest(string itemId, bool audiobook = false, bool song = false)
        => EncodeRegistryFor(audiobook, song).TryGetValue(itemId, out ActiveEncodeGenerations? generations) ? generations.Count : 0;

    /// <summary>
    /// Internal test seam (JF-677, InternalsVisibleTo; same observer shape as
    /// <see cref="FfmpegProcessStartedForTest"/>): when non-null, invoked after
    /// EVERY full playlist content read routed through
    /// <see cref="ReadPlaylistContentAsync"/>, with the path read. Lets a pin
    /// count full reads per playlist file (the JF-677 proof burden: a
    /// validated warm-cache serve performs exactly one read of stream.m3u8).
    /// Null in production; read-only observer.
    /// </summary>
    internal Action<string>? PlaylistContentReadForTest { get; set; }

    /// <summary>
    /// Internal test seam (JF-782, InternalsVisibleTo): when non-null, invoked
    /// between the live-aware probe wrapper's liveness read and the probe's
    /// consumption of that answer, with the cache key and the read's answer.
    /// The gap this exists to expose: <see cref="PlaylistContentReadForTest"/>
    /// fires after BOTH the probe's read and the verdict's re-read, so it can
    /// never plant a mark (or any registry mutation) inside the probe-to-verdict
    /// straddle; this seam is the only deterministic way to pin that window (a
    /// hook body may call the SetEncode*ForTest seams to land the mark in the
    /// gap, the JF-782 leg 1 shape). Read-only observer semantics otherwise:
    /// production never sets it, and the invoke cannot change the answer it
    /// just observes.
    /// </summary>
    internal Action<string, bool>? ProbeLivenessReadForTest { get; set; }

    /// <summary>
    /// Internal test seam (JF-681, InternalsVisibleTo; same observer shape as
    /// <see cref="FfmpegProcessStartedForTest"/>): when non-null, invoked at the
    /// ENTRY of each HLS in-lock double-check scope, with the path's cache key,
    /// the moment the per-item lock is held. Gives the in-lock pins a
    /// DETERMINISTIC in-lock-vs-fast-path attribution: the test helper's 400ms
    /// park assert cannot distinguish a parked request from one whose pre-lock
    /// phase stalled past the window and then served from the FAST path (whose
    /// own-live gate reads the same predicate at the same ticks and produces a
    /// byte-identical serve), while this probe can only fire inside the lock
    /// scope, so a pin asserting it can never be satisfied by a fast-path serve.
    /// Scoped deliberately to the HLS scopes the pins exercise: the MP4 path has
    /// no in-lock pins and keeps its direct
    /// <see cref="VideoAudioCache.LockItemAsync"/> call. Fires once per scope
    /// ENTERED, so a single-chapter audiobook redirect (audiobook core into the
    /// song core) fires it twice; the pins read only the fired/never-fired
    /// distinction. Null in production; read-only observer, no behavior.
    /// </summary>
    internal Action<string>? InLockWarmCacheProbeForTest { get; set; }

    /// <summary>
    /// The ONE HLS per-item lock acquisition (JF-681): awaits the cache lock and
    /// fires <see cref="InLockWarmCacheProbeForTest"/> with the key before the
    /// caller's scope body runs, so every HLS scope that routes through this
    /// wrapper is born probed and the probe's rationale lives here once (the
    /// <see cref="MarkEncodeActive"/> one-wrapper idiom: call sites cannot
    /// drift). The MP4 path deliberately stays on the direct
    /// <see cref="VideoAudioCache.LockItemAsync"/> call. Production behavior is
    /// the lock alone; the invoke is a no-op while the seam is null.
    /// </summary>
    /// <param name="cacheKey">The per-item lock key (the path's cache key).</param>
    /// <param name="artModifiedTicks">The art ticks half of the lock key.</param>
    /// <returns>The lock scope disposable (release on dispose, unchanged).</returns>
    private async Task<IDisposable> LockHlsItemAsync(string cacheKey, long artModifiedTicks)
    {
        IDisposable gate = await _cache.LockItemAsync(cacheKey, artModifiedTicks).ConfigureAwait(false);
        try
        {
            InLockWarmCacheProbeForTest?.Invoke(cacheKey);
            return gate;
        }
        catch
        {
            // A throwing observer (a test lambda asserting inside the seam) must
            // never orphan the acquired gate: the caller's using never binds when
            // this throws, so without this dispose the per-item lock would stay
            // held for the process lifetime. Rethrow unchanged so the pin sees
            // the observer's own exception.
            gate.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The ONE full playlist content read on the verdict/serve paths (JF-677):
    /// the verdict's validating read and every serve that cannot reuse
    /// preloaded content routes here, so the read-count seam
    /// (<see cref="PlaylistContentReadForTest"/>) sees every read. NOT routed
    /// here: <see cref="IsSegmentListedInLivePlaylistAsync"/> (the GetSegment
    /// hold probe, a different lifetime) and the monitor's reads.
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file to read.</param>
    /// <returns>The full file content.</returns>
    private async Task<string> ReadPlaylistContentAsync(string playlistPath)
    {
#pragma warning disable CA3003 // path derived from GUID-validated keys upstream (every caller)
        string content = await System.IO.File.ReadAllTextAsync(playlistPath).ConfigureAwait(false);
#pragma warning restore CA3003
        PlaylistContentReadForTest?.Invoke(playlistPath);
        return content;
    }

    /// <summary>
    /// Resolve the content a SERVE hands out (JF-677): the verdict's
    /// already-read content when threaded through, else a fresh full read. The
    /// threaded path FIRST probes the file's existence and throws the same
    /// <see cref="FileNotFoundException"/> the fresh read would throw when the
    /// playlist vanished between the verdict and the serve: that is the JF-499
    /// W3 vanish-at-serve contract, which must survive losing the second full
    /// read. A vanished playlist means a vanished generation directory,
    /// segments included, so serving the remembered bytes would hand the
    /// player a playlist of dead segment links. COVERAGE BOUNDARY of that
    /// throw (widened by JF-678, reworked by its review round; THIS block is
    /// the ONE authoritative account of the vanish family's coverage, every
    /// other site points here):
    /// TRANSLATED rows (the throw becomes a null the caller falls through
    /// on, via <see cref="TryServeValidatedHlsCacheAsync"/>): the three FAST
    /// paths (song, episode, variants), which fall through to the
    /// lock+re-encode (the pre-JF-678 shape; a breach reached through them is
    /// re-caught by the in-lock row below, pre-existing and out of scope: a
    /// later request finding NO cache at all takes the plain cache-miss path,
    /// which cannot distinguish a breach from a miss); the three IN-LOCK
    /// verdict+serve rows and the two audiobook verdict rows, which fall
    /// through to their own encode branch ONLY after
    /// <see cref="GuardInLockVanishFallThrough"/> rules out the breach shape
    /// (a vanish while a generation of the key is STILL LIVE is a JF-428 pin
    /// breach and fails loud, because falling through would start a second
    /// ffmpeg against the live writer).
    /// UNTRANSLATED rows, by design: the exists-gated serves of a LIVE
    /// pinned encode's files (the audiobook concurrent-guard rows, the
    /// prewrite helpers' fast-path-gate/first-serve call sites, the audiobook
    /// first-fetch PREWRITE row), where a vanish means the pin protocol
    /// itself failed and surfaces as an action-time 500 with a stack, never
    /// masked behind a retryable 503, and no re-encode fall-through exists
    /// structurally (the caller already holds the lock and just ran the
    /// encode). The DEGRADED row: the audiobook ALBUM first-fetch live serve
    /// (<c>existenceConfirmed: false</c> on
    /// <see cref="ServeAudiobookPlaylistAsync"/>), whose path ffmpeg may not
    /// have flushed yet (the first-segment wait polls the segment, not the
    /// playlist), so a read miss keeps the generic catch's PhysicalFile
    /// degrade there. The SAME flush-lag class exists, PRE-EXISTING and
    /// unhandled, on the song/episode/variant FIRST-FETCH tails (the segment
    /// wait then an unwrapped fresh read through
    /// <see cref="ServePlaylistWithTokenAsync"/>): they keep that behavior,
    /// not widened by this rework; the class is closed family-wide only
    /// inside the audiobook serve methods, which own the degrade.
    /// OUTSIDE the contract: the MP4 sibling endpoint
    /// (<see cref="StreamVideoAudio"/>), which serves one cached file with no
    /// segment directory, self-heals as a plain cache miss on the Echo's
    /// retry, and shares none of the serve plumbing hardened here. The
    /// probe is an O(1) stat, so the validated warm-cache serve pays ONE full
    /// read (the verdict's) plus one existence check. BOUNDED RESIDUAL (the
    /// probe tests existence, not generation): if eviction deletes the
    /// directory and a concurrent lock-holder's re-encode RECREATES
    /// stream.m3u8 as a fresh partial inside the sub-millisecond
    /// verdict-to-serve window, the probe passes and the serve hands out the
    /// old generation's remembered ENDLIST bytes over the partially recreated
    /// directory. THE ASYMMETRY WITH THE FRESH-READ ERA, stated honestly: both
    /// eras serve wrong bytes in this race and both recover only on a NEW
    /// request, but the per-fetch degradation differs. The threaded corner
    /// serves ENDLIST bytes whose tail segments 404, and ExoPlayer treats a
    /// playlist it believes COMPLETE as terminal (it does not re-poll for
    /// growth), so that one fetch aborts dead; the fresh-read era served the
    /// recreated no-ENDLIST partial, a live-edge join that keeps following the
    /// re-encode on every reload. Same trigger, same bounded sub-millisecond
    /// window, worse per-fetch shape: the price of removing the second full
    /// read. The probe also conflates
    /// INACCESSIBLE with vanished: File.Exists swallows an access error and
    /// reports false, so an ACL revocation between verdict and probe translates
    /// into the same vanish fall-through (a misleading vanish log plus a
    /// re-encode attempt on an unreadable directory), where the fresh read's
    /// UnauthorizedAccessException failed loudly; the window is the
    /// verdict-to-serve gap only (gate-review note, JF-677 round). The JF-678
    /// decision on that conflation (kept, deliberately) and its rationale live
    /// on <see cref="ProbePlaylistExists"/>.
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file being served.</param>
    /// <param name="preloadedContent">The verdict's read of the same file, when it made one; null reads fresh.</param>
    /// <returns>The playlist content to serve.</returns>
    private async Task<string> ResolveServeContentAsync(string playlistPath, string? preloadedContent)
    {
        if (preloadedContent == null)
        {
            return await ReadPlaylistContentAsync(playlistPath).ConfigureAwait(false);
        }

        ProbePlaylistExists(playlistPath);
        return preloadedContent;
    }

    /// <summary>
    /// The ONE existence probe behind the JF-499 W3 vanish-at-serve contract
    /// (JF-678 gave it a name): throw the canonical
    /// <see cref="FileNotFoundException"/> when the playlist a serve is about
    /// to hand out is gone, so the throw happens at ACTION time where
    /// <see cref="TryServeValidatedHlsCacheAsync"/> can translate it, instead of
    /// at result execution over a dead <c>PhysicalFile</c> path (a 500 that
    /// only self-heals on the Echo's playlist retry). Used by
    /// <see cref="ResolveServeContentAsync"/>'s threaded-content path; the raw
    /// no-token serve reaches the same contract by materializing the bytes
    /// (its read throws the same exception natively). CONFLATION DECISION
    /// (JF-678, deliberate):
    /// <c>File.Exists</c> reports false for an INACCESSIBLE file too, so an
    /// ACL revocation inside the verdict-to-serve window translates into the
    /// vanish fall-through rather than the fresh read's loud
    /// <see cref="UnauthorizedAccessException"/>; the loud failure is not
    /// wanted back as an exception at this point because the fresh-read era's
    /// loud shape was itself a 500, the window is the verdict-to-serve gap
    /// only, and the re-encode the conflation kicks off re-discovers the
    /// access failure loudly at its own writes. The price is honest wording:
    /// the translation's log says "vanished or became unreadable".
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file about to be served.</param>
    private static void ProbePlaylistExists(string playlistPath)
    {
#pragma warning disable CA3003 // path derived from GUID-validated keys upstream (every caller)
        if (!System.IO.File.Exists(playlistPath))
        {
            throw new FileNotFoundException("Playlist vanished between validation and serve", playlistPath);
        }
#pragma warning restore CA3003
    }

    /// <summary>
    /// Serve an HLS playlist file, injecting <c>?token=</c> into every segment URI line so the
    /// Echo carries the stream token when fetching segments (JF-309). ffmpeg-written playlists
    /// (<c>stream.m3u8</c>) don't carry the token (ffmpeg's <c>-hls_base_url</c> can't place it
    /// correctly), so this post-processes the file before serving. If no token is in the request
    /// query, the file is served raw (no rewriting); since JF-678 that raw serve MATERIALIZES
    /// the bytes via <see cref="ResolveServeContentAsync"/> (probe + threaded reuse or a
    /// fresh read, whose miss throws the vanish exception) instead of handing out a raw
    /// <c>PhysicalFile</c>, so the branch is immune AFTER the read exactly like the
    /// tokened branch (a raw file result would 500 at RESULT EXECUTION if the playlist
    /// vanished between the read here and the execution; playlists are kilobytes).
    /// Accepted costs, deliberate: the raw result's conditional-GET/Last-Modified
    /// support is dropped (ExoPlayer rarely conditional-GETs a playlist). Since
    /// JF-682 no production shape reaches this branch: the single-chapter
    /// audiobook redirect, its only historical driver (the secret emptying
    /// between the route gate and the chapter re-mint), now serves the route
    /// gate's own 503 instead of minting an empty chapter token. The branch stays
    /// as the last-resort safety net for any future no-token shape, and a request
    /// that ever reaches it still gets the same materialized read and re-encode
    /// fall-through a tokened serve gets.
    /// Since JF-677 a serve that follows a
    /// validating verdict reuses its read (<paramref name="preloadedContent"/>), keeping the
    /// vanish probe (<see cref="ResolveServeContentAsync"/>) instead of paying a second full
    /// read.
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file to serve.</param>
    /// <param name="overrideToken">Chapter-scoped token overriding the request's own (single-chapter audiobooks).</param>
    /// <param name="preloadedContent">The verdict's read of the playlist, when one preceded this serve; null reads fresh.</param>
    private async Task<ActionResult> ServePlaylistWithTokenAsync(string playlistPath, string? overrideToken = null, string? preloadedContent = null)
    {
        string? token = overrideToken ?? HttpContext.Request.Query["token"];
        if (string.IsNullOrEmpty(token))
        {
            string raw = await ResolveServeContentAsync(playlistPath, preloadedContent).ConfigureAwait(false);
            return Content(raw, "application/vnd.apple.mpegurl");
        }

        string content = await ResolveServeContentAsync(playlistPath, preloadedContent).ConfigureAwait(false);
        string rewritten = RewritePlaylistWithToken(content, token);
        return Content(rewritten, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// THE sliced resume serve (JF-686 consolidation): one body for the slice-or-delegate
    /// + token-rewrite shape the song and episode twins share, so the invariants live once.
    /// A positive <paramref name="startTicks"/> slices the playlist to begin at the resume
    /// segment via <see cref="Alexa.Playback.AudiobookPlaylistBuilder.BuildResumePlaylist"/>
    /// (<paramref name="segmentSeconds"/> is the flat-divisor FALLBACK for playlists whose
    /// EXTINF durations fail to parse; it must be the segment length the caller's path
    /// actually cuts); the Echo Show's ExoPlayer ignores <c>#EXT-X-START</c>, so slicing is
    /// the only resume that actually resumes (the hardware verification and the
    /// seek-bar-becomes-relative trade-off live on
    /// <see cref="Alexa.Playback.AudiobookPlaylistBuilder"/>). A non-positive start serves
    /// exactly what <see cref="ServePlaylistWithTokenAsync"/> would. The token appended
    /// AFTER the slice (idempotent rewrite, <paramref name="overrideToken"/> else the
    /// request's own). Vanish semantics: no local catch, so a playlist that vanishes
    /// between the verdict and this read propagates to the caller (the verdict composite
    /// translates it into the re-encode fall-through); the audiobook path keeps its own
    /// serve because its existenceConfirmed PhysicalFile-degrade genuinely differs.
    /// Since JF-677 a serve that follows a validating verdict reuses its read
    /// (<paramref name="preloadedContent"/>; see <see cref="ResolveServeContentAsync"/>).
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file to serve.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 serves the playlist as-is).</param>
    /// <param name="segmentSeconds">The caller path's segment length in seconds (its -hls_time; the slice's flat-divisor fallback).</param>
    /// <param name="overrideToken">Chapter-scoped token overriding the request's own (single-chapter audiobooks); null uses the request's own.</param>
    /// <param name="preloadedContent">The verdict's read of the playlist, when one preceded this serve; null reads fresh.</param>
    /// <returns>The playlist response (sliced + token-rewritten when applicable).</returns>
    private async Task<ActionResult> ServePlaylistSlicedAsync(
        string playlistPath,
        long startTicks,
        int segmentSeconds,
        string? overrideToken = null,
        string? preloadedContent = null)
    {
        if (startTicks <= 0)
        {
            _logger.LogDebug(
                "HLS resume serve: {Path} served FULL (no position; startTicks={StartTicks})",
                playlistPath, startTicks);
            return await ServePlaylistWithTokenAsync(playlistPath, overrideToken, preloadedContent).ConfigureAwait(false);
        }

        string content = await ResolveServeContentAsync(playlistPath, preloadedContent).ConfigureAwait(false);
        // JF-686 review F5 (the debug-logging policy's playback-position + branching
        // data): the resolved start segment recomputes the builder's own arithmetic,
        // Debug-gated so the parse pass never runs on the Information default.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "HLS resume serve: {Path} served SLICED at segment {StartSegment} (startTicks={StartTicks}, segmentSeconds={SegmentSeconds})",
                playlistPath,
                Alexa.Playback.AudiobookPlaylistBuilder.ResolveStartSegment(content, startTicks, segmentSeconds),
                startTicks,
                segmentSeconds);
        }

        string sliced = Alexa.Playback.AudiobookPlaylistBuilder.BuildResumePlaylist(
            content, startTicks, segmentSeconds);
        string? token = overrideToken ?? HttpContext.Request.Query["token"];
        if (!string.IsNullOrEmpty(token))
        {
            sliced = RewritePlaylistWithToken(sliced, token);
        }

        return Content(sliced, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Serve a single-item (song) playlist honoring an optional resume slice (JF-686), the
    /// song twin of <see cref="ServeEpisodePlaylistAsync"/>: the shared
    /// <see cref="ServePlaylistSlicedAsync"/> core with <see cref="SongHlsSegmentSeconds"/>
    /// (the song core cuts 4s segments, not the audiobook concat's 10s) and the
    /// chapter-scoped <paramref name="overrideToken"/> threaded. This is what makes a
    /// single-chapter audiobook's resume launch play from the saved position instead of
    /// silently restarting the timeline at 0:00.
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file to serve.</param>
    /// <param name="overrideToken">Chapter-scoped token overriding the request's own (single-chapter audiobooks).</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 serves the playlist as-is).</param>
    /// <param name="preloadedContent">The verdict's read of the playlist, when one preceded this serve; null reads fresh.</param>
    /// <returns>The playlist response (sliced + token-rewritten when applicable).</returns>
    private Task<ActionResult> ServeVideoAudioPlaylistAsync(string playlistPath, string? overrideToken, long startTicks, string? preloadedContent = null)
        => ServePlaylistSlicedAsync(playlistPath, startTicks, SongHlsSegmentSeconds, overrideToken, preloadedContent);

    /// <summary>
    /// Serve an episode playlist honoring an optional resume slice (JF-499 W2): the
    /// episode twin of <see cref="ServeAudiobookPlaylistAsync"/>. A positive
    /// <paramref name="startTicks"/> slices the playlist to begin at the resume
    /// segment (the Echo Show's ExoPlayer ignores <c>#EXT-X-START</c>, so slicing is
    /// the only mechanism that actually resumes; the seek-bar-becomes-relative
    /// trade-off is documented on <see cref="Alexa.Playback.AudiobookPlaylistBuilder"/>).
    /// This is what makes the post-interruption self-heal re-encode resume where the
    /// user was instead of restarting the timeline from 0:00, when the request
    /// carries <c>?start=</c>. A non-positive start serves exactly what
    /// <see cref="ServePlaylistWithTokenAsync"/> would. Since JF-677 a serve that
    /// follows a validating verdict reuses its read
    /// (<paramref name="preloadedContent"/>; see <see cref="ResolveServeContentAsync"/>).
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file to serve.</param>
    /// <param name="startTicks">Resume position in .NET ticks (0 serves the playlist as-is).</param>
    /// <param name="preloadedContent">The verdict's read of the playlist, when one preceded this serve; null reads fresh.</param>
    /// <returns>The playlist response (sliced + token-rewritten when applicable).</returns>
    private Task<ActionResult> ServeEpisodePlaylistAsync(string playlistPath, long startTicks, string? preloadedContent = null)
        => ServePlaylistSlicedAsync(playlistPath, startTicks, EpisodeHlsSegmentSeconds, overrideToken: null, preloadedContent);

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
            // Segment URI lines: the shared core delegates to the ONE predicate
            // (Alexa.Playback.AudiobookPlaylistBuilder.IsSegmentUriLine, the contract
            // extracted in JF-778) with the two token-injection-local conditions kept
            // here (the .ts tail and the not-already-tokened guard); no second
            // drift-prone copy (the JF-778 gate-marker F5).
            if (Alexa.Playback.AudiobookPlaylistBuilder.IsSegmentUriLine(line)
                && line.EndsWith(".ts", StringComparison.Ordinal)
                && !line.Contains("?token="))
            {
                lines[i] = line + "?token=" + token;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Serve an audiobook playlist, slicing at the resume position (?start=; ExoPlayer
    /// ignores #EXT-X-START) when a start
    /// position is requested, otherwise serve the raw file. Centralizes resume injection so
    /// every playlist return path (cache hit, encode-in-progress, post-encode) honors ?start=.
    /// Since JF-677 a serve that follows a validating verdict reuses its read
    /// (<paramref name="preloadedContent"/>; see <see cref="ResolveServeContentAsync"/>).
    /// JF-678: a VANISH (the playlist gone between the verdict and the read, or
    /// unreadable) propagates instead of being swallowed into the PhysicalFile
    /// fallback over the dead path, but ONLY when
    /// <paramref name="existenceConfirmed"/> says the caller already confirmed
    /// this path exists (a verdict's FileInfo/threaded read, or an existence
    /// gate): there, a read miss is the vanish race. Without confirmation
    /// (the album FIRST-FETCH serve, whose path ffmpeg may not have FLUSHED
    /// yet: the first-segment wait polls the segment, not the playlist), a
    /// read miss is indistinguishable from one flush cycle of write lag, so
    /// the generic catch keeps the PhysicalFile degrade (the pre-JF-678
    /// behavior). Every OTHER failure keeps the fallback regardless.
    /// The raw no-token tail needs no probe of its own: this route's query
    /// token is gate-validated (there is no override-token mechanism here), so
    /// the tail's no-token entry is unreachable and its reachable entry is the
    /// non-vanish catch fallback, whose PhysicalFile degrade is kept verbatim.
    /// </summary>
    /// <param name="playlistPath">Path of the playlist file to serve.</param>
    /// <param name="startTicks">Resume position in .NET ticks (null/0 serves the playlist as-is).</param>
    /// <param name="preloadedContent">The verdict's read of the playlist, when one preceded this serve; null reads fresh.</param>
    /// <param name="existenceConfirmed">True when the caller holds prior evidence the path exists (a verdict FileInfo/threaded read or an existence gate); false keeps the flush-lag degrade.</param>
    private async Task<ActionResult> ServeAudiobookPlaylistAsync(string playlistPath, long? startTicks, string? preloadedContent = null, bool existenceConfirmed = true)
    {
        string? token = HttpContext.Request.Query["token"];

        if (startTicks.HasValue && startTicks.Value > 0)
        {
            return await ServeResumePlaylistAsync(playlistPath, startTicks.Value, token, preloadedContent, existenceConfirmed).ConfigureAwait(false);
        }

        // Non-resume: inject token into segment lines (JF-309). The cached playlist may be
        // ffmpeg-written (stream.m3u8, no token) or plugin-written (playlist-full.m3u8, has token).
        // RewritePlaylistWithToken is idempotent; it skips lines already carrying ?token=.
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                string content = await ResolveServeContentAsync(playlistPath, preloadedContent).ConfigureAwait(false);
                return Content(RewritePlaylistWithToken(content, token), "application/vnd.apple.mpegurl");
            }
            catch (Exception ex) when (!existenceConfirmed || !IsHlsVanishException(ex))
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
    /// Also injects the stream token into segment lines (JF-309). Since JF-677 a serve
    /// that follows a validating verdict reuses its read
    /// (<paramref name="preloadedContent"/>; see <see cref="ResolveServeContentAsync"/>).
    /// JF-678: a vanish propagates instead of falling back to the dead
    /// PhysicalFile path under the same <paramref name="existenceConfirmed"/>
    /// boundary as <see cref="ServeAudiobookPlaylistAsync"/>'s token branch.
    /// </summary>
    /// <param name="basePlaylistPath">Path of the base playlist file.</param>
    /// <param name="startTicks">Resume position in .NET ticks.</param>
    /// <param name="token">Stream token from the request query, when present.</param>
    /// <param name="preloadedContent">The verdict's read of the playlist, when one preceded this serve; null reads fresh.</param>
    /// <param name="existenceConfirmed">Passed through from <see cref="ServeAudiobookPlaylistAsync"/> (its doc owns the boundary).</param>
    private async Task<ActionResult> ServeResumePlaylistAsync(string basePlaylistPath, long startTicks, string? token, string? preloadedContent = null, bool existenceConfirmed = true)
    {
        try
        {
            string content = await ResolveServeContentAsync(basePlaylistPath, preloadedContent).ConfigureAwait(false);
            string resumeContent = Alexa.Playback.AudiobookPlaylistBuilder.BuildResumePlaylist(
                content, startTicks, AudiobookHlsSegmentSeconds);
            if (!string.IsNullOrEmpty(token))
            {
                resumeContent = RewritePlaylistWithToken(resumeContent, token);
            }

            return Content(resumeContent, "application/vnd.apple.mpegurl");
        }
        catch (Exception ex) when (!existenceConfirmed || !IsHlsVanishException(ex))
        {
            _logger.LogWarning(ex, "Failed to serve resume playlist from {Path}, serving base", basePlaylistPath);
#pragma warning disable CA3003 // path is an internal cache file resolved by the controller
            return PhysicalFile(basePlaylistPath, "application/vnd.apple.mpegurl");
#pragma warning restore CA3003
        }
    }

    /// <summary>
    /// Shared signed-route preamble (JF-651, JF-309): reject a non-GUID route id with
    /// 400, then validate the signed item-scoped stream token, returning its error.
    /// Returns null when the request may proceed. The 400 must stay BEFORE the token
    /// check (the token binds to the GUID); each migrated route's
    /// InvalidItemId_Returns400 + NoToken_Returns401 test pair pins that ordering.
    /// </summary>
    private ActionResult? ValidateSignedRoute(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out _))
        {
            return BadRequest(new { error = "Invalid itemId format" });
        }

        return ValidateStreamToken(itemId, out _);
    }

    /// <summary>
    /// Validate the signed item-scoped stream token (JF-309). The token is carried in the
    /// <c>?token=</c> query parameter and binds the request's itemId to an HMAC signature so a
    /// bare item GUID can no longer stream an item. Returns the 503 of
    /// <see cref="StreamTokenSecretNotConfigured"/> on an empty secret, a 401 result on any
    /// token failure, or null when the request may proceed. Call after the GUID-format check
    /// (the token binds to the GUID).
    /// JF-767 Finding B: the scope-reading out parameter carries the token's library scope
    /// (null for the legacy / unrestricted two-field shape); only the concat enumeration
    /// in <see cref="StreamHlsAudiobook"/> consumes it (resolved once per request there
    /// and applied to both arms through the pre-resolved
    /// <c>LibraryFilter.ApplyLibraryFilter(query, topParentIds)</c> overload).
    /// </summary>
    private ActionResult? ValidateStreamToken(string itemId, out Guid[]? allowedLibraryIds)
    {
        allowedLibraryIds = null;
        string? secret = Plugin.Instance?.Configuration?.StreamTokenSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return StreamTokenSecretNotConfigured();
        }

        string? token = HttpContext.Request.Query["token"];
        if (!StreamTokenHelper.TryValidate(token, itemId, secret, out allowedLibraryIds))
        {
            _logger.LogWarning("VideoAudio: rejected stream request for {ItemId} (missing/invalid/expired token)", itemId);
            return Unauthorized(new { error = "Invalid or expired stream token" });
        }

        return null;
    }

    /// <summary>
    /// The ONE answer to an empty <c>StreamTokenSecret</c> (JF-309 gate, JF-682): the route
    /// gate rejects with it at entry, and the single-chapter audiobook redirect returns it
    /// when the secret it re-reads at the chapter re-mint has been emptied since the gate
    /// (a config save mid-request). One definition, so the redirect's answer stays identical
    /// to the gate's; an empty-secret request must never reach a serve.
    /// </summary>
    private ObjectResult StreamTokenSecretNotConfigured()
    {
        _logger.LogError("VideoAudio: stream token secret not configured");
        return StatusCode(503, new { error = "Stream token secret not configured" });
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
    /// Body of the media-stream read: fetch the item's media streams as a
    /// materialized list, or null when the media source manager is unavailable or the
    /// read fails (the caller degrades to its fail-open shape). Materialized so a
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
    /// The resolved source-media probe of <see cref="ResolveSourceCodecs"/>: both
    /// source codecs plus the combined source bitrate, all from ONE media-stream read.
    /// </summary>
    /// <param name="Video">Lowercase video codec, or null when unknown (no video stream, only attached-picture covers, or a failed stream read).</param>
    /// <param name="Audio">Lowercase audio codec, or null when unknown.</param>
    /// <param name="TotalBitrateBps">Combined video+audio source bitrate in bits per second, or null when no stream of either type carries a <see cref="MediaStream.BitRate"/>.</param>
    internal readonly record struct SourceMediaProbe(string? Video, string? Audio, long? TotalBitrateBps);

    /// <summary>
    /// Combined source-media probe (JF-525; bitrate folded in JF-539): the episode
    /// HLS path needs the video codec (tier decision), the audio codec (ffmpeg
    /// arguments), and the combined bitrate (remux cache-size estimate), and
    /// resolving them through individual resolvers cost one
    /// <see cref="IMediaSourceManager.GetMediaStreams(Guid)"/> read each. This
    /// resolves all three sides from ONE stream read, each side keeping its
    /// individual resolver's semantics: the video-codec side skips blank codecs
    /// and attached-picture covers (<see cref="EpisodeCoverVideoCodecs"/>), the
    /// audio-codec side takes the first audio stream with a non-blank codec, and
    /// the bitrate side (no codec filtering: a blank-codec or cover stream still
    /// carries source bytes) sums the first video and first audio stream that
    /// carry a <see cref="MediaStream.BitRate"/>, null when neither does.
    /// Fail-open shapes are unchanged: null media source manager or a read
    /// failure returns an all-null probe, and a missing stream of a type leaves
    /// that side null.
    /// </summary>
    /// <param name="item">The item whose streams to read.</param>
    /// <returns>The source-media probe; either codec side and the bitrate null when unknown.</returns>
    internal SourceMediaProbe ResolveSourceCodecs(MediaBrowser.Controller.Entities.BaseItem item)
    {
        var streams = TryGetMediaStreams(item);
        if (streams == null)
        {
            return default;
        }

        string? videoCodec = null;
        string? audioCodec = null;
        int? videoBitrateBps = null;
        int? audioBitrateBps = null;
        foreach (MediaStream stream in streams)
        {
            // Bitrate side first: it must NOT inherit the codec-side skips below
            // (a blank-codec or cover stream's bytes still count toward the reserve).
            if (stream.Type == MediaStreamType.Video)
            {
                videoBitrateBps ??= stream.BitRate;
            }
            else if (stream.Type == MediaStreamType.Audio)
            {
                audioBitrateBps ??= stream.BitRate;
            }

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

            if (videoCodec != null && audioCodec != null && videoBitrateBps != null && audioBitrateBps != null)
            {
                break;
            }
        }

        // MediaStream.BitRate is int?; the sum widens to long? for the caller's
        // byte arithmetic. Null (no stream carried a BitRate) resolves to the
        // caller's flat-estimate fallback.
        long? totalBitrateBps = (videoBitrateBps ?? 0) + (audioBitrateBps ?? 0);
        return new SourceMediaProbe(videoCodec, audioCodec, totalBitrateBps > 0 ? totalBitrateBps : null);
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
        args.Add(SongHlsSegmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        string hlsBaseUrl,
        bool audioCopy = true)
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
        // JF-625: with real art (music-album concats), the source image has arbitrary
        // dimensions; the scale+pad filter (the single-item path's) normalizes to 1280x720
        // so libx264 gets even dimensions. The black-frame input is already 1280x720 and
        // deliberately takes no filter: the audiobook encode args are the live-proven set.
        if (!useBlackFrame)
        {
            args.AddRange(VideoFilterArgs);
        }

        args.AddRange([
            "-c:v", "libx264",
            "-tune", "stillimage",
            "-preset", "ultrafast",
            "-crf", "51",
            "-r", "1",
            "-g", "1",          // keyframe every 1s for accurate seeking
            "-pix_fmt", "yuv420p"
        ]);

        // Audio: copy without re-encoding (MP3 remux is instant, no quality loss);
        // the caller drops to AAC when the concat's children have mixed codecs (the
        // silent-truncation hazard documented at the album call site).
        args.AddRange(audioCopy
            ? ["-c:a", "copy"]
            : ["-c:a", "aac", "-b:a", "192k"]);

        // HLS-specific flags: 10-second segments required by ExoPlayer (Echo Show).
        // Longer segments (e.g. 250s) cause buffer stalls after seeking. The value is
        // coupled to AudiobookHlsSegmentSeconds and AudiobookPositionTracker's
        // SegmentDurationSeconds (see the const's doc): retune them together.
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
    /// beyond one rounded hour still outgrows even the raised cap (the reserve
    /// rounds UP per hour), which routes such encodes into the transient root
    /// instead of the capped cache (the regime
    /// <see cref="TranscodeEstimateExceedsCacheCap"/> decides, JF-537.1; under
    /// it the reserve still drives the pre-encode headroom and the playback pin
    /// protects the entry being watched).
    /// </summary>
    /// <param name="runtimeTicks">Content duration (item runtime).</param>
    /// <returns>Estimated bytes the encode writes.</returns>
    internal static long EstimateEpisodeTranscodeEncodeBytes(long runtimeTicks)
        => FlatHourlyEncodeBytes(runtimeTicks, 3072L * 1024 * 1024);

    /// <summary>
    /// JF-537 (superseded resting point) / JF-537.1 (current): whether the transcode
    /// tier's pre-encode estimate exceeds the cache cap the eviction sweep enforces
    /// (<see cref="VideoAudioCache.EffectiveCacheCapMB"/>). Strictly greater: an
    /// estimate EQUAL to the cap still fits (the sweep's target is cap minus headroom,
    /// and the JF-428 half-cap floor bounds how far it evicts). Since JF-537.1 this is
    /// a ROUTING decision: when true, the episode transcode encodes into the TRANSIENT
    /// root (<see cref="VideoAudioCache.GetTransientHlsDirectoryPath"/>), whose bytes
    /// never count against the cap, never evict the real cache, and survive for replay
    /// until the idle reaper (<see cref="VideoAudioCache.ReapIdleTransientEntries"/>)
    /// judges them unwatched (segment-fetch recency; VideoApp emits no playback-stop
    /// event, so recency is the only observable liveness signal). The serve paths
    /// resolve both roots through the liveness-aware resolver
    /// (<see cref="VideoAudioCache.GetCachedHlsPlaylist"/> probes the resolver's
    /// order: the registered dir exclusively while the caller's own generation is
    /// live-or-registering and contained, else cache then transient;
    /// <see cref="VideoAudioCache.FindHlsDirectoryByScan"/> scans both), so a
    /// transient entry keeps serving across cap changes and
    /// restarts. JF-537's interim answer (encode inside the capped cache anyway and
    /// WARN about the per-replay re-encode) is retired with the mode that avoids it.
    ///
    /// Scope is the TRANSCODE tier only. The remux tier is excluded deliberately: its
    /// copy writes at the source's own bitrate (the bytes the user already chose to
    /// store, ~1GB/h at typical TV bitrates, under the cap for ~4h), and its churn
    /// cost is bounded by disk speed (a remux replays at ~20x realtime, minutes --
    /// not the ~27min of CPU per 2h that a CRF re-encode pays), so the invisible
    /// multi-hour re-encode pain is materially transcode-shaped.
    /// </summary>
    /// <param name="estimatedEncodeBytes">The transcode tier's estimate
    /// (<see cref="EstimateEpisodeTranscodeEncodeBytes"/>).</param>
    /// <param name="cacheCapMB">The configured cache cap in MB.</param>
    /// <returns>True when the estimate cannot fit under the cap.</returns>
    internal static bool TranscodeEstimateExceedsCacheCap(long estimatedEncodeBytes, int cacheCapMB)
        => estimatedEncodeBytes > cacheCapMB * 1024L * 1024L;

    /// <summary>
    /// Minutes the HLS background monitor waits WITHOUT OBSERVED ENCODE PROGRESS
    /// before killing ffmpeg (JF-500 review F1 sized it; JF-499 W2 changed the
    /// semantics from a total wall-clock cap to a no-progress window: the monitor
    /// now extends indefinitely while the encode directory keeps changing, so a
    /// slow-but-alive NAS-bound remux is no longer killed mid-encode, and this
    /// number only bounds a genuinely HUNG encode). The remux and audio tiers are
    /// I/O-bound and finish audiobook-length content in minutes (~21x realtime),
    /// so they keep the historical 30-minute window unchanged. The video TRANSCODE
    /// tier runs at a MEASURED 4.40x realtime (minix, 2026-09-08), so its window
    /// scales from the item runtime: half the measured speed (2.0x, the
    /// conservative floor for concurrent-load slowdown) plus 10 minutes of
    /// ffmpeg startup/slack, floored at the 30-minute default for short content.
    /// A transcode tier with an UNKNOWN runtime (missing metadata) gets 120
    /// minutes (JF-500 review R4), not the 30-minute default: 30 would still
    /// hard-kill any movie longer than ~132 minutes, while 120 bounds how long a
    /// hung encode may hold its transcode slot before the monitor reclaims it.
    /// Consequence of the no-progress semantics for the encode slots (JF-500): a
    /// progressing encode now holds its transcode slot for as long as it keeps
    /// progressing; the slot bounds the concurrency of active work, not an
    /// encode's total duration.
    /// </summary>
    /// <param name="videoTranscodeTier">Whether the monitored encode is the
    /// episode video-transcode tier (HEVC re-encode).</param>
    /// <param name="runTimeTicks">The item's runtime, or null/&lt;=0 when unknown.</param>
    /// <returns>The no-progress stall budget in minutes (NOT a wall-clock timeout:
    /// the monitor extends indefinitely while the encode directory keeps
    /// changing).</returns>
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
    /// second (from <see cref="SourceMediaProbe.TotalBitrateBps"/>), or null/0 when unknown.</param>
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

        AppendEventAudioHlsTail(args, playlistPath, segmentPath, hlsBaseUrl);

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
    /// Build ffmpeg argument list for the PLAYBACK-SPEED atempo HLS variant
    /// (JF-636): the item's first audio stream ALONE mapped into MPEG-TS
    /// (<c>-map 0:a:0</c>, no video track), time-stretched by
    /// <c>-af atempo=&lt;rate&gt;</c> (pitch-preserving; the 0.75..2.0 steps all
    /// fit one filter instance, range 0.5-2.0) and re-encoded to AAC 192k stereo
    /// (<c>atempo</c> is a filter, so stream copy is impossible regardless of the
    /// source codec; the AAC target matches
    /// <see cref="EpisodeAudioCodecArgs"/>). Optional <paramref name="startTicks"/>
    /// is a CONTENT-relative input seek (<c>-ss</c> BEFORE <c>-i</c>, the JF-507
    /// shape): the output timeline starts at position 0 for that content
    /// position. 10-second segments, event-style growth (append_list), no
    /// -shortest.
    /// </summary>
    /// <param name="sourceUrl">Static stream URL of the source item (ffmpeg input; its first audio stream is what gets stretched).</param>
    /// <param name="startTicks">CONTENT start position in .NET ticks (0 to encode from the beginning).</param>
    /// <param name="ratePerMille">Playback rate in per-mille form (atempo = perMille/1000).</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildAudioSpeedHlsFfmpegArguments(
        string sourceUrl,
        long startTicks,
        int ratePerMille,
        string playlistPath,
        string segmentPath,
        string hlsBaseUrl)
    {
        var args = new List<string>();

        // Input seek BEFORE -i: starts reading at the CONTENT position. The output
        // timeline starts at 0 for it, so the paired AudioPlayer.Play directive
        // carries offset 0 and the launch base records the content position.
        if (startTicks > 0)
        {
            args.Add("-ss");
            args.Add(FormatSecondsInvariant(startTicks));
        }

        args.Add("-i");
        args.Add(sourceUrl);

        // Audio ONLY: no video mapping (the AudioPlayer surface has no video).
        args.Add("-map");
        args.Add("0:a:0");

        // atempo, then AAC 192k stereo: a filter forbids stream copy, so the copy
        // branch of the sibling audio paths does not exist here.
        args.Add("-af");
        args.Add($"atempo={(ratePerMille / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
        args.AddRange(EpisodeAudioCodecArgs);

        AppendEventAudioHlsTail(args, playlistPath, segmentPath, hlsBaseUrl);

        return args;
    }

    /// <summary>
    /// The SHARED HLS tail of the audio-rate event variants (the JF-507
    /// audio-only episode encode and the JF-636 atempo speed encode, which differ
    /// only in their seek/codec heads): 10-second MPEG-TS segments with
    /// append_list event growth, the segment filename template, the segment base
    /// URL, and the playlist as the positional output. One definition so a
    /// segment-duration or flag change lands once for both variants.
    /// </summary>
    /// <param name="args">The argument list under construction.</param>
    /// <param name="playlistPath">Output playlist file path.</param>
    /// <param name="segmentPath">Segment filename template (e.g. "seg_%04d.ts").</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references in the playlist.</param>
    private static void AppendEventAudioHlsTail(List<string> args, string playlistPath, string segmentPath, string hlsBaseUrl)
    {
        // 10-second segments (the audio-rate value; a 45min episode is ~270 segments
        // and %04d caps at 9999 = ~27h). append_list keeps written segments listed
        // while the playlist grows (the event-playlist shape the Echo family
        // tolerates), and audio-only has no keyframe constraint.
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
    }

    /// <summary>
    /// Pre-write a complete HLS playlist for an audiobook with all segment durations.
    /// Written WITHOUT #EXT-X-ENDLIST so the player treats it as an event playlist:
    /// it plays available segments without failing on missing ones. This gives the Echo
    /// Show the correct total book duration immediately, while ffmpeg generates segments
    /// in the background. ffmpeg writes its own stream.m3u8 separately. Emission is the
    /// shared core's (<see cref="WritePrewrittenEventPlaylist"/>, JF-536); this wrapper
    /// owns the audiobook-specific shape: per-chapter duration splitting with the 250s
    /// fallback for chapters without a runtime, the 10s target duration, per-segment
    /// #EXT-X-DISCONTINUITY (chapter-file boundaries in the concat stream), and 4-digit
    /// segment names.
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
        var segmentDurations = new List<double>();
        foreach (var chapter in chapters)
        {
            double durationSeconds = chapter.RunTimeTicks.HasValue && chapter.RunTimeTicks.Value > 0
                ? chapter.RunTimeTicks.Value / 10000000.0
                : 250.0;

            segmentDurations.AddRange(UniformSegmentDurations(durationSeconds, AudiobookHlsSegmentSeconds));
        }

        WritePrewrittenEventPlaylist(
            playlistPath,
            hlsBaseUrl,
            segmentDurations,
            AudiobookHlsSegmentSeconds,
            segmentIndexDigits: 4,
            discontinuityPerSegment: true,
            token);
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
        => WritePrewrittenEventPlaylist(
            playlistPath,
            hlsBaseUrl,
            UniformSegmentDurations(runtimeTicks / 10000000.0, EpisodeHlsSegmentSeconds),
            EpisodeHlsSegmentSeconds,
            segmentIndexDigits: 4,
            discontinuityPerSegment: false,
            token);

    /// <summary>
    /// Pre-write a complete HLS playlist for a single-item video-audio encode (JF-536:
    /// songs and the single-chapter audiobooks redirected from
    /// <see cref="StreamHlsAudiobook"/>): the single-item twin of
    /// <see cref="WriteEpisodePlaylist"/>. Same event-playlist mechanism and continuous
    /// timeline (no discontinuities), differing only in the constants: the path's
    /// <see cref="SongHlsSegmentSeconds"/> target duration and 3-digit segment names
    /// (the <c>seg_%03d.ts</c> template in <see cref="StreamHlsVideoAudioCore"/>; the
    /// index still renders wider past 999, matching ffmpeg's <c>%03d</c> minimum-width
    /// semantics, and <c>IsValidSegmentName</c> accepts both widths).
    /// </summary>
    /// <param name="playlistPath">File path for the .m3u8 playlist.</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references.</param>
    /// <param name="runtimeTicks">The item's runtime, the duration the listing spans.</param>
    /// <param name="token">Stream token to embed in segment URLs (JF-309), or null.</param>
    internal static void WriteVideoAudioPlaylist(
        string playlistPath,
        string hlsBaseUrl,
        long runtimeTicks,
        string? token)
        => WritePrewrittenEventPlaylist(
            playlistPath,
            hlsBaseUrl,
            UniformSegmentDurations(runtimeTicks / 10000000.0, SongHlsSegmentSeconds),
            SongHlsSegmentSeconds,
            segmentIndexDigits: 3,
            discontinuityPerSegment: false,
            token);

    /// <summary>
    /// The shared emission core of every pre-written event-playlist writer (JF-536):
    /// the header block, the token-suffixed segment URLs, and the deliberate
    /// no-ENDLIST policy, parameterized by the axes the HLS paths genuinely differ on:
    /// the TARGETDURATION (the path's <c>-hls_time</c>), the segment index digit width
    /// (the path's <c>seg_%0Nd</c> naming), per-segment #EXT-X-DISCONTINUITY
    /// (chapter-file boundaries vs one continuous encode), and the per-segment
    /// durations (each writer's own split). The serve layers stay per-path ON PURPOSE:
    /// they differ on real axes (audiobook resume slicing + 503-on-missing vs episode
    /// live-playlist fallback), so only the file EMISSION is shared. All numeric
    /// formatting is invariant-culture and load-bearing: a comma-decimal host culture
    /// would render "3,999346," and HLS parsers would read the duration as 3 (the
    /// latent bug the audiobook writer carried until JF-536; the episode writer fixed
    /// it locally in JF-531). No #EXT-X-ENDLIST is ever written: the event-playlist
    /// policy is the whole point (the player plays available segments without failing
    /// on the not-yet-encoded tail).
    /// </summary>
    /// <param name="playlistPath">File path for the .m3u8 playlist.</param>
    /// <param name="hlsBaseUrl">Base URL prefix for segment references.</param>
    /// <param name="segmentDurations">Duration of every segment the encode will
    /// produce, in order; their sum is the runtime the seekbar shows.</param>
    /// <param name="targetDurationSeconds">The path's HLS TARGETDURATION (its
    /// <c>-hls_time</c>).</param>
    /// <param name="segmentIndexDigits">Minimum width of the segment index (the N in
    /// the path's <c>seg_%0Nd.ts</c> template).</param>
    /// <param name="discontinuityPerSegment">Emit #EXT-X-DISCONTINUITY before every
    /// segment (the audiobook concat's chapter boundaries) or none (single continuous
    /// encodes).</param>
    /// <param name="token">Stream token to embed in segment URLs (JF-309), or null.</param>
    internal static void WritePrewrittenEventPlaylist(
        string playlistPath,
        string hlsBaseUrl,
        IReadOnlyList<double> segmentDurations,
        int targetDurationSeconds,
        int segmentIndexDigits,
        bool discontinuityPerSegment,
        string? token)
    {
        string segmentSuffix = string.IsNullOrEmpty(token) ? string.Empty : $"?token={token}";
        string indexFormat = "D" + segmentIndexDigits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var writer = new StreamWriter(playlistPath);
        writer.WriteLine("#EXTM3U");
        writer.WriteLine("#EXT-X-VERSION:3");
        writer.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"#EXT-X-TARGETDURATION:{targetDurationSeconds}"));
        writer.WriteLine("#EXT-X-MEDIA-SEQUENCE:0");

        for (int i = 0; i < segmentDurations.Count; i++)
        {
            if (discontinuityPerSegment)
            {
                writer.WriteLine("#EXT-X-DISCONTINUITY");
            }

            writer.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{HlsExtInf}{segmentDurations[i]:F6},"));
            writer.WriteLine($"{hlsBaseUrl}seg_{i.ToString(indexFormat, System.Globalization.CultureInfo.InvariantCulture)}.ts{segmentSuffix}");
        }
    }

    /// <summary>
    /// Split a duration into ceil(duration/segmentSeconds) segments, each carrying an
    /// equal share so the durations sum exactly to the input (the seekbar total). Ceil
    /// is the safety margin: the listing never spans less than the runtime, and any
    /// phantom tail beyond the actual segment count is only reachable before the
    /// post-completion ENDLIST playlist supersedes the pre-written file. Never returns
    /// empty: a zero-length input still yields one segment.
    /// </summary>
    /// <param name="durationSeconds">The duration to span, in seconds.</param>
    /// <param name="segmentSeconds">The path's segment length (its <c>-hls_time</c>).</param>
    /// <returns>One duration (seconds) per segment, in order.</returns>
    private static double[] UniformSegmentDurations(double durationSeconds, int segmentSeconds)
    {
        int count = Math.Max(1, (int)Math.Ceiling(durationSeconds / segmentSeconds));
        var durations = new double[count];
        Array.Fill(durations, durationSeconds / count);
        return durations;
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
    /// Validate a cached audiobook HLS playlist and invalidate if stale: the
    /// AUDIOBOOK registry's pairing over the ticks-scoped debris verdict core
    /// (<see cref="ValidateHlsCacheAsync"/>, whose doc holds the decision
    /// table), carrying this path's ENDLIST-content hook with two verdicts: a
    /// completed concat encode lists one segment per 10s of chapter audio, so
    /// an ENDLIST playlist below the duration-derived floor (see
    /// <see cref="ExpectedMinimumConcatSegments"/>; the JF-784 tail raised it
    /// from the bare chapter count, which an encode truncated at exit 0 still
    /// clears) is an incomplete encode's debris; and, since
    /// JF-784 leg 1, the TIMELINE verdict: the encode-metadata sidecar the
    /// encode wrote at start (<see cref="EncodeMetadataFileName"/>) must match
    /// the CURRENT request's live (token-scoped) enumeration in chapter count
    /// and runtime sum, so an entry encoded under a different scope or
    /// membership (or one whose sidecar is missing, fail-closed) is stale and
    /// re-encodes under the requesting scope instead of serving a timeline the
    /// paged head never summed. Before JF-676
    /// this validator had NO liveness gate at all: the undercount verdict
    /// fired unconditionally, and its key-wide Cleanup could wipe a live
    /// foreign-ticks generation's directory mid-write; the core's own-ticks
    /// short-circuit and ticks-scoped cleanup close both. The no-ENDLIST
    /// branch comes from the core: a killed encode's partial is debris now,
    /// not a forever "encoding in progress" serve. The core's documented
    /// bounded window applies to both hook rows: a completed-but-stale
    /// ENDLIST playlist serves once during the [ffmpeg exit, monitor clear]
    /// lag (one poll interval) before the next request's verdict re-encodes
    /// it. Keyed by parentId; used by
    /// both StreamHlsAudiobook call sites (fast path + in-lock double check).
    /// </summary>
    /// <param name="cached">The cached playlist file info (stream.m3u8).</param>
    /// <param name="chapterCount">Expected minimum segment count (from live library).</param>
    /// <param name="chapterDurationTicks">The live enumeration's runtime sum
    /// (the timeline identity's second component, JF-784).</param>
    /// <param name="parentId">Parent audiobook ID for logging and cleanup.</param>
    /// <param name="artModifiedTicks">The caller's art ticks (its own cache directory generation).</param>
    /// <returns>The valid playlist (with the verdict's read content on the read
    /// row), or null when invalidated or vanished.</returns>
    private Task<ValidatedHlsCache?> ValidateAudiobookCacheAsync(FileInfo cached, int chapterCount, long chapterDurationTicks, string parentId, long artModifiedTicks, bool probeOwnGenerationLiveOrRegistering)
        => ValidateHlsCacheAsync(
            cached,
            parentId,
            artModifiedTicks,
            _activeAudiobookEncodes,
            "Audiobook",
            content =>
            {
                int cachedSegments = CountSegmentsInPlaylist(content);
                int minimumSegments = ExpectedMinimumConcatSegments(chapterCount, chapterDurationTicks);
                if (cachedSegments < minimumSegments)
                {
                    return $"expected >= {minimumSegments} segments ({chapterCount} chapters / {chapterDurationTicks} ticks), found only {cachedSegments}";
                }

                return TimelineMismatchReason(cached, chapterCount, chapterDurationTicks);
            },
            probeOwnGenerationLiveOrRegistering);

    /// <summary>
    /// The JF-784 leg 1 timeline verdict: compare the encode-metadata sidecar
    /// (written at encode start over the encode's OWN scoped enumeration) against
    /// the CURRENT request's live enumeration. Both sides carry the same two
    /// DB-derived numbers, so a matching scope and membership compare
    /// bit-identical with no tolerance, and any real membership difference (a
    /// differently-scoped user, a config change, or an in-place library edit)
    /// fails on count, duration, or both. Fail-closed: a missing, incomplete,
    /// or unreadable sidecar (a pre-sidecar entry, or one wiped beside a
    /// surviving playlist) cannot prove its timeline and is stale; the
    /// re-encode rewrites it.
    /// Called on the ENDLIST content rows of the concat verdict only (the
    /// during-encode serve rows are the documented residual: they serve the
    /// running encode's listing by design, see StreamHlsAudiobook's scope note).
    /// </summary>
    /// <param name="cached">The cached playlist (the sidecar sits beside it in
    /// the same generation directory).</param>
    /// <param name="chapterCount">The current request's live chapter count.</param>
    /// <param name="chapterDurationTicks">The current request's live runtime sum.</param>
    /// <returns>The invalidation reason for the verdict log, or null when the
    /// timelines match.</returns>
    private string? TimelineMismatchReason(FileInfo cached, int chapterCount, long chapterDurationTicks)
    {
        if (!TryReadEncodeTimelineMetadata(cached.DirectoryName!, out int encodedChapterCount, out long encodedDurationTicks))
        {
            return $"no encode metadata beside the cached playlist; cannot verify the timeline identity, request enumerates {chapterCount} chapters / {chapterDurationTicks} ticks";
        }

        if (encodedChapterCount != chapterCount || encodedDurationTicks != chapterDurationTicks)
        {
            return $"cache encoded a different timeline (library scope or membership changed since the encode): request enumerates {chapterCount} chapters / {chapterDurationTicks} ticks, cache encoded {encodedChapterCount} / {encodedDurationTicks}";
        }

        return null;
    }

    /// <summary>
    /// The minimum playlist segment count a COMPLETE concat encode lists for a
    /// chapter set (the JF-784 gate-marker tail): the concat cuts one segment
    /// per <see cref="AudiobookHlsSegmentSeconds"/> of chapter audio, so the
    /// runtime sum demands roughly duration/10s segments and the bare chapter
    /// count is only the floor for near-total failures (an encode truncated at
    /// exit 0 keeps listing far more segments than it has chapters). The 2% +
    /// 2-segment tolerance absorbs RunTimeTicks-metadata vs actual-stream
    /// duration drift so a healthy completed cache never re-encodes on this
    /// bar; a real truncation loses multiples of the tolerance and fails.
    /// </summary>
    private static int ExpectedMinimumConcatSegments(int chapterCount, long durationTicks)
    {
        long byDuration = (durationTicks / (AudiobookHlsSegmentSeconds * TimeSpan.TicksPerSecond) * 98 / 100) - 2;
        return (int)Math.Max(chapterCount, Math.Max(0, byDuration));
    }

    /// <summary>
    /// The ONE reader of the encode-metadata sidecar (JF-784 review F6):
    /// parse the timeline identity (chapter count + runtime sum) the concat
    /// encode wrote at start. False when the file is missing, unreadable, or
    /// lacks either field; the two consumers give that answer opposite
    /// meanings (the monitor's completeness row skips itself, the timeline
    /// verdict fails closed). Sync because the verdict's content hook is
    /// synchronous; the monitor's background call tolerates the small
    /// blocking read. The property names are the write site's anonymous-type
    /// properties and live ONLY here.
    /// </summary>
    /// <param name="hlsDir">The concat generation directory.</param>
    /// <param name="chapterCount">On success: the encoded chapter count.</param>
    /// <param name="durationTicks">On success: the encoded runtime sum.</param>
    /// <returns>True when both timeline fields were read.</returns>
    private static bool TryReadEncodeTimelineMetadata(string hlsDir, out int chapterCount, out long durationTicks)
    {
        chapterCount = 0;
        durationTicks = 0;
#pragma warning disable CA3003 // path derived from the GUID-validated cache key, same as the playlist beside it
        try
        {
            string metadataPath = Path.Combine(hlsDir, EncodeMetadataFileName);
            if (!System.IO.File.Exists(metadataPath))
            {
                return false;
            }

            string json = System.IO.File.ReadAllText(metadataPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ExpectedChapterCount", out var countProp)
                || !doc.RootElement.TryGetProperty("ExpectedDurationTicks", out var durationProp))
            {
                return false;
            }

            chapterCount = countProp.GetInt32();
            durationTicks = durationProp.GetInt64();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA3003
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
    /// Precedence: the explicit <see cref="FfmpegPath"/> override first, then
    /// IMediaEncoder's path (only when it exists on disk), then a PATH scan.
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
    /// JF-676 rework internal test seam: when non-null, invoked immediately
    /// after a gated ffmpeg process starts and before the method returns, with
    /// the encode's pin path (the HLS output directory). Lets a pin snapshot,
    /// AT PROCESS-START TIME, whatever state the caller was required to
    /// establish BEFORE starting ffmpeg: the four HLS paths'
    /// mark-before-start invariant (the encode generation must already be
    /// registered when ffmpeg can first write a playlist a concurrent
    /// ticks-scoped debris verdict could judge; see the mark sites). Test-only;
    /// null in production. Instance-scoped like the other per-controller
    /// seams; an exception thrown by the observer surfaces to the endpoint
    /// caller (the pin owns it).
    /// </summary>
    internal Action<string>? FfmpegProcessStartedForTest { get; set; }

    /// <summary>
    /// Starts ffmpeg under the encode gate: the slot is held for the lifetime of the
    /// spawned process (all HLS/song encode paths; the lightweight faststart remux
    /// calls StartFfmpegProcess directly instead). An optional
    /// <paramref name="serializeSlot"/> (the episode transcode tier's
    /// <see cref="_episodeTranscodeSlot"/>, JF-500 review F2) is acquired BEFORE the
    /// gate and released AFTER it, in the same structures, so its ordering against
    /// the gate is fixed and inverse on release.
    /// </summary>
    /// <remarks>
    /// JF-676 rework internal test seam: <see cref="FfmpegProcessStartedForTest"/>,
    /// when set, fires between the process start and the return (the exact instant
    /// the four HLS paths' mark-before-start invariant is about).
    /// </remarks>
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

        // Test-only observer (JF-676 rework): fires between the process start
        // and the return to the caller, the exact instant the mark-before-start
        // invariant is about, so a pin can assert the caller's encode
        // generation was already registered before ffmpeg could write
        // anything. Null in production.
        FfmpegProcessStartedForTest?.Invoke(pinPath);

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
    /// JF-625 review (measured with ffmpeg 8.1.2 on the exact args): the 64MB/h base
    /// is calibrated on the black-frame output (~12MB/h measured); photographic album
    /// art at 1fps 720p IDR keyframes writes ~130-200MB/h, so the art encodes (the
    /// single-item song path and the album concat) must reserve at the higher rate or
    /// the pre-encode sweep under-evicts and the shared LRU cap is silently exceeded.
    /// </summary>
    internal static long EstimateArtEncodeBytes(long runtimeTicks)
        => FlatHourlyEncodeBytes(runtimeTicks, 192L * 1024 * 1024);

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
    /// Optional parameters mirror <see cref="MonitorFfmpegHlsAsync"/> 1:1. The
    /// active-encode handle is REQUIRED (JF-536/JF-665/JF-668): a defaulted
    /// registry once let a new caller silently clear flags in the wrong path's
    /// registry, and a missing or defaulted token would silently break the
    /// finally's compare-and-remove; bundling the three into one required
    /// handle makes both mispairings unrepresentable.
    /// </summary>
    private void StartHlsMonitor(
        Process process,
        string hlsDir,
        string itemId,
        long artModifiedTicks,
        string label,
        ActiveEncodeHandle activeEncode,
        bool videoTranscodeTier = false,
        long? runTimeTicks = null)
        => _ = MonitorFfmpegHlsAsync(process, hlsDir, itemId, artModifiedTicks, label, activeEncode, videoTranscodeTier, runTimeTicks);

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
    /// to exit, extending the wait while the encode keeps making observable progress
    /// (JF-499 W2; see <see cref="WaitForExitOrEncodeStallAsync"/>), logs the outcome,
    /// and triggers cache eviction. Disposes the process when done.
    /// Unlike <see cref="MonitorFfmpegAndRemuxAsync"/>, there is no remux step: HLS
    /// segments are already seekable individually.
    /// </summary>
    /// <param name="process">The ffmpeg process (started, not yet awaited).</param>
    /// <param name="hlsDir">The HLS directory containing playlist and segments.</param>
    /// <param name="itemId">Item ID for logging.</param>
    /// <param name="artModifiedTicks">Art ticks for logging.</param>
    /// <param name="label">Content label for log messages ("Song", "Audiobook", "Episode").</param>
    /// <param name="activeEncode">The encode's handle
    /// (<see cref="MarkEncodeActive"/>: registry + key + art-tick slot +
    /// generation); the finally's clear is compare-and-remove on its own
    /// (art-tick, token) pair, so this monitor cannot drop a newer
    /// registration's slot or another art-tick generation's liveness, and the
    /// bundled registry makes a clear in the wrong path's registry
    /// unrepresentable (JF-665/JF-668/JF-669).</param>
    /// <param name="videoTranscodeTier">Whether this encode is the episode
    /// video-transcode tier (JF-500); only that tier scales its stall budget from the
    /// runtime, the others keep the fixed 30-minute ceiling.</param>
    /// <param name="runTimeTicks">The item's runtime, used only by the transcode tier.</param>
    /// <returns>A task representing the background monitoring operation.</returns>
    private async Task MonitorFfmpegHlsAsync(
        Process process,
        string hlsDir,
        string itemId,
        long artModifiedTicks,
        string label,
        ActiveEncodeHandle activeEncode,
        bool videoTranscodeTier = false,
        long? runTimeTicks = null)
    {
        int timeoutMinutes = HlsMonitorTimeoutMinutes(videoTranscodeTier, runTimeTicks);
        TimeSpan stallBudget = HlsMonitorStallBudgetOverride ?? TimeSpan.FromMinutes(timeoutMinutes);
        try
        {
            // Tier-sized NO-PROGRESS budget: see HlsMonitorTimeoutMinutes (JF-500
            // review F1) for the sizing rationale and arithmetic, and
            // WaitForExitOrEncodeStallAsync (JF-499 W2) for the progress extension.
            bool stalled = await WaitForExitOrEncodeStallAsync(process, hlsDir, label, itemId, stallBudget).ConfigureAwait(false);
            if (stalled)
            {
                _logger.LogWarning(
                    "{Label} HLS encoding STALLED for {ParentId}: no segment-directory progress for {StallBudget} (JF-499)",
                    label, itemId, stallBudget);
                TryKillLiveProcess(
                    process,
                    "{Label} HLS: killing the stalled encode for {ParentId} (no segment-directory progress for {StallBudget})",
                    label, itemId, stallBudget);
                return;
            }

            // Post-completion diagnostics: gather metrics for structured logging
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
            // (the ONE sidecar reader, shared with the serve-time timeline
            // verdict since JF-784 review F6).
            long expectedDurationTicks = 0;
            if (TryReadEncodeTimelineMetadata(hlsDir, out int encodedChapterCount, out long encodedDurationTicks))
            {
                expectedChapterCount = encodedChapterCount;
                expectedDurationTicks = encodedDurationTicks;
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
            else if (expectedChapterCount > 0 && playlistSegmentCount < ExpectedMinimumConcatSegments(expectedChapterCount, expectedDurationTicks))
            {
                // JF-784 review F3 + gate-marker tail F1: the floor is
                // DURATION-derived (one segment per AudiobookHlsSegmentSeconds
                // of chapter audio; see ExpectedMinimumConcatSegments), not the
                // bare chapter count. The old != equality warned INCOMPLETE on
                // every healthy book/album encode (many segments per chapter),
                // and the first fix's bare chapter-count floor saw only
                // near-total failures: an encode truncated at exit 0 keeps far
                // more segments than it has chapters and read "complete".
                _logger.LogWarning(
                    "{Label} HLS encoding INCOMPLETE for {ParentId}: ffmpeg exited 0 but produced only {PlaylistSegmentCount} playlist segments, expected >= {MinimumSegments} ({ExpectedCount} chapters / {ExpectedDurationTicks} ticks), {SegmentFileCount} segment files on disk",
                    label, itemId, playlistSegmentCount,
                    ExpectedMinimumConcatSegments(expectedChapterCount, expectedDurationTicks),
                    expectedChapterCount, expectedDurationTicks, segmentFileCount);
            }
            else
            {
                _logger.LogDebug(
                    "{Label} HLS encoding complete for {ParentId}: {SegmentFileCount} segments",
                    label, itemId, segmentFileCount);
            }

            // Background eviction: don't block
            _ = Task.Run(() => _cache.EvictIfNeeded(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Label} HLS: ffmpeg monitoring failed for {ParentId}", label, itemId);
        }
        finally
        {
            // Generation-aware clear (JF-665/JF-669): only the generation that
            // SET its art-tick slot may clear it, and the entry drops only when
            // the LAST live generation cleared its own; a key-only TryRemove let
            // a displaced generation's late exit drop a newer encode's flag, and
            // the single-token entry let a newer-ticks encode's exit orphan an
            // older-ticks encode's liveness (see <see cref="MarkEncodeActive"/>
            // for the full account).
            activeEncode.Clear();
            process.Dispose();
        }
    }

    /// <summary>
    /// JF-499 W2 internal test hook: when non-null, replaces the tier-sized
    /// no-progress stall budget of <see cref="MonitorFfmpegHlsAsync"/> (see
    /// <see cref="WaitForExitOrEncodeStallAsync"/>) so tests exercise the
    /// progress-extension loop without waiting wall-clock minutes.
    /// </summary>
    internal TimeSpan? HlsMonitorStallBudgetOverride { get; set; }

    /// <summary>
    /// JF-499 W2: wait for the HLS encode process to exit, EXTENDING the wait while
    /// the encode keeps making observable progress. The tier-sized budget is a
    /// NO-PROGRESS window, not a total wall-clock cap: a NAS-bound remux that keeps
    /// writing segments (however slowly) was killed by the old flat ceiling
    /// mid-encode, leaving no-ENDLIST debris mid-playback and forcing a from-zero
    /// re-encode on the self-heal retry. Progress evidence is the latest
    /// LastWriteTimeUtc across the encode directory: every new segment file and each
    /// playlist rewrite advances it.
    /// </summary>
    /// <param name="process">The ffmpeg process to wait on.</param>
    /// <param name="hlsDir">The encode's HLS directory (progress evidence source).</param>
    /// <param name="label">Content label for log messages.</param>
    /// <param name="itemId">Item ID for log messages.</param>
    /// <param name="stallBudget">How long to wait without observed progress before declaring a stall.</param>
    /// <returns>True when the budget elapsed with no progress since the previous
    /// expiry (a hung encode to kill); false when the process exited.</returns>
    private async Task<bool> WaitForExitOrEncodeStallAsync(
        Process process,
        string hlsDir,
        string label,
        string itemId,
        TimeSpan stallBudget)
    {
        DateTime progressMarkUtc = GetLatestWriteTimeUtc(hlsDir);
        while (true)
        {
            using var stallCts = new CancellationTokenSource(stallBudget);
            try
            {
                await process.WaitForExitAsync(stallCts.Token).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                DateTime observedUtc = GetLatestWriteTimeUtc(hlsDir);
                if (observedUtc <= progressMarkUtc)
                {
                    return true;
                }

                progressMarkUtc = observedUtc;
                _logger.LogInformation(
                    "{Label} HLS encode for {ItemId} still writing after {StallBudget} without exiting (slow storage?), extending the monitor budget (JF-499)",
                    label, itemId, stallBudget);
            }
        }
    }

    /// <summary>
    /// Latest LastWriteTimeUtc across an encode directory's files (MinValue when the
    /// directory cannot be read): any segment write or playlist rewrite advances it.
    /// </summary>
    /// <param name="hlsDir">The encode's HLS directory.</param>
    /// <returns>The newest file write time, or <see cref="DateTime.MinValue"/> when unreadable.</returns>
    private static DateTime GetLatestWriteTimeUtc(string hlsDir)
    {
        try
        {
            DateTime latest = DateTime.MinValue;
#pragma warning disable CA3003 // hlsDir is the plugin's own cache dir (GUID-validated itemId)
            foreach (string file in Directory.EnumerateFiles(hlsDir))
            {
                DateTime written = System.IO.File.GetLastWriteTimeUtc(file);
                if (written > latest)
                {
                    latest = written;
                }
            }
#pragma warning restore CA3003

            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable directory: no progress evidence, so the stall decision stands.
            return DateTime.MinValue;
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
    /// <summary>
    /// Best-effort file delete. Internal for the JF-499 W4 permission-denied test:
    /// the helper is reachable from play paths (the JF-531 no-runtime stale-listing
    /// delete, the failed-remux faststart cleanup), so a permission failure must be
    /// swallowed like an I/O failure instead of surfacing as a 500.
    /// </summary>
    /// <param name="path">File path to delete when it exists.</param>
    internal static void TryDelete(string path)
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
        catch (UnauthorizedAccessException)
        {
            // Best effort (JF-499 W4): a permission-denied path must not 500 the play
        }
    }
#pragma warning restore CA3003
}
