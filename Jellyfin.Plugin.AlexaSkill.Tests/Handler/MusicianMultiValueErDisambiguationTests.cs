using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-690: the multi-value ER arbitration. The JF-684 shared-first-word catalog
/// synonyms make a spoken word match SEVERAL catalog entries (live 2026-10-01,
/// catalog v1199: "pink" -> ER_SUCCESS [P!nk, Pink Floyd], both real library
/// artists). The plain JF-659 canonical read keeps only Amazon's rank #1 and
/// the exact-name hit auto-plays through the JF-420.1 equality bypass with no
/// prompt; these suites pin the gate that asks which artist to play when the
/// ER ambiguity is REAL in the library, the JF-884 variant-canonical collapse
/// that auto-plays the single resolved artist instead, the JF-885 rank-1
/// dominance bypass that skips the ask when the rendition is decisively
/// rank-1's ask, and the unchanged cold paths around them.
/// </summary>
[Collection("Plugin")]
public class MusicianMultiValueErDisambiguationTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private BaseItem Pnk() => new MusicArtist { Name = "P!nk", Id = Guid.NewGuid() };
    private BaseItem PinkFloyd() => new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
    private BaseItem Abba() => new MusicArtist { Name = "ABBA", Id = Guid.NewGuid() };
    private BaseItem PinkMartini() => new MusicArtist { Name = "Pink Martini", Id = Guid.NewGuid() };
    private BaseItem PinkFairies() => new MusicArtist { Name = "Pink Fairies", Id = Guid.NewGuid() };
    private BaseItem KingFloyd() => new MusicArtist { Name = "King Floyd", Id = Guid.NewGuid() };

    private static IArtistIndex IndexOf(params BaseItem[] artists)
        => new FakeArtistIndex(artists, FakeArtistIndex.CodesFromArtistNames(artists));

    private PlaySongIntentHandler CreateSongHandler(IArtistIndex? index)
        => new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    private PlayArtistSongsIntentHandler CreateArtistHandler(IArtistIndex index)
        => new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    // JF-715: delegates to the ONE shared builder (TestHelpers.CreatePlaySongIntent,
    // the CountingArtistIndex hoist convention) keeping this suite's local name.
    private static IntentRequest CreateSongIntent(string song, Slot musicianSlot, string locale = "it-IT")
        => TestHelpers.CreatePlaySongIntent(song, musicianSlot, locale);

    private static IntentRequest CreateArtistIntent(Slot musicianSlot, string locale = "it-IT")
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = musicianSlot };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    /// <summary>
    /// Library mock for the legs that PLAY: the artist-scoped songs query
    /// (ArtistIds + Audio) returns the given songs; everything else empty.
    /// </summary>
    private void SetupArtistSongs(params Audio[] songs)
    {
        // Delegates to the capturing helper (code-review dedupe): one mock for
        // the library-manager routing; the capture list is simply ignored.
        _ = SetupArtistSongsCapturingQueries(songs);
    }

    // ---------------------------------------------------------------
    // The multi-value shape: both ER candidates real in the library
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_MultiValueEr_BothInLibrary_AsksWhichArtist()
    {
        // The live shape (2026-10-01): "suona la musica di pink" selects
        // PlaySong with song="la musica" (the JF-697 generic-word leg) and
        // musician ER [P!nk, Pink Floyd]. Pre-JF-690 the generic-word fallback
        // silently played Amazon's rank #1; the gate must ask instead, before
        // the artist search is even paid for.
        // JF-885 non-regression, pinned scores (measured 2026-10-13): the
        // rendition "pink" scores 75 against rank-1 "P!nk" and 90 against
        // "Pink Floyd", a margin of -15, far below the JF-420 bar, so the
        // rank-1 dominance bypass does not fire and the ask stays.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Just Like a Pill", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateSongHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_BothInLibrary_AsksWhichArtist()
    {
        // The carrier form of the same evidence ("suona la cantante pink"
        // selects PlayArtistSongs directly): the gap the task file verified in
        // code, the JF-420.1 equality-bypass auto-play of the rank #1 hit.
        // JF-885 non-regression (pinned scores as in the PlaySong twin above:
        // "pink" scores 75 vs 90, margin -15): the dominance bypass does not
        // fire and the ask stays.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pnk, floyd);
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_FourInLibrary_SpeaksThree_CyclesToTheFourth()
    {
        // JF-707: the long first-word family. JF-684's shared-first-word
        // synonyms can resolve 4-5 same-first-word artists from one spoken
        // word, and the pre-cap ask spoke every name in one breath over a
        // string designed for the two-name shape. The ask now speaks the top
        // MultipleArtistsSpeakCap names while the cycling state keeps all
        // four, so the unspoken rank stays reachable: the third "no" asks
        // about it by name.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var martini = PinkMartini();
        var fairies = PinkFairies();
        var song = new Audio { Name = "Get It Up", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(pnk, floyd, martini, fairies));
        SkillResponse ask = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd", "Pink Martini", "Pink Fairies")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Cap-aware oracle: the top three ER ranks are spoken, the fourth is
        // not, and the session state keeps the FULL resolved list.
        TestHelpers.AssertMultiArtistAsk(ask, pnk, floyd, martini, fairies);

        // "no" walks the list; the third advance reaches and NAMES the fourth
        // artist the initial breath never spoke.
        Dictionary<string, object>? attrs = await AdvanceDisambiguationAsync(
            ask, floyd.Name, martini.Name, fairies.Name);

        // The terminal leg: the fourth "no" exhausts the FULL stored list and
        // ends the flow with NoMoreMatches. Pinned here because every
        // pre-existing exhaustion pin covers only the below-cap two-name shape.
        SkillResponse exhausted = await new NoIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory)
            .HandleAsync(
                CreateNoIntentRequest(), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);
        Assert.True(exhausted.Response.ShouldEndSession, "exhaustion ends the session");
        Assert.Equal(
            Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("NoMoreMatches", "it-IT"),
            TestHelpers.GetSpeechText(exhausted));
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_FourInLibrary_YesAtUnspokenRank_PlaysIt()
    {
        // JF-707 gate-marker rework (F1): the READ side of the full-state
        // contract. The ask speaks only the top MultipleArtistsSpeakCap names,
        // so a confirm at a cycled index BEYOND the cap is exactly the leg a
        // future "harmonization" (a Take at the stored-list consumer) would
        // silently break while every spoken-list pin stays green: "yes" at the
        // unspoken fourth rank must still resolve and PLAY that artist.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var martini = PinkMartini();
        var fairies = PinkFairies();
        var song = new Audio { Name = "Between the Lines", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        var playQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                playQueries.Add(q);
                return q.ArtistIds != null && q.ArtistIds.Length > 0
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>();
            });

        // The yes leg resolves the candidate by id (the PlayAlbumIntentHandler
        // yes-pin construction): GetItemById serves the artist.
        _fx.LibraryManager.Setup(l => l.GetItemById(fairies.Id)).Returns(fairies);

        var handler = CreateArtistHandler(IndexOf(pnk, floyd, martini, fairies));
        SkillResponse ask = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd", "Pink Martini", "Pink Fairies")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);
        TestHelpers.AssertMultiArtistAsk(ask, pnk, floyd, martini, fairies);

        Dictionary<string, object>? attrs = await AdvanceDisambiguationAsync(
            ask, floyd.Name, martini.Name, fairies.Name);

        var yesHandler = new YesIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        var session = _fx.CreateSession();
        SkillResponse confirmed = await yesHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonYes }, Locale = "it-IT", RequestId = "test-yes" },
            _fx.CreateContext(), _fx.CreateUser(), session, attrs, CancellationToken.None);

        // The play is scoped to the UNSPOKEN fourth artist, not to any of the
        // spoken three, and the play itself is delivered (JF-299: session ends).
        Assert.Contains(playQueries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == fairies.Id);
        Assert.DoesNotContain(playQueries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == pnk.Id);
        var playDirective = TestHelpers.GetPlayDirective(confirmed);
        Assert.NotNull(playDirective);
        Assert.Equal(song.Id.ToString(), playDirective!.AudioItem.Stream.Token);
        Assert.True(confirmed.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    /// <summary>
    /// Drives NoIntentHandler through one "no" advance per given name over the
    /// ask's session state, asserting each advance keeps the session open and
    /// speaks the next candidate's name. Returns the session attributes to
    /// feed the NEXT turn (a further "no" for the exhaustion leg, or a "yes"
    /// for the confirm-at-cycled-index pin).
    /// </summary>
    private async Task<Dictionary<string, object>?> AdvanceDisambiguationAsync(SkillResponse ask, params string[] spokenOnAdvance)
    {
        var noHandler = new NoIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        Dictionary<string, object>? attrs = ask.SessionAttributes;
        foreach (string expectedName in spokenOnAdvance)
        {
            SkillResponse next = await noHandler.HandleAsync(
                CreateNoIntentRequest(), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), attrs, CancellationToken.None);
            TestHelpers.AssertSessionOpen(next, "each cycling advance keeps the session open");
            Assert.Contains(expectedName, TestHelpers.GetSpeechText(next), StringComparison.OrdinalIgnoreCase);
            attrs = next.SessionAttributes;
        }

        return attrs;
    }

    private static IntentRequest CreateNoIntentRequest() => new()
    {
        Intent = new Intent { Name = IntentNames.AmazonNo },
        Locale = "it-IT",
        RequestId = "test-no"
    };

    // ---------------------------------------------------------------
    // The single-value control: the ordinary ER shape is unchanged
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_SingleValueEr_PlaysDirectly_NoAsk()
    {
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateSongHandler(IndexOf(floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("la musica", TestHelpers.ResolvedSlot("pink floyd", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        TestHelpers.AssertNoDisambiguationState(response);
    }

    [Fact]
    public async Task PlayArtistSongs_SingleValueEr_ExactNameWithContainmentRival_AutoPlays_JF420p1()
    {
        // The JF-420.1 equality bypass through the ER path: the canonical IS
        // exactly the matched artist's name ("Soul Coughing") while a longer
        // containment rival ("Soul Coughing & Roni Size") also sits in the
        // library. JF-884: the single-value gate now resolves the canonical and
        // ADOPTS the exact hit (the same auto-play the closed gate's search
        // delivered); only a list resolving to DISTINCT artists may prompt.
        var soulCoughing = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var rival = new MusicArtist { Name = "Soul Coughing & Roni Size", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Super Bon Bon", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(soulCoughing, rival));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlot("soul coughing", "Soul Coughing")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true, "the exact-name hit auto-plays (JF-420.1)");
        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    // ---------------------------------------------------------------
    // Stale catalog: only ONE ER candidate exists in the library
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_OnlyRank2Resolves_NoAsk_PlaysTheSurvivor()
    {
        // Rank #1 is stale (artist left the library, weekly sync lag): the
        // ambiguity is NOT real in this library, so no two-name prompt (naming a
        // dead artist would offer a dead end), and the gate does NOT throw its
        // answer away: the single proven library artist plays (code-review
        // finding, applied; pre-change this leg searched the stale rank-#1
        // canonical and answered not-found with the survivor sitting resolved).
        var abba = Abba();
        var song = new Audio { Name = "Dancing Queen", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true);
        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_OnlyRank1Resolves_NoAsk_PlaysIt()
    {
        // The mirror leg: when the SOLE library-resident candidate is rank #1
        // itself, the gate stays closed and the exact hit plays as before
        // (the JF-420.1 shape wearing a multi-value ER list).
        var abba = Abba();
        var song = new Audio { Name = "Fernando", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(abba));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink", "ABBA", "P!nk")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true);
        TestHelpers.AssertNoDisambiguationState(response);
    }

    // ---------------------------------------------------------------
    // PlaySong scope: the gate only arbitrates the generic-music-word shape
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_RealSongTitle_MultiValueEr_NoAsk_PlaysTheTitleScopedToRank1()
    {
        // A REAL song title ("money") plus a multi-value musician keeps today's
        // path: the confirm leg cannot preserve the song constraint, so asking
        // here would drop the explicitly requested song (code-review finding).
        // The title search runs scoped to Amazon's rank #1 artist as before.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var money = new Audio { Name = "Money", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        var artistScopedQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    artistScopedQueries.Add(q);
                    return new List<BaseItem> { money };
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler(IndexOf(pnk, floyd));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("money", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Equal(money.Id, session.NowPlayingQueue![0].Id);
        // The song scope carried ONLY rank #1 (today's behavior for real titles).
        Assert.Contains(artistScopedQueries, q => q.SearchTerm != null && q.ArtistIds!.Length == 1 && q.ArtistIds[0] == pnk.Id);
    }

    [Fact]
    public async Task PlaySong_MultiValueEr_OnlyRank2Resolves_GenericWord_PlaysTheSurvivor()
    {
        // The PlaySong mirror of the survivor leg: a generic music word plus a
        // multi-value musician that collapses to one library artist plays that
        // artist directly (the stale rank-#1 canonical is never searched).
        var abba = Abba();
        var song = new Audio { Name = "Dancing Queen", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateSongHandler(IndexOf(abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "ABBA")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    // ---------------------------------------------------------------
    // Session-state hygiene: the ask supersedes a stale cross-media offer
    // ---------------------------------------------------------------

    [Fact]
    public void AskMultipleArtists_MarksStaleCrossmediaDeclineKeysForRemoval()
    {
        // A cross-media artist offer left unanswered in the same session used to
        // merge its crossmedia_notfound_* keys onto a later plain artist ask
        // (they live in DisambiguationKeys, so MarkOthersInactive skipped them),
        // making "no" answer the OLD song/album not-found instead of cycling.
        // The plain ask builders must supersede them (code-review finding).
        SkillResponse response = DisambiguationHelper.AskMultipleArtists(
            new List<DisambiguationHelper.MatchInfo> { new() { Id = Guid.NewGuid().ToString(), Name = "ABBA" } },
            "it-IT");

        var removal = response.SessionAttributes?.GetValueOrDefault(SessionAttributeRemoval.MarkerKey) as IEnumerable<object>;
        Assert.NotNull(removal);
        var keys = removal!.Select(k => k.ToString()).ToList();
        Assert.Contains(DisambiguationHelper.AttrCrossmediaQuery, keys);
        Assert.Contains(DisambiguationHelper.AttrCrossmediaType, keys);
    }

    // ---------------------------------------------------------------
    // The cold/database path: no in-memory index, the gate stays closed
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_MultiValueEr_NoIndex_ColdPath_PlaysAmazonRank()
    {
        // Same trade-off class as the JF-420 alternative pool and the JF-652
        // near-tie pool: without the pinned in-memory index the gate does not
        // fire and today's behavior (Amazon's rank #1 through the DB search)
        // is preserved.
        var pnk = Pnk();
        var song = new Audio { Name = "Get the Party Started", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))))
            .Returns(new List<BaseItem> { pnk });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem> { song });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => (q.ArtistIds == null || q.ArtistIds.Length == 0)
                    && (q.IncludeItemTypes == null || !q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist)))))
            .Returns(new List<BaseItem>());

        var handler = CreateSongHandler(index: null);
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("la musica", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true);
        TestHelpers.AssertNoDisambiguationState(response);
    }

    // ---------------------------------------------------------------
    // JF-715 rework F2: the rawConstraintSlot sentinel must not conflate
    // null with empty
    // ---------------------------------------------------------------

    [Fact]
    public void TryArbitrate_EmptyConstraintSlot_RunsGateUnrestricted_LikeNull()
    {
        // A future caller passing `slotValue ?? ""` must not permanently close
        // the gate and strand multi-value musician requests: an empty slot
        // carries no song constraint worth preserving, so it behaves exactly
        // like null and the gate runs unrestricted (the multi-artist ask fires).
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var request = CreateSongIntent("pink", TestHelpers.ResolvedSlotMultiValue("pink", "P!nk", "Pink Floyd"));
        var index = IndexOf(pnk, floyd);
        var logger = _fx.LoggerFactory.CreateLogger("jf715-f2");

        var viaEmpty = MultiValueErDisambiguation.TryArbitrate(
            request, _fx.CreateUser(), index, _fx.LibraryManager.Object, logger, "it-IT", rawConstraintSlot: "");
        var viaNull = MultiValueErDisambiguation.TryArbitrate(
            request, _fx.CreateUser(), index, _fx.LibraryManager.Object, logger, "it-IT", rawConstraintSlot: null);

        TestHelpers.AssertMultiArtistAsk(viaEmpty.Ask!, pnk, floyd);
        TestHelpers.AssertMultiArtistAsk(viaNull.Ask!, pnk, floyd);
    }

    // ---------------------------------------------------------------
    // JF-884: the phonetic-variant canonical. The 2026-10-12 catalog
    // re-mint made Amazon ER promote a phonetic synonym string to the
    // canonical VALUE name (live 2026-10-13: "P!nk floyd" with exactly one
    // "Pink Floyd" in the library); the ID-based dedupe across the exact
    // and the phonetic layer must auto-play the single resolved artist
    // instead of prompting, while every genuine multi-id case keeps the ask.
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_VariantCanonical_GenericSongConstraint_PlaysTheResolvedArtist()
    {
        // The live e2e shape verbatim ("metti una canzone dei pink floyd",
        // 2026-10-13): a generic song constraint plus a SINGLE variant canonical
        // on the musician slot. The song composite runs the same gate, so the
        // variant must resolve through it and play the artist, no ask.
        var floyd = PinkFloyd();
        var abba = Abba();
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateSongHandler(IndexOf(floyd, abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("una canzone", TestHelpers.ResolvedSlot("pink floyd", "P!nk floyd")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    [Fact]
    public async Task PlayArtistSongs_VariantCanonical_CollapsesToSingleArtist_AutoPlays()
    {
        // THE REGRESSION PIN: canonical "P!nk floyd" (a phonetic variant, not a
        // library name) with exactly one floyd artist in the library must
        // auto-play "Pink Floyd" with no ask. Pre-JF-884 the exact-only
        // resolution read the variant as zero evidence, the gate closed, and
        // the downstream search/gates fired the yes/no prompt.
        var floyd = PinkFloyd();
        var abba = Abba();
        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(floyd, abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlot("pink floyd", "P!nk floyd")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true, "the single resolved artist auto-plays (JF-884)");
        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    [Fact]
    public async Task PlayArtistSongs_AdoptedSurvivor_StandsDownCoincidentalContainmentGate()
    {
        // Pins the HANDLER half of JF-884: the erSurvivorAdopted stand-down at
        // the JF-377/JF-420 string gates. This canonical/artist pair TRIPS
        // IsCoincidentalContainmentMatch (the artist name is a minority-content
        // whole-word containment inside a longer canonical), so with the
        // stand-down conjunct deleted the ask fires and the whole suite stays
        // green (every other fixture's pair is inert for the predicates). An
        // ADOPTED survivor outranks the query-shape re-judgment.
        var bush = new MusicArtist { Name = "Bush", Id = Guid.NewGuid() };
        var abba = Abba();
        var song = new Audio { Name = "Machinehead", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(bush, abba));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlot("bush live wire tonight", "bush live wire tonight")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true, "the adopted survivor auto-plays, JF-377 stands down (JF-884)");
        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    [Fact]
    public void ResolveErCandidates_CoincidentalContainment_DoesNotResolve()
    {
        // Code-review round (JF-884): the fuzzy layer's score rides the
        // matcher's ContainmentScore shortcut for substring pairs, so the
        // JF-381 containment band must gate it exactly as it gates the search
        // chain's tier 1. "cup" inside "Porcupine Tree" scores 90 but is a
        // coincidental containment (the band's own doc example) and must NOT
        // resolve the variant layer; the honest not-resolve falls to the
        // caller's search path. The JF-377 same-shape downgrade at the handler
        // covers the search side.
        var koop = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var porcupine = new MusicArtist { Name = "Porcupine Tree", Id = Guid.NewGuid() };
        var pool = new List<BaseItem> { koop, porcupine };
        var index = IndexOf(koop, porcupine);

        var resolved = Jellyfin.Plugin.AlexaSkill.Alexa.Util.ArtistSearch.ResolveErCandidates(
            new[] { "cup" }, pool, index,
            FuzzyMatcher.GetEffectiveThreshold(null, Jellyfin.Plugin.AlexaSkill.Alexa.Util.ArtistSearch.ErVariantResolutionThreshold));

        Assert.DoesNotContain(resolved, a => a.Id.Equals(porcupine.Id));
    }

    [Fact]
    public async Task PlayArtistSongs_VariantPlusExactCanonical_DominantRendition_AutoPlaysRank1()
    {
        // JF-885 updates this JF-884-era pin: rank #1 is the variant of one
        // artist and rank #2 exact-names ANOTHER, and the id dedupe still sees
        // TWO distinct library artists (that JF-884 contract is unchanged and
        // still pinned by the close-rendition companion below), but the ask no
        // longer fires when the rendition is decisively rank-1's ask: "pink
        // floyd" scores 100 against Pink Floyd and 30 against P!nk (pinned
        // 2026-10-13), a margin of 70, so the dominant rank-1 plays instead.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        List<InternalItemsQuery> queries = SetupArtistSongsCapturingQueries(song);

        var handler = CreateArtistHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink floyd", "P!nk floyd", "P!nk")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == floyd.Id);
        Assert.DoesNotContain(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == pnk.Id);
    }

    [Fact]
    public async Task PlayArtistSongs_VariantPlusExactCanonical_CloseRendition_StillAsks()
    {
        // The companion that keeps the JF-884 id-dedupe ask alive: the SAME
        // canonical set (variant rank #1 + exact rank #2, two distinct library
        // ids) under a rendition that leaves the resolved artists close ("pink"
        // scores 90 against Pink Floyd and 75 against P!nk, margin 15, below
        // the JF-420 bar), so the two-name ask fires.
        var pnk = Pnk();
        var floyd = PinkFloyd();
        var song = new Audio { Name = "Just Like a Pill", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(pnk, floyd));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink", "P!nk floyd", "P!nk")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, floyd, pnk);
    }

    [Fact]
    public async Task PlayArtistSongs_PearlJamCollaborationFamily_DistinctIds_StillAsks()
    {
        // Guard (c): the collaboration family. Three canonicals that each
        // exact-name a DIFFERENT library artist stay a real ambiguity; the
        // variant dedupe must not collapse them. JF-885 margin check (pinned
        // 2026-10-13): "pearl jam" scores 100 against rank-1 "Pearl Jam" and
        // 90 against both "Pearl Jam w" and "Mad Season & Pearl Jam", a margin
        // of 10, below the JF-420 bar, so the dominance bypass does not fire
        // and the ask stays.
        var pearlJam = new MusicArtist { Name = "Pearl Jam", Id = Guid.NewGuid() };
        var pearlJamW = new MusicArtist { Name = "Pearl Jam w", Id = Guid.NewGuid() };
        var madSeason = new MusicArtist { Name = "Mad Season & Pearl Jam", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Alive", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(pearlJam, pearlJamW, madSeason));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue(
                "pearl jam", "Pearl Jam", "Pearl Jam w", "Mad Season & Pearl Jam")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, pearlJam, pearlJamW, madSeason);
    }

    [Fact]
    public async Task PlayArtistSongs_ExactNameCanonical_StillBypassesUnchanged()
    {
        // Guard (d): an exact-name canonical keeps the JF-420.1 auto-play with
        // a longer containment rival in the library; the survivor adoption
        // stands the string gates down on exactly the evidence the bypass
        // encoded (the canonical IS the matched artist's name).
        var soulCoughing = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var rival = new MusicArtist { Name = "Soul Coughing & Roni Size", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Super Bon Bon", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(soulCoughing, rival));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlot("soul coughing", "Soul Coughing")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true, "the exact-name hit auto-plays (JF-420.1)");
        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
    }

    // ---------------------------------------------------------------
    // JF-885: the rank-1 dominance bypass. When Amazon's rank-1 canonical is
    // decisively what the user asked for, the tail ER values are coincidental
    // token overlaps and the yes/no ask is noise: rank-1's resolved artist
    // auto-plays on the JF-420 auto-select margin over the best other resolved
    // artist. Genuinely close pairs keep the ask (the JF-684 and Pearl Jam
    // pins above). Rank-1 exactness alone is deliberately NOT a bypass (the
    // Pearl Jam pin's rendition IS its rank-1 canonical exactly).
    // ---------------------------------------------------------------

    /// <summary>
    /// Captures every artist-scoped query so a dominance leg can prove WHICH
    /// library artist played (the play is scoped to rank-1's resolve, never to
    /// a tail value's).
    /// </summary>
    private List<InternalItemsQuery> SetupArtistSongsCapturingQueries(params Audio[] songs)
    {
        _fx.SetupUserMock();
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    queries.Add(q);
                    return new List<BaseItem>(songs);
                }

                return new List<BaseItem>();
            });
        return queries;
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_Rank1DominantVariantRendition_AutoPlays_NoAsk()
    {
        // JF-885, THE live case (2026-10-13 e2e retry: "metti una canzone dei
        // pink floyd" with ER [Pink Floyd rank-1, King Floyd], both real library
        // artists). The rendition Amazon mirrors into the slot is the phonetic
        // variant "P!nk floyd": it scores 90 against "Pink Floyd" (one
        // substitution) and 70 against "King Floyd" (three substitutions),
        // pinned 2026-10-13, a margin of EXACTLY the JF-420 bar. The dominance
        // leg fires AT the bar (an above-only comparison would leave the live
        // ask in place, the gap this task closes) and Pink Floyd plays.
        var floyd = PinkFloyd();
        var king = KingFloyd();
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        List<InternalItemsQuery> queries = SetupArtistSongsCapturingQueries(song);

        var handler = CreateArtistHandler(IndexOf(floyd, king));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("P!nk floyd", "Pink Floyd", "King Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == floyd.Id);
        Assert.DoesNotContain(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == king.Id);
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_Rank1DominantSpokenRendition_AutoPlays_NoAsk()
    {
        // The same ER shape under the plain spoken rendition "pink floyd"
        // (pinned 2026-10-13: 100 against "Pink Floyd", 80 against "King
        // Floyd", again a margin of exactly the JF-420 bar). The e2e fixtures'
        // expected outcome (it-IT "metti una canzone dei pink floyd", the
        // es/fr "oye los"/"lis les" smoke rows): the play, no ask.
        var floyd = PinkFloyd();
        var king = KingFloyd();
        var song = new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() };
        List<InternalItemsQuery> queries = SetupArtistSongsCapturingQueries(song);

        var handler = CreateArtistHandler(IndexOf(floyd, king));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("pink floyd", "Pink Floyd", "King Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == floyd.Id);
        Assert.DoesNotContain(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == king.Id);
    }

    [Fact]
    public async Task PlaySong_MultiValueEr_Rank1DominantVariantRendition_AutoPlays_NoAsk()
    {
        // The song composite carries the same bypass (the generic-word leg the
        // live it-IT utterance takes): "una canzone" + the live ER shape plays
        // Pink Floyd through the survivor adoption, no ask.
        var floyd = PinkFloyd();
        var king = KingFloyd();
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        List<InternalItemsQuery> queries = SetupArtistSongsCapturingQueries(song);

        var handler = CreateSongHandler(IndexOf(floyd, king));
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("una canzone", TestHelpers.ResolvedSlotMultiValue("P!nk floyd", "Pink Floyd", "King Floyd")),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        TestHelpers.AssertNoDisambiguationState(response);
        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.True(response.Response.ShouldEndSession == true);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
        Assert.Contains(queries, q => q.ArtistIds!.Length == 1 && q.ArtistIds[0] == floyd.Id);
    }

    [Fact]
    public async Task PlayArtistSongs_MultiValueEr_Rank1DominantButLibraryArtistOutscores_StillAsks()
    {
        // Code-review round (JF-885), the stale-catalog guard: the library HAS
        // the artist the user named ("King Floyd") but the catalog lags (the
        // weekly sync), so Amazon ER matches token-overlapping canonicals
        // [Pink Floyd, P!nk] instead. The margin alone would auto-play rank-1
        // Pink Floyd ("king floyd" scores 80 against it and ~20 against P!nk),
        // but the UNRESOLVED library artist outscores rank-1 (King Floyd at
        // 100 > 80): the rendition is not decisively rank-1's ask, and the ask
        // keeps surfacing the mismatch instead of silently playing the wrong
        // artist.
        var floyd = PinkFloyd();
        var pnk = Pnk();
        var king = KingFloyd();
        var song = new Audio { Name = "Money", Id = Guid.NewGuid() };
        SetupArtistSongs(song);

        var handler = CreateArtistHandler(IndexOf(floyd, pnk, king));
        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent(TestHelpers.ResolvedSlotMultiValue("king floyd", "Pink Floyd", "P!nk")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertMultiArtistAsk(response, floyd, pnk);
    }

    [Fact]
    public void TryArbitrate_NoErCanonical_FreeTextSlot_GateStaysClosed()
    {
        // Guard (e): a musician slot with NO entity resolution (the free-text
        // path) never reaches the dominance leg; the gate closes on the empty
        // canonical list exactly as before JF-885 and the caller's own search
        // path keeps owning the raw value.
        var floyd = PinkFloyd();
        var king = KingFloyd();
        var request = CreateArtistIntent(new Slot { Name = "musician", Value = "pink floyd" });
        var logger = _fx.LoggerFactory.CreateLogger("jf885-guard-e");

        var result = MultiValueErDisambiguation.TryArbitrate(
            request, _fx.CreateUser(), IndexOf(floyd, king), _fx.LibraryManager.Object, logger, "it-IT");

        Assert.Null(result.Ask);
        Assert.Null(result.ResolvedArtist);
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
