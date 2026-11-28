using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Token-gated stream proxy (JF-950 item 2): serves audio, video, and primary-image bytes
/// behind the same signed item-scoped HMAC token the video-audio endpoints use (JF-309),
/// fetching from Jellyfin SERVER-SIDE over the loopback address so the user's long-lived
/// Jellyfin session token never appears in a URL that transits Amazon's servers or a
/// reverse proxy's access logs. The Alexa device presents only the short-lived
/// (10h default) item-scoped token minted at launch time.
/// </summary>
/// <remarks>
/// <para>
/// ROUTING: the kind of stream to serve is derived from the item itself
/// (<see cref="MediaBrowser.Controller.Entities.BaseItem.MediaType"/>): Audio items proxy
/// <c>/Audio/{id}/stream?static=true</c>, Video items <c>/Videos/{id}/stream?static=true</c>,
/// anything else 404s. Range requests are passed through to Jellyfin and the upstream
/// 206/200 status, Content-Range, Accept-Ranges, Content-Length, and Content-Type are
/// copied back verbatim, so AudioPlayer seek and VideoApp resume behave exactly as they do
/// against the native static endpoints.
/// </para>
/// <para>
/// THE <c>u</c> PARAMETER IS NOT AUTHENTICATION. The HMAC token is the only gate. The
/// optional <c>?u={pluginUserId}</c> selects which CONFIGURED plugin user's Jellyfin token
/// performs the server-side fetch; an absent or unknown value falls back to the first
/// configured user with a stored token. It grants nothing on its own: every candidate is
/// an already-configured user, the fetch runs server-side (the Jellyfin token is never
/// returned to the caller), and the stream endpoints the proxy targets do not re-scope
/// content by user (the library scoping happens at launch time, in the handler that
/// resolved the item; jellyfin#13984 documents that the static stream endpoints are not
/// key-gated at all). The current <see cref="Alexa.Util.StreamTokenHelper"/> token shape
/// cannot carry the user id (the HMAC payload is item + expiry + library scope), which is
/// why the user rides the query string instead of the token.
/// </para>
/// <para>
/// NO <c>[Authorize]</c>: the Echo fetches these URLs with no Jellyfin credentials; the
/// HMAC token is the gate (same convention as <see cref="VideoAudioController"/>, whose
/// rejection shapes this controller mirrors: 400 non-GUID id, 401 bad/expired token,
/// 503 empty secret or unconfigured plugin, 404 unknown item).
/// </para>
/// </remarks>
[ApiController]
[Route("alexaskill/api/stream")]
public class StreamProxyController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamProxyController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamProxyController"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager, for resolving the item's media kind.</param>
    /// <param name="httpClientFactory">HTTP client factory; the stream proxy uses the
    /// timeout-free <c>AlexaSkillStreamProxy</c> named client because a media stream runs
    /// at the device's read pace and outlives any fixed timeout.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public StreamProxyController(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = loggerFactory.CreateLogger<StreamProxyController>();
    }

    /// <summary>
    /// Proxy a media stream for the item: audio items via Jellyfin's static
    /// <c>/Audio/{id}/stream</c> endpoint, video items via <c>/Videos/{id}/stream</c>.
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <returns>The proxied stream (200/206 with Range headers copied), or the rejection
    /// shapes documented on the class (400/401/404/503).</returns>
    [HttpGet("{itemId}")]
    public async Task<ActionResult> ProxyStream(string itemId)
    {
        ActionResult? error = ValidateTokenGate(itemId);
        if (error != null)
        {
            return error;
        }

        MediaBrowser.Controller.Entities.BaseItem? item = _libraryManager.GetItemById(Guid.Parse(itemId));
        if (item == null)
        {
            _logger.LogWarning("StreamProxy: item {ItemId} not found", itemId);
            return NotFound(new { error = "Item not found" });
        }

        // JF-575: capital ApiKey is the only query shape Jellyfin 12.0's general API
        // accepts (lowercase api_key 401s there; 10.11 accepts both). The static stream
        // endpoints are themselves auth-open (jellyfin#13984), so the key is belt here,
        // but the capital shape is safe on both server lines.
        string streamSegment = item.MediaType switch
        {
            MediaType.Audio => "Audio",
            MediaType.Video => "Videos",
            _ => string.Empty
        };
        if (streamSegment.Length == 0)
        {
            _logger.LogWarning("StreamProxy: item {ItemId} ({ItemType}) is not an audio or video item", itemId, item.GetType().Name);
            return NotFound(new { error = "Item is not a streamable media type" });
        }

        return await ProxyCore(itemId, $"/{streamSegment}/{itemId}/stream?static=true").ConfigureAwait(false);
    }

    /// <summary>
    /// Proxy an item's primary image via Jellyfin's <c>Items/{id}/Images/Primary</c>
    /// endpoint (the cover art the AudioPlayer metadata and APL documents reference).
    /// </summary>
    /// <param name="itemId">The Jellyfin item GUID.</param>
    /// <returns>The proxied image bytes with Content-Type and cache headers copied, or the
    /// rejection shapes documented on the class (400/401/404/503).</returns>
    [HttpGet("image/{itemId}")]
    public async Task<ActionResult> ProxyImage(string itemId)
    {
        ActionResult? error = ValidateTokenGate(itemId);
        if (error != null)
        {
            return error;
        }

        return await ProxyCore(itemId, $"/Items/{itemId}/Images/Primary").ConfigureAwait(false);
    }

    /// <summary>
    /// The shared signed-route preamble (mirrors VideoAudioController's
    /// ValidateSignedRoute ordering): reject a non-GUID route id with 400 BEFORE the token
    /// check (the token binds to the GUID), then validate the signed item-scoped stream
    /// token. Returns null when the request may proceed.
    /// </summary>
    private ActionResult? ValidateTokenGate(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out _))
        {
            return BadRequest(new { error = "Invalid itemId format" });
        }

        string? secret = Plugin.Instance?.Configuration?.StreamTokenSecret;
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogError("StreamProxy: stream token secret not configured");
            return StatusCode(503, new { error = "Stream token secret not configured" });
        }

        string? token = HttpContext.Request.Query["token"];
        if (!StreamTokenHelper.TryValidate(token, itemId, secret))
        {
            _logger.LogWarning("StreamProxy: rejected stream request for {ItemId} (missing/invalid/expired token)", itemId);
            return Unauthorized(new { error = "Invalid or expired stream token" });
        }

        return null;
    }

    /// <summary>
    /// The shared proxy tail: resolve the fetching user, build the upstream URL against
    /// the configured loopback base, forward the Range header, and stream the upstream
    /// body back without buffering.
    /// </summary>
    /// <param name="itemId">The validated item GUID string.</param>
    /// <param name="upstreamPathAndQuery">The Jellyfin-native path (plus static=true for
    /// streams) to fetch server-side.</param>
    /// <returns>The streaming result, or a 503 when no configured user can fetch.</returns>
    private async Task<ActionResult> ProxyCore(string itemId, string upstreamPathAndQuery)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.InternalServerUrl))
        {
            _logger.LogError("StreamProxy: plugin not configured (InternalServerUrl empty)");
            return StatusCode(503, new { error = "Plugin not configured" });
        }

        Entities.User? fetchUser = ResolveFetchUser(HttpContext.Request.Query["u"]);
        if (fetchUser == null)
        {
            _logger.LogError("StreamProxy: no configured plugin user with a Jellyfin token to fetch {ItemId}", itemId);
            return StatusCode(503, new { error = "No configured user with a Jellyfin token" });
        }

        // Capital ApiKey (JF-575, see ProxyStream), appended with the separator the
        // variant's path dictates (the stream path already carries ?static=true, the
        // image path carries no query). The token stays server-side: it is spent on the
        // loopback fetch only and never appears in any response to the device.
        string separator = upstreamPathAndQuery.Contains('?') ? "&" : "?";
        string upstreamUrl = config.InternalServerUrl.TrimEnd('/') + upstreamPathAndQuery + separator + "ApiKey=" + Uri.EscapeDataString(fetchUser.JellyfinToken!);

        // Do NOT dispose the factory-created HttpClient (pooled by IHttpClientFactory), and
        // do NOT dispose the upstream response before its body stream has been fully
        // relayed: FileStreamResult takes ownership of the stream, and disposing the
        // content stream is what returns the pooled connection.
        HttpClient client = _httpClientFactory.CreateClient("AlexaSkillStreamProxy");
        HttpResponseMessage upstream;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
            if (Request.Headers.TryGetValue("Range", out var range) && !string.IsNullOrWhiteSpace(range))
            {
                // Seek/resume on AudioPlayer and VideoApp arrives as Range requests; the
                // upstream 206 + Content-Range must reach the device verbatim.
                request.Headers.TryAddWithoutValidation("Range", range.ToString());
            }

            upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "StreamProxy: upstream fetch failed for {ItemId}", itemId);
            return StatusCode(502, new { error = "Upstream stream fetch failed" });
        }

        string contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        Response.StatusCode = (int)upstream.StatusCode;

        if (upstream.Content.Headers.ContentRange != null)
        {
            Response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
        }

        if (upstream.Headers.AcceptRanges.Count > 0)
        {
            Response.Headers["Accept-Ranges"] = string.Join(", ", upstream.Headers.AcceptRanges);
        }

        if (upstream.Content.Headers.ContentLength.HasValue)
        {
            Response.Headers["Content-Length"] = upstream.Content.Headers.ContentLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Image caching (the image variant): Jellyfin sends Cache-Control/ETag for image
        // responses; copying them lets the Echo cache cover art like it does today.
        // (Cache-Control and ETag are response headers, not content headers, in the
        // HttpResponseMessage model.)
        if (upstream.Headers.CacheControl != null)
        {
            Response.Headers["Cache-Control"] = upstream.Headers.CacheControl.ToString();
        }

        var etag = upstream.Headers.ETag;
        if (etag != null)
        {
            Response.Headers["ETag"] = etag.ToString();
        }

        System.IO.Stream body = await upstream.Content.ReadAsStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return File(body, contentType);
    }

    /// <summary>
    /// Resolve the configured plugin user whose Jellyfin token performs the server-side
    /// fetch: the <c>?u=</c> id when it names a configured user WITH a stored token,
    /// otherwise the FIRST configured user with a stored token. See the class remarks for
    /// why this is a fetch-credential selector, not an authentication input.
    /// </summary>
    /// <param name="u">The raw <c>?u=</c> query value from the request.</param>
    /// <returns>The user to fetch as, or null when no configured user has a token.</returns>
    private static Entities.User? ResolveFetchUser(string? u)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(u) && Guid.TryParse(u, out Guid userId))
        {
            Entities.User? match = config.Users.FirstOrDefault(user => user.Id == userId && user.HasJellyfinToken);
            if (match != null)
            {
                return match;
            }
        }

        return config.Users.FirstOrDefault(user => user.HasJellyfinToken);
    }
}
