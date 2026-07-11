using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Resolves a playable stream URL for a Live TV channel via Jellyfin's PlaybackInfo
/// endpoint (<c>/Items/{id}/PlaybackInfo?AutoOpenLiveStream=true</c>).
/// </summary>
/// <remarks>
/// <para>
/// Live TV channels cannot use the static stream endpoint that serves files
/// (<c>/Audio|Videos/{id}/stream?static=true</c>) — it returns HTTP 500 for a live
/// source. This resolver queries PlaybackInfo and chooses between two strategies:
/// </para>
/// <list type="bullet">
/// <item><b>Direct remote</b> (IPTV/M3U sources): the media source exposes a remote
/// HLS master URL as its <c>Path</c>. Feeding that URL directly to VideoApp.Launch
/// works because the stream is already H.264/AAC HLS that the Echo Show's ExoPlayer
/// can play. (Jellyfin's own HLS re-wrap via <c>master.m3u8 -> live.m3u8</c> 500s here
/// because the source is <i>already</i> HLS.)</item>
/// <item><b>Jellyfin dynamic HLS</b> (hardware tuners / sources needing transcode):
/// fall back to <c>/Videos/{id}/master.m3u8?MediaSourceId=...[&amp;LiveStreamId=...]</c>.</item>
/// </list>
/// </remarks>
public class LiveTvStreamResolver : ILiveTvStreamResolver
{
    /// <summary>Hard cap on the PlaybackInfo self-call to avoid the unbounded-HTTP hang class of bugs.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PluginConfiguration _config;
    private readonly ILogger<LiveTvStreamResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvStreamResolver"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory (registered "AlexaSkill" named client).</param>
    /// <param name="config">Plugin configuration (provides <see cref="PluginConfiguration.ServerAddress"/>).</param>
    /// <param name="logger">Logger.</param>
    public LiveTvStreamResolver(IHttpClientFactory httpClientFactory, PluginConfiguration config, ILogger<LiveTvStreamResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LiveTvStream?> ResolveAsync(BaseItem channel, Entities.User user, CancellationToken cancellationToken)
    {
        string? server = _config.ServerAddress;
        string? token = user.JellyfinToken;
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
        {
            _logger.LogDebug("ResolveAsync: missing ServerAddress or JellyfinToken — cannot resolve channel");
            return null;
        }

        string root = server.TrimEnd('/');
        string channelId = channel.Id.ToString("N");
        string url = $"{root}/Items/{channelId}/PlaybackInfo"
            + $"?UserId={user.Id}&IsPlayback=true&AutoOpenLiveStream=true&api_key={token}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            // Note: do not dispose the factory-created HttpClient (pooled by IHttpClientFactory).
            var client = _httpClientFactory.CreateClient("AlexaSkill");
            using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("ResolveAsync: PlaybackInfo returned {Status} for channel {Id}", (int)response.StatusCode, channelId);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("MediaSources", out var sources)
                || sources.ValueKind != JsonValueKind.Array
                || sources.GetArrayLength() == 0)
            {
                _logger.LogDebug("ResolveAsync: no MediaSources returned for channel {Id}", channelId);
                return null;
            }

            JsonElement ms = sources[0];
            string? mediaSourceId = GetOptionalString(ms, "Id");
            if (string.IsNullOrWhiteSpace(mediaSourceId))
            {
                _logger.LogDebug("ResolveAsync: MediaSource has no Id for channel {Id}", channelId);
                return null;
            }

            bool supportsDirect = ms.TryGetProperty("SupportsDirectStream", out var sdProp) && sdProp.ValueKind == JsonValueKind.True;
            string? path = GetOptionalString(ms, "Path");

            // Direct-remote: a remote http(s) HLS URL that ExoPlayer plays directly. The URL scheme
            // is the authoritative signal — Jellyfin may tag IPTV sources with various Protocol
            // values while still exposing a playable remote HLS URL in Path.
            if (supportsDirect
                && !string.IsNullOrWhiteSpace(path)
                && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("ResolveAsync: direct-remote stream for channel {Id} -> {Url}", channelId, path);
                return new LiveTvStream(path);
            }

            // Fallback: hardware tuner / transcode path via Jellyfin dynamic HLS master playlist.
            string? liveStreamId = GetOptionalString(ms, "LiveStreamId");
            string fallback = $"{root}/Videos/{channelId}/master.m3u8?MediaSourceId={Uri.EscapeDataString(mediaSourceId)}&api_key={token}";
            if (!string.IsNullOrWhiteSpace(liveStreamId))
            {
                fallback += $"&LiveStreamId={Uri.EscapeDataString(liveStreamId)}";
            }

            _logger.LogDebug("ResolveAsync: dynamic-HLS fallback for channel {Id} -> {Url}", channelId, fallback);
            return new LiveTvStream(fallback);
        }
        // If the caller cancelled (upstream abandoned the request), propagate cancellation
        // instead of masking it as a synthetic "resolve failed".
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "ResolveAsync: failed to resolve PlaybackInfo for channel {Id}", channelId);
            return null;
        }
    }

    /// <summary>Reads an optional string property; returns null when absent or non-string.</summary>
    private static string? GetOptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
