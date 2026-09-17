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
using Jellyfin.Plugin.AlexaSkill.Alexa;
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
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-574 crash-recovery rehydration: a restart (DLL hot-swap) or a mid-playback
/// session re-registration wipes the session's in-memory NowPlayingQueue while the
/// Echo keeps playing a stream enqueued from the PERSISTED per-device queue. The
/// PlaybackNearlyFinished resolver must rehydrate the session queue from the
/// device queue when the evidence is coherent (empty session queue + the playing
/// token is a member of the persisted queue) instead of declaring false
/// queue-exhaustion, which lets PostPlay AutoPlay replace the artist queue with
/// radio tracks (live incident 2026-09-16 07:28: Norah Jones queue of 5, 4
/// remaining tracks in the persisted file, radio pool of 15 played instead).
/// </summary>
[Collection("Plugin")]
public class PlaybackRestartRehydrationTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PlaybackNearlyFinishedEventHandler _handler;
    private readonly DeviceQueueManager _queueManager;
    private readonly Guid _userId = Guid.NewGuid();

    public PlaybackRestartRehydrationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _queueManager = TestHelpers.CreateDeviceQueueManager("jf574-rehydration", _loggerFactory.CreateLogger<DeviceQueueManager>());
        _handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory, _queueManager);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "jf574-rehydration");
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        _loggerFactory.Dispose();
    }

    [Fact]
    public async Task RestartWipedSessionQueue_CoherentDeviceQueue_ServesNextQueuedTrackNotRadio()
    {
        // The incident shape: PostPlay AutoPlay ON (the user's setting), the session
        // queue wiped by the restart (nothing playing reported since), and the
        // persisted device queue still holding the 5-track artist queue with the
        // playing track as itemIds[0]. The resolver must serve itemIds[1], NOT let
        // AutoPlay replace the queue with radio tracks.
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;

        var tracks = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        var radioTrackId = Guid.NewGuid();
        SetupLibraryForQueueAndRadio(tracks, radioTrackId);

        // The pre-restart play path mirrored its queue to the device store; the
        // restart wiped ONLY the session side.
        _queueManager.SetQueue(DeviceId, tracks.Select(g => g.ToString()).ToList(), currentIndex: 0);

        var (request, context, user, session) = CreateNearlyFinishedContext(tracks[0].ToString());
        session.NowPlayingQueue = new List<QueueItem>();
        session.FullNowPlayingItem = null;

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The gapless enqueue is the queue's own next item...
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(tracks[1].ToString(), playDirective.AudioItem.Stream.Token);

        // ...and PostPlay radio did NOT fire over the surviving queue.
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));

        // The session queue is rehydrated in full so the NEXT advance resolves
        // from it (in the device queue's stored order).
        Assert.Equal(tracks, session.NowPlayingQueue.Select(q => q.Id).ToList());
    }

    [Fact]
    public async Task DeviceQueueNotContainingPlayingToken_StaleQueueDoesNotHijack()
    {
        // Coherence guard: the persisted queue holds an OLDER playback's items and
        // does not contain the item the device is actually playing. Rehydrating
        // from it would hijack the current playback, so the session queue stays
        // empty and today's exhaustion behavior (PostPlay AutoPlay) runs unchanged.
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;

        var currentId = Guid.NewGuid();
        var staleTrack = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        SetupLibraryForQueueAndRadio(new List<Guid> { staleTrack }, radioTrackId);

        _queueManager.SetQueue(DeviceId, new List<string> { staleTrack.ToString() }, currentIndex: 0);

        var (request, context, user, session) = CreateNearlyFinishedContext(currentId.ToString());
        session.NowPlayingQueue = new List<QueueItem>();
        session.FullNowPlayingItem = null;

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // No rehydration: the stale queue's tracks never reach the session, and
        // the exhaustion path (AutoPlay radio) proceeds exactly as before.
        Assert.DoesNotContain(staleTrack, session.NowPlayingQueue.Select(q => q.Id));
        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(radioTrackId.ToString(), playDirective.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task NonEmptySessionQueue_IsNeverRehydratedFromDeviceQueue()
    {
        // Anti-hijack for the post-restart NEW playback: a fresh single-song play
        // sets its own one-item session queue while the device store still holds an
        // older queue that happens to contain the same song. The non-empty session
        // queue is the live playback's truth; the resolver must exhaust it (and run
        // PostPlay) rather than extending the play with the old queue's tracks.
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;

        var currentId = Guid.NewGuid();
        var olderQueueTrack = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        SetupLibraryForQueueAndRadio(new List<Guid> { currentId, olderQueueTrack }, radioTrackId);

        _queueManager.SetQueue(
            DeviceId, new List<string> { currentId.ToString(), olderQueueTrack.ToString() }, currentIndex: 0);

        var (request, context, user, session) = CreateNearlyFinishedContext(currentId.ToString());
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };
        session.FullNowPlayingItem = TestHelpers.CreateSong(id: currentId);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The old queue's continuation never played: AutoPlay radio answered.
        Assert.DoesNotContain(olderQueueTrack, session.NowPlayingQueue.Select(q => q.Id));
        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(radioTrackId.ToString(), playDirective.AudioItem.Stream.Token);
    }

    /// <summary>
    /// Library setup shared by the three scenarios: every queue member and the
    /// radio candidate resolve via GetItemById, and the radio/PostPlay genre query
    /// (GetItemList) answers the radio candidate so a FALSE exhaustion is visible
    /// as a radio enqueue rather than a silent empty response.
    /// </summary>
    private void SetupLibraryForQueueAndRadio(IReadOnlyList<Guid> queueTrackIds, Guid radioTrackId)
    {
        var byId = new Dictionary<Guid, BaseItem>();
        foreach (Guid id in queueTrackIds)
        {
            byId[id] = TestHelpers.CreateSong(genres: new[] { "Jazz" }, id: id);
        }

        var radioTrack = TestHelpers.CreateSong(id: radioTrackId);
        byId[radioTrackId] = radioTrack;

        _libraryManagerMock.Setup(lm => lm.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => byId.TryGetValue(id, out BaseItem? item)
                ? item
                : TestHelpers.CreateSong(genres: new[] { "Jazz" }, id: id));

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        _userManagerMock.Setup(um => um.GetUserById(_userId))
            .Returns(TestHelpers.CreateJellyfinUser(authProviderId: "test", passwordProviderId: "test", id: _userId));
    }

    private (Request request, Context context, Entities.User user, SessionInfo session)
        CreateNearlyFinishedContext(string tokenId)
    {
        var request = AlexaRequestFactory.CreateAudioPlayerRequest(
            "AudioPlayer.PlaybackNearlyFinished", tokenId, 0);

        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, tokenId, 0, "PLAYING");

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>())
        {
            UserId = _userId,
        };

        return (request, context, user, session);
    }

}
