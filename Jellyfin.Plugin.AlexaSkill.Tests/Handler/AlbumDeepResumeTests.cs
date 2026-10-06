using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-796: the album head's page-1-bounded resume on the AUDIO route (the JF-793
/// Finding 4 audiobook twin). FindResumeTrackIndex scanned only the 5-track initial
/// page, so UserData progress on a track beyond the page (track 22 of 26) was
/// invisible and a fresh ask relaunched from track 1. The pins here mirror the
/// PlayBookResumeTests deep-resume pair: the position-holding track launches, the
/// continuation offset rebases to the re-sliced page, in-page progress stays on the
/// page scan alone, and a fresh multi-page album keeps the page-1 outcome (while
/// paying the documented one-fetch trade). The seek-mode (JF-625 tracker) route has
/// its own companion pin in AlbumAnnounceVehicleTests: the tracker veto keeps that
/// route byte-identical.
/// </summary>
[Collection("Plugin")]
public class AlbumDeepResumeTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;

    public AlbumDeepResumeTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("AlbumDeepResumeTests");

        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "album-deep-resume-tests");
    }

    // JF-535: dispose so the 2s debounce flush runs deterministically at test end (no post-test straggler).
    public void Dispose() => _queueManager.Dispose();

    private PlayAlbumIntentHandler CreateHandler()
    {
        return new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string album)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["album"] = new Slot { Name = "album", Value = album }
        };

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    /// <summary>
    /// The shared deep-resume fixture (the JF-796 pair): a paging-honoring tracks
    /// mock (initial page 5, the deep unpaged fetch all; the zero-padded Names keep
    /// mock insertion order equal to the server's SortName order) and optional
    /// in-progress UserData on the track at <paramref name="progressTrackIndex"/>.
    /// Recorded track queries land in <paramref name="queries"/> when given.
    /// </summary>
    private (MusicAlbum Album, List<BaseItem> Tracks) SetupDeepResumeAlbum(
        int trackCount,
        int? progressTrackIndex = null,
        long positionTicks = 0,
        List<InternalItemsQuery>? queries = null)
    {
        var album = new MusicAlbum { Name = "Deep Resume Album", Id = Guid.NewGuid(), ProductionYear = 2020 };
        List<BaseItem> tracks = Enumerable.Range(1, trackCount)
            .Select(i => (BaseItem)new Audio
            {
                Name = $"Deep Resume Album track {i:00}",
                Id = Guid.NewGuid(),
                Album = album.Name,
                ParentId = album.Id,
                RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
            })
            .ToList();

        // The album search resolves the album (any GetItemList query).
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { album });

        // The tracks query honors paging.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                return new QueryResult<BaseItem>
                {
                    Items = tracks.Skip(q.StartIndex ?? 0).Take(q.Limit ?? tracks.Count).ToList(),
                    TotalRecordCount = tracks.Count
                };
            });

        if (progressTrackIndex is int progressIdx)
        {
            var inProgress = new UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = positionTicks
            };
            _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
                .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                    item.Id == tracks[progressIdx].Id ? inProgress : null);
        }

        return (album, tracks);
    }

    private static AudioPlayerPlayDirective GetPlayDirective(SkillResponse response)
    {
        AudioPlayerPlayDirective? playDirective = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(playDirective);
        return playDirective!;
    }

    // JF-796 RED PROOF: the audio route's page-1-bounded resume. FindResumeTrackIndex
    // scanned only the 5-track initial page, so UserData progress on track 22 of 26
    // was invisible and the fresh ask relaunched from track 1 (pre-fix red: the
    // directive token named track 1). The bounded resolution: when the page yields no
    // position and the album extends beyond it, the album is fetched once unpaged and
    // the ONE resume decision re-run on the full track list; the page re-slices at the
    // position-holding track, so the launch, the queue, and the continuation all
    // start there.
    [Fact]
    public async Task HandleAsync_DeepProgressBeyondInitialPage_ResumesAtPositionHoldingTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        (MusicAlbum _, List<BaseItem> tracks) = SetupDeepResumeAlbum(26, progressTrackIndex: 21, positionTicks: TimeSpan.FromMinutes(1).Ticks);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            AudioPlayerPlayDirective playDirective = GetPlayDirective(response);

            // The position-holding track launches, not track 1.
            Assert.Equal(tracks[21].Id.ToString(), playDirective.AudioItem.Stream.Token);

            // The queue starts at the position-holding track and carries the rest of
            // the re-sliced page (tracks 22 to 26); nothing remains beyond it, so no
            // continuation is minted.
            Assert.Equal(5, session.NowPlayingQueue.Count);
            Assert.Equal(tracks[21].Id, session.FullNowPlayingItem!.Id);
            Assert.Equal(tracks[21].Id, session.NowPlayingQueue[0].Id);
            Assert.Equal(tracks[25].Id, session.NowPlayingQueue[4].Id);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-796 companion pin: the continuation offset after a deep resume. A 40-track
    // album resumed at track 22 (index 21) re-slices the page at tracks 22 to 26, so
    // the continuation must fetch from index 26 (page start 21 + page count 5)
    // against the real total 40: the pre-fix shape stored StartIndex 5 (page 1 only)
    // regardless of the resume point.
    [Fact]
    public async Task HandleAsync_DeepProgressBeyondInitialPage_ContinuationOffsetAtResumePoint()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        (MusicAlbum album, List<BaseItem> _) = SetupDeepResumeAlbum(40, progressTrackIndex: 21, positionTicks: TimeSpan.FromMinutes(1).Ticks);

        try
        {
            await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal("Album", continuation!.SourceType);
            Assert.Equal(album.Id, continuation.ParentId);
            Assert.Equal(26, continuation.StartIndex);
            Assert.Equal(40, continuation.TotalCount);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-796 companion pin: in-page progress stays on the page scan alone. Progress
    // on page 1 (track 3 of 26) must resume there WITHOUT the unpaged deep fetch
    // (the guard keys on the page scan's no-position answer).
    [Fact]
    public async Task HandleAsync_InPageProgress_ResumesOnThePage_WithoutTheDeepFetch()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        var queries = new List<InternalItemsQuery>();
        (MusicAlbum _, List<BaseItem> tracks) = SetupDeepResumeAlbum(26, progressTrackIndex: 2, positionTicks: TimeSpan.FromMinutes(1).Ticks, queries: queries);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            AudioPlayerPlayDirective playDirective = GetPlayDirective(response);
            Assert.Equal(tracks[2].Id.ToString(), playDirective.AudioItem.Stream.Token);
            Assert.Equal(tracks[2].Id, session.FullNowPlayingItem!.Id);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(5, continuation!.StartIndex);
            Assert.Equal(26, continuation.TotalCount);

            // No unpaged (deep) tracks query ran: every page query carries a Limit.
            Assert.DoesNotContain(queries, q => q.Limit is null);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-796 companion pin: a fresh multi-page album keeps the page-1 outcome. The
    // no-position page answer fires the deep fetch (the documented trade: every
    // first-ever ask of a multi-page album pays one bounded unpaged query), but the
    // scan finds nothing, so no re-slice happens: track 1 launches and the
    // continuation keeps the page-1 values.
    [Fact]
    public async Task HandleAsync_FreshMultiPageAlbum_StartsAtTrackOne_PaysTheOneFetchTrade()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        var queries = new List<InternalItemsQuery>();
        (MusicAlbum _, List<BaseItem> tracks) = SetupDeepResumeAlbum(26, queries: queries);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            AudioPlayerPlayDirective playDirective = GetPlayDirective(response);
            Assert.Equal(tracks[0].Id.ToString(), playDirective.AudioItem.Stream.Token);
            Assert.Equal(tracks[0].Id, session.FullNowPlayingItem!.Id);
            Assert.Equal(5, session.NowPlayingQueue.Count);
            Assert.Equal(tracks[4].Id, session.NowPlayingQueue[4].Id);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(5, continuation!.StartIndex);
            Assert.Equal(26, continuation.TotalCount);

            // The trade, pinned: exactly one unpaged tracks query ran and found no
            // position (the re-slice guard stayed cold).
            Assert.Single(queries, q => q.Limit is null);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }
}
