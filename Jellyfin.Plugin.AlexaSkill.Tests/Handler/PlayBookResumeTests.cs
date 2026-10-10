using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for PlayBookIntentHandler resume behavior:
/// book progress detection, chapter skipping, and offset calculation.
/// </summary>
[Collection("Plugin")]
public class PlayBookResumeTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;

    public PlayBookResumeTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("PlayBookResumeTests");

        TestHelpers.EnsurePluginInstance(
            _fx.Config, _fx.LoggerFactory, c => { }, "playbook-resume-tests");
    }

    // JF-535: dispose so the 2s debounce flush runs deterministically at test end (no post-test straggler).
    public void Dispose() => _queueManager.Dispose();

    private PlayBookIntentHandler CreateHandler()
    {
        return new PlayBookIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string bookName)
    {
        var intent = new global::Alexa.NET.Request.Intent { Name = IntentNames.PlayBook };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
        {
            ["book"] = new global::Alexa.NET.Request.Slot { Name = "book", Value = bookName }
        };

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    private void SetupBookAndTracks(string bookName, List<Audio> tracks)
    {
        var bookItem = new Audio
        {
            Name = bookName,
            Id = Guid.NewGuid()
        };

        // Book search returns the book
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        // Track listing returns the tracks
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = tracks.Cast<BaseItem>().ToList(),
                TotalRecordCount = tracks.Count
            });
    }

    [Fact]
    public async Task HandleAsync_BookWithProgress_ResumesFromCorrectChapter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        // Create 5 tracks for the book
        var tracks = new List<Audio>();
        for (int i = 0; i < 5; i++)
        {
            tracks.Add(new Audio
            {
                Name = $"Chapter {i + 1}",
                Id = Guid.NewGuid()
            });
        }

        SetupBookAndTracks("The Hobbit", tracks);

        // Chapters 0 and 1 are played, chapter 2 has in-progress position at 45 seconds
        var playedData0 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        var playedData1 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        var inProgressData2 = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromSeconds(45).Ticks
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(playedData0);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[1]))
            .Returns(playedData1);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[2]))
            .Returns(inProgressData2);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[3]))
            .Returns((UserItemData?)null);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[4]))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // Should resume from chapter 3 (index 2) with offset 45 seconds
        Assert.Equal((int)TimeSpan.FromSeconds(45).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // The stream URL should contain the chapter 3 track's ID (tracks are sliced starting from the resume index)
        Assert.Contains(tracks[2].Id.ToString(), audioDirective.AudioItem.Stream.Url);

        // Output speech should indicate resumption
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("resuming", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_BookWithNoProgress_StartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var tracks = new List<Audio>
        {
            new() { Name = "Chapter 1", Id = Guid.NewGuid() },
            new() { Name = "Chapter 2", Id = Guid.NewGuid() },
            new() { Name = "Chapter 3", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("The Hobbit", tracks);

        // No progress on any track
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // Should start from the beginning with offset 0
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Stream URL should contain the first track's ID
        Assert.Contains(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }

    [Fact]
    public async Task HandleAsync_BookAllPlayed_ResumesFromTrackAfterLastPlayed()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var tracks = new List<Audio>
        {
            new() { Name = "Chapter 1", Id = Guid.NewGuid() },
            new() { Name = "Chapter 2", Id = Guid.NewGuid() },
            new() { Name = "Chapter 3", Id = Guid.NewGuid() },
            new() { Name = "Chapter 4", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("The Hobbit", tracks);

        // Chapters 0 and 1 are fully played, no in-progress chapter
        var playedData0 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        var playedData1 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(playedData0);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[1]))
            .Returns(playedData1);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[2]))
            .Returns((UserItemData?)null);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[3]))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // Should resume from the track after the last played one (chapter 3, index 2)
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains(tracks[2].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }

    [Fact]
    public async Task HandleAsync_BookAllPlayed_NoMoreTracks_StartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Short Book");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        // Only one track, fully played
        var tracks = new List<Audio>
        {
            new() { Name = "Chapter 1", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("Short Book", tracks);

        var playedData = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(playedData);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // All played, no track after last played -> starts from beginning
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }

    // The shared deep-resume fixture (the JF-793 Finding 4 pair): the JF-791 live
    // entry shape (a chapter leaf that climbs to its pathed book folder), a
    // paging-honoring chapters mock (initial page 5, the deep unpaged fetch all),
    // and deep in-progress UserData on the chapter at progressIndex (default 22,
    // index 21) at the given position. JF-797: the mock routes through the
    // flag-honoring TestHelpers.ServePagedTracks so the resume-probe queries the
    // discriminator issues are answered like the server answers them, and it
    // records every tracks query for the query-count pins.
    private (List<BaseItem> Chapters, Guid BookFolderId) SetupDeepResumeBook(
        int chapterCount,
        long positionTicks,
        int progressIndex = 21,
        List<int>? playedIndexes = null,
        List<InternalItemsQuery>? queries = null)
    {
        Guid bookFolderId = Guid.NewGuid();
        var chapterLeaf = new AudioBook
        {
            Name = "Measure What Matters",
            Id = Guid.NewGuid(),
            ParentId = bookFolderId,
            Path = "/audiobooks/measure-what-matters/ch01.mp3"
        };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { chapterLeaf });
        _fx.LibraryManager.Setup(l => l.GetItemById(bookFolderId))
            .Returns(new Folder
            {
                Name = "Measure What Matters",
                Id = bookFolderId,
                Path = "/audiobooks/measure-what-matters"
            });

        List<BaseItem> chapters = Enumerable.Range(1, chapterCount)
            .Select(i => (BaseItem)new Audio
            {
                Name = $"Measure What Matters - Chapter {i:00}",
                Id = Guid.NewGuid(),
                // JF-790: the tagged class (this book is tagged in the live census);
                // without IndexNumber the chapters model the untagged class, whose
                // plays now take the filename-order branch and would fetch the book
                // unpaged, reddening the DB-path pins below (the skip-the-deep-fetch
                // query-count pin first).
                IndexNumber = i
            })
            .ToList();

        // The chapters query honors paging: the initial page serves 5 (the
        // zero-padded Names keep mock insertion order equal to the server's SortName
        // order), the deep unpaged fetch serves the whole book. The flag probes
        // filter before paging (the server's order).
        HashSet<Guid> playedIds = playedIndexes?.Select(i => chapters[i].Id).ToHashSet() ?? new HashSet<Guid>();
        BaseItem? progressRow = chapters[progressIndex];
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                queries?.Add(q);
                return TestHelpers.ServePagedTracks(
                    chapters,
                    q,
                    playedRow: c => playedIds.Contains(c.Id),
                    resumableRow: c => c.Id == progressRow.Id && positionTicks > 0);
            });

        var inProgress = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = positionTicks
        };
        var played = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                item.Id == chapters[progressIndex].Id && positionTicks > 0 ? inProgress
                : playedIds.Contains(item.Id) ? played
                : null);

        return (chapters, bookFolderId);
    }

    // JF-793 Finding 4 RED PROOF: the page-1-bounded resume. FindResumeTrackIndex
    // scans only the 5-item initial page, so UserData progress on chapter 22 of 26
    // is invisible and the fresh ask relaunched from chapter 1 at 0:00 (pre-JF-791
    // the same ask played the matched chapter at its position, then silence; the
    // album precedent is not liftable, JF-625 criterion 3 is the video-route tracker
    // override). The bounded resolution: when page 1 yields no position and the book
    // extends beyond the page, the full chapter list is fetched once and the ONE
    // resume decision re-run on it; the page re-slices at the position-holding
    // chapter, so the launch, the queue, and the continuation all start there.
    [Fact]
    public async Task HandleAsync_DeepProgressBeyondInitialPage_ResumesAtPositionHoldingChapter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        (List<BaseItem> chapters, Guid _) = SetupDeepResumeBook(26, TimeSpan.FromMinutes(10).Ticks);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            // The position-holding chapter launches at its position, not chapter 1
            // at 0:00.
            Assert.Equal(chapters[21].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal((int)TimeSpan.FromMinutes(10).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

            // The queue starts at the position-holding chapter and carries the rest
            // of the re-sliced page (chapters 22-26); nothing remains beyond it, so
            // no continuation is minted.
            Assert.Equal(5, session.NowPlayingQueue.Count);
            Assert.Equal(chapters[21].Id, session.FullNowPlayingItem!.Id);
            Assert.Equal(chapters[21].Id, session.NowPlayingQueue[0].Id);
            Assert.Equal(chapters[25].Id, session.NowPlayingQueue[4].Id);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-793 Finding 4 companion pin: the continuation offset after a deep resume.
    // A 40-chapter book resumed at chapter 22 (index 21) re-slices the page at
    // chapters 22-26, so the continuation must fetch from index 26 (page start 21 +
    // page count 5) against the real total 40: the pre-fix shape stored StartIndex 5
    // (page 1 only) regardless of the resume point.
    [Fact]
    public async Task HandleAsync_DeepProgressBeyondInitialPage_ContinuationOffsetAtResumePoint()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        (List<BaseItem> _, Guid bookFolderId) = SetupDeepResumeBook(40, TimeSpan.FromMinutes(3).Ticks);

        try
        {
            await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal("Audiobook", continuation!.SourceType);
            Assert.Equal(bookFolderId, continuation.ParentId);
            Assert.Equal(26, continuation.StartIndex);
            Assert.Equal(40, continuation.TotalCount);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-797 item 1 RED PROOF (the masking shape): the deep-resume gate keyed on
    // startIndex == 0, so a PLAYED PREFIX on page 1 (the after-last-played answer
    // (3, 0)) suppressed the deep scan even though an in-progress chapter exists
    // at track 10 (index 9) beyond the page: the ask relaunched at the shallow
    // prefix position (chapter 4). The gate must fire on ticks == 0 regardless of
    // the prefix index, and the deep scan must re-slice at the in-progress chapter.
    [Fact]
    public async Task HandleAsync_PlayedPrefixOnPage1_DeeperInProgressBeyondPage_ResumesAtInProgressChapter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        var queries = new List<InternalItemsQuery>();
        (List<BaseItem> chapters, Guid _) = SetupDeepResumeBook(
            26,
            TimeSpan.FromMinutes(10).Ticks,
            progressIndex: 9,
            playedIndexes: new List<int> { 0, 1, 2 },
            queries: queries);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            // The in-progress chapter 10 launches at its position, not the prefix
            // successor chapter 4 (the pre-fix shallow answer).
            Assert.Equal(chapters[9].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal((int)TimeSpan.FromMinutes(10).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
            Assert.Equal(chapters[9].Id, session.FullNowPlayingItem!.Id);
            Assert.Equal(chapters[9].Id, session.NowPlayingQueue[0].Id);

            // The deep fetch ran (one unpaged tracks query) to find it.
            Assert.Single(queries, q => q.Limit == null);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-797 item 2 (the fresh-ask discriminator, the query-count pin): a first-ever
    // multi-page ask carries no user data anywhere and a clean device queue, so the
    // two bounded resume-probe queries (IsPlayed, IsResumable, one row each) must
    // answer the gate INSTEAD of the unpaged full-book fetch. The page-1 outcome is
    // kept byte-identical: chapter 1 launches, the continuation carries the
    // page-1 values.
    [Fact]
    public async Task HandleAsync_FreshMultiPageBook_NoUserDataAnywhere_SkipsTheDeepFetch()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        var queries = new List<InternalItemsQuery>();
        (List<BaseItem> chapters, Guid bookFolderId) = SetupDeepResumeBook(26, positionTicks: 0, queries: queries);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);
            Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(bookFolderId, continuation!.ParentId);
            Assert.Equal(5, continuation.StartIndex);
            Assert.Equal(26, continuation.TotalCount);

            // NO unpaged (deep) tracks query ran; the gate cost is the two bounded
            // probes (one IsPlayed, one IsResumable, one row each).
            Assert.DoesNotContain(queries, q => q.Limit == null);
            Assert.Equal(2, queries.Count(q => q.IsPlayed == true || q.IsResumable == true));
            Assert.Single(queries, q => q.IsPlayed == true);
            Assert.Single(queries, q => q.IsResumable == true);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-812: the shared FreshAsk valve fixture (the query-count pin shape): the
    // clean-UserData 26-chapter multi-page book, a device store seeded with the
    // given kind stamps, the fresh ask run, and every tracks query recorded for
    // the fetch-count asserts. The continuation entry is removed on every path.
    private async Task<(SkillResponse Response, List<InternalItemsQuery> Queries, List<BaseItem> Chapters)> RunFreshAskWithPositionStamps(
        params bool?[] bookShapedStamps)
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        var queries = new List<InternalItemsQuery>();
        (List<BaseItem> chapters, Guid _) = SetupDeepResumeBook(26, positionTicks: 0, queries: queries);

        DeviceQueue queue = _queueManager.GetOrCreateQueue("test-device");
        foreach (bool? bookShaped in bookShapedStamps)
        {
            _queueManager.RecordStoppedPositionAndTrim(
                "test-device", queue, Guid.NewGuid(), TimeSpan.FromMinutes(1).Ticks, bookShaped);
        }

        try
        {
            return (await handler.HandleAsync(request, context, user, session, CancellationToken.None), queries, chapters);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-797 item 2 (the JF-581 over-fire pin, JF-812 kindless-legacy semantics):
    // the resume scan also reads the device queue's ItemPositionState, and a
    // server-side UserData write loss can leave the ONLY progress there. A
    // KINDLESS positioned entry (the pre-JF-812 store shape: positions persisted
    // before the kind stamps existed, or a write whose item could not be
    // resolved) must still force the deep fetch (an in-memory check) even with
    // clean UserData, or the discriminator would drop exactly that resume.
    [Fact]
    public async Task HandleAsync_FreshAsk_DeviceQueueHoldsKindlessPositionedEntry_StillFetchesDeep()
    {
        var (response, queries, chapters) = await RunFreshAskWithPositionStamps(new bool?[] { null });

        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // The positioned entry's item is foreign to this book, so the deep scan
        // finds no resume position and the fresh launch stands: the pin is the
        // FETCH, not the outcome.
        Assert.Equal(chapters[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
        Assert.Single(queries, q => q.Limit == null);
    }

    // JF-812 (the tightening, the query-count pin shape): a device whose position
    // store holds ONLY song-shaped entries (every entry stamped Other at write
    // time) must SKIP the deep fetch on a fresh multi-page book ask: the valve
    // exists to keep the JF-581 write-loss shape covered, and a song's position
    // can never be a book chapter's. The pre-JF-812 behavior released the fetch
    // for ANY positioned entry, so after any household song playback every
    // first-ever multi-page book ask paid the unconditional unpaged fetch.
    [Fact]
    public async Task HandleAsync_FreshAsk_DeviceQueueHoldsOnlySongShapedEntries_SkipsTheDeepFetch()
    {
        var (response, queries, chapters) = await RunFreshAskWithPositionStamps(new bool?[] { false, false });

        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // The page-1 fresh outcome stands (chapter 1 at 0:00)...
        Assert.Equal(chapters[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // ...and the gate cost is the two bounded probes, NOT the unpaged
        // full-book fetch (the valve skipped the song-shaped store, so the
        // probes answered the gate).
        Assert.DoesNotContain(queries, q => q.Limit == null);
        Assert.Equal(2, queries.Count(q => q.IsPlayed == true || q.IsResumable == true));
    }

    // JF-812 companion release pin: a BOOK-shaped stamped entry (an audiobook
    // chapter stopped through the skill) still releases the valve, so the JF-581
    // write-loss shape stays covered for stores written since the kind stamps
    // landed (the point of stamping rather than narrowing the store).
    [Fact]
    public async Task HandleAsync_FreshAsk_DeviceQueueHoldsBookShapedEntry_StillFetchesDeep()
    {
        var (response, queries, chapters) = await RunFreshAskWithPositionStamps(new bool?[] { true, false });

        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // The book-shaped entry releases the fetch (one unpaged tracks query);
        // its item is foreign to this book, so the deep scan finds no resume
        // and the fresh launch stands: the pin is the FETCH, not the outcome.
        Assert.Equal(chapters[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
        Assert.Single(queries, q => q.Limit == null);
    }

    [Fact]
    public async Task HandleAsync_BookWithProgress_StartsFromCorrectOffset()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Dune");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var tracks = new List<Audio>
        {
            new() { Name = "Part 1", Id = Guid.NewGuid() },
            new() { Name = "Part 2", Id = Guid.NewGuid() },
            new() { Name = "Part 3", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("Dune", tracks);

        // Part 1 has in-progress at 12 minutes 34 seconds
        var inProgressData = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(12).Ticks + TimeSpan.FromSeconds(34).Ticks
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(inProgressData);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[1]))
            .Returns((UserItemData?)null);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[2]))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        int expectedOffsetMs = (int)(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(34)).TotalMilliseconds;
        Assert.Equal(expectedOffsetMs, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Should resume from Part 1 (index 0) since it is still in progress
        Assert.Contains(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }
}
