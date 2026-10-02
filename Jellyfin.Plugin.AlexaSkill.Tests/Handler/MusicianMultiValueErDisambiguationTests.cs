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
using Newtonsoft.Json;
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

    private static IntentRequest CreateSongIntent(string song, Slot musicianSlot, string locale = "it-IT")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song },
            ["musician"] = musicianSlot
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static IntentRequest CreateArtistIntent(Slot musicianSlot, string locale = "it-IT")
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = musicianSlot };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    /// <summary>
    /// The disambiguation ask the gate fires: open session, the multi-artist
    /// prompt naming every resolved artist, and the JF-420.2 session state
    /// (real ids, type=artist, cursor 0) that YesIntentHandler.PlayArtist and
    /// NoIntentHandler's cycling consume unchanged.
    /// </summary>
    private static void AssertMultiArtistAsk(SkillResponse response, params BaseItem[] expected)
    {
        TestHelpers.AssertSessionOpen(response, "the ask keeps the session open");
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.NotNull(response.SessionAttributes);
        var matchesJson = response.SessionAttributes!.GetValueOrDefault(DisambiguationHelper.AttrMatches)?.ToString();
        Assert.NotNull(matchesJson);
        var matches = JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(matchesJson!);
        Assert.NotNull(matches);
        // Rank order preserved: the first entry is Amazon's rank #1, the one
        // "yes" plays (AskMultipleArtists' winner-first contract).
        Assert.Equal(expected.Select(a => a.Id.ToString()), matches!.Select(m => m.Id));
        Assert.Equal(expected.Select(a => a.Name), matches.Select(m => m.Name));
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes.GetValueOrDefault(DisambiguationHelper.AttrType)?.ToString());
        Assert.Equal(0, Convert.ToInt32(response.SessionAttributes.GetValueOrDefault(DisambiguationHelper.AttrIndex)));
        string speech = TestHelpers.GetSpeechText(response);
        foreach (BaseItem artist in expected)
        {
            Assert.Contains(artist.Name, speech, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertNoDisambiguationState(SkillResponse response)
    {
        Assert.Null(response.SessionAttributes?.GetValueOrDefault(DisambiguationHelper.AttrMatches));
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

        AssertMultiArtistAsk(response, pnk, floyd);
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

        AssertMultiArtistAsk(response, pnk, floyd);
    }

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
        AssertNoDisambiguationState(response);
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
        AssertNoDisambiguationState(response);
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
        AssertNoDisambiguationState(response);
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
        AssertNoDisambiguationState(response);
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

        AssertNoDisambiguationState(response);
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

        AssertNoDisambiguationState(response);
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
        AssertNoDisambiguationState(response);
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
