using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Assertions;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-777 (the addendum's verification-debt leg): per-surface pins for the six
/// JF-776 HandleFuzzyMiss selector sites left untested (AddToQueue, PlayNext,
/// PlaySong, PlayVideo, SearchMedia, PlayPodcast) plus the PlayPodcast JF-640
/// guard leg. Each pin drives its handler with a kana-tagged candidate pool and
/// a kana query, so reverting that site's scoring selector to the raw
/// <c>x =&gt; x.Name</c> (the JF-776 pre-change shape, the regression class the
/// addendum records as suite-passing) fails the pin: reading-side scoring
/// auto-accepts the exact match while raw scoring falls below the suggestion
/// threshold into the site's not-found/disambiguation branch. The SearchMedia
/// pin instead holds the Confirm-mode leg (its full-coverage pre-check owns the
/// auto-accept shape; see <see cref="SongKanaBarRefuseAndContinueTests"/>), and
/// the two PlayPodcast pins discriminate the miss block and the guard leg
/// separately (an unrelated Latin album as the raw-best bait). 'ヨルニカケル'
/// reads 'yorunikakeru' (the JF-773 fixtures). NOTE: the pools of the
/// SearchTerm-fed sites (AddToQueue, PlayNext, PlayPodcast) are mock-wired with
/// kana-tagged rows the production SearchTerm tier cannot return by itself; the
/// pins hold the flow-coupling contract those sites were converted for (the
/// guard/miss agreement the JF-776 simplify round documented), which is exactly
/// what silently rots when a selector reverts.
/// </summary>
[Collection("Plugin")]
public class KanaTaggedHandleFuzzyMissSiteReachabilityTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private const string KanaSongName = "ヨルニカケル";
    private const string KanaSuffixedSongName = "ヨルニカケル デラックス";

    private static Audio Song(string name) => TestHelpers.CreateSong(name);

    private void SetupPlugin()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "jf777-fuzzymiss-site-reachability");
        _fx.SetupUserMock();
    }

    private static IntentRequest IntentWithSlots(string intentName, Dictionary<string, string?> slots, string locale = "ja-JP")
    {
        var intent = new Intent { Name = intentName };
        intent.Slots = new Dictionary<string, Slot>();
        foreach (var (name, value) in slots)
        {
            intent.Slots[name] = new Slot { Name = name, Value = value };
        }

        // The queue twins read the musician slot key directly (Slots?["musician"]
        // throws on a missing key, an empty value is the no-musician shape).
        intent.Slots.TryAdd("musician", new Slot { Name = "musician" });
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    /// <summary>
    /// Wires GetItemList to return <paramref name="songs"/> for every Audio-kind
    /// query that is not an artist-scoped lookup (the SearchTerm scans of the
    /// queue sites and SearchMedia's playable-kind scan; the artist-fallback
    /// queries must miss so the pool stays the mock's list) and miss otherwise.
    /// </summary>
    private void SetupSongQueries(List<BaseItem> songs)
    {
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.IncludeItemTypes != null
                && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio)
                && q.ArtistIds is not { Length: > 0 }
                ? songs
                : new List<BaseItem>());
    }

    // ---------------------------------------------------------------
    // AddToQueue / PlayNext (the queue twins, two-song pools)
    // ---------------------------------------------------------------

    [Fact]
    public async Task AddToQueue_KanaTaggedSongs_KanaQuery_Plays_JF777()
    {
        // Reading-side scoring auto-accepts the exact 'ヨルニカケル' (100 on the
        // reading) and the fresh-session queue add starts playback; the raw-name
        // revert scores ~0, falls below the suggestion threshold, and answers the
        // AskFirstMatch disambiguation with no play.
        SetupPlugin();
        SetupSongQueries(new List<BaseItem> { Song(KanaSongName), Song(KanaSuffixedSongName) });
        using var queueManager = TestHelpers.CreateDeviceQueueManager("jf777-addtoqueue");
        var handler = new AddToQueueIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.LoggerFactory,
            queueManager: queueManager);

        SkillResponse response = await handler.HandleAsync(
            IntentWithSlots(IntentNames.AddToQueue, new Dictionary<string, string?> { ["song"] = KanaSongName }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the kana-tagged song must be reachable through the queue site's reading-side scoring");
    }

    [Fact]
    public async Task PlayNext_KanaTaggedSongs_KanaQuery_Plays_JF777()
    {
        SetupPlugin();
        SetupSongQueries(new List<BaseItem> { Song(KanaSongName), Song(KanaSuffixedSongName) });
        using var queueManager = TestHelpers.CreateDeviceQueueManager("jf777-playnext");
        var handler = new PlayNextIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.LoggerFactory,
            queueManager: queueManager);

        SkillResponse response = await handler.HandleAsync(
            IntentWithSlots(IntentNames.PlayNext, new Dictionary<string, string?> { ["song"] = KanaSongName }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the kana-tagged song must be reachable through the insert-next site's reading-side scoring");
    }

    // ---------------------------------------------------------------
    // PlaySong (the n-gram title fallback feeding a two-song pool)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlaySong_KanaTaggedPair_KanaQuery_Plays_JF777()
    {
        // Both index hits clear the shared JF-654 list bar (the exact at the
        // near-exact plain score, the parenthetical-live variant through the
        // stripped-reading collision), so the pool reaches the miss block and
        // the reading-side selector auto-accepts the exact; the raw-name revert
        // scores ~0 on both and answers the AskFirstMatch disambiguation.
        SetupPlugin();
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
            songNgramIndex: new TestHelpers.FakeSongIndex((Song(KanaSongName), 105.0), (Song("ヨルニカケル（ライブ）"), 85.0)));

        SkillResponse response = await handler.HandleAsync(
            IntentWithSlots(IntentNames.PlaySong, new Dictionary<string, string?> { ["song"] = KanaSongName }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the kana-tagged song must be reachable through the title fallback's miss block");
    }

    // ---------------------------------------------------------------
    // PlayVideo (the two-movie pool)
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayVideo_KanaTaggedMovies_KanaQuery_Plays_JF777()
    {
        SetupPlugin();
        var exact = new Movie { Name = KanaSongName, Id = Guid.NewGuid() };
        var suffixed = new Movie { Name = KanaSuffixedSongName, Id = Guid.NewGuid() };
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { suffixed, exact });
        var handler = new PlayVideoIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);

        SkillResponse response = await handler.HandleAsync(
            IntentWithSlots(IntentNames.PlayVideo, new Dictionary<string, string?> { ["title"] = KanaSongName }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.HasDirective<VideoAppLaunchDirective>());
    }

    // ---------------------------------------------------------------
    // SearchMedia (the Confirm-mode leg; the auto-accept shape is the
    // pre-check walk's, pinned in SongKanaBarRefuseAndContinueTests)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SearchMedia_KanaTaggedCandidates_PartialCoverageQuery_ConfirmAskNamesTheCandidate_JF777()
    {
        // The pre-check withholds the partial-coverage pick ('デラックス' is not
        // a token of the exact song's title) and the miss block's reading-side
        // scoring carries the SAME candidate into the Confirm "did you mean"
        // ask, speaking the DISPLAY name (the speechSelector seam); the raw-name
        // revert scores ~0, falls below the suggestion threshold, and answers
        // the multi-result disambiguation prompt instead.
        SetupPlugin();
        SetupSongQueries(new List<BaseItem> { Song(KanaSongName), Song("サトル") });
        var handler = new SearchMediaIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);

        SkillResponse response = await handler.HandleAsync(
            IntentWithSlots(IntentNames.SearchMedia, new Dictionary<string, string?> { ["query"] = $"{KanaSongName} デラックス" }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(KanaSongName, speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("のことですか", speech, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // PlayPodcast: the miss block and the JF-640 guard leg, discriminated
    // ---------------------------------------------------------------

    private PlayPodcastIntentHandler CreatePodcastHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    /// <summary>
    /// Wires the podcast shape queries: the album-kind SearchTerm scan returns
    /// <paramref name="albums"/>, the series-kind scan <paramref name="series"/>,
    /// and the episode resolution returns <paramref name="episodes"/>.
    /// </summary>
    private void SetupPodcastQueries(List<BaseItem> albums, List<BaseItem> series, List<BaseItem> episodes)
    {
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode) ? episodes
                : q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series) ? series
                : q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum) ? albums
                : new List<BaseItem>());
    }

    private static (List<BaseItem> Series, List<BaseItem> Episodes) KanaSeriesWithEpisode(string seriesName)
    {
        var seriesItem = new Series { Name = seriesName, Id = Guid.NewGuid() };
        var episode = new Episode { Name = "Episode 1", Id = Guid.NewGuid(), DateCreated = DateTime.UtcNow.AddDays(-1) };
        return (new List<BaseItem> { seriesItem }, new List<BaseItem> { episode });
    }

    [Fact]
    public async Task PlayPodcast_KanaTaggedSeriesPair_KanaQuery_Plays_JF777()
    {
        // The miss block's leg: the reading-side selector auto-accepts the exact
        // Series (the guard keeps the delegate, a Series is not the guarded
        // MusicAlbum) and the latest episode plays; the raw-name revert scores
        // ~0 on both series, falls below the suggestion threshold, and answers
        // the AskFirstMatch disambiguation.
        SetupPlugin();
        var exact = KanaSeriesWithEpisode(KanaSongName);
        var suffixed = new Series { Name = KanaSuffixedSongName, Id = Guid.NewGuid() };
        SetupPodcastQueries(
            albums: new List<BaseItem>(),
            series: new List<BaseItem>(new[] { exact.Series[0], suffixed }),
            episodes: exact.Episodes);

        SkillResponse response = await CreatePodcastHandler().HandleAsync(
            IntentWithSlots(IntentNames.PlayPodcast, new Dictionary<string, string?> { ["podcast_name"] = KanaSongName }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the kana-tagged series must be reachable through the podcast miss block's reading-side scoring");
    }

    [Fact]
    public async Task PlayPodcast_Jf640Guard_ReadingBestSeries_RawBestAlbum_StillPlaysTheSeries_JF777()
    {
        // The guard's own leg, discriminated from the miss block: the pool holds
        // an unrelated Latin album (the raw-name best, low but nonzero while the
        // kana series scores 0 raw) and the kana series (the reading-side best,
        // exact). The reading-aware guard sees a Series best and keeps the
        // auto-play delegate, so the miss block's exact match plays; a guard
        // reverted to the raw name sees the MusicAlbum best, suppresses the
        // delegate, and the same miss block falls to its Confirm ask instead.
        SetupPlugin();
        var exact = KanaSeriesWithEpisode(KanaSongName);
        SetupPodcastQueries(
            albums: new List<BaseItem> { new MusicAlbum { Name = "Nightcall Deluxe", Id = Guid.NewGuid() } },
            series: exact.Series,
            episodes: exact.Episodes);

        SkillResponse response = await CreatePodcastHandler().HandleAsync(
            IntentWithSlots(IntentNames.PlayPodcast, new Dictionary<string, string?> { ["podcast_name"] = KanaSongName }),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(TestHelpers.GetPlayDirective(response) != null, "the reading-aware guard must not downgrade a Series-best kana query into the album confirm");
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();
}
