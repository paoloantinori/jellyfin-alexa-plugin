#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-712 pins (derive-then-commit in PlaybackNearlyFinishedEventHandler): a
/// refused continuation leaves the device queue pointer naming the FINISHING item
/// with its position intact (CurrentIndex unmoved, no enqueue record, session queue
/// un-appended, radio mode not armed), and a successful continuation still advances
/// the pointer exactly once. The refusal is driven the way production reaches it
/// here: the JF-636 rate-continuity advance launches the successor through the
/// token-gated atempo endpoint (the launch-scope store seeded at 1500/1000), and an
/// empty <see cref="PluginConfiguration.StreamTokenSecret"/> makes
/// BuildAudioPlayerResponse throw <see cref="StreamTokenNotConfiguredException"/>
/// before any of its own records. RequestPipeline owns the translated response
/// (pinned in PipelineTests); these pins assert the STATE the handler leaves.
/// RED PROOFS: reverting the reorder at an arm (moving its write back above the
/// builder call) flips that arm's pin to a written-state assertion failure (the
/// pointer/append/radio arming lands), and deleting the commit call flips the
/// success-parity pin.
/// </summary>
[Collection("Plugin")]
public class PlaybackNearlyFinishedRefusalTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly DeviceQueueManager _queueManager;

    private const string DeviceId = "jf712-device";

    public PlaybackNearlyFinishedRefusalTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _queueManager = TestHelpers.CreateDeviceQueueManager("jf712-nearly-finished-refusal");

        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
        NextTrackPrecomputeCache.Invalidate(DeviceId);
    }

    public void Dispose()
    {
        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
        NextTrackPrecomputeCache.Invalidate(DeviceId);
        _queueManager.Dispose();
        _loggerFactory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The empty-secret config every refusal pin runs under: a server address (the
    /// URL builders need one) plus the JF-636 launch-scope rate seeded on the
    /// finishing item, which routes the advance's launch through the token-gated
    /// atempo endpoint (rate 1500/1000, the podcast-at-1.5x shape).
    /// </summary>
    private static PluginConfiguration RefusalConfig()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        config.StreamTokenSecret = string.Empty;
        return config;
    }

    private PlaybackNearlyFinishedEventHandler CreateHandler(PluginConfiguration config)
        => new(
            _sessionManagerMock.Object,
            config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            _queueManager);

    private static AudioPlayerRequest CreateNearlyFinishedRequest(string token, long offsetMs = 120_000)
        => new()
        {
            Type = "AudioPlayer.PlaybackNearlyFinished",
            Token = token,
            OffsetInMilliseconds = offsetMs
        };

    /// <summary>
    /// The two-track continuation scene: the device store carries the queue the play
    /// seeded (SetQueue, index 0), the session queue mirrors it, the finishing item
    /// resolves through the library, and the launch-scope store carries the 1.5x
    /// rate the advance continues. The device pointer's pre-event state is written
    /// the way the event flow leaves it mid-playback (RecordNowPlaying: the
    /// finishing item at a mid-listen position).
    /// </summary>
    private (SessionInfo Session, Context Context, Guid Track1, Guid Track2, long MidListenTicks) SeedTwoTrackScene(bool seedRate = true)
    {
        Guid track1 = Guid.NewGuid();
        Guid track2 = Guid.NewGuid();

        _queueManager.SetQueue(DeviceId, new List<string> { track1.ToString(), track2.ToString() }, currentIndex: 0);

        long midListenTicks = TimeSpan.FromMinutes(2).Ticks;
        _queueManager.RecordNowPlaying(DeviceId, track1.ToString(), midListenTicks);

        if (seedRate)
        {
            // The active (non-enqueued) scope the JF-636 advance reads: the finishing
            // stream launched at 1.5x, so the successor's launch routes to atempo.
            _queueManager.RecordLaunchBase(DeviceId, track1.ToString(), 0, enqueued: false, ratePerMille: 1500);
        }

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1 },
            new() { Id = track2 }
        };

        var track1Item = new Audio { Id = track1, Name = "Finishing Song" };
        var track2Item = new Audio { Id = track2, Name = "Successor Song" };
        session.FullNowPlayingItem = track1Item;
        _libraryManagerMock.Setup(l => l.GetItemById(track1)).Returns(track1Item);
        _libraryManagerMock.Setup(l => l.GetItemById(track2)).Returns(track2Item);

        Context context = TestHelpers.CreateContextWithToken(track1.ToString(), DeviceId, "PLAYING");
        return (session, context, track1, track2, midListenTicks);
    }

    /// <summary>
    /// The exhausted-queue scene the three exhaustion-arm pins share: a single-item
    /// device queue and session queue around the finishing item, the mid-listen
    /// pointer position, the 1.5x launch scope that routes the derived successor
    /// through the token-gated atempo URL, the library/user mocks (the successor
    /// resolvable, any list query answering it), and the radio-mode pre-state. The
    /// episode arm layers its series/candidates query mocks on top (registered
    /// after this, so they win Moq's last-match rule).
    /// </summary>
    private (SessionInfo Session, Context Context, long MidListenTicks) SeedExhaustedScene(BaseItem finishingItem, BaseItem successorItem, bool radioOn)
    {
        _queueManager.SetQueue(DeviceId, new List<string> { finishingItem.Id.ToString() }, currentIndex: 0);
        long midListenTicks = TimeSpan.FromMinutes(2).Ticks;
        _queueManager.RecordNowPlaying(DeviceId, finishingItem.Id.ToString(), midListenTicks);
        _queueManager.RecordLaunchBase(DeviceId, finishingItem.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = finishingItem.Id } };
        session.FullNowPlayingItem = finishingItem;
        _libraryManagerMock.Setup(l => l.GetItemById(finishingItem.Id)).Returns(finishingItem);
        _libraryManagerMock.Setup(l => l.GetItemById(successorItem.Id)).Returns(successorItem);
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { successorItem });
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());

        if (radioOn)
        {
            RadioModeState.Enable(session.UserId, DeviceId);
        }
        else
        {
            RadioModeState.Disable(session.UserId, DeviceId);
        }

        Context context = TestHelpers.CreateContextWithToken(finishingItem.Id.ToString(), DeviceId, "PLAYING");
        return (session, context, midListenTicks);
    }

    /// <summary>
    /// The JF-712 refusal pin's shared assertion: the pointer still names the
    /// finishing item with its position intact. Returns the queue for the caller's
    /// arm-specific follow-ups (the enqueue record, the index).
    /// </summary>
    private DeviceQueue AssertPointerIntact(Guid finishingItemId, long expectedTicks)
    {
        DeviceQueue queue = _queueManager.GetQueue(DeviceId)!;
        Assert.Equal(finishingItemId.ToString(), queue.CurrentItemId);
        Assert.Equal(expectedTicks, queue.CurrentPositionTicks);
        return queue;
    }

    // ------------------------------------------------------------------
    // Main continuation arm
    // ------------------------------------------------------------------

    [Fact]
    public async Task MainArm_AtempoAdvanceEmptySecret_Refuses_PointerNamesFinishingItemWithPosition()
    {
        var (session, context, track1, _, midListenTicks) = SeedTwoTrackScene();

        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => CreateHandler(RefusalConfig()).HandleAsync(
                CreateNearlyFinishedRequest(track1.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None));

        // JF-712 pin: the pointer still names the FINISHING item with its position
        // intact (the pre-refusal defect advanced it to the successor and zeroed
        // the position, so the post-fix recovery read resumed the wrong item at 0).
        DeviceQueue queue = AssertPointerIntact(track1, midListenTicks);
        Assert.Equal(0, queue.CurrentIndex);

        // The builder refused before its own records: nothing was enqueued.
        Assert.Null(queue.LastEnqueueNextItemId);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task MainArm_Success_AdvancesPointerExactlyOnceAndRecordsEnqueue()
    {
        // The success-parity half of the pin: a non-token-gated launch (identity
        // rate, static stream URL) still commits exactly one advance.
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "http://localhost:8096");
        var (session, context, track1, track2, _) = SeedTwoTrackScene(seedRate: false);

        SkillResponse response = await CreateHandler(config).HandleAsync(
            CreateNearlyFinishedRequest(track1.ToString()), context, TestHelpers.CreateTestUser(jellyfinToken: "tok"), session, CancellationToken.None);

        Assert.NotNull(response.Response.Directives.OfType<AudioPlayerPlayDirective>().Single());

        DeviceQueue queue = _queueManager.GetQueue(DeviceId)!;
        Assert.Equal(track2.ToString(), queue.CurrentItemId);
        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal(track2.ToString(), queue.LastEnqueueNextItemId);
        Assert.Equal(track1.ToString(), queue.LastEnqueueAfterToken);

        // The finishing item's position was consumed by the advance (a DIFFERENT
        // next item resets it to 0; the same-item wrap is the JF-522 refresh).
        Assert.Equal(0, queue.CurrentPositionTicks);
    }

    // ------------------------------------------------------------------
    // Precompute cached-enqueue arm
    // ------------------------------------------------------------------

    [Fact]
    public async Task CachedEnqueueArm_TokenGatedCachedUrlEmptySecret_Refuses_PointerNamesFinishingItem()
    {
        var config = RefusalConfig();
        config.PreEnqueueOnStart = true;
        var (session, context, track1, track2, midListenTicks) = SeedTwoTrackScene(seedRate: false);

        // The precompute entry PlaybackStarted would have stored for a rate
        // continuation: the successor item with an atempo (token-gated) URL.
        var track2Item = new Audio { Id = track2, Name = "Successor Song" };
        string tokenGatedUrl = $"https://test.example.com/alexaskill/api/audio-speed/{track2}/1500/stream.m3u8?token=x";
        NextTrackPrecomputeCache.Store(DeviceId, track1.ToString(), track2, track2Item, tokenGatedUrl, 1500);
        try
        {
            await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
                () => CreateHandler(config).HandleAsync(
                    CreateNearlyFinishedRequest(track1.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None));

            DeviceQueue queue = AssertPointerIntact(track1, midListenTicks);
            Assert.Equal(0, queue.CurrentIndex);
            Assert.Null(queue.LastEnqueueNextItemId);
        }
        finally
        {
            NextTrackPrecomputeCache.Invalidate(DeviceId);
        }
    }

    // ------------------------------------------------------------------
    // Radio arm (queue exhausted, radio mode on)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RadioArm_AtempoAdvanceEmptySecret_Refuses_QueueNotAppended()
    {
        Guid currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        var similarTrack = new Audio { Id = Guid.NewGuid(), Name = "Similar Rock Song" };

        var (session, context, midListenTicks) = SeedExhaustedScene(currentAudio, similarTrack, radioOn: true);
        try
        {
            await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
                () => CreateHandler(RefusalConfig()).HandleAsync(
                    CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None));

            // JF-712 pin: the derived radio tracks never reached the session queue
            // and nothing was enqueued.
            Assert.Single(session.NowPlayingQueue);
            DeviceQueue queue = AssertPointerIntact(currentId, midListenTicks);
            Assert.Null(queue.LastEnqueueNextItemId);
        }
        finally
        {
            RadioModeState.Disable(session.UserId, DeviceId);
        }
    }

    // ------------------------------------------------------------------
    // PostPlay AutoPlay arm (queue exhausted, radio off): RadioModeState.Enable is
    // the worst phantom of the family (an armed-but-dead radio mode drives later
    // continuation decisions).
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostPlayArm_AtempoAdvanceEmptySecret_Refuses_RadioNotArmedQueueNotAppended()
    {
        var config = RefusalConfig();
        config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        Guid currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        var similarTrack = new Audio { Id = Guid.NewGuid(), Name = "Similar Rock Song" };

        var (session, context, midListenTicks) = SeedExhaustedScene(currentAudio, similarTrack, radioOn: false);
        try
        {
            await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
                () => CreateHandler(config).HandleAsync(
                    CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None));

            Assert.False(RadioModeState.IsEnabled(session.UserId, DeviceId), "a refused PostPlay continuation must not arm radio mode");
            Assert.Single(session.NowPlayingQueue);
            AssertPointerIntact(currentId, midListenTicks);
        }
        finally
        {
            RadioModeState.Disable(session.UserId, DeviceId);
        }
    }

    // ------------------------------------------------------------------
    // Commit idempotence under the multi-fire race (code-review F1)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PostPlayCommit_SiblingFireCommittedSameTrackInsideTheWindow_NoDoubleAppend()
    {
        // Amazon multi-fires NearlyFinished as concurrent requests; a sibling fire
        // can commit the same derived population while THIS request sits between
        // its derive and its build (a window that spans the whole launch build).
        // The commit re-runs the membership dedup, so the track lands ONCE. The
        // sibling's commit is simulated at the exact in-window seam: the successor
        // item's GetItemById runs after the derive and before the build.
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "http://localhost:8096");
        config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;

        Guid currentId = Guid.NewGuid();
        Guid similarId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        var similarTrack = new Audio { Id = similarId, Name = "Similar Rock Song" };

        var (session, context, _) = SeedExhaustedScene(currentAudio, similarTrack, radioOn: false);
        _libraryManagerMock.Setup(l => l.GetItemById(similarId))
            .Callback(() =>
            {
                var siblingQueue = new List<QueueItem>(session.NowPlayingQueue) { new() { Id = similarId } };
                session.NowPlayingQueue = siblingQueue;
            })
            .Returns(similarTrack);

        SkillResponse response = await CreateHandler(config).HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(jellyfinToken: "tok"), session, CancellationToken.None);

        Assert.NotNull(response.Response.Directives.OfType<AudioPlayerPlayDirective>().Single());
        Assert.Equal(2, session.NowPlayingQueue.Count);
        Assert.Single(session.NowPlayingQueue, q => q.Id == similarId);
    }

    // ------------------------------------------------------------------
    // Episode auto-advance arm (the third session-queue append site, treated
    // uniformly with the AutoPopulate arms)
    // ------------------------------------------------------------------

    [Fact]
    public async Task EpisodeAdvanceArm_AtempoAdvanceEmptySecret_Refuses_EpisodeNotAppended()
    {
        var config = RefusalConfig();
        config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        Guid seriesId = Guid.NewGuid();
        Guid currentId = Guid.NewGuid();
        Guid nextId = Guid.NewGuid();

        var currentEpisode = new Episode { Id = currentId, Name = "S03E05", SeriesId = seriesId, SeriesName = "Test Show" };
        var nextEpisode = new Episode { Id = nextId, Name = "S03E06", SeriesId = seriesId, SeriesName = "Test Show" };

        var (session, context, midListenTicks) = SeedExhaustedScene(currentEpisode, nextEpisode, radioOn: false);

        // Series authorization (any Series query) and the direct unplayed-episodes
        // query (the PostPlayHandlerTests shape), layered after the scene's generic
        // list mock so they win Moq's last-match rule: the finishing episode is
        // still unplayed, so it is the first candidate and the true next follows it.
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Series))))
            .Returns(new List<BaseItem> { new Series { Id = seriesId, Name = "Test Show" } }.AsReadOnly());
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Episode) && q.IsPlayed == false && q.AncestorIds.Length == 1 && q.ParentIndexNumberNotEquals == 0)))
            .Returns(new List<BaseItem> { currentEpisode, nextEpisode }.AsReadOnly());

        try
        {
            await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
                () => CreateHandler(config).HandleAsync(
                    CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None));

            // The resolved next episode never reached the session queue.
            Assert.Single(session.NowPlayingQueue);
            DeviceQueue queue = AssertPointerIntact(currentId, midListenTicks);
            Assert.Null(queue.LastEnqueueNextItemId);
        }
        finally
        {
            RadioModeState.Disable(session.UserId, DeviceId);
        }
    }
}
