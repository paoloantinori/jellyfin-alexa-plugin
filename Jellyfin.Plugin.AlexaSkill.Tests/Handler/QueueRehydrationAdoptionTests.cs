using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
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
using Newtonsoft.Json.Linq;
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

    private AplUserEventHandler AplHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<IUserDataManager>(),
            _queueManager,
            _loggerFactory);

    private static AplUserEventRequest CreateTap(string action)
        => new() { Arguments = new JArray(action) };

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

    // === AplUserEventHandler next/previous taps (JF-579) ===

    [Fact]
    public async Task AplNext_RestartWipedSession_CoherentDeviceQueue_ServesNextQueuedTrack()
    {
        // The tap on the NowPlaying screen is a customer-initiated request while the
        // skill was most recently playing audio, so context.AudioPlayer carries the
        // playing token (the docs inclusion rule; see the JF-579 adoption comment).
        // The wiped session plays queue member [1] of the persisted 4-track queue;
        // the next tap must serve member [2] from the rehydrated queue instead of
        // the false Empty.
        var songs = SetupQueueSongs(4);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 1);

        var handler = AplHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateTap("next"),
            CreatePlayingContext(songs[1].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            null,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[2].Id.ToString(), play.AudioItem.Stream.Token);

        // The session queue was rehydrated in full (device-queue order) and the
        // served item became the session's now-playing item exactly as on the
        // normal path.
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
        Assert.Equal(songs[2].Id, session.FullNowPlayingItem?.Id);
    }

    [Fact]
    public async Task AplNext_RestartWipedSession_StaleDeviceQueue_KeepsEmptyAnswer()
    {
        // Coherence leg 2: the persisted queue does not contain the item the device
        // is actually playing, so it must never rehydrate and the tap keeps today's
        // Empty answer.
        var songs = SetupQueueSongs(2);
        var staleId = Guid.NewGuid();
        _queueManager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var handler = AplHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateTap("next"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            null,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public async Task AplNext_NonEmptySession_KeepsTodayNextAnswer()
    {
        // A populated session queue belongs to a live playback: the guard's leg 1
        // declines and the tap serves the session queue's successor exactly as
        // before the adoption (no device queue is even present).
        var songs = SetupQueueSongs(3);
        var session = CreateWipedSession();
        session.NowPlayingQueue = songs.Select(s => s.Id).Select(id => new QueueItem { Id = id }).ToList();
        session.FullNowPlayingItem = songs[1];

        SkillResponse response = await AplHandler().HandleAsync(
            CreateTap("next"),
            CreatePlayingContext(songs[1].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            null,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[2].Id.ToString(), play.AudioItem.Stream.Token);
        Assert.Equal(songs[2].Id, session.FullNowPlayingItem?.Id);
    }

    [Fact]
    public async Task AplPrevious_RestartWipedSession_CoherentDeviceQueue_ServesPreviousQueuedTrack()
    {
        // The wiped session plays queue member [2] of the persisted 4-track queue;
        // the previous tap must serve member [1] from the rehydrated queue instead
        // of the false Empty.
        var songs = SetupQueueSongs(4);
        _queueManager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 2);

        var handler = AplHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateTap("prev"),
            CreatePlayingContext(songs[2].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            null,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[1].Id.ToString(), play.AudioItem.Stream.Token);
        Assert.Equal(songs.Select(s => s.Id), session.NowPlayingQueue.Select(q => q.Id));
        Assert.Equal(songs[1].Id, session.FullNowPlayingItem?.Id);
    }

    [Fact]
    public async Task AplPrevious_RestartWipedSession_StaleDeviceQueue_KeepsEmptyAnswer()
    {
        // Coherence leg 2: the persisted queue does not contain the playing item,
        // so it must never rehydrate and the tap keeps today's Empty.
        var songs = SetupQueueSongs(2);
        var staleId = Guid.NewGuid();
        _queueManager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var handler = AplHandler();

        var session = CreateWipedSession();
        SkillResponse response = await handler.HandleAsync(
            CreateTap("prev"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            null,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public async Task AplPrevious_NonEmptySession_KeepsTodayPreviousAnswer()
    {
        // A populated session queue belongs to a live playback: the guard's leg 1
        // declines and the tap serves the session queue's predecessor exactly as
        // before the adoption (no device queue is even present).
        var songs = SetupQueueSongs(3);
        var session = CreateWipedSession();
        session.NowPlayingQueue = songs.Select(s => s.Id).Select(id => new QueueItem { Id = id }).ToList();
        session.FullNowPlayingItem = songs[2];

        SkillResponse response = await AplHandler().HandleAsync(
            CreateTap("prev"),
            CreatePlayingContext(songs[2].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            null,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(songs[1].Id.ToString(), play.AudioItem.Stream.Token);
        Assert.Equal(songs[1].Id, session.FullNowPlayingItem?.Id);
    }

    // === AddToQueue / PlayNext (JF-578, the both-stores queue writer) ===

    /// <summary>
    /// The JF-578 adoption needs the PERSISTED file asserted (the add surviving a
    /// restart is the point), so these tests build their manager over a known
    /// registered temp dir instead of the class fixture's (whose dir is internal
    /// to TestHelpers). Disposal is per test.
    /// </summary>
    private static (DeviceQueueManager Manager, string Dir) CreateManagerWithDir(string suffix)
    {
        string dir = TestHelpers.CreateRegisteredTempDir(suffix);
        return (new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance), dir);
    }

    private AddToQueueIntentHandler AddToQueueHandler(DeviceQueueManager queueManager)
    {
        var userManager = new Mock<IUserManager>();
        userManager.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns(TestHelpers.CreateJellyfinUser());
        return new AddToQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, userManager.Object, _loggerFactory, queueManager: queueManager);
    }

    private PlayNextIntentHandler PlayNextHandler(DeviceQueueManager queueManager)
    {
        var userManager = new Mock<IUserManager>();
        userManager.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns(TestHelpers.CreateJellyfinUser());
        return new PlayNextIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, userManager.Object, _loggerFactory, queueManager: queueManager);
    }

    /// <summary>
    /// The song search must resolve to exactly ONE item so the handlers reach the
    /// queue write without the fuzzy/disambiguation branches.
    /// </summary>
    private void SetupSingleSongResult(MediaBrowser.Controller.Entities.BaseItem song)
        => _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { song });

    private static IntentRequest CreateAddIntent(string name)
        => new()
        {
            Intent = new Intent
            {
                Name = name,
                Slots = new Dictionary<string, Slot>
                {
                    ["song"] = new Slot { Name = "song", Value = "added song" },
                    ["musician"] = new Slot { Name = "musician" }
                }
            },
            Locale = "en-US"
        };

    [Fact]
    public async Task AddToQueue_RestartWipedSession_CoherentDeviceQueue_AddLandsInBothStores()
    {
        // The JF-578 target shape: the restart wiped the session queue while the
        // device plays queue member [1] of the coherent persisted 4-track queue.
        // The add must land in BOTH stores (the device queue is the one that
        // survives the next restart) and the session must mirror the device
        // order; and because the playing token IS a current item on the
        // rehydrated shape, the add lands BEHIND the live stream instead of the
        // pre-adoption ReplaceAll launch of the added song over it.
        var songs = SetupQueueSongs(4);
        var added = TestHelpers.CreateSong("Added Song");
        SetupSingleSongResult(added);
        var (manager, dir) = CreateManagerWithDir("jf578-add");
        manager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 1);

        var session = CreateWipedSession();
        SkillResponse response = await AddToQueueHandler(manager).HandleAsync(
            CreateAddIntent("AddToQueueIntent"),
            CreatePlayingContext(songs[1].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        // The added song was queued, not launched: no play directive, the
        // localized add confirmation speaks its name.
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("Added Song", TestHelpers.GetSpeechText(response));

        // The session queue mirrors the device order with the add at the end.
        Assert.Equal(songs.Select(s => s.Id).Append(added.Id), session.NowPlayingQueue.Select(q => q.Id));

        // The in-memory device store took the insert at the end, pointer intact.
        DeviceQueue deviceQueue = manager.GetQueue(DeviceId)!;
        Assert.Equal(songs.Select(s => s.Id.ToString()).Append(added.Id.ToString()), deviceQueue.ItemIds);
        Assert.Equal(1, deviceQueue.CurrentIndex);

        // The PERSISTED store carries it too: fire the debounced write, then a
        // fresh manager over the same directory (the restart simulation) sees
        // the add as the last queued item.
        manager.FirePersistForTest(DeviceId);
        using var reloaded = new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);
        Assert.Equal(added.Id.ToString(), reloaded.GetQueue(DeviceId)!.ItemIds[^1]);
        manager.Dispose();
    }

    [Fact]
    public async Task AddToQueue_StaleDeviceQueue_PopulatedSession_DoesNotHijackTheLiveQueue()
    {
        // The review BLOCKER shape (JF-578): the persisted device queue is a STALE
        // leftover album queue whose members do NOT include the playing item, while
        // the session carries a live fresh single-song play. The mirror leg must NOT
        // fire (mirroring would park the playing song last behind nine stale items
        // and end the play); the add lands session-only, playing item first.
        var playing = TestHelpers.CreateSong("Playing Song");
        var added = TestHelpers.CreateSong("Added Song");
        var stale = Enumerable.Range(0, 3).Select(_ => TestHelpers.CreateSong("Stale")).ToList();
        SetupSingleSongResult(added);
        var (manager, dir) = CreateManagerWithDir("jf578-stale-add");
        manager.SetQueue(DeviceId, stale.Select(s => s.Id.ToString()).ToList(), currentIndex: 0);

        var session = CreateWipedSession();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = playing.Id } };
        session.FullNowPlayingItem = playing;

        SkillResponse response = await AddToQueueHandler(manager).HandleAsync(
            CreateAddIntent("AddToQueueIntent"),
            CreatePlayingContext(playing.Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        // Session: the live playback stays FIRST, the add behind it, no stale item.
        Assert.Equal(new[] { playing.Id, added.Id }, session.NowPlayingQueue.Select(q => q.Id));
        // The durable write still landed in the device store (the both-stores
        // contract) without reordering the session around it.
        manager.FirePersistForTest(DeviceId);
        using var reloaded = new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);
        DeviceQueue persisted = reloaded.GetQueue(DeviceId)!;
        Assert.Contains(added.Id.ToString(), persisted.ItemIds);
        Assert.DoesNotContain(playing.Id.ToString(), persisted.ItemIds);
        manager.Dispose();
    }

    [Fact]
    public async Task AddToQueue_RestartWipedSession_StaleDeviceQueue_KeepsTodayLaunchBehavior()
    {
        // Coherence leg 2: the persisted queue does not contain the playing
        // item, so the guard declines and the handler keeps today's shape: the
        // one-item session queue plus the start-playback ReplaceAll launch of
        // the added song. The device store still takes the insert (the add
        // lands durably in the queue the user asked to extend), which is the
        // both-stores contract, not a rehydration.
        var songs = SetupQueueSongs(2);
        var added = TestHelpers.CreateSong("Added Song");
        SetupSingleSongResult(added);
        var (manager, _) = CreateManagerWithDir("jf578-add-stale");
        var staleId = Guid.NewGuid();
        manager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var session = CreateWipedSession();
        SkillResponse response = await AddToQueueHandler(manager).HandleAsync(
            CreateAddIntent("AddToQueueIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(added.Id.ToString(), play.AudioItem.Stream.Token);
        Assert.Equal(new[] { added.Id }, session.NowPlayingQueue.Select(q => q.Id));
        Assert.Equal(
            new[] { staleId.ToString(), added.Id.ToString() },
            manager.GetQueue(DeviceId)!.ItemIds);
        manager.Dispose();
    }

    [Fact]
    public async Task AddToQueue_NormalSession_AddLandsInBothStores()
    {
        // The normal (non-wiped) path pins the both-stores widening: a live
        // playback's populated session queue and matching device queue both take
        // the add, so the queue survives a restart that would previously drop
        // it (the session-only writer's loss the JF-578 description names).
        var songs = SetupQueueSongs(2);
        var added = TestHelpers.CreateSong("Added Song");
        SetupSingleSongResult(added);
        var (manager, dir) = CreateManagerWithDir("jf578-add-normal");
        manager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 0);

        var session = CreateWipedSession();
        session.NowPlayingQueue = songs.Select(s => s.Id).Select(id => new QueueItem { Id = id }).ToList();
        session.FullNowPlayingItem = songs[0];

        SkillResponse response = await AddToQueueHandler(manager).HandleAsync(
            CreateAddIntent("AddToQueueIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("Added Song", TestHelpers.GetSpeechText(response));
        Assert.Equal(songs.Select(s => s.Id).Append(added.Id), session.NowPlayingQueue.Select(q => q.Id));

        manager.FirePersistForTest(DeviceId);
        using var reloaded = new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);
        Assert.Equal(added.Id.ToString(), reloaded.GetQueue(DeviceId)!.ItemIds[^1]);
        manager.Dispose();
    }

    [Fact]
    public async Task PlayNext_RestartWipedSession_CoherentDeviceQueue_InsertsAfterCurrentInBothStores()
    {
        // The PlayNext shape through the same writer: the wiped session plays
        // queue member [1]; the insert lands right after it in BOTH stores and
        // the coherent playing token keeps the live stream playing instead of
        // the pre-adoption ReplaceAll launch over it.
        var songs = SetupQueueSongs(4);
        var added = TestHelpers.CreateSong("Added Song");
        SetupSingleSongResult(added);
        var (manager, dir) = CreateManagerWithDir("jf578-next");
        manager.SetQueue(DeviceId, songs.Select(s => s.Id.ToString()).ToList(), currentIndex: 1);

        var session = CreateWipedSession();
        SkillResponse response = await PlayNextHandler(manager).HandleAsync(
            CreateAddIntent("PlayNextIntent"),
            CreatePlayingContext(songs[1].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("Added Song", TestHelpers.GetSpeechText(response));

        // Both stores carry the insert behind the current item; the pointer
        // still names the current item (the insert happened after it).
        Assert.Equal(
            songs.Take(2).Select(s => s.Id).Append(added.Id).Concat(songs.Skip(2).Select(s => s.Id)),
            session.NowPlayingQueue.Select(q => q.Id));
        DeviceQueue deviceQueue = manager.GetQueue(DeviceId)!;
        Assert.Equal(
            songs.Take(2).Select(s => s.Id.ToString()).Append(added.Id.ToString()).Concat(songs.Skip(2).Select(s => s.Id.ToString())),
            deviceQueue.ItemIds);
        Assert.Equal(1, deviceQueue.CurrentIndex);

        manager.FirePersistForTest(DeviceId);
        using var reloaded = new DeviceQueueManager(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger<DeviceQueueManager>.Instance);
        Assert.Equal(added.Id.ToString(), reloaded.GetQueue(DeviceId)!.ItemIds[2]);
        manager.Dispose();
    }

    [Fact]
    public async Task PlayNext_RestartWipedSession_StaleDeviceQueue_KeepsTodayLaunchBehavior()
    {
        // Coherence leg 2: the guard declines, nothing is current, and the
        // handler keeps today's shape: the one-item session queue (the front
        // insert on an empty queue) plus the start-playback launch. The device
        // store takes the front insert per the both-stores contract.
        var songs = SetupQueueSongs(2);
        var added = TestHelpers.CreateSong("Added Song");
        SetupSingleSongResult(added);
        var (manager, _) = CreateManagerWithDir("jf578-next-stale");
        var staleId = Guid.NewGuid();
        manager.SetQueue(DeviceId, new List<string> { staleId.ToString() }, currentIndex: 0);

        var session = CreateWipedSession();
        SkillResponse response = await PlayNextHandler(manager).HandleAsync(
            CreateAddIntent("PlayNextIntent"),
            CreatePlayingContext(songs[0].Id),
            TestHelpers.CreateTestUser(id: _userId),
            session,
            CancellationToken.None);

        AudioPlayerPlayDirective? play = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(play);
        Assert.Equal(added.Id.ToString(), play.AudioItem.Stream.Token);
        Assert.Equal(new[] { added.Id }, session.NowPlayingQueue.Select(q => q.Id));
        Assert.Equal(
            new[] { added.Id.ToString(), staleId.ToString() },
            manager.GetQueue(DeviceId)!.ItemIds);
        manager.Dispose();
    }
}
