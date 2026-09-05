using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Microsoft.Extensions.Logging;
using PluginVideoApp = Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Response interceptor that records the last-played item per device for VIDEO content
/// (movies/TV episodes): the one play path that bypasses
/// <c>BaseHandler.BuildAudioPlayerResponse</c>. Those handlers build a <c>VideoApp.Launch</c>
/// directive inline with either a <c>/Videos/{id}/stream</c> source URL (compatible codecs,
/// served statically) or the episode remux HLS URL
/// (<c>/alexaskill/api/video-audio/episode/{id}/stream.m3u8</c>, JF-498), so this
/// interceptor recovers the item ID from the outgoing directive for both shapes.
/// </summary>
/// <remarks>
/// Audio (incl. audiobooks, with chapter precision) is recorded separately in
/// <c>BaseHandler.BuildAudioPlayerResponse</c>; this interceptor intentionally only acts on
/// the two VIDEO source shapes to avoid duplicating that recording and to preserve audiobook
/// chapter accuracy (the audiobook HLS concat URL carries only the book ID, not the chapter).
/// </remarks>
public class LastPlayedResponseInterceptor : IResponseInterceptor
{
    private const string VideoPathSegment = "/Videos/";

    /// <summary>
    /// The JF-498 episode remux playlist URL prefix; the GUID it carries is the
    /// movie/episode item ID. Deliberately distinct from the single-item audio path
    /// (<c>/video-audio/{guid}/</c>) and the audiobook concat path
    /// (<c>/video-audio/audiobook/{guid}/</c>), which must NOT be recorded here.
    /// </summary>
    private const string EpisodeHlsPathSegment = "/alexaskill/api/video-audio/episode/";

    private static readonly char[] PathOrQueryDelimiters = { '/', '?' };

    private readonly DeviceQueueManager _queueManager;
    private readonly ILogger<LastPlayedResponseInterceptor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LastPlayedResponseInterceptor"/> class.
    /// </summary>
    /// <param name="queueManager">Per-device playback queue manager (records last-played item).</param>
    /// <param name="logger">Logger instance.</param>
    public LastPlayedResponseInterceptor(DeviceQueueManager queueManager, ILogger<LastPlayedResponseInterceptor> logger)
    {
        _queueManager = queueManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        // Cheap no-op guard: nothing to inspect.
        if (context.Response?.Response?.Directives is null)
        {
            return Task.CompletedTask;
        }

        string? deviceId = context.AlexaContext?.System?.Device?.DeviceID;
        if (string.IsNullOrEmpty(deviceId))
        {
            return Task.CompletedTask;
        }

        // First VideoApp.Launch directive pointing at a /Videos/ stream = a movie/episode play.
        // (Audio-via-VideoApp and audiobook-concat URLs use /alexaskill/api/video-audio/... and
        // are recorded with chapter precision by BaseHandler.BuildAudioPlayerResponse instead.)
        foreach (IDirective directive in context.Response.Response.Directives)
        {
            if (directive is not PluginVideoApp videoDirective)
            {
                continue;
            }

            string? source = videoDirective.VideoItem?.Source;
            if (string.IsNullOrEmpty(source))
            {
                continue;
            }

            // Extract the GUID path segment from .../Videos/{guid}/stream?... (static
            // video source) or .../video-audio/episode/{guid}/stream.m3u8?... (JF-498
            // remux source) and validate it. Returns null for anything else
            // (audio-via-VideoApp, audiobook concat).
            string? itemId = ExtractVideoItemId(source);
            if (itemId == null)
            {
                _logger.LogDebug("LastPlayed: no video item GUID in source {Source}", source);
                continue;
            }

            _queueManager.RecordLastPlayed(deviceId, itemId);
            _logger.LogDebug(
                "Recorded last played (video) for device {DeviceId}: item={ItemId}, corr={CorrelationId}",
                deviceId, itemId, context.CorrelationId);
            break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Extract the item ID from a video launch source URL: either Jellyfin's static
    /// stream (<c>/Videos/{guid}/stream</c>) or the episode remux playlist
    /// (<c>/alexaskill/api/video-audio/episode/{guid}/stream.m3u8</c>, JF-498).
    /// Returns null when the source matches neither shape or its segment is not a
    /// valid GUID.
    /// </summary>
    private static string? ExtractVideoItemId(string source)
    {
        int start = source.IndexOf(VideoPathSegment, StringComparison.Ordinal);
        if (start < 0)
        {
            start = source.IndexOf(EpisodeHlsPathSegment, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += EpisodeHlsPathSegment.Length;
        }
        else
        {
            start += VideoPathSegment.Length;
        }

        int end = source.IndexOfAny(PathOrQueryDelimiters, start);
        string segment = end < 0 ? source[start..] : source[start..end];
        return Guid.TryParse(segment, out _) ? segment : null;
    }
}