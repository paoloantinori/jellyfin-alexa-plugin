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
/// (movies/TV episodes) — the one play path that bypasses
/// <c>BaseHandler.BuildAudioPlayerResponse</c>. Those handlers build a <c>VideoApp.Launch</c>
/// directive inline with a <c>/Videos/{id}/stream</c> source URL, so this interceptor recovers
/// the item ID from the outgoing directive.
/// </summary>
/// <remarks>
/// Audio (incl. audiobooks, with chapter precision) is recorded separately in
/// <c>BaseHandler.BuildAudioPlayerResponse</c>; this interceptor intentionally only acts on
/// <c>/Videos/</c> sources to avoid duplicating that recording and to preserve audiobook chapter
/// accuracy (the audiobook HLS concat URL carries only the book ID, not the chapter).
/// </remarks>
public class LastPlayedResponseInterceptor : IResponseInterceptor
{
    private const string VideoPathSegment = "/Videos/";

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

            // Extract the GUID path segment from .../Videos/{guid}/stream?... and validate it.
            // Returns null for non-/Videos/ sources (audio-via-VideoApp, audiobook concat).
            string? itemId = ExtractVideoItemId(source);
            if (itemId == null)
            {
                _logger.LogDebug("LastPlayed: no /Videos/ item GUID in source {Source}", source);
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
    /// Extract the item ID from a Jellyfin video stream URL (<c>/Videos/{guid}/stream</c>).
    /// Returns null when the source is not a <c>/Videos/</c> URL or its segment is not a valid GUID.
    /// </summary>
    private static string? ExtractVideoItemId(string source)
    {
        int start = source.IndexOf(VideoPathSegment, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += VideoPathSegment.Length;
        int end = source.IndexOfAny(PathOrQueryDelimiters, start);
        string segment = end < 0 ? source[start..] : source[start..end];
        return Guid.TryParse(segment, out _) ? segment : null;
    }
}