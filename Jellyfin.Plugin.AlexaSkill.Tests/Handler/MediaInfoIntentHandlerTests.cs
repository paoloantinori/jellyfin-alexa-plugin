using System;
using System.Collections.Generic;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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

    private static Context CreateContext()
    {
        return new Context
        {
            System = new Alexa.NET.Request.System
            {
                User = new Alexa.NET.Request.User
                {
                    AccessToken = Guid.NewGuid().ToString()
                },
                Device = new Device { DeviceID = "test-device" }
            }
        };
    }

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
    public void Handle_NoMediaPlaying_ReturnsNothingPlaying()
    {
        var handler = CreateHandler();
        var session = new SessionInfo { NowPlayingItem = null };

        var response = handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session);

        Assert.Contains("Nothing is currently playing", GetSpeechText(response));
    }

    [Fact]
    public void Handle_AudioItem_ReportsTrackArtistAndAlbum()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Bohemian Rhapsody",
                Type = "Audio",
                AlbumArtist = "Queen",
                Album = "A Night at the Opera"
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Bohemian Rhapsody", text);
        Assert.Contains("Queen", text);
        Assert.Contains("A Night at the Opera", text);
    }

    [Fact]
    public void Handle_AudioItem_NoAlbum_ReportsTrackAndArtist()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Yesterday",
                Type = "Audio",
                AlbumArtist = "The Beatles"
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Yesterday", text);
        Assert.Contains("The Beatles", text);
    }

    [Fact]
    public void Handle_AudioItem_NoArtistOrAlbum_ReportsTrackOnly()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Mystery Track",
                Type = "Audio"
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Mystery Track", text);
    }

    [Fact]
    public void Handle_EpisodeItem_ReportsSeriesSeasonEpisode()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Pilot",
                Type = "Episode",
                SeriesName = "Breaking Bad",
                ParentIndexNumber = 1,
                IndexNumber = 1
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Breaking Bad", text);
        Assert.Contains("season 1", text);
        Assert.Contains("episode 1", text);
        Assert.Contains("Pilot", text);
    }

    [Fact]
    public void Handle_EpisodeItem_NoSeriesName_ReportsEpisodeOnly()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Unknown Episode",
                Type = "Episode"
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Unknown Episode", text);
    }

    [Fact]
    public void Handle_MovieItem_ReportsTitleAndYear()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "The Matrix",
                Type = "Movie",
                ProductionYear = 1999
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("The Matrix", text);
        Assert.Contains("1999", text);
    }

    [Fact]
    public void Handle_MovieItem_NoYear_ReportsTitleOnly()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Old Movie",
                Type = "Movie"
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Old Movie", text);
        Assert.DoesNotContain("(", text);
    }

    [Fact]
    public void Handle_UnknownType_ReportsName()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Some Media",
                Type = "Photo"
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("Some Media", text);
    }

    [Fact]
    public void Handle_WithPositionAndRuntime_ReportsPosition()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Test Song",
                Type = "Audio",
                RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
            },
            PlayState = new PlayerStateInfo
            {
                PositionTicks = TimeSpan.FromMinutes(2).Ticks
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("2 minutes", text);
        Assert.Contains("4 minutes", text);
    }

    [Fact]
    public void Handle_WithPositionNoRuntime_ReportsPositionOnly()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Test Song",
                Type = "Audio"
            },
            PlayState = new PlayerStateInfo
            {
                PositionTicks = TimeSpan.FromMinutes(1).Ticks
            }
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.Contains("1 minutes", text);
        Assert.DoesNotContain("of", text);
    }

    [Fact]
    public void Handle_NullPlayState_NoPositionReported()
    {
        var handler = CreateHandler();
        var session = new SessionInfo
        {
            NowPlayingItem = new BaseItemDto
            {
                Name = "Test Song",
                Type = "Audio",
                AlbumArtist = "Artist"
            },
            PlayState = null
        };

        var text = GetSpeechText(handler.Handle(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session));

        Assert.DoesNotContain("Position", text);
    }
}
