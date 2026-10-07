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
/// JF-790 behavioral proofs for the untagged audiobook class on the DEFAULT paged
/// AudioPlayer path. The JF-672 live census (12.2.0, evidence in that task record)
/// split the production library into a tagged class (chapter order correct under
/// the pinned AudiobookChapterOrder, because Jellyfin derives zero-padded SortName
/// from the tags) and an untagged class whose rows carry no usable DB order key:
/// "Thinking Better" (14 chapters named '1'..'14', IndexNumber NULL) plays
/// 1, 10, 11, ... lexicographic, and "The Upside of Irrationality" (100 rows
/// sharing SortName AND Name) plays fully scrambled. The ONLY datum that orders
/// them is the file name, which never reaches the DB sort (ItemSortBy has no
/// Path axis); the concat endpoint already re-sorts by trailing filename number,
/// so the fix mirrors that comparator onto the queue path. These tests drive the
/// handler end to end and pin the LAUNCH and QUEUE order against the file order;
/// the tagged-class companion pins the DB path's invariance (no unpaged fetch,
/// no in-memory sort) so the 13-of-14 correct books keep byte-identical behavior.
/// </summary>
[Collection("Plugin")]
public class PlayBookChapterFileNameOrderTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;

    public PlayBookChapterFileNameOrderTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("playbook-filename-order-tests");

        TestHelpers.EnsurePluginInstance(
            _fx.Config, _fx.LoggerFactory, c => { }, "playbook-filename-order-tests");
    }

    // JF-535: dispose so the 2s debounce flush runs deterministically at test end.
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

    /// <summary>
    /// One chapter row shaped like the census rows: a file number the comparator
    /// can read off the path, plus the Name/SortName/IndexNumber the DB order
    /// keys on. IndexNumber null models the untagged class.
    /// </summary>
    private static Audio Chapter(string bookPath, string fileNumber, string name, string sortName, int? indexNumber)
        => new()
        {
            Name = name,
            SortName = sortName,
            IndexNumber = indexNumber,
            Id = Guid.NewGuid(),
            Path = $"{bookPath}/{fileNumber}.mp3"
        };

    /// <summary>
    /// The JF-791 live entry shape (a chapter leaf that climbs to its pathed book
    /// folder) plus a paging-honoring chapters mock that serves
    /// <paramref name="dbOrderChapters"/> in the given order (the server's
    /// (SortName, Name) answer) and records every query for the fetch-shape
    /// assertions. No user data anywhere unless
    /// <paramref name="resumableRow"/>/<paramref name="resumeTicks"/> model an
    /// in-progress chapter (the probe-matcher and UserData shapes ride together,
    /// the SetupDeepResumeBook convention).
    /// </summary>
    private (List<BaseItem> Chapters, Guid BookFolderId, List<InternalItemsQuery> Queries) SetupBook(
        string bookName,
        string bookPath,
        List<BaseItem> dbOrderChapters,
        Func<BaseItem, bool>? resumableRow = null,
        long resumeTicks = 0)
    {
        Guid bookFolderId = Guid.NewGuid();
        var chapterLeaf = new AudioBook
        {
            Name = bookName,
            Id = Guid.NewGuid(),
            ParentId = bookFolderId,
            Path = $"{bookPath}/001.mp3"
        };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { chapterLeaf });
        _fx.LibraryManager.Setup(l => l.GetItemById(bookFolderId))
            .Returns(new Folder
            {
                Name = bookName,
                Id = bookFolderId,
                Path = bookPath
            });

        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                queries.Add(q);
                return TestHelpers.ServePagedTracks(dbOrderChapters, q, resumableRow: resumableRow);
            });

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                resumableRow != null && resumableRow(item) && resumeTicks > 0
                    ? new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = resumeTicks }
                    : null);

        return (dbOrderChapters, bookFolderId, queries);
    }

    // The "Thinking Better" shape (JF-672 probe): 14 chapters NAMED '1'..'14' with
    // IndexNumber NULL, so the (SortName, Name) DB order is lexicographic and the
    // queue plays 1, 10, 11, 12, 13 before 2. The file names carry the true order.
    // RED on the pre-JF-790 tree: the queue assertion fails with the lexicographic
    // page (expected chapter 2 at queue slot 1, actual chapter 10).
    [Fact]
    public async Task HandleAsync_UntaggedDigitNamedBook_PlaysInFileNameOrder()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Thinking Better");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        const string bookPath = "/audiobooks/thinking-better";
        List<BaseItem> byFile = Enumerable.Range(1, 14)
            .Select(n => (BaseItem)Chapter(bookPath, n.ToString(), n.ToString(), n.ToString(), indexNumber: null))
            .ToList();

        // The server's (SortName, Name) answer: lexicographic on the digit names.
        List<BaseItem> dbOrder = byFile
            .OrderBy(c => c.SortName, StringComparer.Ordinal)
            .ToList();

        (List<BaseItem> _, Guid _, List<InternalItemsQuery> _) = SetupBook("Thinking Better", bookPath, dbOrder);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            // Chapter 1 launches first in both orders; the QUEUE is where the
            // lexicographic death shows (slot 1 must be chapter 2, not chapter 10).
            Assert.Equal(byFile[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
            Assert.Equal(
                byFile.Take(5).Select(c => c.Id),
                session.NowPlayingQueue.Select(q => q.Id));

            // The continuation pages the SORTED list from the page boundary.
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(5, continuation!.StartIndex);
            Assert.Equal(14, continuation.TotalCount);
            Assert.NotNull(continuation.CachedTracks);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // The "Upside of Irrationality" shape (JF-672 probe): every row shares SortName
    // AND Name and carries no IndexNumber, so the DB order is server whim (the
    // census observed it fully scrambled vs the 001.mp3..100.mp3 files). RED on the
    // pre-JF-790 tree: the LAUNCH assertion itself fails (the scrambled first row,
    // not file 001, is what plays).
    [Fact]
    public async Task HandleAsync_UntaggedIdenticalNameBook_PlaysInFileNameOrder()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Upside of Irrationality");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        const string bookPath = "/audiobooks/the-upside-of-irrationality";
        const string rowName = "The Upside of Irrationality";
        List<BaseItem> byFile = Enumerable.Range(1, 12)
            .Select(n => (BaseItem)Chapter(bookPath, n.ToString("000"), rowName, rowName, indexNumber: null))
            .ToList();

        // The observed arbitrary order: a fixed scramble of the file order.
        int[] scramble = { 7, 3, 11, 1, 9, 5, 12, 2, 8, 4, 10, 6 };
        List<BaseItem> dbOrder = scramble.Select(n => byFile[n - 1]).ToList();

        (List<BaseItem> _, Guid _, List<InternalItemsQuery> _) = SetupBook(rowName, bookPath, dbOrder);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            // File 001 launches first (the scramble served 007 first).
            Assert.Equal(byFile[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
            Assert.Equal(
                byFile.Take(5).Select(c => c.Id),
                session.NowPlayingQueue.Select(q => q.Id));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // The "Art of Deception" shape (JF-672 probe): IndexNumber PRESENT (the two
    // tagged parts) but every row ties on (SortName, Name), so the DB key cannot
    // order within the part and the answer is arbitrary vs the [01-27] files. The
    // full-key tie is the second detection trigger (independent of the untagged
    // trigger). RED on the pre-JF-790 tree: the launch assertion fails (the
    // scrambled first row plays, not file 01).
    [Fact]
    public async Task HandleAsync_TiedSortNameAndNameBook_PlaysInFileNameOrder()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Art of Deception");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        const string bookPath = "/audiobooks/the-art-of-deception";
        List<BaseItem> byFile = Enumerable.Range(1, 12)
            .Select(n => (BaseItem)Chapter(
                bookPath, n.ToString("00"), "Part 1", "0001 - Part 1", indexNumber: 1))
            .ToList();

        int[] scramble = { 5, 2, 10, 1, 8, 4, 12, 3, 9, 6, 11, 7 };
        List<BaseItem> dbOrder = scramble.Select(n => byFile[n - 1]).ToList();

        (List<BaseItem> _, Guid _, List<InternalItemsQuery> _) = SetupBook("The Art of Deception", bookPath, dbOrder);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            Assert.Equal(byFile[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
            Assert.Equal(
                byFile.Take(5).Select(c => c.Id),
                session.NowPlayingQueue.Select(q => q.Id));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // The JF-797 composition, the JF-790 leg: an untagged book whose resume
    // position sits BEYOND the initial page (chapter 9 of 14, DB page of 5). The
    // detected shape scans the full SORTED book once, so the resume lands on the
    // in-progress chapter with the page re-sliced in FILE order and the
    // continuation carrying the sorted list. RED on the pre-JF-790 tree: the deep
    // fetch scanned the UNSORTED DB order, so the queue after the resume chapter
    // ran on (the lexicographic page, not file 10, 11, ...) and no sorted
    // continuation existed.
    [Fact]
    public async Task HandleAsync_UntaggedBook_DeepProgressBeyondPage_ResumesInFileOrder()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Thinking Better");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        const string bookPath = "/audiobooks/thinking-better";
        List<BaseItem> byFile = Enumerable.Range(1, 14)
            .Select(n => (BaseItem)Chapter(bookPath, n.ToString(), n.ToString(), n.ToString(), indexNumber: null))
            .ToList();
        List<BaseItem> dbOrder = byFile
            .OrderBy(c => c.SortName, StringComparer.Ordinal)
            .ToList();

        // Chapter 9 of the file order in progress at 10 minutes (DB index 11).
        (List<BaseItem> _, Guid _, List<InternalItemsQuery> queries) = SetupBook(
            "Thinking Better", bookPath, dbOrder,
            resumableRow: c => c.Id == byFile[8].Id,
            resumeTicks: TimeSpan.FromMinutes(10).Ticks);

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            // The in-progress chapter launches at its position...
            Assert.Equal(byFile[8].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
            Assert.Equal((int)TimeSpan.FromMinutes(10).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

            // ...and the re-sliced page continues in FILE order (file 10, 11, 12,
            // 13), not the lexicographic DB order (14, 2, 3, 4).
            Assert.Equal(
                byFile.Skip(8).Take(5).Select(c => c.Id),
                session.NowPlayingQueue.Select(q => q.Id));

            // The continuation pages the sorted list from the re-slice boundary
            // (page start 8 + page count 5).
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(13, continuation!.StartIndex);
            Assert.Equal(14, continuation.TotalCount);
            Assert.NotNull(continuation.CachedTracks);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // The tagged-class invariance companion (the 13-of-14 correct books): chapters
    // carry IndexNumber and distinct zero-padded SortName, so the DB order IS the
    // chapter order and the play must stay on the DB paged path: no unpaged fetch
    // (no Limit==null query), no in-memory sorted continuation (CachedTracks null),
    // the classic page-1 continuation values. Green on the pre-JF-790 tree (the
    // baseline) and the regression guard after the fix.
    [Fact]
    public async Task HandleAsync_TaggedBook_StaysOnTheDbPagedPath()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Moonwalking with Einstein");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        const string bookPath = "/audiobooks/moonwalking-with-einstein";
        List<BaseItem> byFile = Enumerable.Range(1, 14)
            .Select(n => (BaseItem)Chapter(
                bookPath,
                n.ToString("00"),
                $"Moonwalking with Einstein - Chapter {n:00}",
                $"0001 - {n:0000} - Moonwalking with Einstein - Chapter {n:00}",
                indexNumber: n))
            .ToList();

        // The DB order equals the file order for the tagged class (the zero-padded
        // SortName derives from the tags).
        (List<BaseItem> _, Guid _, List<InternalItemsQuery> queries) = SetupBook(
            "Moonwalking with Einstein", bookPath, byFile.ToList());

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
            Assert.NotNull(audioDirective);

            Assert.Equal(byFile[0].Id.ToString(), audioDirective!.AudioItem.Stream.Token);
            Assert.Equal(
                byFile.Take(5).Select(c => c.Id),
                session.NowPlayingQueue.Select(q => q.Id));

            // The DB paged path: the initial page query is the ONLY chapters query
            // (no unpaged Limit==null fetch), and the continuation pages the DB.
            Assert.DoesNotContain(queries, q => q.Limit == null);
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(5, continuation!.StartIndex);
            Assert.Equal(14, continuation.TotalCount);
            Assert.Null(continuation.CachedTracks);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }
}
