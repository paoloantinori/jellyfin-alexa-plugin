using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
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
    /// <paramref name="endUnknownPage"/> simulates the JF-673/JF-753 NRE-class
    /// server: the paged tracks queries NRE on GetItemsResult (the head's
    /// SafeGetItemsResult then serves the page through the GetItemList fallback
    /// with the SENTINEL total), while the unpaged deep query answers normally.
    /// </summary>
    private (MusicAlbum Album, List<BaseItem> Tracks) SetupDeepResumeAlbum(
        int trackCount,
        int? progressTrackIndex = null,
        long positionTicks = 0,
        List<InternalItemsQuery>? queries = null,
        bool endUnknownPage = false)
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

        if (endUnknownPage)
        {
            // Registered AFTER the generic setups so Moq's last-match-wins order
            // routes the paged tracks queries here: they NRE on GetItemsResult and
            // the head's SafeGetItemsResult serves them through the GetItemList
            // fallback (which, under the head's unknownTotalOnFallback, reports
            // the SENTINEL total). The unpaged deep query (Limit == null) keeps
            // the generic paging mock and its honest count.
            _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.Limit != null)))
                .Throws(new NullReferenceException());
            _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                    q.Limit != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))))
                .Returns((InternalItemsQuery q) => tracks.Skip(q.StartIndex ?? 0).Take(q.Limit ?? tracks.Count).ToList());
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

    // Gate-marker tail F2: the ParentId arm's DEEP query field pin. The JF-757
    // lockstep fixture's ParentId page is empty by design, so its
    // AssertSharedQueryShape only ever sees the AlbumIds arm's deep fetch, and this
    // suite's generic mock returns identical rows for any query shape - the
    // DOMINANT production arm (a normal album serving page 1 by ParentId, then
    // deep-fetching) had no field-level pin: a pageUsedAlbumIds inversion or a
    // ParentId deep-query drift stayed green. This pin captures the unpaged deep
    // fetch (Limit == null) and asserts it mirrors the serving arm (ParentId
    // scope, no AlbumIds) with the shared core's order.
    [Fact]
    public async Task HandleAsync_DeepFetchOnParentIdArm_UsesScopedParentIdQueryFields()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();
        var queries = new List<InternalItemsQuery>();

        _fx.SetupUserMock();
        (MusicAlbum album, List<BaseItem> tracks) = SetupDeepResumeAlbum(26, progressTrackIndex: 21, positionTicks: TimeSpan.FromMinutes(1).Ticks, queries: queries);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            GetPlayDirective(response);

            InternalItemsQuery deepQuery = Assert.Single(queries, q => q.Limit == null);
            Assert.Equal(album.Id, deepQuery.ParentId);
            // The shared core initializes AlbumIds to the empty array on the ParentId
            // arm (its constant field set); the semantic pinned is "does not FILTER
            // by AlbumIds".
            Assert.True(deepQuery.AlbumIds is null || deepQuery.AlbumIds.Length == 0,
                $"the ParentId-arm deep query must not filter by AlbumIds, got [{string.Join(",", deepQuery.AlbumIds ?? Array.Empty<Guid>())}]");
            Assert.NotNull(deepQuery.OrderBy);
            Assert.Contains(deepQuery.OrderBy, o => o.Item1 == ItemSortBy.ParentIndexNumber);
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

    // JF-796 companion pin (the code-review F4 gap): the end-unknown regime
    // transition. On an NRE-class server the page arrives through the GetItemList
    // fallback carrying the SENTINEL total (unknownTotalOnFallback), so the
    // continuation the deep re-slice mints must carry the fetch-all list's HONEST
    // count, not the sentinel: an end-unknown continuation after a re-slice would
    // flip the tail's exhaustion semantics (AdvanceOrMarkExhausted marks on short
    // pages) and double-fetch or truncate the album tail on exactly those servers.
    [Fact]
    public async Task HandleAsync_DeepProgressBeyondInitialPage_EndUnknownPageRegime_RebasesToTheHonestTotal()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        (MusicAlbum _, List<BaseItem> tracks) = SetupDeepResumeAlbum(
            40, progressTrackIndex: 21, positionTicks: TimeSpan.FromMinutes(1).Ticks, endUnknownPage: true);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            AudioPlayerPlayDirective playDirective = GetPlayDirective(response);
            Assert.Equal(tracks[21].Id.ToString(), playDirective.AudioItem.Stream.Token);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(26, continuation!.StartIndex);
            // The honest fetch-all count, not the int.MaxValue sentinel the page
            // carried (Equal pins the value, so the sentinel shape reds here).
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

    // JF-796 companion pin (the F4 pin's premise, verified empirically): the
    // endUnknownPage knob genuinely routes the page through the NRE fallback, so
    // with no deep progress the head stores the SENTINEL total (the JF-753
    // behavior). If the knob's mock order ever breaks, the F4 regime pin above
    // would silently degrade to the known-total regime and stay green; this pin
    // reds instead.
    [Fact]
    public async Task HandleAsync_EndUnknownPageRegime_NoDeepProgress_StoresTheSentinelTotal()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("deep resume album");
        var context = _fx.CreateContext();
        var session = CreateSession();
        _fx.SetupUserMock();
        SetupDeepResumeAlbum(40, endUnknownPage: true);
        try
        {
            await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);
            QueueContinuation? probeContinuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(probeContinuation);
            Assert.Equal(Jellyfin.Plugin.AlexaSkill.Alexa.Util.SearchService.UnknownTotal, probeContinuation!.TotalCount);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }
}
