using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class MediaInfoIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public MediaInfoIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private MediaInfoIntentHandler CreateHandler()
    {
        return new MediaInfoIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    private static IntentRequest CreateMediaInfoRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "MediaInfoIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static string GetSpeechText(SkillResponse response)
    {
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        return speech.Text;
    }

    [Theory]
    [InlineData("MediaInfoIntent", true)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_NoMediaPlaying_ReturnsNothingPlaying()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains("Nothing is currently playing", GetSpeechText(response));
    }

    [Fact]
    public async Task Handle_AudioItem_ReportsTrackArtistAndAlbum()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Queen",
            Album = "A Night at the Opera"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Bohemian Rhapsody", text);
        Assert.Contains("Queen", text);
        Assert.Contains("A Night at the Opera", text);
    }

    [Fact]
    public async Task Handle_AudioItem_NoAlbum_ReportsTrackAndArtist()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Yesterday",
            Type = BaseItemKind.Audio,
            AlbumArtist = "The Beatles"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Yesterday", text);
        Assert.Contains("The Beatles", text);
    }

    [Fact]
    public async Task Handle_AudioItem_NoArtistOrAlbum_ReportsTrackOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Mystery Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Mystery Track", text);
    }

    [Fact]
    public async Task Handle_EpisodeItem_ReportsSeriesSeasonEpisode()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Pilot",
            Type = BaseItemKind.Episode,
            SeriesName = "Breaking Bad",
            ParentIndexNumber = 1,
            IndexNumber = 1
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Breaking Bad", text);
        Assert.Contains("season 1", text);
        Assert.Contains("episode 1", text);
        Assert.Contains("Pilot", text);
    }

    [Fact]
    public async Task Handle_EpisodeItem_NoSeriesName_ReportsEpisodeOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Unknown Episode",
            Type = BaseItemKind.Episode
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Unknown Episode", text);
    }

    [Fact]
    public async Task Handle_MovieItem_ReportsTitleAndYear()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "The Matrix",
            Type = BaseItemKind.Movie,
            ProductionYear = 1999
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("The Matrix", text);
        Assert.Contains("1999", text);
    }

    [Fact]
    public async Task Handle_MovieItem_NoYear_ReportsTitleOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Old Movie",
            Type = BaseItemKind.Movie
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Old Movie", text);
        Assert.DoesNotContain("(", text);
    }

    [Fact]
    public async Task Handle_UnknownType_ReportsName()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Some Media",
            Type = BaseItemKind.Photo
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Some Media", text);
    }

    [Fact]
    public async Task Handle_WithPositionAndRuntime_ReportsPosition()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(2).Ticks
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("2 minutes", text);
        Assert.Contains("4 minutes", text);
    }

    [Fact]
    public async Task Handle_WithPositionNoRuntime_ReportsPositionOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(1).Ticks
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("1 minutes", text);
        Assert.DoesNotContain("of", text);
    }

    [Fact]
    public async Task Handle_NullPlayState_NoPositionReported()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist"
        };
        session.PlayState = null;

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.DoesNotContain("Position", text);
    }
}
