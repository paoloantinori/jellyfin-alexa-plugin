using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// Tests for <see cref="StreamProxyController"/> (JF-950 item 2): the signed-token
/// stream proxy that removes the long-lived Jellyfin session token from device-facing
/// stream and image URLs. The upstream Jellyfin fetch is faked via a configurable
/// <see cref="HttpMessageHandler"/> (the FakeSmaCatalogHandler convention); the token
/// gate reuses the shared Plugin instance's auto-generated secret, so this class is a
/// Plugin-collection member (the VideoAudioControllerTests static-surface rule).
/// Pins: the JF-575 capital ApiKey on the server-side fetch, the Range passthrough with
/// 206/Content-Range copied back, the Audio-vs-Video path selection by item media kind,
/// the 400/401/404/503 rejection shapes mirroring VideoAudioController, the image
/// variant with cache headers, and the u-param fetch-user selection (including the
/// missing/unknown-u fallback to the first tokened configured user).
/// </summary>
[Collection("Plugin")]
public class StreamProxyControllerTests : PluginTestBase, IDisposable
{
    private const string InternalUrl = "http://localhost:8096";

    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly FakeStreamUpstreamHandler _upstream = new();
    private PluginConfiguration Config => Plugin.Instance!.Configuration;

    public StreamProxyControllerTests()
    {
        EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            cfg =>
            {
                // The JF-950 fixtures own config.Users across the class: start from the
                // canonical empty state so a previous class's users cannot leak into the
                // u-selection pins.
                cfg.Users.Clear();
                cfg.InternalServerUrl = InternalUrl;
            },
            "stream-proxy-test");
    }

    public void Dispose()
    {
        // Leave the shared config in the canonical empty-users state for the next class.
        Config.Users.Clear();
        Config.InternalServerUrl = "http://localhost:8096";
    }

    private static Entities.User AddUser(string jellyfinToken, Guid? id = null)
    {
        var user = new Entities.User { Id = id ?? Guid.NewGuid(), JellyfinToken = jellyfinToken };
        Plugin.Instance!.Configuration.Users.Add(user);
        return user;
    }

    private StreamProxyController CreateController(
        string? itemIdForToken = null,
        string? u = null,
        string? range = null,
        TimeSpan? tokenTtl = null)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(_upstream));

        var controller = new StreamProxyController(_libraryManagerMock.Object, factoryMock.Object, _loggerFactory);

        var query = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        if (itemIdForToken != null)
        {
            query["token"] = StreamTokenHelper.Mint(itemIdForToken, Config.StreamTokenSecret, tokenTtl);
        }

        if (u != null)
        {
            query["u"] = u;
        }

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Query = new QueryCollection(query);
        if (range != null)
        {
            httpContext.Request.Headers["Range"] = range;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static Audio AudioItem(Guid id) => new() { Id = id, Name = "Song" };

    private static Movie MovieItem(Guid id) => new() { Id = id, Name = "Movie" };

    // ========== Token gate (the VideoAudioController rejection-shape mirrors) ==========

    [Fact]
    public async Task ProxyStream_InvalidItemId_Returns400()
    {
        var controller = CreateController();

        ActionResult result = await controller.ProxyAudio("not-a-guid");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task ProxyStream_NoToken_Returns401()
    {
        AddUser("tok");
        var controller = CreateController(); // no token in query

        ActionResult result = await controller.ProxyAudio(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task ProxyStream_ExpiredToken_Returns401()
    {
        AddUser("tok");
        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemIdForToken: itemId, tokenTtl: TimeSpan.FromHours(-1));

        ActionResult result = await controller.ProxyAudio(itemId);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task ProxyStream_WrongItemToken_Returns401()
    {
        AddUser("tok");
        var controller = CreateController(itemIdForToken: Guid.NewGuid().ToString()); // token for OTHER item

        ActionResult result = await controller.ProxyAudio(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task ProxyStream_UnknownItem_Returns404()
    {
        AddUser("tok");
        _libraryManagerMock.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);
        string itemId = Guid.NewGuid().ToString();
        var controller = CreateController(itemIdForToken: itemId);

        ActionResult result = await controller.ProxyAudio(itemId);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
        Assert.Empty(_upstream.Requests);
    }


    // ========== The proxy path: audio ==========

    [Fact]
    public async Task ProxyStream_AudioItem_FetchesUpstreamWithCapitalApiKeyAndReturns206()
    {
        Entities.User user = AddUser("secret-token");
        var song = AudioItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(song.Id)).Returns(song);
        string itemId = song.Id.ToString();
        var controller = CreateController(itemIdForToken: itemId, u: user.Id.ToString(), range: "bytes=0-99");

        ActionResult result = await controller.ProxyAudio(itemId);

        // The upstream fetch: loopback base, Audio path, static=true, CAPITAL ApiKey
        // (JF-575: lowercase api_key 401s on Jellyfin 12.0), and the Range copied.
        var (url, range) = Assert.Single(_upstream.Requests);
        Assert.Equal($"{InternalUrl}/Audio/{itemId}/stream?static=true&ApiKey=secret-token", url);
        Assert.Equal("bytes=0-99", range);

        // The response relays the upstream 206 with Content-Range/Content-Type, unbuffered.
        Assert.Equal(StatusCodes.Status206PartialContent, controller.Response.StatusCode);
        Assert.Equal("bytes 0-99/1000", controller.Response.Headers["Content-Range"].ToString());
        Assert.Equal("bytes", controller.Response.Headers["Accept-Ranges"].ToString());
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("audio/mpeg", file.ContentType);
    }

    [Fact]
    public async Task ProxyStream_VideoItem_FetchesVideosPath()
    {
        AddUser("tok");
        var movie = MovieItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(movie.Id)).Returns(movie);
        string itemId = movie.Id.ToString();
        _upstream.Status = HttpStatusCode.OK;
        _upstream.IncludeContentRange = false;
        _upstream.ContentType = "video/mp4";
        var controller = CreateController(itemIdForToken: itemId);

        ActionResult result = await controller.ProxyVideo(itemId);

        var (url, range) = Assert.Single(_upstream.Requests);
        Assert.Equal($"{InternalUrl}/Videos/{itemId}/stream?static=true&ApiKey=tok", url);
        Assert.Null(range); // no Range on the request -> none forwarded
        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("video/mp4", file.ContentType);
    }

    [Fact]
    public async Task ProxyStream_NoTokenedUser_Returns503()
    {
        var song = AudioItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(song.Id)).Returns(song);
        string itemId = song.Id.ToString();
        var controller = CreateController(itemIdForToken: itemId); // config.Users is empty

        ActionResult result = await controller.ProxyAudio(itemId);

        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    // ========== The u-param fetch-user selection ==========

    [Fact]
    public async Task ProxyStream_MissingU_FallsBackToFirstTokenedUser()
    {
        AddUser("first-token");
        AddUser("second-token");
        var song = AudioItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(song.Id)).Returns(song);
        string itemId = song.Id.ToString();
        var controller = CreateController(itemIdForToken: itemId); // no u

        await controller.ProxyAudio(itemId);

        var (url, _) = Assert.Single(_upstream.Requests);
        Assert.EndsWith("ApiKey=first-token", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyStream_UnknownU_FallsBackToFirstTokenedUser()
    {
        AddUser("first-token");
        var song = AudioItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(song.Id)).Returns(song);
        string itemId = song.Id.ToString();
        var controller = CreateController(itemIdForToken: itemId, u: Guid.NewGuid().ToString());

        await controller.ProxyAudio(itemId);

        var (url, _) = Assert.Single(_upstream.Requests);
        Assert.EndsWith("ApiKey=first-token", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyStream_USelectsThatUsersFetchToken()
    {
        AddUser("first-token");
        Entities.User second = AddUser("second-token");
        var song = AudioItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(song.Id)).Returns(song);
        string itemId = song.Id.ToString();
        var controller = CreateController(itemIdForToken: itemId, u: second.Id.ToString());

        await controller.ProxyAudio(itemId);

        var (url, _) = Assert.Single(_upstream.Requests);
        Assert.EndsWith("ApiKey=second-token", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyStream_UForTokenlessUser_FallsBackToFirstTokenedUser()
    {
        Entities.User tokenless = new() { Id = Guid.NewGuid() }; // no JellyfinToken
        Plugin.Instance!.Configuration.Users.Add(tokenless);
        AddUser("first-token");
        var song = AudioItem(Guid.NewGuid());
        _libraryManagerMock.Setup(m => m.GetItemById(song.Id)).Returns(song);
        string itemId = song.Id.ToString();
        var controller = CreateController(itemIdForToken: itemId, u: tokenless.Id.ToString());

        await controller.ProxyAudio(itemId);

        var (url, _) = Assert.Single(_upstream.Requests);
        Assert.EndsWith("ApiKey=first-token", url, StringComparison.Ordinal);
    }

    // ========== The image variant ==========

    [Fact]
    public async Task ProxyImage_ValidToken_ProxiesPrimaryImageWithCacheHeaders()
    {
        Entities.User user = AddUser("img-token");
        string itemId = Guid.NewGuid().ToString();
        _upstream.Status = HttpStatusCode.OK;
        _upstream.IncludeContentRange = false;
        _upstream.ContentType = "image/jpeg";
        var controller = CreateController(itemIdForToken: itemId, u: user.Id.ToString());

        ActionResult result = await controller.ProxyImage(itemId);

        var (url, range) = Assert.Single(_upstream.Requests);
        Assert.Equal($"{InternalUrl}/Items/{itemId}/Images/Primary?ApiKey=img-token", url);
        Assert.Null(range);

        Assert.Equal(StatusCodes.Status200OK, controller.Response.StatusCode);
        Assert.Equal("public, max-age=86400", controller.Response.Headers["Cache-Control"].ToString());
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    [Fact]
    public async Task ProxyImage_NoToken_Returns401()
    {
        AddUser("tok");
        var controller = CreateController(); // no token

        ActionResult result = await controller.ProxyImage(Guid.NewGuid().ToString());

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Empty(_upstream.Requests);
    }

    /// <summary>
    /// The fake Jellyfin upstream: records every GET as a (url, Range) pair (the
    /// HttpRequestMessage is disposed by the controller after SendAsync, so the recorded
    /// data is copied out eagerly) and answers with a configurable status, content type,
    /// Content-Range, Accept-Ranges, Cache-Control, and body. Defaults are the audio
    /// Range-answer shape (206 + Content-Range).
    /// </summary>
    internal sealed class FakeStreamUpstreamHandler : HttpMessageHandler
    {
        public List<(string Url, string? Range)> Requests { get; } = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.PartialContent;

        public string ContentType { get; set; } = "audio/mpeg";

        public bool IncludeContentRange { get; set; } = true;

        public string CacheControl { get; set; } = "public, max-age=86400";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Range?.ToString()));

            var content = new ByteArrayContent(new byte[100]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ContentType);
            if (IncludeContentRange)
            {
                content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 99, 1000);
            }

            var response = new HttpResponseMessage(Status) { Content = content };
            response.Headers.AcceptRanges.ParseAdd("bytes");
            response.Headers.TryAddWithoutValidation("Cache-Control", CacheControl);
            return Task.FromResult(response);
        }
    }
}
