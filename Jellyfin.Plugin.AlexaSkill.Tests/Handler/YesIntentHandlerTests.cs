using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Assertions;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
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
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class YesIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public YesIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userManagerMock
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private YesIntentHandler CreateHandler()
    {
        return new YesIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static IntentRequest CreateYesIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.YesIntent" }
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private Dictionary<string, object> CreateDisambiguationAttrs(
        List<DisambiguationHelper.MatchInfo> matches,
        int index,
        string type)
    {
        return new Dictionary<string, object>
        {
            ["disambig_matches"] = JsonConvert.SerializeObject(matches),
            ["disambig_index"] = index,
            ["disambig_type"] = type
        };
    }

    [Fact]
    public void CanHandle_YesIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "AMAZON.YesIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoSessionAttributes_ReturnsUnexpectedResponse()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithSessionAttributes_NoDisambiguationState_ReturnsUnexpectedResponse()
    {
        var handler = CreateHandler();
        var emptyAttrs = new Dictionary<string, object>();

        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            emptyAttrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_SongType_ReturnsAudioPlay()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(songId))
            .Returns(song);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_VideoType_ReturnsVideoDirective()
    {
        var videoId = Guid.NewGuid();
        var movie = new Movie { Name = "Test Movie", Id = videoId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(videoId))
            .Returns(movie);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = videoId.ToString(), Name = "Test Movie" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "video");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>();
        // VideoApp.Launch must NOT include shouldEndSession
        Assert.Null(response.Response.ShouldEndSession);
        // JF-349: the disambiguation-confirmed video launch now announces the title (was silent).
        Assert.NotNull(response.Response.OutputSpeech);
        string announceText = response.Response.OutputSpeech is SsmlOutputSpeech ss
            ? ss.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("Test Movie", announceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DisambiguationAlbumType_AudioBookItem_DoesNotReturnNoSongsInAlbum()
    {
        // JF-361: PlayBook disambiguation uses MediaTypeAlbum label for audiobooks. When the user
        // confirms, YesIntentHandler routes to PlayAlbum. A single-file AudioBook (no children)
        // must be treated as its own single track, not return "Non ci sono canzoni nell'album".
        var bookId = Guid.NewGuid();
        var book = new AudioBook { Name = "Test Book", Id = bookId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(bookId))
            .Returns(book);
        // Single-file audiobook: no child tracks, item itself is the audio.
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookId.ToString(), Name = "Test Book" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        // Must produce an AudioPlayer.Play (the item is playable audio), not a "NoSongsInAlbum" tell.
        Assert.NotNull(response.Response.Directives);
        Assert.True(response.Response.Directives.Count > 0 && response.Response.Directives[0] is AudioPlayerPlayDirective);
    }

    [Fact]
    public async Task HandleAsync_WithDisambiguationState_AlbumType_ReturnsAudioPlay()
    {
        var albumId = Guid.NewGuid();
        var songId = Guid.NewGuid();
        var album = new MusicAlbum { Name = "Test Album", Id = albumId };
        var song = new Audio { Name = "Track 1", Id = songId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(albumId))
            .Returns(album);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = albumId.ToString(), Name = "Test Album" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_InvalidIndex_ReturnsUnexpectedResponse()
    {
        var songId = Guid.NewGuid();
        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        // Index 5 is out of bounds for a list of 1 item
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 5, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ItemNotFound_ReturnsMediaNotFound()
    {
        var songId = Guid.NewGuid();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(songId))
            .Returns((BaseItem?)null);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Missing Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("could not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SongType_SetsSessionQueue()
    {
        var songId = Guid.NewGuid();
        var song = new Audio { Name = "Test Song", Id = songId };
        var session = CreateSession();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(songId))
            .Returns(song);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "song");

        var handler = CreateHandler();
        await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session,
            attrs,
            CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(songId, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task HandleAsync_NegativeIndex_ReturnsUnexpectedResponse()
    {
        var songId = Guid.NewGuid();
        var matchInfo = new DisambiguationHelper.MatchInfo { Id = songId.ToString(), Name = "Test Song" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, -1, "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_VideoType_DirectiveContainsTitle()
    {
        var videoId = Guid.NewGuid();
        var movie = new Movie { Name = "The Matrix", Id = videoId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(videoId))
            .Returns(movie);

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = videoId.ToString(), Name = "The Matrix" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "video");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var directive = response.HasDirective<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>();
        Assert.NotNull(directive.VideoItem);
        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("The Matrix", directive.VideoItem.Metadata.Title);
    }

    [Fact]
    public async Task HandleAsync_AlbumType_AudioBookItem_UsesMediaTypesForChapters()
    {
        // Regression guard (JF-339 /code-review): PlayBook disambiguation reuses the "album"
        // type for audiobooks, so PlayAlbum receives an AudioBook. It must filter by
        // MediaTypes=Audio (covers AudioBook chapters); IncludeItemTypes=Audio would drop them
        // (chapters are BaseItemKind.AudioBook) and speak NoSongsInAlbum.
        var bookId = Guid.NewGuid();
        var chapter = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var book = new AudioBook { Name = "Test Audiobook", Id = bookId };

        _libraryManagerMock.Setup(lm => lm.GetItemById(bookId)).Returns(book);

        InternalItemsQuery? captured = null;
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q => q.MediaTypes != null)))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { chapter });

        var matchInfo = new DisambiguationHelper.MatchInfo { Id = bookId.ToString(), Name = "Test Audiobook" };
        var attrs = CreateDisambiguationAttrs(new List<DisambiguationHelper.MatchInfo> { matchInfo }, 0, "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.IncludeItemTypes == null || captured.IncludeItemTypes.Length == 0,
            "PlayAlbum must use MediaTypes=Audio for the album/audiobook path, not IncludeItemTypes — AudioBook chapters are BaseItemKind.AudioBook and would be dropped.");
        response.HasDirective<AudioPlayerPlayDirective>();
    }
}
