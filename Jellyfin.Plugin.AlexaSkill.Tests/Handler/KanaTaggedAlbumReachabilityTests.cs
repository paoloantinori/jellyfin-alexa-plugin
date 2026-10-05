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
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-773: the album candidate legs' kana-tagged reachability pins. JF-755 closed
/// the query-side-only narrowing on the two in-memory indexes (artists, songs);
/// the ALBUM surfaces have the same query-side romanization with NO
/// candidate-side counterpart, and unlike the artist/song cold-window residual
/// that narrowing is PERMANENT (albums have no in-memory index to carry a romaji
/// key), so a kana-tagged album library was unreachable by BOTH the kana and the
/// romaji spelling of its name on every album fuzzy arm (the raw kana name
/// scores ~0 against the always-romanized query on the Latin-script Levenshtein
/// scale). These pins hold the score-time fix (the KeywordMatcher.ScoringName
/// replacement resolver at both fuzzy arms' selectors and the album kana bar's
/// collision input, the JF-755 song-bar weighing): the kana-tagged album plays
/// from the romaji query (the kana flag inert) and from the kana query (the bar
/// reading the romanized candidate), the Latin matrix is byte-identical
/// (ScoringName is the identity for kana-free names), and the bar's
/// suffix-widening refusal survives the romaji reading. 'ヨルニカケル' romanizes
/// to 'yorunikakeru' (the JF-755 TitleTokens pin's fixture).
/// </summary>
[Collection("Plugin")]
public class KanaTaggedAlbumReachabilityTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private const string KanaAlbumName = "ヨルニカケル";
    private const string RomajiReading = "yorunikakeru";

    private static IntentRequest AlbumIntent(string album, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot> { ["album"] = new Slot { Name = "album", Value = album } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private PlayAlbumIntentHandler CreateAlbumHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf773-album-reachability");
        _fx.SetupUserMock();
    }

    /// <summary>
    /// Mocks the library so every album SearchTerm tier misses (PlayAlbum's exact
    /// tier and the cascade's tier 1) while the no-SearchTerm full-catalog scans
    /// (PlayAlbum's JF-336 arm and the cascade's tier 2) return
    /// <paramref name="fuzzyAlbums"/> (the KanaOriginAlbumFuzzyArmTests shape: the
    /// production shape for a kana-tagged library, whose kana names Jellyfin's own
    /// index cannot match to a Latin SearchTerm). Serves both the handler tests and
    /// the cascade probe below.
    /// </summary>
    private void SetupExactMissWithFuzzyAlbums(List<BaseItem> fuzzyAlbums)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // The exact tiers (MusicAlbum + SearchTerm): miss.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) && q.SearchTerm != null)
                {
                    return new List<BaseItem>();
                }

                // The full-catalog scans (MusicAlbum, no SearchTerm).
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))
                {
                    return fuzzyAlbums;
                }

                // Everything else (artist fallback queries): empty.
                return new List<BaseItem>();
            });
    }

    private void SetupAlbumTracks(MusicAlbum album, List<BaseItem> tracks)
    {
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                Guid playKey = q.ParentId != Guid.Empty
                    ? q.ParentId
                    : q.AlbumIds is { Length: > 0 } ? q.AlbumIds[0] : Guid.Empty;
                return playKey == album.Id
                    ? new QueryResult<BaseItem> { Items = tracks, TotalRecordCount = tracks.Count }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 };
            });
    }

    private static (MusicAlbum Album, List<BaseItem> Tracks) MakeAlbum(string name)
    {
        var album = new MusicAlbum { Name = name, Id = Guid.NewGuid() };
        var tracks = new List<BaseItem> { new Audio { Name = $"{name} track 1", Id = Guid.NewGuid(), ParentId = album.Id } };
        return (album, tracks);
    }

    // ---------------------------------------------------------------
    // PlayAlbum's own fuzzy arm (the JF-336 arm, JF-662's surface)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayAlbum_KanaTaggedAlbum_RomajiQuery_PlaysThroughFuzzyArm_JF773()
    {
        // The DoD verification shape: a kana-tagged album reached by a ROMAJI
        // query. Pre-fix the arm's selector read the raw kana name, which the
        // Latin-script Levenshtein loop scores ~0, so the album was not-found
        // despite being the only candidate; the score-time reading resolves it.
        SetupPlugin();
        var (album, tracks) = MakeAlbum(KanaAlbumName);
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);

        var handler = CreateAlbumHandler();

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent(RomajiReading), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a romaji query must reach the kana-tagged album through the fuzzy arm");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(KanaAlbumName, speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_KanaTaggedAlbum_KanaQuery_PlaysThroughFuzzyArm_JF773()
    {
        // The mirror direction: the kana query (romanized at entry, the JF-643
        // wiring) against the kana-tagged album. This leg exercises BOTH halves
        // of the fix: the selector's romanized reading (the match) and the
        // JF-662 kana bar's collision input (armed on the kana origin; on the
        // raw kana name the bar was structurally dead, empty Double Metaphone
        // codes and a kana-vs-romaji length band, so the album the arm had just
        // matched would have been refused as a plain-fuzzy bait).
        SetupPlugin();
        var (album, tracks) = MakeAlbum(KanaAlbumName);
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);

        var handler = CreateAlbumHandler();

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent(KanaAlbumName), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a kana query must reach the kana-tagged album through the fuzzy arm");
    }

    [Fact]
    public async Task PlayAlbum_LatinAlbum_SameArm_LatinControl_Unchanged_JF773()
    {
        // The control pin: the identical arm over a kana-free library keeps the
        // pre-fix behavior byte-for-byte (the resolver is the identity for
        // kana-free names, the JF-755 TitleTokens control's string sibling).
        SetupPlugin();
        var (album, tracks) = MakeAlbum("Abbey Road");
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);

        var handler = CreateAlbumHandler();

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent("abby road", "en-US"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the Latin spelling-drift control must keep playing through the arm");
    }

    // ---------------------------------------------------------------
    // The JF-345 song-to-album cascade (AlbumPlayService's fuzzy arm)
    // ---------------------------------------------------------------

    private Task<SkillResponse?> CallAlbumCascade(string slotText, MusicAlbum album)
    {
        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        return probe.CallTryAlbumFallbackAsync(
            slotText, TestHelpers.CreateJellyfinUser(), _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, "jf773 album cascade probe", CancellationToken.None, kanaOrigin: null);
    }

    [Fact]
    public async Task AlbumCascade_KanaTaggedAlbum_RomajiQuery_Plays_JF773()
    {
        SetupPlugin();
        var (album, tracks) = MakeAlbum(KanaAlbumName);
        SetupAlbumTracks(album, tracks);

        SkillResponse? result = await CallAlbumCascade(RomajiReading, album);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "the cascade's fuzzy arm must reach the kana-tagged album from the romaji query");
    }

    [Fact]
    public async Task AlbumCascade_KanaTaggedAlbum_KanaQuery_Plays_JF773()
    {
        // The cascade's own entry romanization (the flag self-computed on the
        // kana slot) with the JF-661 bar armed: the same two halves as the
        // PlayAlbum kana leg, at the cascade's 90 containment-grade bar.
        SetupPlugin();
        var (album, tracks) = MakeAlbum(KanaAlbumName);
        SetupAlbumTracks(album, tracks);

        SkillResponse? result = await CallAlbumCascade(KanaAlbumName, album);

        Assert.NotNull(result);
        Assert.True(TestHelpers.GetPlayDirective(result!) != null, "the cascade's fuzzy arm must reach the kana-tagged album from the kana query");
    }

    // ---------------------------------------------------------------
    // The album kana bar's collision input (the JF-755 song-bar weighing)
    // ---------------------------------------------------------------

    [Fact]
    public void AlbumKanaBar_KanaTaggedAlbum_RomajiReading_Collides_JF773()
    {
        // The bar's collision input resolves through the same romanized
        // reading: 'yorunikakeru' vs the kana-tagged name's reading is the
        // identity collision (identical codes, band 0). On the raw kana name
        // the leg was structurally dead (the encoder has no kana arm and the
        // band compared kana characters against romaji characters), so every
        // kana-tagged album the reachability fix matches would have been
        // refused by the armed bar.
        var album = new MusicAlbum { Name = KanaAlbumName, Id = Guid.NewGuid() };

        Assert.True(AlbumPlayService.PassesKanaOriginAlbumAcceptance(RomajiReading, album));
    }

    [Fact]
    public void AlbumKanaBar_KanaTaggedAlbum_SuffixWidenedReading_StillRefused_JF773()
    {
        // Composition pin: the bar's suffix-widening refusal (the JF-661
        // 'Bitoruzu Deluxe' shape) survives the romanized reading. The suffixed
        // reading 'yorunikakeruderakkusu' (21) sits 9 past the query's 12
        // against the band's 3, so a containment-scored suffix match stays the
        // honest miss in the romaji space exactly as it was in the Latin one.
        var album = new MusicAlbum { Name = "ヨルニカケルデラックス", Id = Guid.NewGuid() };

        Assert.False(AlbumPlayService.PassesKanaOriginAlbumAcceptance(RomajiReading, album));
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
