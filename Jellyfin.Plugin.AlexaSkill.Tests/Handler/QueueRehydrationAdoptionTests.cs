using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-577 adoption pins for the shared JF-574 rehydration guard: a restart (DLL
/// hot-swap) or a mid-playback session re-registration wipes the session's
/// in-memory NowPlayingQueue while the Echo keeps playing a stream enqueued from
/// the PERSISTED per-device queue. Each former false-empty consumer (Next,
/// Previous, ListQueue) must answer from the rehydrated queue instead of the
/// wiped one; the stale-queue coherence leg (the persisted queue does NOT
/// contain the playing token) must keep today's empty answer everywhere.
/// </summary>
[Collection("Plugin")]
public class QueueRehydrationAdoptionTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "jf577-device";
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;
    private readonly Guid _userId = Guid.NewGuid();

    public QueueRehydrationAdoptionTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _queueManager = TestHelpers.CreateDeviceQueueManager(
            "jf577-adoption", _loggerFactory.CreateLogger<DeviceQueueManager>());
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "jf577-adoption");
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        _loggerFactory.Dispose();
    }

    // === shared fixtures ===

    private List<BaseItem> SetupQueueSongs(int count)
    {
        var songs = Enumerable.Range(0, count)
            .Select(i => (BaseItem)TestHelpers.CreateSong("Queue Song " + i))
            .ToList();
        var byId = songs.ToDictionary(s => s.Id);
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => byId.TryGetValue(id, out BaseItem? item) ? item : null);
        return songs;
    }

    private static IntentRequest CreateIntent(string name)
        => new() { Intent = new Intent { Name = name }, Locale = "en-US" };

    private SessionInfo CreateWipedSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.NowPlayingQueue = new List<QueueItem>();
        session.FullNowPlayingItem = null;
        return session;
    }

    private Context CreatePlayingContext(Guid tokenId)
        => TestHelpers.CreateContextWithToken(tokenId.ToString(), DeviceId, "PLAYING");

    private NextIntentHandler NextHandler()
        => new(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory, _queueManager);

    private PreviousIntentHandler PreviousHandler()
        => new(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory, _queueManager);

    private ListQueueIntentHandler ListQueueHandler()
        => new(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory, _queueManager);

    // === NextIntentHandler ===

    [Fact]
    public async Task Next_RestartWipedSession_CoherentDeviceQueue_ServesNextQueuedTrack()
    {
        // The incident shape: the restart wiped the session queue (and its
        // now-playing item) while the device plays queue member [1] of the
        // persisted 4-track queue. Next must serve member [2] from the
        // rehydrated queue instead of the false "no more tracks" Empty.
        var songs = SetupQueueSongs(4);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 1);

        var handler = NextHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            CreatePlayingContext(songs[1].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[2].Id.ToString(), play.AudioItem.Stream.Token);

        // The session queue was rehydrated in full (device-queue order) so the
        // NEXT advance resolves from it, and the served item became the
        // session's now-playing item exactly as on the normal path.
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
        Assert.Equal(songs[2].Id, session.FullNowPlayingItem?.Id);
    }

    [Fact]
    public async Task Next_AfterAnotherConsumerRehydrated_ServesNextQueuedTrack()
    {
        // Second-adopter window (review finding, JF-577): ListQueue rehydrated the
        // queue first (it never sets a now-playing item), so Next's own guard
        // declines on leg 1 while the now-playing item is STILL null. The token
        // being a MEMBER of the now-populated session queue proves the same
        // coherence the guard validates, so it stands in and Next serves the real
        // successor instead of the false "no more tracks" Empty.
        var songs = SetupQueueSongs(4);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 1);

        var session = CreateWipedSession();
        Context context = CreatePlayingContext(songs[1].Id);
        Entities.User user = TestHelpers.CreateTestUser(id: _userId);

        _ = await ListQueueHandler().HandleAsync(CreateIntent("ListQueueIntent"), context, user, session, CancellationToken.None);
        Assert.NotEmpty(session.NowPlayingQueue);
        Assert.Null(session.FullNowPlayingItem);

        SkillResponse response = await NextHandler().HandleAsync(
            CreateIntent("AMAZON.NextIntent"), context, user, session, CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[2].Id.ToString(), play.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task Next_RestartWipedSession_StaleDeviceQueue_KeepsEmptyAnswer()
    {
        // Coherence leg 2: the persisted queue does not contain the item the
        // device is actually playing (an older playback's queue), so it must
        // never rehydrate and Next keeps today's Empty answer.
        var songs = SetupQueueSongs(2);
        var staleId = Guid.NewGuid();
        _queueManager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var handler = NextHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public async Task Next_RestartWipedSession_PlayingLastItem_KeepsEmptyAnswer()
    {
        // Rehydration must not manufacture a successor: the playing token is the
        // LAST queue member, so the scan finds nothing after it and the honest
        // answer stays Empty (the queue itself is still rehydrated).
        var songs = SetupQueueSongs(3);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 2);

        var handler = NextHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("AMAZON.NextIntent"),
            CreatePlayingContext(songs[2].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
    }

    // === PreviousIntentHandler ===

    [Fact]
    public async Task Previous_RestartWipedSession_CoherentDeviceQueue_ServesPreviousQueuedTrack()
    {
        // The wiped session plays queue member [2] of the persisted 4-track
        // queue; Previous must serve member [1] from the rehydrated queue
        // instead of the false "no more tracks" Empty.
        var songs = SetupQueueSongs(4);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 2);

        var handler = PreviousHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("AMAZON.PreviousIntent"),
            CreatePlayingContext(songs[2].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[1].Id.ToString(), play.AudioItem.Stream.Token);
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
        Assert.Equal(songs[1].Id, session.FullNowPlayingItem?.Id);
    }

    [Fact]
    public async Task Previous_RestartWipedSession_StaleDeviceQueue_KeepsEmptyAnswer()
    {
        // Coherence leg 2: the persisted queue does not contain the playing
        // item, so it must never rehydrate and Previous keeps today's Empty.
        var songs = SetupQueueSongs(2);
        var staleId = Guid.NewGuid();
        _queueManager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var handler = PreviousHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("AMAZON.PreviousIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public async Task Previous_RestartWipedSession_PlayingFirstItem_KeepsEmptyAnswer()
    {
        // Rehydration must not manufacture a predecessor: the playing token is
        // the FIRST queue member, so the honest answer stays Empty (the queue
        // itself is still rehydrated).
        var songs = SetupQueueSongs(3);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 0);

        var handler = PreviousHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("AMAZON.PreviousIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
    }

    // === ListQueueIntentHandler ===

    [Fact]
    public async Task ListQueue_RestartWipedSession_CoherentDeviceQueue_ListsRehydratedQueue()
    {
        // The wiped session queue made ListQueue speak the empty line while the
        // coherent device queue survives; after adoption it lists the
        // rehydrated queue's names. With no now-playing item on the wiped
        // session the list starts at the queue head, exactly the handler's
        // existing behavior for a populated queue without a now-playing item.
        var songs = SetupQueueSongs(3);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 0);

        var handler = ListQueueHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("ListQueueIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        string text = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("empty", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Queue Song 1", text);
        Assert.Contains("Queue Song 2", text);
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
    }

    [Fact]
    public async Task ListQueue_RestartWipedSession_StaleDeviceQueue_KeepsEmptyAnswer()
    {
        // Coherence leg 2: the persisted queue does not contain the playing
        // item, so it must never rehydrate and ListQueue keeps the empty line.
        var songs = SetupQueueSongs(2);
        var staleId = Guid.NewGuid();
        _queueManager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var handler = ListQueueHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent("ListQueueIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        string text = TestHelpers.GetSpeechText(response);
        Assert.Contains("empty", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.NowPlayingQueue);
    }
}
