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
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayBookIntentHandlerTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;

    public PlayBookIntentHandlerTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("PlayBookIntentHandlerTests");

        TestHelpers.EnsurePluginInstance(
            _fx.Config, _fx.LoggerFactory, c => { }, "playbook-tests");
    }

    // JF-535: dispose so the 2s debounce flush runs deterministically at test end (no post-test straggler).
    public void Dispose() => _queueManager.Dispose();

    private sealed class RecordingPlayBookHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        global::Jellyfin.Plugin.AlexaSkill.Alexa.Playback.DeviceQueueManager queueManager)
        : PlayBookIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory, queueManager)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingPlayBookHandler CreateHandler()
    {
        return new RecordingPlayBookHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string? bookName = null)
    {
        var intent = new Intent { Name = IntentNames.PlayBook };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (bookName != null)
        {
            intent.Slots["book"] = new global::Alexa.NET.Request.Slot { Name = "book", Value = bookName };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    [Fact]
    public void CanHandle_PlayBookIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlayAlbumIntent" },
            Locale = "en-US",
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoBookSlot_AsksForBookName()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("book", speech, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_BookNotFound_ReturnsNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Nonexistent Book");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nonexistent book", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_FeatureDisabled_ReturnsFeatureDisabled()
    {
        Plugin.Instance!.Configuration.BooksEnabled = false;
        try
        {
            var handler = CreateHandler();
            var request = CreateIntentRequest(bookName: "The Hobbit");
            var context = _fx.CreateContext();
            var user = _fx.CreateUser();
            var session = CreateSession();

            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            Assert.True(response.Response.ShouldEndSession);
            string speech = TestHelpers.GetSpeechText(response);
            Assert.Contains("disabled", speech, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Plugin.Instance!.Configuration.BooksEnabled = true;
        }
    }

    [Fact]
    public async Task HandleAsync_SingleBookFound_PlaysAudio()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
        Assert.NotNull(audioDirective);
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    // JF-670 review F5: the initial page MUST be the shared builder's output. A
    // hand-edit of the handler query (an IncludeItemTypes reflex, an IndexNumber
    // sort) desyncs head and tail ordering and recreates the mid-book truncation
    // and misorder class; the fetcher-side pins lock the builder itself, this one
    // locks the handler to the builder. User identity rides the same resolution on
    // both sides of the builder and is out of the pin's scope.
    // JF-767 (code-review F3): the head runs the SCOPED sibling (restricted user
    // below), the same JF-666 pairing the tail's FetchAudiobookChapters and the
    // YesIntent PlayBook confirm run, so the direct ask and the confirm cannot
    // diverge on the scope axis. RED on the pre-JF-767 shape: the unscoped builder
    // leaves TopParentIds empty.
    [Fact]
    public async Task PlayBook_InitialPage_UsesSharedChaptersQueryBuilder()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        Guid bookLib = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { bookLib.ToString() });
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        InternalItemsQuery? captured = null;
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(captured);
        InternalItemsQuery expected = QueueContinuationFetcher.BuildAudiobookChaptersQuery(
            null, bookItem.Id, 0, ProgressiveQueueConstants.GetInitialFetchSize());
        Assert.Equal(expected.ParentId, captured!.ParentId);
        Assert.Equal(expected.Recursive, captured.Recursive);
        Assert.Equal(expected.MediaTypes, captured.MediaTypes);
        Assert.Equal(expected.StartIndex, captured.StartIndex);
        Assert.Equal(expected.Limit, captured.Limit);
        Assert.True(
            captured.IncludeItemTypes == null || captured.IncludeItemTypes.Length == 0,
            "head query must not grow an IncludeItemTypes filter the tail does not run");
        // JF-672 supersedes the old no-order assert: head and tail now BOTH carry the
        // shared explicit chapter order (constant equality, the album-arm pin form;
        // the ONE literal honesty pin is ProgressiveQueueTests' builder Fact), so
        // the head still cannot grow an order the tail does not run.
        Assert.Equal(QueueContinuationFetcher.AudiobookChapterOrder, captured.OrderBy);
        // The JF-666 scope the tail and the confirm run under (JF-767).
        Assert.Contains(bookLib, captured.TopParentIds);
    }

    // JF-791 RED PROOF: Jellyfin never types a multi-file book folder as AudioBook
    // (the AudioResolver skips multi-file directory collapsing at v10.11.8 and v12.2,
    // byte-identical; AudioBook : Audio.Audio is a leaf class), so this search's
    // match for a multi-chapter book is a CHAPTER leaf (the live census: the book
    // parents are all plain Folders; the deployed build logged "with 1 tracks" for a
    // 26-chapter book and launched chapter 22 alone). The handler must climb the
    // leaf's ParentId to the book folder, the same climb the VideoApp builders run,
    // and enumerate the BOOK: the pre-fix head query ran on the leaf's own Id,
    // enumerated zero children, and the single-file fallback played the ONE matched
    // chapter (the device's one-chapter-then-silence).
    [Fact]
    public async Task PlayBook_MultiChapterBook_ChapterLeafMatch_ClimbsToBookFolder_AndMintsContinuation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid bookFolderId = Guid.NewGuid();
        // The live entry shape: the search returns CHAPTER leaves, so the flow runs
        // the multi-match disambiguation (HandleFuzzyMiss) BEFORE the climb. The best
        // leaf is the book's own first chapter named exactly like the book, an exact
        // match that auto-accepts; the looser leaf is another book's chapter the
        // SearchTerm also returned. Whichever leaf wins, the climb must re-point it
        // at the BOOK.
        var chapterLeaf = new AudioBook
        {
            Name = "Measure What Matters",
            Id = Guid.NewGuid(),
            ParentId = bookFolderId,
            Path = "/audiobooks/measure-what-matters/book.mp3"
        };
        var looseLeaf = new AudioBook
        {
            Name = "The Upside of Irrationality - Chapter 5",
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Path = "/audiobooks/upside/ch05.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { chapterLeaf, looseLeaf });

        _fx.LibraryManager.Setup(l => l.GetItemById(bookFolderId))
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId });

        // 26 chapters with one initial page of 5 (GetInitialFetchSize): the head
        // query must run on the FOLDER id and see the full count; any other parent
        // (the pre-fix leaf id) enumerates nothing. The Names are zero-padded so
        // the mock's insertion order equals the real server's SortName order
        // (unpadded 'Chapter 10' would sort inside the first page on a real
        // server; the gate-marker's test-realism fix).
        List<BaseItem> chapters = Enumerable.Range(1, 26)
            .Select(i => (BaseItem)new AudioBook
            {
                Name = $"Measure What Matters - Chapter {i:00}",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = $"/audiobooks/measure-what-matters/ch{i:00}.mp3"
            })
            .ToList();
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == bookFolderId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = chapters.Take(ProgressiveQueueConstants.GetInitialFetchSize()).ToArray(),
                    TotalRecordCount = chapters.Count
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            // The book plays from its FIRST chapter, not the matched leaf alone.
            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);

            // The queue carries the BOOK's head page and the continuation the tail
            // fetches; the pre-fix 1-track signature fails both.
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
            Assert.Equal(chapters[0].Id, session.FullNowPlayingItem!.Id);
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal("Audiobook", continuation!.SourceType);
            Assert.Equal(bookFolderId, continuation.ParentId);
            Assert.Equal(chapters.Count, continuation.TotalCount);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), continuation.StartIndex);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-791 companion pin: the single-file AudioBook (empty ParentId) is its own
    // track (the JF-361 shape); the folder climb must not change it.
    [Fact]
    public async Task PlayBook_SingleFileAudioBook_EmptyParentId_PlaysAsOwnTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new AudioBook
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid(),
            Path = "/audiobooks/the-hobbit.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
        Assert.NotNull(audioDirective);
        Assert.Equal(bookItem.Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Single(session.NowPlayingQueue);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
    }

    // JF-791 companion pin (code-review F3): the common single-file layout, the book
    // file inside its OWN folder (a non-empty ParentId that resolves), must keep
    // playing as its own single track under the climb: the paged machinery enumerates
    // the folder's only audio child, the book itself, so the climb is a behavioral
    // no-op for the per-book-folder layout (the pin is change-invariant by design).
    // The shared-container merge shape (sibling single-file books under one parent)
    // is the JF-793 hazard and is deliberately NOT pinned as expected behavior.
    [Fact]
    public async Task PlayBook_SingleFileAudioBook_InOwnFolder_StillPlaysAsOwnTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid ownFolderId = Guid.NewGuid();
        var bookItem = new AudioBook
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid(),
            ParentId = ownFolderId,
            Path = "/audiobooks/the-hobbit/the-hobbit.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemById(ownFolderId))
            .Returns(new Folder { Name = "The Hobbit", Id = ownFolderId });

        // The folder's only audio child is the book itself; any other parent (the
        // leaf's own Id, the no-climb shape) enumerates nothing, the real server's
        // answer for a leaf. The observable outcome is therefore IDENTICAL with and
        // without the climb: the pin is change-invariant by design.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == ownFolderId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = new[] { bookItem },
                    TotalRecordCount = 1
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
        Assert.NotNull(audioDirective);
        Assert.Equal(bookItem.Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Single(session.NowPlayingQueue);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
    }

    // JF-791 companion pin: a FAILED folder resolution (the ParentId does not resolve
    // to a Folder) degrades to the leaf shape, never a failed request.
    [Fact]
    public async Task PlayBook_ChapterLeafMatch_FolderResolutionFails_PlaysLeafAsOwnTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        // ParentId set, but GetItemById returns null (the loose mock's default).
        var chapterLeaf = new AudioBook
        {
            Name = "Measure What Matters - Chapter 22",
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Path = "/audiobooks/measure-what-matters/ch22.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { chapterLeaf });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
        Assert.NotNull(audioDirective);
        Assert.Equal(chapterLeaf.Id.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Single(session.NowPlayingQueue);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
    }

    // JF-673 RED PROOF: on NRE-class servers the chapters page arrives through the
    // SafeGetItemsResult fallback (GetItemList), which cannot know the library
    // total. The pre-fix fallback wrapped the PAGE SIZE as TotalRecordCount, so a
    // FULL initial page read as "complete", the store condition
    // (TotalRecordCount > Items.Count) never fired, and the book truncated at the
    // initial page exactly on the servers the NRE guard exists for. The honest
    // end-unknown total must make the head engage the continuation store with the
    // end-unknown regime so the tail keeps fetching (short page = end, the
    // FetchArtistSongs shape).
    [Fact]
    public async Task PlayBook_NreFallbackFullInitialPage_StoresEndUnknownContinuation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        // The NRE-class server: GetItemsResult (used ONLY by the chapters query on
        // this path; the book search itself goes through GetItemList) throws, and
        // the fallback serves a FULL initial page (5 = GetInitialFetchSize).
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        List<BaseItem> fullPage = Enumerable.Range(0, ProgressiveQueueConstants.GetInitialFetchSize())
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Chapter {i + 1}" })
            .ToList();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == bookItem.Id)))
            .Returns(fullPage);

        try
        {
            await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal("Audiobook", continuation!.SourceType);
            Assert.Equal(bookItem.Id, continuation.ParentId);
            Assert.Equal(SearchService.UnknownTotal, continuation.TotalCount);
            Assert.Equal(fullPage.Count, continuation.StartIndex);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-673 guardrail: under the end-unknown regime a SHORT initial page IS the
    // book's end signal; the head must not store a doomed continuation whose first
    // batch would come back empty and WARN.
    [Fact]
    public async Task PlayBook_NreFallbackShortInitialPage_StoresNoContinuation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        // A 2-chapter book: 2 < 5 = the short page that ends the book.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == bookItem.Id)))
            .Returns(new List<BaseItem>
            {
                new Audio { Id = Guid.NewGuid(), Name = "Chapter 1" },
                new Audio { Id = Guid.NewGuid(), Name = "Chapter 2" }
            });

        try
        {
            await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-673 guardrail: the single-file audiobook shape (zero chapter tracks; the
    // AudioBook item IS the track) must keep working when the fallback reports the
    // end-unknown total instead of 0: the zero-check carries its own end-unknown
    // arm, and the book still plays.
    [Fact]
    public async Task PlayBook_NreFallbackZeroChapters_SingleFileBookStillPlays()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == bookItem.Id)))
            .Returns(new List<BaseItem>());

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            // The single-file path: the book item itself is played as the one track.
            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(bookItem.Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    [Fact]
    public async Task HandleAsync_SingleBookFound_NativeControls_FreshStart_AnnouncesTitle()
    {
        // F2: a fresh-start audiobook via VideoApp (NativeControlsForBooks, no resume position)
        // must announce the book title instead of launching silently.
        _fx.Config.NativeControlsForBooks = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp")));
        // JF-501: the announce rides the progressive-response vehicle; the final launch
        // response carries the directive ONLY.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("The Hobbit"), "progressive announce must speak the book title");
    }

    /// <summary>
    /// JF-501: with the announce toggle OFF the fresh-start audiobook launch must keep
    /// today's silent shape: no progressive announce and no OutputSpeech.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SingleBookFound_NativeControls_AnnounceOff_NoProgressiveAnnounce()
    {
        _fx.Config.NativeControlsForBooks = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        user.AnnounceNowPlaying = false;
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp")));
        Assert.Null(response.Response.OutputSpeech);
        Assert.False(handler.Progressive.Contains("The Hobbit"), "announce off must not send a progressive announce");
    }

    [Fact]
    public async Task HandleAsync_BookWithNoTracks_ReturnsNoContentMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Empty Book");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        // Use a Folder-based item (not Audio) so MediaType != Audio,
        // triggering the "no content" path after our single-file audiobook fix.
        var bookItem = new CollectionFolder
        {
            Name = "Empty Book",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("empty book", speech, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-567: the NativeControlsForBooks sliced-playlist VideoApp resume response must
    /// OMIT shouldEndSession (the repo reference rule for VideoApp.Launch). The old
    /// shape set it true, an incidental copy from the AudioPlayer resume path below
    /// (commit ef30a07a, no incident or pinned test behind it).
    /// </summary>
    [Fact]
    public async Task HandleAsync_BookWithProgress_NativeControls_ResumePlaylist_OmitsShouldEndSession()
    {
        _fx.Config.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("playbook-resume-jf567");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);

        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        // Tracker holds segment 31 (conservative resume position 30 * 10s = 5 min),
        // keyed by the chapter's ParentId fallback (the chapter has no parent here,
        // so the book key is the chapter id itself).
        tracker.RecordSegment(trackItem.Id.ToString(), 31);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));
        // JF-567: omitted from the JSON, so the parsed shape is null.
        Assert.Null(response.Response.ShouldEndSession);
        string? source = videoDirective.VideoItem?.Source;
        Assert.NotNull(source);
        Assert.Contains("start=", source);

        // The resume announce still speaks.
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("resuming", speech, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-693 residuals 2 + 3 (and the JF-687 gate-marker phantom-state item): with an
    /// empty StreamTokenSecret the tracked-resume concat URL is dead at birth, so the
    /// builder answers the JF-687 refusal Tell. That Tell must SURVIVE the handler tail:
    /// the "Riprendo ..." announce never replaces it, it answers in the REQUEST locale
    /// (the it-IT threading, previously unreachable), and the queue/session/continuation
    /// writes never run for a launch that did not happen (no phantom now-playing for
    /// MediaInfo, no stale QueueContinuation).
    /// </summary>
    [Fact]
    public async Task HandleAsync_TrackedResume_EmptySecret_Refuses_NoPhantomState()
    {
        _fx.Config.NativeControlsForBooks = true;
        _fx.Config.StreamTokenSecret = string.Empty;
        var tracker = TestHelpers.CreatePositionTracker("playbook-refusal-jf693");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);

        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.PlayBook,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["book"] = new() { Name = "book", Value = "The Hobbit" }
                }
            },
            Locale = "it-IT",
            RequestId = "test-req"
        };
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        tracker.RecordSegment(trackItem.Id.ToString(), 31);

        // JF-699 item 1: the refusal is the typed exception; RequestPipeline
        // translates it into the localized Tell (pinned at the pipeline level). The
        // handler tail (state writes + announce) never runs on a refusal.
        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => handler.HandleAsync(request, context, user, session, CancellationToken.None));

        // No phantom playback state: MediaInfo keeps answering honestly and no
        // progressive continuation was recorded for the refused launch.
        Assert.Null(session.FullNowPlayingItem);
        Assert.Empty(session.NowPlayingQueue);
        Assert.Null(Jellyfin.Plugin.AlexaSkill.Alexa.QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
    }

    /// <summary>
    /// JF-693 (code-review finding 1): the SUCCESS-path end-state. ApplyBookPlaybackState's
    /// SetQueue runs AFTER the builder recorded the launch in the device ledger (the
    /// refusal-before-phantom-state ordering), so the queue reset must CARRY that record:
    /// a wipe here blinded the JF-632 medium gates (a later sleep-timer or speed ask over
    /// the running book misclassified the medium and re-issued parallel audio).
    /// </summary>
    [Fact]
    public async Task HandleAsync_TrackedResume_LaunchLedgerSurvivesTheQueueReset()
    {
        _fx.Config.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("playbook-ledger-jf693");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);
        using var queueSwap = TestHelpers.SwapPluginQueueManager(_queueManager);

        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        tracker.RecordSegment(trackItem.Id.ToString(), 31);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp")));
        (string? itemId, DeviceQueueManager.LaunchRoute? route) = _queueManager.GetLastPlayedSnapshot(session.DeviceId);
        Assert.Equal(trackItem.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }
}
