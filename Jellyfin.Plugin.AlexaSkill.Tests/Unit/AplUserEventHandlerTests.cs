using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class AplUserEventHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManager;
    private readonly Mock<ILibraryManager> _libraryManager;
    private readonly Mock<IUserManager> _userManager;
    private readonly Mock<IUserDataManager> _userDataManager;
    private readonly PluginConfiguration _config;
    private readonly AplUserEventHandler _handler;
    private readonly Entities.User _user;
    private readonly Context _context;

    public AplUserEventHandlerTests()
    {
        _sessionManager = new Mock<ISessionManager>();
        _libraryManager = new Mock<ILibraryManager>();
        _userManager = new Mock<IUserManager>();
        _userDataManager = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(_config, loggerFactory, c => { }, "apl-handler-tests");

        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        var queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        _handler = new AplUserEventHandler(
            _sessionManager.Object,
            _config,
            _libraryManager.Object,
            _userManager.Object,
            _userDataManager.Object,
            queueManager,
            loggerFactory);

        _user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "test-token" };
        _context = new Context();
    }

    private static SessionInfo CreateSession()
    {
        var session = new SessionInfo(Mock.Of<ISessionManager>(), Mock.Of<ILogger<SessionInfo>>());
        session.DeviceId = "test-device";
        return session;
    }

    private static AplUserEventRequest CreateAplEvent(params string[] arguments)
    {
        var args = new JArray();
        foreach (var arg in arguments)
        {
            args.Add(arg);
        }

        return new AplUserEventRequest { Arguments = args };
    }

    // CanHandle tests

    [Fact]
    public void CanHandle_AplUserEventRequest_ReturnsTrue()
    {
        var request = new AplUserEventRequest();
        Assert.True(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_IntentRequest_ReturnsFalse()
    {
        var request = new IntentRequest { Intent = new Intent { Name = "AMAZON.NextIntent" } };
        Assert.False(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_LaunchRequest_ReturnsFalse()
    {
        var request = new LaunchRequest();
        Assert.False(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_PlaybackControllerRequest_ReturnsFalse()
    {
        var request = new PlaybackControllerRequest();
        Assert.False(_handler.CanHandle(request));
    }

    // Pause action tests

    [Fact]
    public async Task HandleAsync_PauseAction_ReturnsAudioPlayerStop()
    {
        var request = CreateAplEvent("pause");
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        var stopDirective = Assert.Single(response.Response.Directives);
        Assert.IsType<StopDirective>(stopDirective);
    }

    // Next action tests

    [Fact]
    public async Task HandleAsync_NextAction_EmptyQueue_ReturnsEmpty()
    {
        var request = CreateAplEvent("next");
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NextAction_WithQueue_ReturnsNextItem()
    {
        var request = CreateAplEvent("next");
        var session = CreateSession();

        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();
        var item1 = new Audio { Name = "Song 1", Id = itemId1 };
        var item2 = new Audio { Name = "Song 2", Id = itemId2 };

        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = itemId1 },
            new() { Id = itemId2 }
        };
        session.FullNowPlayingItem = item1;

        _libraryManager.Setup(l => l.GetItemById(itemId2)).Returns(item2);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.Directives);
        Assert.Equal(item2, session.FullNowPlayingItem);

        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective);
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_NextAction_AtEndOfQueue_ReturnsEmpty()
    {
        var request = CreateAplEvent("next");
        var session = CreateSession();

        var itemId = Guid.NewGuid();
        var item = new Audio { Name = "Last Song", Id = itemId };

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = item;

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    // Previous action tests

    [Fact]
    public async Task HandleAsync_PrevAction_WithQueue_ReturnsPreviousItem()
    {
        var request = CreateAplEvent("prev");
        var session = CreateSession();

        var itemId1 = Guid.NewGuid();
        var itemId2 = Guid.NewGuid();
        var item1 = new Audio { Name = "Song 1", Id = itemId1 };
        var item2 = new Audio { Name = "Song 2", Id = itemId2 };

        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = itemId1 },
            new() { Id = itemId2 }
        };
        session.FullNowPlayingItem = item2;

        _libraryManager.Setup(l => l.GetItemById(itemId1)).Returns(item1);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(item1, session.FullNowPlayingItem);

        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective);
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_PrevAction_AtStartOfQueue_ReturnsEmpty()
    {
        var request = CreateAplEvent("prev");
        var session = CreateSession();

        var itemId = Guid.NewGuid();
        var item = new Audio { Name = "First Song", Id = itemId };

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = item;

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    // SelectItem action tests

    [Fact]
    public async Task HandleAsync_SelectItem_ValidAudioId_PlaysAudio()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Test Song", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(audio, session.FullNowPlayingItem);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(itemId, session.NowPlayingQueue[0].Id);

        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective);
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_ValidMovieId_PlaysVideo()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Test Movie", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(movie);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(movie, session.FullNowPlayingItem);
        Assert.Single(session.NowPlayingQueue);

        var videoDirective = response.Response.Directives.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp"));
        Assert.NotNull(videoDirective);
    }

    [Fact]
    public async Task HandleAsync_PlayTrack_ValidId_PlaysAudio()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("playTrack", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Test Song", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(audio, session.FullNowPlayingItem);

        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective);
        Assert.NotNull(playDirective);
    }

    // CarouselTap action tests

    [Fact]
    public async Task HandleAsync_CarouselTap_ValidAudioId_PlaysAudio()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("carouselTap", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Carousel Song", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(audio, session.FullNowPlayingItem);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(itemId, session.NowPlayingQueue[0].Id);

        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective);
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_CarouselTap_ValidMovieId_PlaysVideo()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("carouselTap", itemId.ToString());
        var session = CreateSession();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Carousel Movie", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(movie);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(movie, session.FullNowPlayingItem);
        Assert.Single(session.NowPlayingQueue);

        var videoDirective = response.Response.Directives.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp"));
        Assert.NotNull(videoDirective);
    }

    [Fact]
    public async Task HandleAsync_CarouselTap_InvalidGuid_ReturnsEmpty()
    {
        var request = CreateAplEvent("carouselTap", "not-a-guid");
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    // Resume offset tests

    [Fact]
    public async Task HandleAsync_SelectItem_WithProgress_ResumesFromSavedPosition()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Audiobook Track", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        _userManager.Setup(u => u.GetUserById(session.UserId)).Returns(jellyfinUser);

        var progressData = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(15).Ticks
        };
        _userDataManager.Setup(x => x.GetUserData(jellyfinUser, audio)).Returns(progressData);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal((int)TimeSpan.FromMinutes(15).TotalMilliseconds, playDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_NoProgress_StartsFromBeginning()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Audiobook Track", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        _userManager.Setup(u => u.GetUserById(session.UserId)).Returns(jellyfinUser);

        _userDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), audio))
            .Returns((UserItemData?)null);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(0, playDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_PlayedItem_StartsFromBeginning()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("carouselTap", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Finished Track", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        _userManager.Setup(u => u.GetUserById(session.UserId)).Returns(jellyfinUser);

        var playedData = new UserItemData
        {
            Key = "test",
            Played = true,
            PlaybackPositionTicks = 0
        };
        _userDataManager.Setup(x => x.GetUserData(jellyfinUser, audio)).Returns(playedData);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
        Assert.Equal(0, playDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    // Edge case tests

    [Fact]
    public async Task HandleAsync_SelectItem_InvalidGuid_ReturnsEmpty()
    {
        var request = CreateAplEvent("selectItem", "not-a-guid");
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_ItemNotFound_ReturnsEmpty()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns((BaseItem?)null);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NullArguments_ReturnsEmpty()
    {
        var request = new AplUserEventRequest { Arguments = null };
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_UnknownAction_ReturnsEmpty()
    {
        var request = CreateAplEvent("unknownAction");
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_EmptyArguments_ReturnsEmpty()
    {
        var request = new AplUserEventRequest { Arguments = new JArray() };
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_MissingItemId_ReturnsEmpty()
    {
        var request = CreateAplEvent("selectItem");
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    // ─── Folder Resolution Tests ────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_CarouselTapOnFolder_ResolvesToFirstChildAudio()
    {
        // Folder with 3 audio children → should play first child
        var folderId = Guid.NewGuid();
        var child1 = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var child2 = new Audio { Name = "Chapter 2", Id = Guid.NewGuid() };
        var child3 = new Audio { Name = "Chapter 3", Id = Guid.NewGuid() };

        var folder = new Folder { Name = "My Audiobook", Id = folderId };

        _libraryManager.Setup(l => l.GetItemById(folderId)).Returns(folder);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { child1, child2, child3 });

        var request = CreateAplEvent("carouselTap", folderId.ToString());
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Should have queued all children
        Assert.Equal(3, session.NowPlayingQueue.Count);
        Assert.Equal(child1.Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(child1, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task HandleAsync_CarouselTapOnFolder_NoAudioChildren_ReturnsErrorMessage()
    {
        // Folder with no audio children → should return error speech
        var folderId = Guid.NewGuid();
        var folder = new Folder { Name = "Empty Folder", Id = folderId };

        _libraryManager.Setup(l => l.GetItemById(folderId)).Returns(folder);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var request = CreateAplEvent("carouselTap", folderId.ToString());
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.Contains("no playable content", ((PlainTextOutputSpeech)response.Response.OutputSpeech).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_CarouselTapOnAudioItem_SkipsFolderResolution()
    {
        // Regular audio item → should NOT trigger folder resolution
        var audioId = Guid.NewGuid();
        var audio = new Audio { Name = "Song", Id = audioId };

        _libraryManager.Setup(l => l.GetItemById(audioId)).Returns(audio);

        var request = CreateAplEvent("carouselTap", audioId.ToString());
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(audioId, session.NowPlayingQueue[0].Id);
        // GetItemList should NOT have been called (no folder resolution)
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_CarouselTapOnFolder_QueueContainsAllChildren()
    {
        // Verify the full queue is built from folder children
        var folderId = Guid.NewGuid();
        var children = new List<BaseItem>();
        for (int i = 0; i < 5; i++)
        {
            children.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        var folder = new Folder { Name = "Album", Id = folderId };

        _libraryManager.Setup(l => l.GetItemById(folderId)).Returns(folder);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(children);

        var request = CreateAplEvent("carouselTap", folderId.ToString());
        var session = CreateSession();

        await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.Equal(5, session.NowPlayingQueue.Count);
        // First in queue is the first child
        Assert.Equal(children[0].Id, session.NowPlayingQueue[0].Id);
        // Last in queue is the last child
        Assert.Equal(children[4].Id, session.NowPlayingQueue[4].Id);
    }

    // ─── APL NowPlaying Directive Tests ────────────────────────────────────

    private static Context CreateAplContext()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    SupportedInterfaces = new Dictionary<string, object?>
                    {
                        { "Alexa.Presentation.APL", null }
                    }
                }
            }
        };
    }

    [Fact]
    public async Task HandleAsync_SelectItem_AplDevice_AudioItem_IncludesNowPlayingDirective()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();
        var aplContext = CreateAplContext();

        var audio = new Audio { Name = "Test Song", Id = itemId };
        audio.Artists = new List<string> { "Test Artist" };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, aplContext, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var directives = response.Response.Directives;

        // Should have AudioPlayer.Play + APL RenderDocument
        Assert.Equal(2, directives.Count);
        Assert.Contains(directives, d => d is AudioPlayerPlayDirective);
        var aplDirective = Assert.Single(directives.OfType<AplRenderDocumentDirective>());
        Assert.Equal("nowPlaying", aplDirective.Token);
    }

    [Fact]
    public async Task HandleAsync_CarouselTap_AplDevice_AudioItem_IncludesNowPlayingDirective()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("carouselTap", itemId.ToString());
        var session = CreateSession();
        var aplContext = CreateAplContext();

        var audio = new Audio { Name = "Carousel Song", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, aplContext, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var aplDirective = response.Response.Directives.OfType<AplRenderDocumentDirective>().FirstOrDefault();
        Assert.NotNull(aplDirective);
        Assert.Equal("nowPlaying", aplDirective.Token);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_NonAplDevice_AudioItem_NoNowPlayingDirective()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Test Song", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, _context, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var aplDirective = response.Response.Directives.OfType<AplRenderDocumentDirective>().FirstOrDefault();
        Assert.Null(aplDirective);
        // Only AudioPlayer directive
        Assert.Single(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_AplDevice_MovieItem_NoNowPlayingDirective()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();
        var aplContext = CreateAplContext();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Test Movie", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(movie);

        var response = await _handler.HandleAsync(request, aplContext, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Movies use VideoApp, no APL NowPlaying
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    [Fact]
    public async Task HandleAsync_SelectItem_AplDevice_FolderItem_IncludesNowPlayingDirective()
    {
        var folderId = Guid.NewGuid();
        var child = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var folder = new Folder { Name = "My Audiobook", Id = folderId };

        _libraryManager.Setup(l => l.GetItemById(folderId)).Returns(folder);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { child });

        var request = CreateAplEvent("carouselTap", folderId.ToString());
        var session = CreateSession();
        var aplContext = CreateAplContext();

        var response = await _handler.HandleAsync(request, aplContext, _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var aplDirective = response.Response.Directives.OfType<AplRenderDocumentDirective>().FirstOrDefault();
        Assert.NotNull(aplDirective);
        Assert.Equal("nowPlaying", aplDirective.Token);
    }
}

/// <summary>
/// Tests for the VideoApp (NativeControlsForAudio) path through AplUserEventHandler.
/// This path was completely untested — the default test config has NativeControlsForAudio=false,
/// so BuildAudioPlayerResponse always returned AudioPlayer.Play, never VideoApp.Launch.
/// </summary>
[Collection("Plugin")]
public class AplUserEventHandlerVideoAppTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManager;
    private readonly Mock<ILibraryManager> _libraryManager;
    private readonly Mock<IUserManager> _userManager;
    private readonly Mock<IUserDataManager> _userDataManager;
    private readonly PluginConfiguration _config;
    private readonly AplUserEventHandler _handler;
    private readonly Entities.User _user;

    public AplUserEventHandlerVideoAppTests()
    {
        _sessionManager = new Mock<ISessionManager>();
        _libraryManager = new Mock<ILibraryManager>();
        _userManager = new Mock<IUserManager>();
        _userDataManager = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true };
        var loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(_config, loggerFactory, c => c.NativeControlsForAudio = true, "apl-videoapp-tests");

        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        var queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        _handler = new AplUserEventHandler(
            _sessionManager.Object,
            _config,
            _libraryManager.Object,
            _userManager.Object,
            _userDataManager.Object,
            queueManager,
            loggerFactory);

        _user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "test-token" };
    }

    private static SessionInfo CreateSession()
    {
        var session = new SessionInfo(Mock.Of<ISessionManager>(), Mock.Of<ILogger<SessionInfo>>());
        session.DeviceId = "test-device";
        return session;
    }

    private static AplUserEventRequest CreateAplEvent(params string[] arguments)
    {
        var args = new JArray();
        foreach (var arg in arguments)
        {
            args.Add(arg);
        }

        return new AplUserEventRequest { Arguments = args };
    }

    [Fact]
    public async Task HandleAsync_SelectItem_AudioItem_NativeControls_ReturnsVideoApp()
    {
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Test Song", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var response = await _handler.HandleAsync(request, new Context(), _user, session, CancellationToken.None);

        Assert.NotNull(response);
        var videoDirective = response.Response.Directives.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp"));
        Assert.NotNull(videoDirective);
        // Should NOT have an AudioPlayer directive — VideoApp replaces it
        Assert.Empty(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        // Should NOT have an APL NowPlaying directive — VideoApp renders its own UI
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
        // VideoApp responses MUST end the session — null/false breaks intent routing
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_FolderItem_NativeControls_ReturnsVideoApp()
    {
        var folderId = Guid.NewGuid();
        var child = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        var folder = new Folder { Name = "My Audiobook", Id = folderId };

        _libraryManager.Setup(l => l.GetItemById(folderId)).Returns(folder);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { child });

        var request = CreateAplEvent("carouselTap", folderId.ToString());
        var session = CreateSession();

        var response = await _handler.HandleAsync(request, new Context(), _user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Folder resolved to audio child → NativeControlsForAudio → VideoApp
        var videoDirective = response.Response.Directives.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp"));
        Assert.NotNull(videoDirective);
        Assert.Empty(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
        // Queue should contain the folder's children
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(child.Id, session.FullNowPlayingItem!.Id);
        // VideoApp responses MUST end the session — null/false breaks intent routing
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_SelectItem_AudioItem_NativeControls_WithOffset_UsesAudioPlayer()
    {
        // When resuming (offset > 0), BuildAudioPlayerResponse stays on AudioPlayer path
        // because VideoApp has no offset support.
        var itemId = Guid.NewGuid();
        var request = CreateAplEvent("selectItem", itemId.ToString());
        var session = CreateSession();

        var audio = new Audio { Name = "Audiobook Track", Id = itemId };
        _libraryManager.Setup(l => l.GetItemById(itemId)).Returns(audio);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        _userManager.Setup(u => u.GetUserById(session.UserId)).Returns(jellyfinUser);

        var progressData = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(15).Ticks
        };
        _userDataManager.Setup(x => x.GetUserData(jellyfinUser, audio)).Returns(progressData);

        var response = await _handler.HandleAsync(request, new Context(), _user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Offset > 0 → stays on AudioPlayer despite NativeControlsForAudio
        Assert.NotNull(response.Response.Directives.FirstOrDefault(d => d is AudioPlayerPlayDirective));
        Assert.Empty(response.Response.Directives.Where(d => d.GetType().Name.Contains("VideoApp")));
    }
}

// AplUserEventRequest and AplUserEventRequestConverter tests
[Collection("Plugin")]
public class AplUserEventRequestConverterTests : PluginTestBase
{
    [Fact]
    public void CanConvert_AplUserEventType_ReturnsTrue()
    {
        var converter = new AplUserEventRequestConverter();
        Assert.True(converter.CanConvert("Alexa.Presentation.APL.UserEvent"));
    }

    [Fact]
    public void CanConvert_OtherType_ReturnsFalse()
    {
        var converter = new AplUserEventRequestConverter();
        Assert.False(converter.CanConvert("IntentRequest"));
        Assert.False(converter.CanConvert("LaunchRequest"));
    }

    [Fact]
    public void Convert_String_ReturnsAplUserEventRequest()
    {
        var converter = new AplUserEventRequestConverter();
        var result = converter.Convert("Alexa.Presentation.APL.UserEvent");
        Assert.IsType<AplUserEventRequest>(result);
    }

    [Fact]
    public void Convert_JObject_ParsesFields()
    {
        var converter = new AplUserEventRequestConverter();
        var data = JObject.Parse(@"{
            ""requestId"": ""test-req-123"",
            ""locale"": ""en-US"",
            ""timestamp"": ""2026-05-12T10:00:00Z"",
            ""token"": ""nowPlaying"",
            ""arguments"": [""next""],
            ""source"": { ""type"": ""TouchWrapper"" },
            ""components"": {}
        }");

        var result = converter.Convert(data);
        var aplRequest = Assert.IsType<AplUserEventRequest>(result);

        Assert.Equal("test-req-123", aplRequest.RequestId);
        Assert.Equal("en-US", aplRequest.Locale);
        Assert.Equal("nowPlaying", aplRequest.Token);
        Assert.NotNull(aplRequest.Arguments);
        Assert.Equal("next", aplRequest.Arguments[0].ToString());
        Assert.NotNull(aplRequest.Source);
    }

    [Fact]
    public void Convert_JObject_SelectItemEvent_ParsesItemId()
    {
        var converter = new AplUserEventRequestConverter();
        var data = JObject.Parse(@"{
            ""requestId"": ""test-req-456"",
            ""locale"": ""en-US"",
            ""arguments"": [""selectItem"", ""abc123""],
            ""source"": { ""type"": ""TouchWrapper"" }
        }");

        var result = converter.Convert(data);
        var aplRequest = Assert.IsType<AplUserEventRequest>(result);

        Assert.NotNull(aplRequest.Arguments);
        Assert.Equal(2, aplRequest.Arguments.Count);
        Assert.Equal("selectItem", aplRequest.Arguments[0].ToString());
        Assert.Equal("abc123", aplRequest.Arguments[1].ToString());
    }
}

[Collection("Plugin")]
public class AplHelperTouchEventTests : PluginTestBase
{
    [Fact]
    public void GetTouchEventArgument_AplUserEvent_ReturnsFirstArgument()
    {
        var request = new AplUserEventRequest
        {
            Arguments = new JArray("next")
        };

        Assert.Equal("next", AplHelper.GetTouchEventArgument(request));
    }

    [Fact]
    public void GetTouchEventArgument_IntentRequest_ReturnsNull()
    {
        var request = new IntentRequest();
        Assert.Null(AplHelper.GetTouchEventArgument(request));
    }

    [Fact]
    public void GetTouchEventArgument_NullArguments_ReturnsNull()
    {
        var request = new AplUserEventRequest { Arguments = null };
        Assert.Null(AplHelper.GetTouchEventArgument(request));
    }

    [Fact]
    public void GetTouchEventArguments_AplUserEvent_ReturnsArray()
    {
        var args = new JArray("selectItem", "item123");
        var request = new AplUserEventRequest { Arguments = args };

        var result = AplHelper.GetTouchEventArguments(request);
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("selectItem", result[0].ToString());
        Assert.Equal("item123", result[1].ToString());
    }

    [Fact]
    public void GetTouchEventArguments_IntentRequest_ReturnsNull()
    {
        var request = new IntentRequest();
        Assert.Null(AplHelper.GetTouchEventArguments(request));
    }
}
