using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayRandomIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayRandomIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private sealed class RecordingPlayRandomHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory)
        : PlayRandomIntentHandler(sessionManager, config, libraryManager, userManager, loggerFactory)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingPlayRandomHandler CreateHandler()
    {
        return new RecordingPlayRandomHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? mediaType = null, string? genre = null)
    {
        var intent = new Intent { Name = IntentNames.PlayRandom };
        intent.Slots = new Dictionary<string, Slot>();

        if (mediaType != null)
        {
            intent.Slots["media_type"] = new Slot { Name = "media_type", Value = mediaType };
        }

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public void CanHandle_PlayRandomIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoItems_ReturnsNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_WithAudioItems_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mediaType: "audio");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audioItem = CreateTestAudio("Test Song", Guid.NewGuid());

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_WithGenreFilter_PassesGenreToQuery()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { CreateTestAudio("Rock Song", Guid.NewGuid()) });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.Genres);
        Assert.Contains("rock", capturedQuery.Genres);
    }

    [Fact]
    public async Task HandleAsync_WithVideoMediaType_FiltersToMoviesAndEpisodes()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mediaType: "video");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.Equal(2, capturedQuery.IncludeItemTypes.Length);
    }

    [Fact]
    public async Task HandleAsync_WithNoMediaType_DefaultsToVideo()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.Equal(2, capturedQuery.IncludeItemTypes.Length);
        Assert.Equal(BaseItemKind.Movie, capturedQuery.IncludeItemTypes[0]);
        Assert.Equal(BaseItemKind.Episode, capturedQuery.IncludeItemTypes[1]);
    }

    [Fact]
    public async Task HandleAsync_WithGenreButNoItems_ReturnsGenreNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "polka");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_VideoNameWithReservedChars_AnnouncesEscapedSsml()
    {
        // JF-323: the now-playing SSML announce must escape reserved chars in the item name,
        // else a name with & < > yields invalid SSML -> InvalidResponse.
        var handler = CreateHandler();
        var request = CreateIntentRequest(); // default mediaType -> video
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Rock & Roll <Live>",
            Id = Guid.NewGuid(),
        };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        // JF-501: the (escaped) SSML announce now rides the progressive-response vehicle
        // verbatim; the final launch response carries the directive ONLY.
        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Rock &amp; Roll &lt;Live&gt;"), "progressive announce must carry the escaped SSML title");
        Assert.False(handler.Progressive.Contains("Rock & Roll <Live>"), "progressive announce must not carry the raw unescaped title");
    }

    /// <summary>
    /// JF-501: with the announce toggle OFF the launch must keep today's silent shape:
    /// no progressive announce (only the SearchingMedia ping may arrive) and no
    /// OutputSpeech on the final response.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AnnounceOff_SendsNoProgressiveAnnounceAndNoOutputSpeech()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(); // default mediaType -> video
        var user = CreateUser();
        user.AnnounceNowPlaying = false;
        var session = CreateSession();
        SetupUserMock();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Rock & Roll <Live>",
            Id = Guid.NewGuid(),
        };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Null(response.Response.OutputSpeech);
        Assert.False(handler.Progressive.Contains("Rock & Roll"), "announce off must not send a progressive announce");
    }

    private static Audio CreateTestAudio(string name, Guid id)
    {
        return new Audio
        {
            Name = name,
            Id = id,
        };
    }
}
