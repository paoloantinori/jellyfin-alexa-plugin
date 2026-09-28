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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
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
/// JF-661: the kana-origin flag threaded into the song-to-album cascade
/// (TryAlbumFallbackAsync). The cascade romanized its slot at entry and
/// plain-accepted at CrossMediaAlbumThreshold=90 with no kana awareness, so a
/// kana-origin song miss (already refused by the JF-654 song bar and the
/// JF-652/JF-660 artist bar) could migrate one medium over and auto-play a
/// plain-fuzzy album match. These pins hold the threading: the pinned flag
/// restores the honest song not-found, the identical Latin query with the flag
/// unpinned keeps the JF-345 recall, and a real collision still plays. Matcher
/// scores and Double Metaphone codes here were dumped from the production
/// matcher, not assumed: 'sato' scores 90 against 'Sator' (the containment
/// floor) with codes ST vs STR; 'bitoruzu' scores 90 against 'Bitoruzu Deluxe'
/// the same way, with the suffix widening the collision band's input to 15 vs
/// 8; 'satoru' vs 'Satoru' is the identity collision STR/STR; and
/// 'Bitoruzu (Deluxe)' scores 90 while its parenthetical suffix strips off the
/// band's evidence input.
/// </summary>
[Collection("Plugin")]
public class KanaOriginAlbumCascadeTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf661-album-cascade");
        _fx.SetupUserMock();
    }

    private static IntentRequest SongIntent(string song, string locale = "ja-JP")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot> { ["song"] = new Slot { Name = "song", Value = song } };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private PlaySongIntentHandler CreateSongHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            songNgramIndex: new TestHelpers.FakeSongIndex());

    /// <summary>
    /// Mocks the library so the song search, the artist cascade, and the album
    /// cascade's tier-1 SearchTerm all miss while tier 2 (the bounded fuzzy scan)
    /// returns <paramref name="fuzzyAlbums"/> (the PlaySongAlbumFallbackTests shape).
    /// </summary>
    private void SetupMissWithFuzzyAlbums(List<BaseItem> fuzzyAlbums)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search (Audio + SearchTerm): always misses.
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem>();
                }

                // Artist cascade DB queries (MusicArtist): no artist.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem>();
                }

                // Album cascade tier 1 (MusicAlbum + SearchTerm): the indexed exact lookup.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) && q.SearchTerm != null)
                {
                    return new List<BaseItem>();
                }

                // Album cascade tier 2 (MusicAlbum, no SearchTerm): the bounded fuzzy scan.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))
                {
                    return fuzzyAlbums;
                }

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

    private static MusicAlbum BaitedAlbum(string name)
        => new() { Name = name, Id = Guid.NewGuid() };

    private static List<BaseItem> TracksFor(MusicAlbum album)
        => new() { new Audio { Name = $"{album.Name} track 1", Id = Guid.NewGuid(), ParentId = album.Id } };

    private Task<SkillResponse?> CallAlbumCascade(string slotText, List<BaseItem> fuzzyAlbums, bool? kanaOrigin)
    {
        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        SetupMissWithFuzzyAlbums(fuzzyAlbums);
        return probe.CallTryAlbumFallbackAsync(
            slotText, TestHelpers.CreateJellyfinUser(), _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, "kana album cascade probe", CancellationToken.None, kanaOrigin);
    }

    /// <summary>
    /// Mocks the library so the cascade's tier-1 indexed SearchTerm query returns
    /// <paramref name="exactAlbums"/> (tier 2 never runs: the cascade only scans the
    /// fuzzy tier on an empty tier 1); every other query misses.
    /// </summary>
    private void SetupTierOneWithAlbums(List<BaseItem> exactAlbums)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) && q.SearchTerm != null
                    ? exactAlbums
                    : new List<BaseItem>());
    }

    private Task<SkillResponse?> CallAlbumCascadeTierOne(string slotText, List<BaseItem> exactAlbums, bool? kanaOrigin)
    {
        var probe = new SharedGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        SetupTierOneWithAlbums(exactAlbums);
        return probe.CallTryAlbumFallbackAsync(
            slotText, TestHelpers.CreateJellyfinUser(), _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "ja-JP",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, "kana album cascade probe", CancellationToken.None, kanaOrigin);
    }

    // ---------------------------------------------------------------
    // The gate: pinned flag vs self-computed on the identical input
    // ---------------------------------------------------------------

    [Fact]
    public async Task AlbumCascade_RomanizedQuery_PinnedKanaFlag_PlainFuzzyAlbum_ReturnsNull()
    {
        // The exact input the pre-romanized caller hands the cascade: the Latin
        // local 'sato' plus the flag pinned on the raw slot. 'sato' containment-
        // matches 'Sator' at exactly the 90 bar with no Double Metaphone collision
        // (ST vs STR): the plain-fuzzy class the bar refuses, never an auto-play.
        SetupPlugin();

        SkillResponse? result = await CallAlbumCascade("sato", new List<BaseItem> { BaitedAlbum("Sator") }, kanaOrigin: true);

        Assert.Null(result);
    }

    [Fact]
    public async Task AlbumCascade_TierOneSearchTermWinner_PinnedKanaFlag_PlainFuzzyAlbum_ReturnsNull()
    {
        // The 'composes over BOTH candidate tiers' contract, pinned against the
        // 'tier-1 winners are literal' doctrine drift: here the tier-1 indexed
        // SearchTerm query ITSELF returns the 'Sator' bait for 'sato' (containment
        // 90, no ST/STR collision), so the winner flows from the literal tier, not
        // the fuzzy scan. The cascade's acceptance is a SUBSTITUTION (the
        // FoundAlbumInstead announcement speaks a name the user did not say), so
        // the tier-1 winner needs the same collision evidence as a fuzzy one and
        // the pinned flag keeps it the honest miss; the identical SearchTerm hit
        // on PlayAlbum's own direct-play path stays ungated (the notes' doctrine
        // criterion).
        SetupPlugin();

        SkillResponse? result = await CallAlbumCascadeTierOne("sato", new List<BaseItem> { BaitedAlbum("Sator") }, kanaOrigin: true);

        Assert.Null(result);
    }

    [Fact]
    public async Task AlbumCascade_LatinQuery_FlagUnpinned_SameAlbum_Plays()
    {
        // The pre-fix shape and the Latin control in one: the IDENTICAL Latin
        // input with the flag left to self-compute (false: the text has no kana),
        // which is both what the bug produced and what a Latin-origin query
        // legitimately computes. The JF-345 recall (a bare "play sator" cascade)
        // is unchanged by the threading; only the kana provenance changes the
        // outcome.
        SetupPlugin();
        var album = BaitedAlbum("Sator");
        var tracks = TracksFor(album);
        SetupAlbumTracks(album, tracks);

        SkillResponse? result = await CallAlbumCascade("sato", new List<BaseItem> { album }, kanaOrigin: null);

        Assert.NotNull(TestHelpers.GetPlayDirective(result!));
        string speech = TestHelpers.GetSpeechText(result!);
        Assert.Contains("Sator", speech, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // PlaySong (the production caller)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_KanaTitle_ContainmentBaitAlbum_HonestSongNotFound_NeverAlbumPlay()
    {
        SetupPlugin();
        var album = BaitedAlbum("Bitoruzu Deluxe");
        var tracks = TracksFor(album);
        SetupMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);

        // The task's verification shape: song=ビートルズ romanizes to 'bitoruzu',
        // whose containment match on 'Bitoruzu Deluxe' scores exactly the 90 bar.
        // The threaded flag demands the collision evidence the suffix widens the
        // name past (15 vs 8, band 3): the honest song not-found, never the album
        // auto-play the bare bar produced.
        var handler = CreateSongHandler();

        SkillResponse response = await handler.HandleAsync(
            SongIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the song not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundSongByName", "ja-JP", "bitoruzu"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bitoruzu Deluxe", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_KanaTitle_RealCollisionAlbum_StillPlays()
    {
        // Composition control: 'サトル' romanizes to 'satoru', whose Double
        // Metaphone codes are identical to 'Satoru''s (STR/STR, band 0). The
        // threaded bar composes with the 90 threshold, it does not replace it: a
        // real collision still plays through the cascade on the threaded caller.
        SetupPlugin();
        var album = BaitedAlbum("Satoru");
        var tracks = TracksFor(album);
        SetupMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);
        var handler = CreateSongHandler();

        SkillResponse response = await handler.HandleAsync(
            SongIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a real code collision must still play through the threaded cascade");
    }

    [Fact]
    public async Task PlaySong_KanaTitle_ParentheticalSuffix_StrippedCoreCollides_StillPlays()
    {
        // The shared helper's strip semantics on the album bar: 'Bitoruzu
        // (Deluxe)' scores exactly 90 for 'bitoruzu' and its parenthetical suffix
        // is metadata the band's evidence input must not see (stripped to the
        // colliding 'Bitoruzu' core, band 0). The legitimate colliding album
        // still plays; only a NON-parenthetical suffix (the test above) widens
        // the band past the collision.
        SetupPlugin();
        var album = BaitedAlbum("Bitoruzu (Deluxe)");
        var tracks = TracksFor(album);
        SetupMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);
        var handler = CreateSongHandler();

        SkillResponse response = await handler.HandleAsync(
            SongIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the parenthetical-stripped colliding core must still play through the threaded cascade");
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
