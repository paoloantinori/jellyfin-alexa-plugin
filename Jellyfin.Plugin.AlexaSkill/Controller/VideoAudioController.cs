using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
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
    private readonly VideoAudioCache _cache;
    private readonly ILogger<VideoAudioController> _logger;

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
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
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
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out Guid itemGuid))
        {
            return BadRequest(new { error = "Invalid itemId format" });
        }

        // Resolve ffmpeg path
        string ffmpeg = ResolveFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogError("ffmpeg not available for VideoAudio request");
            return StatusCode(503, new { error = "ffmpeg is not available on this server" });
        }

        MediaBrowser.Controller.Entities.BaseItem? item = _libraryManager.GetItemById(itemGuid);
        if (item == null)
        {
            _logger.LogWarning("VideoAudio: item {ItemId} not found", itemId);
            return NotFound(new { error = "Item not found" });
        }

        // Only items with media sources (Audio, Video) can be streamed.
        // Folders, Book folders, etc. would cause ffmpeg to fail with a 500 from Jellyfin.
        if (item is not MediaBrowser.Controller.Entities.IHasMediaSources)
        {
            _logger.LogWarning("VideoAudio: item {ItemId} ({ItemType}) is not a streamable media type", itemId, item.GetType().Name);
            return BadRequest(new { error = "Item is not a streamable media type" });
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.ServerAddress))
        {
            return StatusCode(503, new { error = "Plugin not configured" });
        }

        string serverUrl = config.ServerAddress.TrimEnd('/');

        // Determine cache key: itemId + album art modification time
        long artModifiedTicks = GetArtModifiedTicks(item);

        // Check cache first
        FileInfo? cached = await _cache.GetCachedFile(itemId, artModifiedTicks).ConfigureAwait(false);
        if (cached != null)
        {
            _logger.LogDebug("VideoAudio: serving cached file for item {ItemId}", itemId);
            return PhysicalFile(cached.FullName, "video/mp4");
        }

        // Build audio URL (ffmpeg fetches it directly via HTTP).
        // Jellyfin's /Audio/{id}/stream endpoint works without auth for static streams.
        string audioUrl = $"{serverUrl}/Audio/{itemId}/stream?static=true";

        // Determine album art URL — prefer item image, then parent, then black frame.
        // Image endpoints also work without auth.
        string? artUrl = ResolveArtUrl(item, serverUrl);
        bool useBlackFrame = artUrl == null;

        _logger.LogDebug(
            "VideoAudio: itemId={ItemId}, artUrl={ArtUrl}, useBlackFrame={UseBlackFrame}",
            itemId,
            artUrl ?? "(black frame)",
            useBlackFrame);

        // Prepare cache file path
        string cachePath = _cache.GetCacheFilePath(itemId, artModifiedTicks);
        string? cacheDir = Path.GetDirectoryName(cachePath);
        if (cacheDir != null)
        {
            Directory.CreateDirectory(cacheDir);
        }

        // Build ffmpeg arguments (includes output file path directly)
        var ffmpegArgs = BuildFfmpegArguments(artUrl, audioUrl, useBlackFrame, cachePath);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("VideoAudio: ffmpeg arguments: {Args}", string.Join(" ", ffmpegArgs));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            // Generate to file first, then serve — gives proper Content-Length header
            // which VideoApp needs for reliable playback on Echo Show.
            await RunFfmpegToFileAsync(ffmpeg, ffmpegArgs, cachePath, cts.Token).ConfigureAwait(false);

            _logger.LogDebug("VideoAudio: serving generated file for item {ItemId}", itemId);
            return PhysicalFile(cachePath, "video/mp4");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("VideoAudio: ffmpeg timed out for item {ItemId}", itemId);
            DeleteCacheFile(cachePath);
            return StatusCode(504, new { error = "Video generation timed out" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VideoAudio: ffmpeg failed for item {ItemId}", itemId);
            DeleteCacheFile(cachePath);
            return StatusCode(500, new { error = "Video generation failed" });
        }
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
    /// <returns>List of ffmpeg arguments (one token per entry).</returns>
    internal static List<string> BuildFfmpegArguments(string? artUrl, string audioUrl, bool useBlackFrame, string outputPath)
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
        args.AddRange(AudioCodecArgs);
        args.AddRange(PixelFormatArgs);
        args.AddRange(VideoFilterArgs);
        args.AddRange(OutputFormatArgs);
        args.Add("-shortest");
        args.Add(outputPath);

        return args;
    }

    private static readonly string[] BlackFrameInputArgs = ["-f", "lavfi", "-i", "color=c=black:s=1280x720:d=999"];
    private static readonly string[] ArtInputPrefixArgs = ["-loop", "1", "-framerate", "1", "-i"];
    private static readonly string[] VideoCodecArgs = ["-c:v", "libx264", "-tune", "stillimage", "-preset", "ultrafast", "-crf", "28"];
    private static readonly string[] AudioCodecArgs = ["-c:a", "aac", "-b:a", "128k"];
    private static readonly string[] PixelFormatArgs = ["-pix_fmt", "yuv420p", "-r", "1"];
    private static readonly string[] VideoFilterArgs = ["-vf", "scale=1280x720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black"];
    private static readonly string[] OutputFormatArgs = ["-f", "mp4", "-movflags", "frag_keyframe+empty_moov"];

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
    /// Run ffmpeg to generate the MP4 file to disk.
    /// Uses <see cref="ProcessStartInfo.ArgumentList"/> to pass arguments as individual tokens,
    /// eliminating shell interpretation and command-line injection risk (CWE-78/CWE-88).
    /// </summary>
    /// <param name="ffmpegPath">Path to ffmpeg binary.</param>
    /// <param name="arguments">ffmpeg command-line arguments as individual tokens.</param>
    /// <param name="outputPath">Path to write the output file.</param>
    /// <param name="cancellationToken">Cancellation token for timeout/abort.</param>
    private async Task RunFfmpegToFileAsync(
        string ffmpegPath,
        List<string> arguments,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
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

        // Read stderr asynchronously to prevent deadlock
        var stderrTask = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                _logger.LogDebug("ffmpeg stderr: {Line}", line);
            }
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg exited with code {ExitCode}", process.ExitCode);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
        }

        // Background eviction — don't block the response
        _ = Task.Run(() => _cache.EvictIfNeeded(), CancellationToken.None);
    }

    /// <summary>
    /// Delete an incomplete cache file. Called when ffmpeg fails or times out.
    /// </summary>
    /// <param name="cachePath">Path to the cache file to delete.</param>
    private void DeleteCacheFile(string cachePath)
    {
        try
        {
#pragma warning disable CA3003 // cachePath is built from GUID-validated itemId and numeric ticks
            if (System.IO.File.Exists(cachePath))
            {
                System.IO.File.Delete(cachePath);
#pragma warning restore CA3003
                _logger.LogDebug("Deleted incomplete cache file: {Path}", cachePath);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete incomplete cache file: {Path}", cachePath);
        }
    }
}
