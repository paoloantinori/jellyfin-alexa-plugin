using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.LiveTv;

/// <summary>
/// Unit tests for <see cref="LiveTvStreamResolver"/>. The Jellyfin PlaybackInfo
/// endpoint is faked via a configurable <see cref="HttpMessageHandler"/>.
/// </summary>
public class LiveTvStreamResolverTests
{
    private const string ServerAddress = "http://localhost:8096";

    private static LiveTvStreamResolver CreateResolver(FakeHandler handler)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, ServerAddress);
        var logger = LoggerFactory.Create(b => { }).CreateLogger<LiveTvStreamResolver>();
        return new LiveTvStreamResolver(factoryMock.Object, config, logger);
    }

    private static BaseItem CreateChannel()
        => new Movie { Name = "DW English", Id = Guid.Parse("11111111-1111-1111-1111-111111111111") };

    private static Entities.User CreateUser()
        => new() { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), JellyfinToken = "tok" };

    [Fact]
    public async Task ResolveAsync_DirectRemoteSource_ReturnsRemotePath()
    {
        var handler = new FakeHandler
        {
            ResponseBody = @"{""MediaSources"":[{""Id"":""abc"",""LiveStreamId"":null,""Protocol"":""Http"",""SupportsDirectStream"":true,""Path"":""https://amg.example/playlist.m3u8""}]}"
        };
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync(CreateChannel(), CreateUser(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("https://amg.example/playlist.m3u8", result!.Url);
    }

    [Fact]
    public async Task ResolveAsync_NonHttpSource_ReturnsMasterPlaylistWithMediaSourceId()
    {
        var handler = new FakeHandler
        {
            ResponseBody = @"{""MediaSources"":[{""Id"":""ms1"",""LiveStreamId"":""live-xyz"",""Protocol"":""File"",""SupportsDirectStream"":false,""Path"":""/data/tuner/channel.ts""}]}"
        };
        var resolver = CreateResolver(handler);
        var channel = CreateChannel();

        var result = await resolver.ResolveAsync(channel, CreateUser(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains($"Videos/{channel.Id.ToString("N")}/master.m3u8", result.Url);
        Assert.Contains("MediaSourceId=ms1", result.Url);
        Assert.Contains("LiveStreamId=live-xyz", result.Url);
        Assert.Contains("api_key=tok", result.Url);
    }

    [Fact]
    public async Task ResolveAsync_NullLiveStreamId_OmitsLiveStreamParam()
    {
        var handler = new FakeHandler
        {
            ResponseBody = @"{""MediaSources"":[{""Id"":""ms2"",""LiveStreamId"":null,""Protocol"":""File"",""SupportsDirectStream"":false,""Path"":""/local""}]}"
        };
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync(CreateChannel(), CreateUser(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("MediaSourceId=ms2", result!.Url);
        Assert.DoesNotContain("LiveStreamId", result.Url);
    }

    [Fact]
    public async Task ResolveAsync_BuildsPlaybackInfoUrlWithAutoOpenLiveStream()
    {
        var handler = new FakeHandler
        {
            ResponseBody = @"{""MediaSources"":[{""Id"":""abc"",""Protocol"":""Http"",""SupportsDirectStream"":true,""Path"":""https://x.example/p.m3u8""}]}"
        };
        var resolver = CreateResolver(handler);
        var channel = CreateChannel();

        await resolver.ResolveAsync(channel, CreateUser(), CancellationToken.None);

        Assert.NotNull(handler.LastRequest?.RequestUri);
        string url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains($"/Items/{channel.Id.ToString("N")}/PlaybackInfo", url);
        Assert.Contains("IsPlayback=true", url);
        Assert.Contains("AutoOpenLiveStream=true", url);
        Assert.Contains("api_key=tok", url);
    }

    [Fact]
    public async Task ResolveAsync_ServerError_ReturnsNull()
    {
        var handler = new FakeHandler
        {
            StatusCode = HttpStatusCode.InternalServerError,
            ResponseBody = "Error processing request."
        };
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync(CreateChannel(), CreateUser(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_HttpException_ReturnsNull()
    {
        var handler = new FakeHandler { ThrowException = new HttpRequestException("boom") };
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync(CreateChannel(), CreateUser(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_NoMediaSources_ReturnsNull()
    {
        var handler = new FakeHandler { ResponseBody = @"{""MediaSources"":[]}" };
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync(CreateChannel(), CreateUser(), CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(@"{""MediaSources"":null}")]
    [InlineData(@"{""MediaSources"":{""not"":""an array""}}")]
    public async Task ResolveAsync_NonArrayMediaSources_ReturnsNull(string body)
    {
        // Jellyfin serializes null MediaSources when a tuner is offline; the resolver must treat
        // a present-but-non-array value as "no sources" rather than throwing GetArrayLength's
        // InvalidOperationException (which would 500 the skill request).
        var handler = new FakeHandler { ResponseBody = body };
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync(CreateChannel(), CreateUser(), CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// Configurable HTTP handler used to fake Jellyfin's PlaybackInfo responses.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? ResponseBody { get; set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public Exception? ThrowException { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (ThrowException is not null)
            {
                return Task.FromException<HttpResponseMessage>(ThrowException);
            }

            var content = new StringContent(ResponseBody ?? string.Empty, Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = content });
        }
    }
}
