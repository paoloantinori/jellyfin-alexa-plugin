using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// A minimal concrete handler used to test the <see cref="BaseHandler.BuildAudioPlayerResponse"/> method directly.
/// </summary>
internal class CoverArtTestHandler : BaseHandler
{
    public CoverArtTestHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => true;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(ResponseBuilder.Empty());
}

public class CoverArtTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public CoverArtTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private CoverArtTestHandler CreateHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private static Entities.User CreateUser(Guid? id = null, string token = "test-token")
        => TestHelpers.CreateTestUser(id, jellyfinToken: token);

    private static Audio CreateSong(string name = "Test Song", Guid? id = null)
        => new() { Name = name, Id = id ?? Guid.NewGuid() };

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_IncludesCoverArt()
    {
        var handler = CreateHandler();
        var song = CreateSong("My Song");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.NotNull(directive.AudioItem.Metadata);
        Assert.Equal("My Song", directive.AudioItem.Metadata.Title);

        Assert.NotNull(directive.AudioItem.Metadata.Art);
        Assert.NotEmpty(directive.AudioItem.Metadata.Art.Sources);
        Assert.NotNull(directive.AudioItem.Metadata.BackgroundImage);
        Assert.NotEmpty(directive.AudioItem.Metadata.BackgroundImage.Sources);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_CorrectImageUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong(id: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var user = CreateUser(token: "my-api-key");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        string expectedImageUrl = "http://localhost:8096/Items/11111111-1111-1111-1111-111111111111/Images/Primary?api_key=my-api-key";
        Assert.Equal(expectedImageUrl, directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.Equal(expectedImageUrl, directive.AudioItem.Metadata.BackgroundImage.Sources[0].Url);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_SetsStreamUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser(token: "stream-token");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(streamUrl, directive.AudioItem.Stream.Url);
        Assert.Equal(itemId, directive.AudioItem.Stream.Token);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithOffset_IncludesOffset()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user, offsetInMilliseconds: 30000);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(30000, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithZeroOffset_DefaultsToZero()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NullItem_EmptyMetadataUrls()
    {
        var handler = CreateHandler();
        var user = CreateUser(token: "null-test-token");
        string itemId = Guid.NewGuid().ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, null!, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.NotNull(directive.AudioItem.Metadata);
        Assert.Equal(string.Empty, directive.AudioItem.Metadata.Title);
        Assert.Equal(string.Empty, directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.Equal(string.Empty, directive.AudioItem.Metadata.BackgroundImage.Sources[0].Url);
    }

    [Fact]
    public void BuildAudioPlayerResponse_UsesReplaceAllPlayBehavior()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);
    }

    [Fact]
    public void BuildAudioPlayerResponse_UsesEnqueuePlayBehavior()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.Enqueue, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
    }

    [Fact]
    public void BuildAudioPlayerResponse_EndsSession()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void BuildAudioPlayerResponse_VersionIsSet()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.Equal("1.0", response.Version);
    }

    [Fact]
    public void BuildAudioPlayerResponse_SingleDirective()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
    }

    [Fact]
    public void BuildAudioPlayerResponse_ArtAndBackgroundImageUseSameUrl()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(
            directive.AudioItem.Metadata.Art.Sources[0].Url,
            directive.AudioItem.Metadata.BackgroundImage.Sources[0].Url);
    }

    [Fact]
    public void BuildAudioPlayerResponse_ImageUrlContainsItemId()
    {
        var handler = CreateHandler();
        var songId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var song = CreateSong("Song", songId);
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Contains(songId.ToString(), directive.AudioItem.Metadata.Art.Sources[0].Url);
        Assert.Contains("Images/Primary", directive.AudioItem.Metadata.Art.Sources[0].Url);
    }

    [Fact]
    public void BuildAudioPlayerResponse_ImageUrlContainsApiKey()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser(token: "secret-key-123");
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Contains("api_key=secret-key-123", directive.AudioItem.Metadata.Art.Sources[0].Url);
    }

    [Fact]
    public void BuildAudioPlayerResponse_WithItem_NameWithSpecialCharacters()
    {
        var handler = CreateHandler();
        var song = CreateSong("Rock & Roll - Live (Remastered)");
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal("Rock & Roll - Live (Remastered)", directive.AudioItem.Metadata.Title);
    }

    [Fact]
    public void BuildAudioPlayerResponse_UsesReplaceEnqueuedPlayBehavior()
    {
        var handler = CreateHandler();
        var song = CreateSong();
        var user = CreateUser();
        string itemId = song.Id.ToString();
        string streamUrl = handler.GetStreamUrl(itemId, user);

        var response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceEnqueued, streamUrl, itemId, song, user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives));

        Assert.Equal(PlayBehavior.ReplaceEnqueued, directive.PlayBehavior);
    }
}
