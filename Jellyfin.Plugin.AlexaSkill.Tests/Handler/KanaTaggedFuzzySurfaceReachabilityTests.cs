using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-776 section A: the JF-773 album reachability fix's residual family on every
/// OTHER string-level fuzzy surface fed by an always-romanized query. The choke
/// point is <see cref="SearchService.SearchItemsFuzzyAsync"/> (its bounded scan
/// scored the RAW item name against the JF-643 romanized query, so a kana-tagged
/// name was unreachable by both spellings on every consumer: PlayBook,
/// PlayPodcast, PlayVideo, PlayPlaylist, SearchMedia, SeriesFuzzyFallback,
/// PlayChannel, PlayRadio, BrowseLibrary - none has an in-memory index). These
/// pins hold the KeywordMatcher.ScoringName selector at that choke point plus the
/// two site-level legs the filing names (SearchMedia's FuzzyMatch pre-check and
/// the playlist surface's FuzzyMatch / HandleFuzzyMiss / JF-663 bar collision
/// input, which must move TOGETHER with the playlist candidate legs per the
/// JF-755/JF-773 coupling rule) and the HandleFuzzyMiss speechSelector seam
/// adoption (scoring through the reading, speech keeping the display name). The
/// playlist pins prove the coupling in both directions: the kana query needs the
/// choke point AND the bar's reading to accept, and the suffixed kana bait stays
/// the honest refusal. 'サトル' romanizes to 'satoru', 'ヨルニカケル' to
/// 'yorunikakeru' (the JF-755/JF-773 fixtures).
/// </summary>
[Collection("Plugin")]
public class KanaTaggedFuzzySurfaceReachabilityTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private const string KanaBookName = "ヨルニカケル";
    private const string RomajiBookReading = "yorunikakeru";

    private static IntentRequest PlaylistIntent(string playlist, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlayPlaylist };
        intent.Slots = new Dictionary<string, Slot> { ["playlist"] = new Slot { Name = "playlist", Value = playlist } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static IntentRequest BookIntent(string book, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlayBook };
        intent.Slots = new Dictionary<string, Slot> { ["book"] = new Slot { Name = "book", Value = book } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf776-fuzzy-surface-reachability");
        _fx.SetupUserMock();
    }

    // ---------------------------------------------------------------
    // The choke point: SearchItemsFuzzyAsync's bounded candidate scan
    // ---------------------------------------------------------------

    private SearchService CreateSearchService()
        => new(_fx.Config, _fx.LoggerFactory.CreateLogger<SearchService>(), RetryHelper.AlexaRequestTimeoutMs);

    private async Task AssertFuzzyMatchAsync(string query, BaseItem candidate, BaseItemKind kind, string locale = "ja-JP")
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { candidate });
        var search = CreateSearchService();

        var match = await search.SearchItemsFuzzyAsync(
            query,
            jellyfinUser: null,
            user: _fx.CreateUser(),
            libraryManager: libraryManager.Object,
            itemTypes: new[] { kind },
            cancellationToken: CancellationToken.None,
            operationLabel: "JF776ChokePointProbe",
            locale: locale).ConfigureAwait(false);

        Assert.NotNull(match);
        Assert.Equal(candidate.Id, match!.Value.Item.Id);
    }

    [Fact]
    public async Task SearchItemsFuzzyAsync_KanaTaggedCandidate_RomajiQuery_Matches_JF776()
    {
        // The choke-point red proof: the always-romanized query against the RAW
        // kana name scored ~0 on the Latin-script Levenshtein scale (6 katakana
        // vs 12 romaji, no shared characters), so every SearchItemsFuzzyAsync
        // consumer answered not-found for a library whose only item was
        // kana-tagged; the ScoringName selector scores the reading instead.
        SetupPlugin();
        var album = new MusicAlbum { Name = KanaBookName, Id = Guid.NewGuid() };

        await AssertFuzzyMatchAsync(RomajiBookReading, album, BaseItemKind.MusicAlbum);
    }

    [Fact]
    public async Task SearchItemsFuzzyAsync_KanaTaggedCandidate_KanaQuery_Matches_JF776()
    {
        // The mirror direction: the kana query romanizes at entry (the JF-643
        // wiring) and then scores the same reading-side candidate.
        SetupPlugin();
        var album = new MusicAlbum { Name = KanaBookName, Id = Guid.NewGuid() };

        await AssertFuzzyMatchAsync(KanaBookName, album, BaseItemKind.MusicAlbum);
    }

    [Fact]
    public async Task SearchItemsFuzzyAsync_LatinCandidate_LatinControl_Unchanged_JF776()
    {
        // The control pin: the resolver is the identity for kana-free names, so
        // the Latin match is byte-identical (the JF-773 control's
        // SearchService-layer sibling). The query carries full keyword coverage
        // ('abbey road' over 'Abbey Road'): this layer's JF-508/JF-526
        // short-query coverage gate deliberately withholds partial-coverage
        // picks ('abby road'), which is the gate's own pinned behavior, not the
        // selector's.
        SetupPlugin();
        var album = new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() };

        await AssertFuzzyMatchAsync("abbey road", album, BaseItemKind.MusicAlbum, locale: "en-US");
    }

    // ---------------------------------------------------------------
    // The playlist surface (choke point + JF-663 bar coupling)
    // ---------------------------------------------------------------

    private PlayPlaylistIntentHandler CreatePlayHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    private void SetupPlaylistQueries(List<BaseItem>? serverHits, List<BaseItem>? fuzzyScanItems)
    {
        var hits = serverHits ?? new List<BaseItem>();
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = hits, TotalRecordCount = hits.Count });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(fuzzyScanItems ?? new List<BaseItem>());
    }

    private static BaseItem PlaylistFolder(string name)
        => new Folder { Name = name, Id = Guid.NewGuid(), Tags = Array.Empty<string>() };

    [Fact]
    public async Task PlayPlaylist_KanaTaggedPlaylist_KanaQuery_PlaysThroughFuzzyFallback_JF776()
    {
        // The coupling proof (both halves in one red): a kana query whose
        // kana-tagged playlist exists. Pre-fix the choke point's raw-name
        // selector scored ~0 (not-found), and the choke point alone is NOT
        // enough post-fix either; the JF-663 bar reads the collision input,
        // which on the raw kana name is structurally dead (empty Double
        // Metaphone codes), so the bar would refuse the very candidate the fix
        // matched. The bar's input moves in the SAME change as the candidate
        // legs (the JF-755/JF-773 coupling the JF-773 commit message records
        // for the playlist surface's still-raw legs).
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("サトル") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertPlaylistAccepted(response, "ja-JP", "satoru");
    }

    [Fact]
    public async Task PlayPlaylist_KanaTaggedPlaylist_RomajiQuery_PlaysThroughFuzzyFallback_JF776()
    {
        // The romaji direction: no kana provenance, the bar is inert, only the
        // choke point gates the recall (the DoD's romaji leg).
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("サトル") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("satoru"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertPlaylistAccepted(response, "ja-JP", "satoru");
    }

    [Fact]
    public async Task PlayPlaylist_KanaTaggedSuffixedBait_KanaQuery_StillHonestNotFound_JF776()
    {
        // The refusal-survival pin (the filing's suffix-widening bar for the
        // playlist surface): 'サトルデラックス' reading 'satoruderakkusu' (15)
        // sits 9 past the query's 6 against the band's 3, so the coupled bar
        // still refuses the suffixed bait the reading made reachable; the
        // coverage gate withholds it first, the bar refuses it second, and
        // either refusal alone lands the honest not-found.
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("サトルデラックス") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertPlaylistRefusedAsNotFound(response, "ja-JP", "satoru", "サトルデラックス");
    }

    [Fact]
    public void PlaylistKanaBar_KanaTaggedExactReading_Collides_JF776()
    {
        // The bar-coupling unit pin: the playlist bar's collision input resolves
        // through the same reading as the candidate legs. On the raw kana name
        // the leg is structurally dead (the encoder has no kana arm), so this
        // pins the ScoringName input, not just the matcher legs.
        Assert.True(AlbumPlayService.PassesKanaOriginPlaylistAcceptance("satoru", PlaylistFolder("サトル")));
    }

    [Fact]
    public void PlaylistKanaBar_KanaTaggedSuffixWidenedReading_StillRefused_JF776()
    {
        Assert.False(AlbumPlayService.PassesKanaOriginPlaylistAcceptance("satoru", PlaylistFolder("サトルデラックス")));
    }

    [Fact]
    public async Task PlayPlaylist_LatinPlaylist_SameFuzzyFallback_LatinControl_Unchanged_JF776()
    {
        // The Latin control on the same production shape (server miss, fuzzy
        // scan hit): the reading is the identity for kana-free names.
        SetupPlugin();
        SetupPlaylistQueries(serverHits: null, fuzzyScanItems: new List<BaseItem> { PlaylistFolder("Bitoruzu Deluxe") });

        SkillResponse response = await CreatePlayHandler().HandleAsync(
            PlaylistIntent("bitoruzu", "en-US"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertPlaylistAccepted(response, "en-US", "bitoruzu");
    }

    // ---------------------------------------------------------------
    // The audiobook surface (the choke point's PlayBook consumer)
    // ---------------------------------------------------------------

    private DeviceQueueManager? _bookQueueManager;

    private PlayBookIntentHandler CreateBookHandler()
    {
        _bookQueueManager = TestHelpers.CreateDeviceQueueManager("jf776-book-reachability");
        return new PlayBookIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _bookQueueManager);
    }

    private SessionInfo CreateBookSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    private void SetupBookQueries(Audio book, BaseItem track)
    {
        // The exact SearchTerm tier misses (the production shape for a
        // kana-tagged library: Jellyfin's own index cannot match the romanized
        // term to the kana name); the SearchItemsFuzzyAsync 500-row scan
        // (no SearchTerm) returns the book.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.SearchTerm != null
                ? new List<BaseItem>()
                : new List<BaseItem> { book });
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == book.Id)))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { track }, TotalRecordCount = 1 });
    }

    private static (Audio Book, Audio Track) MakeBook(string name)
    {
        var book = new Audio { Name = name, Id = Guid.NewGuid() };
        var track = new Audio { Name = $"{name} chapter 1", Id = Guid.NewGuid() };
        return (book, track);
    }

    [Fact]
    public async Task PlayBook_KanaTaggedBook_KanaQuery_Plays_JF776()
    {
        // The audiobook-surface red proof (the DoD's third leg): the kana query
        // romanizes at entry and the fuzzy fallback's raw-name selector scored
        // the kana name ~0, so the honest NotFoundBook fired with the book in
        // the library; the choke point's reading resolves it.
        SetupPlugin();
        Plugin.Instance!.Configuration.BooksEnabled = true;
        var (book, track) = MakeBook(KanaBookName);
        SetupBookQueries(book, track);

        SkillResponse response = await CreateBookHandler().HandleAsync(
            BookIntent(KanaBookName), _fx.CreateContext(), _fx.CreateUser(), CreateBookSession(), CancellationToken.None);

        // The AudioPlayer directive IS the reachability proof; the launch
        // announce rides the progressive vehicle (the JF-538/JF-693 shape), so
        // the final response is directive-only with a null OutputSpeech.
        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a kana query must reach the kana-tagged book through the fuzzy fallback");
    }

    [Fact]
    public async Task PlayBook_KanaTaggedBook_RomajiQuery_Plays_JF776()
    {
        SetupPlugin();
        Plugin.Instance!.Configuration.BooksEnabled = true;
        var (book, track) = MakeBook(KanaBookName);
        SetupBookQueries(book, track);

        SkillResponse response = await CreateBookHandler().HandleAsync(
            BookIntent(RomajiBookReading), _fx.CreateContext(), _fx.CreateUser(), CreateBookSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a romaji query must reach the kana-tagged book through the fuzzy fallback");
    }

    [Fact]
    public async Task PlayBook_LatinBook_SameFallback_LatinControl_Unchanged_JF776()
    {
        SetupPlugin();
        Plugin.Instance!.Configuration.BooksEnabled = true;
        var (book, track) = MakeBook("The Hobbit");
        SetupBookQueries(book, track);

        SkillResponse response = await CreateBookHandler().HandleAsync(
            BookIntent("the hobbit", "en-US"), _fx.CreateContext(), _fx.CreateUser(), CreateBookSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the Latin control keeps the fuzzy fallback's recall byte-identical");
    }

    public void Dispose()
    {
        _bookQueueManager?.Dispose();
        _fx.LoggerFactory.Dispose();
    }
}
