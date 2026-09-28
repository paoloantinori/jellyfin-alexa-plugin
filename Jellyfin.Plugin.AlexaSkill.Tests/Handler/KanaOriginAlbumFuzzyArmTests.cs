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
/// JF-662: PlayAlbum's own JF-336 fuzzy album arm (the in-handler full-catalog
/// scan at the bare default threshold) has the kana bar. The arm is the FIRST
/// fuzzy acceptance point a kana album miss flows through and sits one gate
/// before the JF-660-fixed entity fallback; with both gated, every album
/// acceptance surface on PlayAlbum is covered. These pins hold the bar: a
/// kana-origin album miss over a plain-fuzzy bait takes the honest album
/// not-found, the Latin spelling-drift matrix (the JF-336 class, 'caffè' vs
/// 'Cafe') is unchanged, and a real collision still plays. Matcher scores and
/// Double Metaphone codes here were dumped from the production matcher, not
/// assumed: 'bitoruzu' scores 75 against 'Bitorudzu' (>= the 60 bar) with no
/// code collision (PTRS vs PTRT); 'beatels' scores 71 against 'Beatles' (the
/// Latin control); 'satoru' vs 'Satoru' is the identity collision STR/STR.
/// </summary>
[Collection("Plugin")]
public class KanaOriginAlbumFuzzyArmTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

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
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf662-album-fuzzy-arm");
        _fx.SetupUserMock();
    }

    /// <summary>
    /// Mocks the library so PlayAlbum's exact SearchTerm tier misses while the
    /// JF-336 arm's full-catalog scan returns <paramref name="fuzzyAlbums"/>, and
    /// no artist exists anywhere (the JF-336 class: an accent/spelling miss over
    /// a real library).
    /// </summary>
    private void SetupExactMissWithFuzzyAlbums(List<BaseItem> fuzzyAlbums)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // The exact tier (MusicAlbum + SearchTerm): misses.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) && q.SearchTerm != null)
                {
                    return new List<BaseItem>();
                }

                // The JF-336 arm's full-catalog scan (MusicAlbum, no SearchTerm).
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

    [Fact]
    public async Task PlayAlbum_KanaAlbum_PlainFuzzyBait_HonestAlbumNotFound_NeverBaitPlay()
    {
        SetupPlugin();
        var (bait, baitTracks) = MakeAlbum("Bitorudzu");
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { bait });
        SetupAlbumTracks(bait, baitTracks);

        // The task's verification shape: album=ビートルズ romanizes to
        // 'bitoruzu', whose plain-fuzzy match on 'Bitorudzu' scores 75, over the
        // bare 60 default threshold the arm accepts at, with no Double Metaphone
        // collision (PTRS vs PTRT). The bar takes the honest album not-found;
        // pre-fix this arm auto-played the bait with FoundAlbumInstead.
        var handler = CreateAlbumHandler();

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession == true, "the honest outcome is the album not-found Tell");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NotFoundAlbumByName", "ja-JP", "bitoruzu"), speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bitorudzu", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_LatinAlbum_SameArm_SpellingDrift_StillPlays()
    {
        // The Latin control: the same arm, a Latin query with the spelling-drift
        // shape the JF-336 arm exists for. 'beatels' vs 'Beatles' scores 71,
        // above the same 60 bar the bait cleared, and with no kana origin the
        // matrix is byte-identical: the match plays with FoundAlbumInstead.
        SetupPlugin();
        var (album, tracks) = MakeAlbum("Beatles");
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);

        var handler = CreateAlbumHandler();

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent("beatels", "en-US"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Beatles", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_KanaAlbum_RealCollision_StillPlays()
    {
        // Composition control: 'サトル' romanizes to 'satoru', whose Double
        // Metaphone codes are identical to 'Satoru''s (STR/STR, band 0). The bar
        // composes with the arm's threshold, it does not replace it: a real
        // collision still plays through the arm on a kana-origin query.
        SetupPlugin();
        var (album, tracks) = MakeAlbum("Satoru");
        SetupExactMissWithFuzzyAlbums(new List<BaseItem> { album });
        SetupAlbumTracks(album, tracks);

        var handler = CreateAlbumHandler();

        SkillResponse response = await handler.HandleAsync(
            AlbumIntent("サトル"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "a real code collision must still play through the gated arm");
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
