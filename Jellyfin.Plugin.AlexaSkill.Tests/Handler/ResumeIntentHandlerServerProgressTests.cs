using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Alexa.NET.Assertions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for ResumeIntentHandler fallback tier 4: Jellyfin server-side progress
/// when no AudioPlayer token, no session FullNowPlayingItem, and no DeviceQueue state exists.
/// </summary>
public class ResumeIntentHandlerServerProgressTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly JellyfinUser _jellyfinUser;
    private readonly Guid _sessionUserId;

    public ResumeIntentHandlerServerProgressTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _jellyfinUser = new JellyfinUser("testuser", "test", "test");
        _sessionUserId = Guid.NewGuid();

        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c => { },
            "resume-server-progress-tests");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private ResumeIntentHandler CreateHandler()
    {
        return new ResumeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object);
    }

    private static IntentRequest CreateResumeRequest()
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.ResumeIntent" }
        };
    }

    private static Context CreateContextNoAudioPlayer()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new global::Alexa.NET.Request.Device { DeviceID = "test-device" },
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                ApiAccessToken = "test-token",
                ApiEndpoint = "https://api.amazonalexa.com"
            },
            AudioPlayer = new PlaybackState
            {
                PlayerActivity = "IDLE"
            }
        };
    }

    private SessionInfo CreateEmptySession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.UserId = _sessionUserId;
        // FullNowPlayingItem is null by default -- triggers server-side fallback
        _userManagerMock.Setup(x => x.GetUserById(_sessionUserId)).Returns(_jellyfinUser);
        return session;
    }

    [Fact]
    public async Task ServerProgressFallback_AudioBook_ReturnsAudioWithOffset()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Server has an audiobook chapter with progress at 5 minutes
        var audioItem = new Audio
        {
            Name = "Chapter 7",
            Id = Guid.NewGuid(),
            Path = "/audiobooks/book/chapter7.mp3"
        };

        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        long progressTicks = TimeSpan.FromMinutes(5).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);

        // Should return AudioPlayer directive with correct offset
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);

        int expectedOffsetMs = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
        Assert.Equal(expectedOffsetMs, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task ServerProgressFallback_Movie_ReturnsVideoAppResponse()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Server has a movie with progress at 30 minutes
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Test Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/test.mkv"
        };

        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        long progressTicks = TimeSpan.FromMinutes(30).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);

        // Should contain a video directive with Videos/ URL
        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);

        // Output speech should contain movie name and position information
        Assert.NotNull(response.Response.OutputSpeech);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("Test Movie", speech.Text);
    }

    [Fact]
    public async Task ServerProgressFallback_NoProgress_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // No items with progress on the server
        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_AnnouncesPosition_WhenEnabled()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = true
        });

        var session = CreateEmptySession();

        // Server has an audio item with progress at 2 minutes 30 seconds
        var audioItem = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        long progressTicks = TimeSpan.FromMinutes(2).Ticks + TimeSpan.FromSeconds(30).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        // Output speech should contain position announcement (from ResumingAtPosition string)
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("resuming", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_SkipsPlayedItems()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // First item is fully played (Played=true), second item has progress
        var playedItem = new Audio
        {
            Name = "Played Song",
            Id = Guid.NewGuid(),
            Path = "/music/played.mp3"
        };

        var inProgressItem = new Audio
        {
            Name = "In Progress Song",
            Id = Guid.NewGuid(),
            Path = "/music/inprogress.mp3"
        };

        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { playedItem, inProgressItem });

        var playedUserData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(3).Ticks,
            Played = true
        };

        var inProgressUserData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks,
            Played = false
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), playedItem))
            .Returns(playedUserData);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), inProgressItem))
            .Returns(inProgressUserData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Should play the in-progress item, not the played one
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Contains(inProgressItem.Id.ToString(), audioDirective.AudioItem.Stream.Url);
        Assert.Equal((int)TimeSpan.FromMinutes(1).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task ServerProgressFallback_SkipsZeroProgressItems()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Item with zero progress should be skipped
        var zeroProgressItem = new Audio
        {
            Name = "Zero Progress Song",
            Id = Guid.NewGuid(),
            Path = "/music/zero.mp3"
        };

        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { zeroProgressItem });

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = 0,
            Played = false
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_NullSession_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();
        var user = TestHelpers.CreateTestUser();

        // null session should return NoMediaPlaying (handler checks session == null)
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_Episode_ReturnsVideoAppResponse()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Server has a TV episode with progress
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Test Episode",
            Id = Guid.NewGuid(),
            Path = "/tv/show/s01e01.mkv"
        };

        _libraryManagerMock.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });

        long progressTicks = TimeSpan.FromMinutes(15).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Episodes should produce a response with shouldEndSession=true for video
        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);
    }
}
