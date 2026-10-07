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
        global::Jellyfin.Plugin.AlexaSkill.Alexa.Playback.DeviceQueueManager queueManager,
        IArtistIndex? artistIndex = null)
        : PlayBookIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, loggerFactory, queueManager, artistIndex)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingPlayBookHandler CreateHandler(IArtistIndex? artistIndex = null)
    {
        return new RecordingPlayBookHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager,
            artistIndex);
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

    // ========== JF-807: the book ask's Layer-1 warming gate (the ask-side twin of the JF-806 book-confirm gate) ==========

    /// <summary>
    /// The single-book play mocks shared by the JF-807 warming pins (the
    /// HandleAsync_SingleBookFound_PlaysAudio setup), so the pre-fix RED failure
    /// of the entry-gate pin is the clean no-throw (the ungated query ran and
    /// played), not a mock-default null crashing the handler.
    /// </summary>
    private void SetupSingleBookPlay()
    {
        var bookItem = new Audio
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { new Audio { Name = "Chapter 1", Id = Guid.NewGuid() } },
                TotalRecordCount = 1
            });
    }

    /// <summary>
    /// JF-807 RED PROOF (the warming axis, the ASK): a book ask running while the
    /// artist index is still loading (the post-restart window) must refuse at
    /// entry, before the "SearchingBook" announcement and the cold AudioBook
    /// SearchTerm scan, instead of running it. Pre-JF-807 the ask ran its whole
    /// cold surface ungated (the recursive SearchTerm scan, the fuzzy cascade,
    /// the book-folder climbs, and the resolved-book composition) while its own
    /// confirm was already gated (JF-806): the inverted asymmetry this task
    /// closes. Index choice: books have no in-memory index of their own (neither
    /// the artist nor the song n-gram index serves AudioBooks), so the artist
    /// index stands in for the shared cold database (the PlayAlbum Layer-1
    /// precedent) and must match the JF-806 confirm's gate: a song-index
    /// stand-in would make ask and confirm diverge whenever the two indexes'
    /// readiness differs, and the song window outlasts the artist's.
    /// </summary>
    [Fact]
    public async Task HandleAsync_BookAsk_WhileIndexWarming_ThrowsAtEntry()
    {
        var handler = CreateHandler(Mock.Of<IArtistIndex>(i => i.IsReady == false));
        SetupSingleBookPlay();
        _fx.SetupUserMock();

        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(
                CreateIntentRequest(bookName: "The Hobbit"),
                _fx.CreateContext(),
                _fx.CreateUser(),
                CreateSession(),
                CancellationToken.None));
        Assert.StartsWith("artist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-807 companion pin (gate transparency): a READY artist index leaves the
    /// book ask unchanged; the gate only converts the warming window, never the
    /// warm path (the JF-806 confirm-twin idiom,
    /// MusicAlbumConfirm_ReadyIndex_PlaysUnchanged).
    /// </summary>
    [Fact]
    public async Task HandleAsync_BookAsk_ReadyIndex_PlaysUnchanged()
    {
        var handler = CreateHandler(Mock.Of<IArtistIndex>(i => i.IsReady == true));
        SetupSingleBookPlay();
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(bookName: "The Hobbit"),
            _fx.CreateContext(),
            _fx.CreateUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response.Response.Directives?[0] as AudioPlayerPlayDirective);
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// JF-807 placement pin (the gate-ORDER contract): the ask orders the books
    /// gate BEFORE the warming gate, mirroring the JF-806 book confirm's own
    /// order, so the warming+disabled intersection answers the FeatureDisabled
    /// Tell on both sides (the confirm-must-match-ask rule extends to the
    /// warming answer: ask and confirm answer identically in the intersection).
    /// </summary>
    [Fact]
    public async Task HandleAsync_BookAsk_WarmingAndBooksDisabled_AnswersDisabledFirst()
    {
        var handler = CreateHandler(Mock.Of<IArtistIndex>(i => i.IsReady == false));
        SetupSingleBookPlay();

        bool originalBooksEnabled = Plugin.Instance!.Configuration.BooksEnabled;
        Plugin.Instance!.Configuration.BooksEnabled = false;
        try
        {
            SkillResponse response = await handler.HandleAsync(
                CreateIntentRequest(bookName: "The Hobbit"),
                _fx.CreateContext(),
                _fx.CreateUser(),
                CreateSession(),
                CancellationToken.None);

            Assert.True(response.Response.Directives == null || response.Response.Directives.Count == 0,
                "the intersection must answer the disabled Tell, not query or launch");
            Assert.True(response.Response.ShouldEndSession);
            Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Plugin.Instance!.Configuration.BooksEnabled = originalBooksEnabled;
        }
    }

    /// <summary>
    /// JF-807 placement pin (the slot-elicitation hatch): the empty-slot
    /// ElicitBookName Ask sits BEFORE the warming gate, so a slot-less ask
    /// during the warming window still elicits the book name instead of being
    /// converted to the warming refusal. The CLAUDE.md Layer-1 placement
    /// contract fixes the gate before the "searching" announcement and after
    /// the cancel-word escape hatch; PlayBook carries no cancel hatch and no
    /// Dialog.ElicitSlot flow (its elicitation is a plain Ask that sets no
    /// session state), so this pin fixes the remaining open order for this
    /// handler's geometry: the QueryArtistLibrary/AddToQueue elicit-then-gate
    /// majority shape, NOT the PlaySong/PlayAlbum warming-before-elicit
    /// exception (those handlers' elicits are registered Dialog.ElicitSlot
    /// flows whose music-gate interleaving forced that order).
    /// </summary>
    [Fact]
    public async Task HandleAsync_BookAsk_WhileIndexWarming_EmptySlot_StillElicitsBookName()
    {
        var handler = CreateHandler(Mock.Of<IArtistIndex>(i => i.IsReady == false));

        SkillResponse response = await handler.HandleAsync(
            CreateIntentRequest(),
            _fx.CreateContext(),
            _fx.CreateUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        Assert.Contains("book", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Response.ShouldEndSession);
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
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId, Path = "/audiobooks/measure-what-matters" });

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
    // is the JF-793 hazard and now HAS its own pin (the discriminator's reject arm);
    // this pin's folder carries its Path since JF-793 so it also exercises the
    // discriminator's ACCEPT arm (the file sits directly inside the folder).
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
            .Returns(new Folder { Name = "The Hobbit", Id = ownFolderId, Path = "/audiobooks/the-hobbit" });

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

    // JF-793 Finding 2 RED PROOF (the live probe found the shape): the minix census
    // (2026-10-06, 383 AudioBook leaves grouped by ParentId) found 14 parents; 13 are
    // BOOK folders whose every child file sits DIRECTLY inside the parent path, and
    // ONE is the "Audiobooks" library container (/data/media/audiobook/Audiobooks,
    // parentId=None) directly holding 6 COLLAPSED single-file books (The Honest Truth
    // About Dishonesty, two HBR 10 Must Reads volumes, Managing Humans, Power Moves,
    // Radical Candor), each an AudioBook leaf whose file sits one directory DEEPER
    // than the container (the resolver collapsed each single-file directory, so the
    // leaf's ParentId is the folder ABOVE its own book folder). The unconditional
    // JF-791 climb merges all of them (worse: the recursive chapters query
    // enumerates the WHOLE library) into one queue. The discriminator: a leaf whose
    // file does not sit directly inside the resolved parent means the parent is a
    // shared container, not a book folder: keep the leaf (the JF-361 duality, the
    // collapsed single-file book IS the book and plays as its own track).
    [Fact]
    public async Task PlayBook_CollapsedSingleFileBook_UnderSharedContainer_PlaysAsOwnTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Managing Humans");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Managing Humans: Biting and Humorous Tales of a Software Engineering Manager",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            // The collapsed shape: the file sits in its OWN subfolder below the shared
            // container (the live path shape from the census).
            Path = "/audiobooks/Managing Humans/Audiobook - Managing_Humans.m4b"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { book });

        // The container resolves (a plain Folder with its live path); its recursive
        // enumeration would return the sibling single-file books (and, on the real
        // server, every chapter of every book in the library): the merge the
        // discriminator must prevent.
        _fx.LibraryManager.Setup(l => l.GetItemById(containerId))
            .Returns(new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" });
        List<BaseItem> siblings = new()
        {
            book,
            new AudioBook
            {
                Name = "Radical Candor",
                Id = Guid.NewGuid(),
                ParentId = containerId,
                Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
            },
            new AudioBook
            {
                Name = "Power Moves: Lessons from Davos",
                Id = Guid.NewGuid(),
                ParentId = containerId,
                Path = "/audiobooks/Adam Grant - Power Moves/Power Moves.mp3"
            }
        };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == containerId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = siblings.ToArray(),
                    TotalRecordCount = siblings.Count
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            // The ONE book plays as its own track: no container merge, no continuation.
            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(book.Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Single(session.NowPlayingQueue);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-794 RED PROOF (the flag-on twin of the test above, the defect this task
    // closes): with NativeControlsForBooks ON, the fresh launch routed through
    // BuildAudiobookVideoAppLaunchResponseAsync, whose BuildVideoAppAudioResponse
    // climbed the RAW ParentId, so the census shape minted
    // audiobook/{containerId} and the VideoApp player would have served every
    // sibling book as one seek-bar timeline. The builders now share the ONE
    // JF-793 discriminator, so the collapsed book launches through the
    // SINGLE-ITEM video-audio endpoint keyed by its own leaf id (the same shape a
    // root-level single-file book gets), never the container concat.
    [Fact]
    public async Task PlayBook_CollapsedSingleFileBook_UnderSharedContainer_NativeControls_PlaysSingleItemLaunch()
    {
        _fx.Config.NativeControlsForBooks = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Managing Humans");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Managing Humans: Biting and Humorous Tales of a Software Engineering Manager",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            // The collapsed shape: the file sits in its OWN subfolder below the shared
            // container (the live path shape from the census).
            Path = "/audiobooks/Managing Humans/Audiobook - Managing_Humans.m4b"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { book });

        _fx.LibraryManager.Setup(l => l.GetItemById(containerId))
            .Returns(new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" });

        // The chapters query under the CONTAINER would enumerate the sibling
        // single-file books (and, on the real server, every chapter of every book
        // in the library): the merge the discriminator must prevent. Under the
        // leaf's own id the real server enumerates nothing (the no-climb shape),
        // which the else branch models.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == containerId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = new[]
                    {
                        book,
                        new AudioBook
                        {
                            Name = "Radical Candor",
                            Id = Guid.NewGuid(),
                            ParentId = containerId,
                            Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
                        }
                    },
                    TotalRecordCount = 2
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));
        Assert.NotNull(videoDirective.VideoItem?.Source);
        Assert.Contains($"alexaskill/api/video-audio/{book.Id}/stream.m3u8", videoDirective.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"audiobook/{containerId}", videoDirective.VideoItem.Source, StringComparison.Ordinal);
    }

    // Gate-marker tail F1 pins: the discriminator FAILS CLOSED. The earlier
    // fail-open returned true (the climb) for every shape it could not verify,
    // so a Path-less or bare-filename leaf under a shared ParentId container
    // reproduced the exact library-merge hazard finding 2 closed through the
    // unverifiable shapes. The unverifiable layout plays the leaf alone (the
    // pre-JF-791 behavior), never the merged container. RED on the pre-tail
    // tree: the climb fires and the queue swallows the siblings.
    [Fact]
    public async Task PlayBook_PathlessLeaf_UnderSharedContainer_FailsClosedToOwnTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Managing Humans");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Managing Humans: Biting and Humorous Tales of a Software Engineering Manager",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            Path = null
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { book });

        _fx.LibraryManager.Setup(l => l.GetItemById(containerId))
            .Returns(new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" });
        List<BaseItem> siblings = new()
        {
            book,
            new AudioBook
            {
                Name = "Radical Candor",
                Id = Guid.NewGuid(),
                ParentId = containerId,
                Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
            }
        };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == containerId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = siblings.ToArray(),
                    TotalRecordCount = siblings.Count
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(book.Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Single(session.NowPlayingQueue);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    [Fact]
    public async Task PlayBook_BareFilenameLeaf_UnderSharedContainer_FailsClosedToOwnTrack()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Managing Humans");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Managing Humans: Biting and Humorous Tales of a Software Engineering Manager",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            Path = "Audiobook - Managing_Humans.m4b"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { book });

        _fx.LibraryManager.Setup(l => l.GetItemById(containerId))
            .Returns(new Folder { Name = "Audiobooks", Id = containerId, Path = "/audiobooks" });
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == containerId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = new List<BaseItem>
                    {
                        book,
                        new AudioBook
                        {
                            Name = "Radical Candor",
                            Id = Guid.NewGuid(),
                            ParentId = containerId,
                            Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
                        }
                    }.ToArray(),
                    TotalRecordCount = 2
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(book.Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Single(session.NowPlayingQueue);
            Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-793 Finding 2 companion pin (the accept arm): a multi-chapter book whose
    // folder carries its path (the live 13-of-14 census shape) still climbs: the
    // chapter file sits DIRECTLY inside the book folder. Locks the discriminator's
    // true-negative so the shared-container rejection cannot overfire onto the
    // JF-791 fix itself.
    [Fact]
    public async Task PlayBook_ChapterLeaf_InPathedBookFolder_StillClimbsToBookFolder()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

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

        // The book folder with its live path; the chapter file sits directly inside it.
        _fx.LibraryManager.Setup(l => l.GetItemById(bookFolderId))
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId, Path = "/audiobooks/measure-what-matters" });

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

            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // JF-793 Finding 3 RED PROOF: the disambiguation must present BOOK-granular
    // choices. A multi-chapter book's search candidates are N chapter leaves (the
    // JF-791 shape), so the multi-match prompt presented chapter-granular entries
    // ("... - Chapter 01" vs "... - Chapter 02" of the SAME book) whose confirm
    // payload was a chapter leaf id: an illusory choice, since the climb then plays
    // the whole book from chapter 1 whichever entry wins (and the "- Chapter N"
    // name tails drag the fuzzy scores below the >= 90 auto-play bar the folder
    // name clears exactly). The candidate set must be normalized through
    // TryResolveBookFolder and deduped by folder id BEFORE the disambiguation
    // consumers; single-file books pass through unchanged.
    [Fact]
    public async Task PlayBook_MultiMatchDisambiguation_PresentsBookGranularChoices()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "zzzqqq");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid bookFolderId = Guid.NewGuid();
        var chapter1 = new AudioBook
        {
            Name = "Measure What Matters - Chapter 01",
            Id = Guid.NewGuid(),
            ParentId = bookFolderId,
            Path = "/audiobooks/measure-what-matters/ch01.mp3"
        };
        var chapter2 = new AudioBook
        {
            Name = "Measure What Matters - Chapter 02",
            Id = Guid.NewGuid(),
            ParentId = bookFolderId,
            Path = "/audiobooks/measure-what-matters/ch02.mp3"
        };
        var singleFileBook = new AudioBook
        {
            Name = "The Manager's Path",
            Id = Guid.NewGuid(),
            Path = "/audiobooks/managers-path.m4b"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { chapter1, chapter2, singleFileBook });

        _fx.LibraryManager.Setup(l => l.GetItemById(bookFolderId))
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId, Path = "/audiobooks/measure-what-matters" });

        // The ask path returns before the head chapters query.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The disambiguation state carries ONE entry per BOOK, named for the book,
        // with the FOLDER id as the confirm payload: no chapter-granular entries.
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.TryGetValue("disambig_matches", out object? matchesObj));
        var matches = Newtonsoft.Json.JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(
            (string)matchesObj!);
        Assert.NotNull(matches);
        Assert.Equal(2, matches!.Count);
        Assert.Equal("Measure What Matters", matches[0].Name);
        Assert.Equal(bookFolderId.ToString(), matches[0].Id);
        Assert.Equal("The Manager's Path", matches[1].Name);
        Assert.DoesNotContain("Chapter", string.Join("|", matches.Select(m => m.Name)), StringComparison.Ordinal);
    }

    // JF-793 code-review F2 RED PROOF: the no-collapse shape. Chapter leaves of TWO
    // DISTINCT books normalize onto two distinct folders, the count is preserved,
    // and the pre-fix pass-through returned the ORIGINAL leaves: the prompt stayed
    // chapter-granular ('Book Alpha - Chapter 03'), the confirm payload stayed a
    // leaf id, and the '- Chapter N' tails kept dragging the fuzzy scores below the
    // >= 90 auto-play bar - both finding-3 defects surviving exactly when no dedup
    // collapsed anything. Normalization must replace, not only dedup.
    [Fact]
    public async Task PlayBook_MultiMatchDisambiguation_DistinctBooks_PresentFolderGranularChoices()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "zzzqqq");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid alphaFolderId = Guid.NewGuid();
        Guid betaFolderId = Guid.NewGuid();
        var alphaLeaf = new AudioBook
        {
            Name = "Book Alpha - Chapter 03",
            Id = Guid.NewGuid(),
            ParentId = alphaFolderId,
            Path = "/audiobooks/book-alpha/ch03.mp3"
        };
        var betaLeaf = new AudioBook
        {
            Name = "Book Beta - Chapter 09",
            Id = Guid.NewGuid(),
            ParentId = betaFolderId,
            Path = "/audiobooks/book-beta/ch09.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { alphaLeaf, betaLeaf });

        _fx.LibraryManager.Setup(l => l.GetItemById(alphaFolderId))
            .Returns(new Folder { Name = "Book Alpha", Id = alphaFolderId, Path = "/audiobooks/book-alpha" });
        _fx.LibraryManager.Setup(l => l.GetItemById(betaFolderId))
            .Returns(new Folder { Name = "Book Beta", Id = betaFolderId, Path = "/audiobooks/book-beta" });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.TryGetValue("disambig_matches", out object? matchesObj));
        var matches = Newtonsoft.Json.JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(
            (string)matchesObj!);
        Assert.NotNull(matches);
        Assert.Equal(2, matches!.Count);
        Assert.Equal("Book Alpha", matches[0].Name);
        Assert.Equal(alphaFolderId.ToString(), matches[0].Id);
        Assert.Equal("Book Beta", matches[1].Name);
        Assert.Equal(betaFolderId.ToString(), matches[1].Id);
        Assert.DoesNotContain("Chapter", string.Join("|", matches.Select(m => m.Name)), StringComparison.Ordinal);
    }

    // JF-793 code-review F4 companion pin: the same-book collapse. Chapter leaves
    // of ONE book normalize to a single folder entry, the count drops to 1, and the
    // disambiguation block is skipped entirely in favor of a direct play: the book
    // auto-plays from its first chapter with the paged queue and continuation, no
    // spurious choice prompt (and no disambiguation state left behind).
    [Fact]
    public async Task PlayBook_SameBookChapterLeaves_CollapseToDirectPlay_NoDisambiguationPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Measure What Matters");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid bookFolderId = Guid.NewGuid();
        List<BaseItem> chapterLeaves = Enumerable.Range(1, 2)
            .Select(i => (BaseItem)new AudioBook
            {
                Name = $"Measure What Matters - Chapter {i:00}",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = $"/audiobooks/measure-what-matters/ch{i:00}.mp3"
            })
            .ToList();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(chapterLeaves);

        _fx.LibraryManager.Setup(l => l.GetItemById(bookFolderId))
            .Returns(new Folder { Name = "Measure What Matters", Id = bookFolderId, Path = "/audiobooks/measure-what-matters" });

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

            var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
            Assert.NotNull(audioDirective);
            Assert.Equal(chapters[0].Id.ToString(), audioDirective.AudioItem.Stream.Token);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);
            QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!);
            Assert.NotNull(continuation);
            Assert.Equal(bookFolderId, continuation!.ParentId);
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
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


    /// <summary>
    /// JF-794: the VERIFIED multi-chapter book fixture the flag-on concat arms demand
    /// (the chapter files sit DIRECTLY inside the resolved folder, so the builders'
    /// shared discriminator accepts the climb and the concat mints under the folder
    /// id). Wires the AudioBook search, the folder resolution, and the chapters page;
    /// the caller warms the tracker under the FOLDER key (GetAudiobookBookKey of any
    /// chapter = the folder id).
    /// </summary>
    private (Folder BookFolder, AudioBook Chapter) SetupVerifiedHobbitBook()
    {
        Guid folderId = Guid.NewGuid();
        var searchLeaf = new AudioBook
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid(),
            ParentId = folderId,
            Path = "/audiobooks/the-hobbit/ch00.mp3"
        };
        var chapter = new AudioBook
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            ParentId = folderId,
            Path = "/audiobooks/the-hobbit/ch01.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { searchLeaf });

        _fx.LibraryManager.Setup(l => l.GetItemById(folderId))
            .Returns(new Folder { Name = "The Hobbit", Id = folderId, Path = "/audiobooks/the-hobbit" });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == folderId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = new[] { chapter },
                    TotalRecordCount = 1
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        return (new Folder { Name = "The Hobbit", Id = folderId, Path = "/audiobooks/the-hobbit" }, chapter);
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

        // JF-794: the verified book-folder fixture (the pre-fix bare-Audio shape no
        // longer concats: its unverified climb degrades to the flat resume).
        var (bookFolder, trackItem) = SetupVerifiedHobbitBook();

        // Tracker holds segment 31 (conservative resume position 30 * 10s = 5 min),
        // keyed by the book FOLDER id (GetAudiobookBookKey of the chapter).
        tracker.RecordSegment(bookFolder.Id.ToString(), 31);

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

        // JF-794: the verified book-folder fixture (the pre-fix bare-Audio shape no
        // longer concats: its unverified climb degrades to the flat resume).
        var (bookFolder, trackItem) = SetupVerifiedHobbitBook();

        tracker.RecordSegment(bookFolder.Id.ToString(), 31);

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

        // JF-794: the verified book-folder fixture (the pre-fix bare-Audio shape no
        // longer concats: its unverified climb degrades to the flat resume).
        var (bookFolder, trackItem) = SetupVerifiedHobbitBook();

        tracker.RecordSegment(bookFolder.Id.ToString(), 31);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp")));
        (string? itemId, DeviceQueueManager.LaunchRoute? route) = _queueManager.GetLastPlayedSnapshot(session.DeviceId);
        Assert.Equal(trackItem.Id.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
    }

    // JF-794 gate-marker BLOCKER 2 (the read side of the cross-book bleed): the
    // tracked-resume read resolves the ONE verdict-aware key, so a collapsed census
    // book reads its OWN leaf key - not the shared container key a sibling book's
    // listening wrote. RED on the pre-blocker tree: the read used the raw
    // GetAudiobookBookKey (the container), so this pin saw the sibling's 5-minute
    // mark instead of the book's own 50-second position.
    [Fact]
    public async Task PlayBook_CollapsedBook_TrackedResume_ReadsOwnLeafKeyNotTheSharedContainerKey()
    {
        _fx.Config.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("playbook-crossbook-jf794");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);

        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Radical Candor");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        Guid containerId = Guid.NewGuid();
        var book = new AudioBook
        {
            Name = "Radical Candor",
            Id = Guid.NewGuid(),
            ParentId = containerId,
            RunTimeTicks = TimeSpan.FromMinutes(20).Ticks,
            Path = "/audiobooks/Radical Candor/Radical Candor.m4b"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { book });

        _fx.SetupBookFolder(containerId, "Audiobooks", "/audiobooks");

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        // The sibling book listened 5 minutes (its mark under the SHARED container
        // key, the pre-blocker blend); THIS book listened 50 seconds (its own leaf
        // key, the verdict-aware shape the record gate now writes).
        tracker.RecordSegment(containerId.ToString(), 31);
        tracker.RecordSegment(book.Id.ToString(), 6);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The tracked arm fired (the leaf key is warm) and the flat chapter resume
        // carries THIS book's 50-second position - not the sibling's 5 minutes.
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal((int)TimeSpan.FromSeconds(50).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(book.Id.ToString(), audioDirective.AudioItem.Stream.Token);
    }

    // Code-review F4 (the JF-794 round): the METADATA-REMAPPED book shape - the
    // chapters query returns Audio-typed rows (the JF-784 leg-3 row set) - has a
    // deliberately ASYMMETRIC behavior this pin holds in place while JF-799 decides
    // the unification: the fresh arm's isAudioBook gate serves such chapters as
    // single items, but the tracked-resume builder's climb is TYPE-AGNOSTIC, so a
    // warm folder key resumes the WHOLE-BOOK concat. Without this pin a future
    // AudioBook gate on BuildAudiobookResumeResponse compiles green while silently
    // deleting the remapped book's seek-bar resume.
    [Fact]
    public async Task PlayBook_TrackedResume_AudioTypedRemapChapter_ResumesViaFolderConcat()
    {
        _fx.Config.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("playbook-remap-jf794");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);

        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        // The remap shape: the search still returns an AudioBook leaf (so the head
        // climb resolves the folder), but the chapters page under the folder yields
        // an Audio-TYPED row (the metadata-remap the concat endpoint's
        // MediaTypes=Audio row set exists for).
        Guid folderId = Guid.NewGuid();
        var searchLeaf = new AudioBook
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid(),
            ParentId = folderId,
            Path = "/audiobooks/the-hobbit/ch00.mp3"
        };
        var remapChapter = new Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            ParentId = folderId,
            Path = "/audiobooks/the-hobbit/ch01.mp3"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { searchLeaf });

        _fx.SetupBookFolder(folderId, "The Hobbit", "/audiobooks/the-hobbit");

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ParentId == folderId
                ? new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = new[] { remapChapter },
                    TotalRecordCount = 1
                }
                : new MediaBrowser.Model.Querying.QueryResult<BaseItem>
                {
                    Items = Array.Empty<BaseItem>(),
                    TotalRecordCount = 0
                });

        // The tracker is warm under the FOLDER key (GetAudiobookBookKey of the
        // Audio-typed chapter is still its ParentId): segment 31 = a conservative
        // 5-minute book-timeline position.
        tracker.RecordSegment(folderId.ToString(), 31);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
            Assert.Single(response.Response.Directives));
        Assert.Contains(
            $"alexaskill/api/video-audio/audiobook/{folderId}/stream.m3u8?start={TimeSpan.FromMinutes(5).Ticks}&token=",
            videoDirective.VideoItem!.Source,
            StringComparison.Ordinal);
    }
}
