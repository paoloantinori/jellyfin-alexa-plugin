using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class QueueIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public QueueIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public void ClearQueue_CanHandle_ReturnsTrue()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void ClearQueue_CanHandle_ReturnsFalseForOtherIntent()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task ClearQueue_WithPlayingItem_KeepsCurrentItem()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();

        var currentItemId = Guid.NewGuid();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentItemId, Name = "Current Song" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentItemId },
            new() { Id = Guid.NewGuid() },
            new() { Id = Guid.NewGuid() }
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(currentItemId, session.NowPlayingQueue[0].Id);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("cleared", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearQueue_WithNoPlayingItem_ClearsAll()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = Guid.NewGuid() },
            new() { Id = Guid.NewGuid() }
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Empty(session.NowPlayingQueue);
    }

    // JF-424.1: clearing the queue must also drop the device's pre-computed next-track
    // entry, which was computed against the queue being cleared; otherwise a later
    // NearlyFinished for the still-playing item could serve it.
    [Fact]
    public async Task ClearQueue_InvalidatesPrecomputeCache()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        var currentItemId = Guid.NewGuid();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentItemId, Name = "Current Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentItemId } };
        string deviceId = "clear-queue-jf4241-" + Guid.NewGuid().ToString("N");
        var cachedNextId = Guid.NewGuid();
        NextTrackPrecomputeCache.Store(
            deviceId, currentItemId.ToString(), cachedNextId,
            new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Cached Next", Id = cachedNextId }, "https://stream/next");

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" }, Locale = "en-US", RequestId = "clear-req" },
            TestHelpers.CreateTestContext(deviceId), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, currentItemId.ToString(), out _, out _, out _));
    }

    [Fact]
    public void ListQueue_CanHandle_ReturnsTrue()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task ListQueue_EmptyQueue_ReturnsEmptyMessage()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem>();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("empty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListQueue_WithUpcomingItems_ListsNames()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);
        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentId, Name = "Current" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentId },
            new() { Id = nextId }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(nextId))
            .Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = nextId, Name = "Next Song" });

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Next Song", text);
    }

    [Fact]
    public void AddToQueue_CanHandle_ReturnsTrue()
    {
        var handler = new AddToQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "AddToQueueIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlayNext_CanHandle_ReturnsTrue()
    {
        var handler = new PlayNextIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "PlayNextIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    // JF-424.1: PlayNext inserts an item right after the currently playing track,
    // displacing the item any pre-computed next-track entry points at; the insertion
    // must drop the entry (the serve-time successor check is the second defense).
    [Fact]
    public async Task PlayNext_Insertion_InvalidatesPrecomputeCache()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
        var currentId = Guid.NewGuid();
        var oldNextId = Guid.NewGuid();
        var insertedSongId = Guid.NewGuid();
        // Jellyfin 10.11: InternalItemsQuery lives in MediaBrowser.Controller.Entities.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>
            {
                new MediaBrowser.Controller.Entities.Audio.Audio { Id = insertedSongId, Name = "Queued Song" }
            });

        var handler = new PlayNextIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        string deviceId = "playnext-jf4241-" + Guid.NewGuid().ToString("N");
        var session = CreateSession();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentId, Name = "Current Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId }, new() { Id = oldNextId } };
        NextTrackPrecomputeCache.Store(
            deviceId, currentId.ToString(), oldNextId,
            new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Old Next", Id = oldNextId }, "https://stream/oldnext");

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayNextIntent",
                Slots = new Dictionary<string, Slot>
                {
                    ["song"] = new Slot { Name = "song", Value = "queued song" },
                    ["musician"] = new Slot { Name = "musician" } // no artist: read via the indexer, may be unfilled
                }
            },
            Locale = "en-US",
            RequestId = "playnext-req"
        };

        await handler.HandleAsync(request, TestHelpers.CreateTestContext(deviceId), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // The insertion happened (queued song now follows the current item)...
        Assert.Equal(insertedSongId, session.NowPlayingQueue[1].Id);

        // ...and the pre-computed entry for the displaced successor is gone.
        Assert.False(NextTrackPrecomputeCache.TryGet(deviceId, currentId.ToString(), out _, out _, out _));
    }
}
