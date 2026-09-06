using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-505 resume-offer half: the LaunchRequest resume offer must never offer a VIDEO
/// item (Movie/Episode) to a screenless device, because the accepted offer can only
/// fail there (device evidence 2026-09-06: a Show-played episode was offered to an
/// Echo Dot). On screenless: fall back to the most recent AUDIO item with progress in
/// the per-user last-played ledger, else no offer (straight to welcome).
/// </summary>
[Collection("Plugin")]
public class ScreenlessResumeOfferTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ScreenlessResumeOfferTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private LaunchRequestHandler CreateLaunchHandler()
        => new LaunchRequestHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    /// <summary>The prior-playback context: an AudioPlayer token names the last item.</summary>
    private static Context CreateContextWithAudioToken(string token, bool screenless)
    {
        var interfaces = new Dictionary<string, object>();
        if (!screenless)
        {
            interfaces["VideoApp"] = new { };
        }

        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = interfaces
                }
            },
            AudioPlayer = new PlaybackState
            {
                Token = token,
                OffsetInMilliseconds = 0
            }
        };
    }

    private static ResumeHelper.ResumeState ReadResumeState(SkillResponse response)
    {
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("resume_state"), "an offer must carry resume_state");
        var state = ResumeHelper.ReadState(response.SessionAttributes);
        Assert.NotNull(state);
        return state!;
    }

    [Fact]
    public async Task Screenless_VideoLastPlayed_FallsBackToAudioOffer()
    {
        var movieId = Guid.NewGuid();
        var audioId = Guid.NewGuid();
        var movie = new Movie { Name = "The Bear Ribs", Id = movieId };
        var audio = new Audio { Name = "Bohemian Rhapsody", Id = audioId };

        _libraryManagerMock.Setup(l => l.GetItemById(movieId)).Returns(movie);
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });
        _userDataManagerMock
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(3).Ticks });

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };

        SkillResponse response = await handler.HandleAsync(
            request, CreateContextWithAudioToken(movieId.ToString(), screenless: true), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        // The offer targets the AUDIO fallback, never the episode the Dot cannot play.
        var state = ReadResumeState(response);
        Assert.Equal(audioId.ToString(), state.ItemId);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Bohemian Rhapsody", speech, StringComparison.Ordinal);
        Assert.DoesNotContain("The Bear Ribs", speech, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Screenless_VideoLastPlayed_NoAudioCandidate_GoesToWelcome()
    {
        var movieId = Guid.NewGuid();
        var movie = new Movie { Name = "The Bear Ribs", Id = movieId };

        _libraryManagerMock.Setup(l => l.GetItemById(movieId)).Returns(movie);
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };

        SkillResponse response = await handler.HandleAsync(
            request, CreateContextWithAudioToken(movieId.ToString(), screenless: true), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        // No offerable audio: straight to welcome, no resume_state.
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Welcome", speech, StringComparison.Ordinal);
        Assert.True(response.SessionAttributes == null || !response.SessionAttributes.ContainsKey("resume_state"),
            "no resume_state may ride on the welcome response");
    }

    [Fact]
    public async Task Screenless_AudioLastPlayed_OffersAudioDirectly()
    {
        var audioId = Guid.NewGuid();
        var audio = new Audio { Name = "Bohemian Rhapsody", Id = audioId };

        _libraryManagerMock.Setup(l => l.GetItemById(audioId)).Returns(audio);

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };

        SkillResponse response = await handler.HandleAsync(
            request, CreateContextWithAudioToken(audioId.ToString(), screenless: true), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        // An audio item needs no substitution: offered as-is, with no ledger query.
        var state = ReadResumeState(response);
        Assert.Equal(audioId.ToString(), state.ItemId);
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task ScreenCapable_VideoLastPlayed_OffersVideo()
    {
        var movieId = Guid.NewGuid();
        var movie = new Movie { Name = "The Bear Ribs", Id = movieId };

        _libraryManagerMock.Setup(l => l.GetItemById(movieId)).Returns(movie);

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };

        SkillResponse response = await handler.HandleAsync(
            request, CreateContextWithAudioToken(movieId.ToString(), screenless: false), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        // A screen-capable device keeps the video offer: the gate must not over-reach.
        var state = ReadResumeState(response);
        Assert.Equal(movieId.ToString(), state.ItemId);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("The Bear Ribs", speech, StringComparison.Ordinal);
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }
}
