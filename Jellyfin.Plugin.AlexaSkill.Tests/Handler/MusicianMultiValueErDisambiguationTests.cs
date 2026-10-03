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
/// ER ambiguity is REAL in the library, and the unchanged single-value/cold
/// paths around it.
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
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>(songs));
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.ArtistIds == null || q.ArtistIds.Length == 0)))
            .Returns(new List<BaseItem>());
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
        SkillResponse exhausted = await new NoIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory)
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
        var noHandler = new NoIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
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
        // library. The single-value gate stays closed and the exact hit
        // auto-plays; only a MULTI-value ER list may prompt.
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

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
