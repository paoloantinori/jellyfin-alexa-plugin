using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-659: the ER canonical for musician slots (the genre-canonical pattern of
/// JF-642 applied to the catalog-backed JellyfinArtist type). Naturalized ja
/// voice selects PlayArtistSongs with ER_SUCCESS_MATCH ('クイーン' resolving to
/// the canonical 'Queen'), but the handler used to resolve from the RAW slot,
/// romanize to 'kuin', and hit the Queen/Keane Double Metaphone tie, firing the
/// multi-artist ask on every tie-shaped name despite the resolved entity. With
/// the canonical feeding the search, the exact library name resolves directly.
/// The raw-slot path (no ER, the simulator shape) keeps today's honest ask
/// (the control legs here; KanaOriginAcceptanceTests owns the original pins).
/// </summary>
[Collection("Plugin")]
public class MusicianErCanonicalTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private BaseItem Queen() => new MusicArtist { Name = "Queen", Id = Guid.NewGuid() };
    private BaseItem Keane() => new MusicArtist { Name = "Keane", Id = Guid.NewGuid() };

    private static Dictionary<Guid, (string Primary, string? Alternate)> CodesFromNames(params BaseItem[] artists)
    {
        // The production encoder: the KN collision between Queen and Keane is the
        // live tie shape, not a hand-written assumption.
        return artists.ToDictionary(a => a.Id, a => DoubleMetaphone.Encode(a.Name!));
    }

    private static IntentRequest CreateArtistIntent(string musician, string? canonical = null)
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        // The ER_SUCCESS_MATCH shape the JellyfinArtist catalog produces for a
        // spoken kana variant resolving to the canonical library name
        // (TestHelpers.ResolvedSlot, with null canonical for the raw-slot legs).
        intent.Slots = new Dictionary<string, Slot> { ["musician"] = TestHelpers.ResolvedSlot(musician, canonical) };
        return new IntentRequest { Intent = intent, Locale = "ja-JP", RequestId = "test-req" };
    }

    private static IntentRequest CreateSongIntent(string song, string musician, string canonical)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song },
            ["musician"] = TestHelpers.ResolvedSlot(musician, canonical)
        };
        return new IntentRequest { Intent = intent, Locale = "ja-JP", RequestId = "test-req" };
    }

    private PlayArtistSongsIntentHandler CreateArtistHandler(IArtistIndex index)
        => new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    private PlaySongIntentHandler CreateSongHandler(IArtistIndex index)
        => new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            index);

    private static bool HasAudioPlayerDirective(SkillResponse response)
        => TestHelpers.GetPlayDirective(response) != null;

    private static bool IsDisambiguationAsk(SkillResponse response, params string[] expectedNames)
    {
        if (response.Response.ShouldEndSession == true || HasAudioPlayerDirective(response))
        {
            return false;
        }

        var matches = response.SessionAttributes?.GetValueOrDefault(DisambiguationHelper.AttrMatches)?.ToString();
        return matches != null && expectedNames.All(n => matches.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------
    // PlayArtistSongs: the ER-resolved tie shape plays, no extra turn
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_ErResolvedMusician_PlaysCanonicalArtist_WithoutTieAsk_JF659()
    {
        // The live incident shape: 'クイーン' carries ER_SUCCESS_MATCH to 'Queen'
        // while the index holds the Queen/Keane DM-colliding pair. The canonical
        // feeds the search, so the exact library name resolves and the response
        // plays Queen's songs (AudioPlayer.Play carrying Queen's track) instead of
        // the multi-artist tie ask.
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, CodesFromNames(queen, keane));
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン", canonical: "Queen"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the ER-resolved artist must play directly");
        Assert.True(response.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        Assert.False(IsDisambiguationAsk(response, "Queen", "Keane"), "the tie ask must not fire once ER resolved the entity");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == queen.Id);
    }

    [Fact]
    public async Task HandleAsync_RawKanaMusician_NoResolution_KeepsTheTieAsk_JF659()
    {
        // Control leg (KanaOriginAcceptanceTests owns the original pin): the same
        // colliding pair WITHOUT entity resolution keeps the honest ask, because
        // the raw romanized query genuinely cannot distinguish the winner.
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, CodesFromNames(queen, keane));

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(IsDisambiguationAsk(response, "Queen", "Keane"), "the raw kana path keeps today's tie ask");
        Assert.False(HasAudioPlayerDirective(response));
    }

    // ---------------------------------------------------------------
    // PlaySong: the canonical scopes the artist filter deterministically
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_ErResolvedMusician_ScopesSongSearchToCanonicalArtist_JF659()
    {
        // The musician filter resolves through the canonical, so the artist-scoped
        // song search carries Queen's id (not the romanized 'kuin' coin flip
        // between the DM-colliding pair) and the found song plays.
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, CodesFromNames(queen, keane));
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };

        var artistScopedSongQueries = new List<Guid[]>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0 && q.SearchTerm != null)))
            .Returns((InternalItemsQuery q) =>
            {
                artistScopedSongQueries.Add(q.ArtistIds);
                return new List<BaseItem> { song };
            });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds == null || q.ArtistIds.Length == 0)))
            .Returns(new List<BaseItem>());

        var handler = CreateSongHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateSongIntent("bohemian rhapsody", "クイーン", "Queen"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(HasAudioPlayerDirective(response), "the ER-resolved artist filter must lead to a play");
        Assert.Contains(artistScopedSongQueries, ids => ids.Length == 1 && ids[0] == queen.Id);
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
