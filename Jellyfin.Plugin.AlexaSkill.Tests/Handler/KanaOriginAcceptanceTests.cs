using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
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
/// JF-652: the kana-origin acceptance bar. A katakana query romanizes into a
/// romaji class whose score distribution the Latin-calibrated acceptance machinery
/// was never calibrated for (live, deployed a39eb2a8: 'クイーン' -> 'kuin'
/// silently resolved a 91-tie between Queen and Keane to a wrong auto-play;
/// 'ビートルズ' -> 'bitoruzu' plain-fuzzy-accepted 'Sator' at 60). A kana-origin
/// query auto-plays an artist only on a real Double Metaphone collision (the
/// phonetic floor); a plain-fuzzy-only pick is the honest not-found; a floor-level
/// near-tie fires the existing multi-artist disambiguation. Latin queries never
/// enter the gates (the whole pre-existing suite is the Latin regression matrix).
/// </summary>
[Collection("Plugin")]
public class KanaOriginAcceptanceTests : PluginTestBase, IDisposable
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

    private static IntentRequest CreateArtistIntent(string musician)
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["musician"] = new Slot { Name = "musician", Value = musician }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
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

    private static bool HasAudioPlayerDirective(SkillResponse response)
        => response.Response.Directives?.Any(d => d.Type == "AudioPlayer.Play") == true;

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
    // Shape 1 (handler): DM-colliding pair, kana query -> disambiguation
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_KanaQuery_DmCollidingPair_AskDisambiguation_NeverAutoPlay()
    {
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, CodesFromNames(queen, keane));

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(IsDisambiguationAsk(response, "Queen", "Keane"), "must be the multi-artist disambiguation ask naming both");
        Assert.False(HasAudioPlayerDirective(response));
    }

    // ---------------------------------------------------------------
    // Shape 2 (handler): plain-fuzzy-only candidate -> honest not-found
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_KanaQuery_PlainFuzzyOnlyMatch_HonestNotFound_NeverFalseAccept()
    {
        var sator = new MusicArtist { Name = "Sator", Id = Guid.NewGuid() };
        var nirvana = new MusicArtist { Name = "Nirvana", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { sator, nirvana }, CodesFromNames(sator, nirvana));

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("ビートルズ"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "a not-found Tell ends the session");
        Assert.False(HasAudioPlayerDirective(response), "Sator must never auto-play");
    }

    // ---------------------------------------------------------------
    // Shape 3 (handler): single clear DM-collision winner -> auto-play
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_KanaQuery_SingleDmCollisionWinner_AutoPlays()
    {
        var queen = Queen();
        var index = new FakeArtistIndex(new[] { queen }, CodesFromNames(queen));
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem> { song });

        var handler = CreateArtistHandler(index);
        _fx.SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            CreateArtistIntent("クイーン"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == true, "a play ends the session (JF-299 rule)");
        Assert.True(HasAudioPlayerDirective(response), "the clear DM-collision winner must auto-play");
    }

    // ---------------------------------------------------------------
    // Cross-media path: the same two shapes at TryEntityFallbackAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task TryEntityFallback_KanaQuery_DmCollidingPair_AskDisambiguation()
    {
        var queen = Queen();
        var keane = Keane();
        var index = new FakeArtistIndex(new[] { queen, keane }, CodesFromNames(queen, keane));

        // The in-memory search branch issues no library query; the play-shape sink
        // is never reached because the tie downgrades to the ask.
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        var probe = new KanaGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

        SkillResponse? result = await probe.CallTryEntityFallbackAsync(
            "クイーン", jellyfinUser, _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "en-US",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, index, "kana probe", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(IsDisambiguationAsk(result, "Queen", "Keane"), "the cross-media tie must ask, not play");
        Assert.False(HasAudioPlayerDirective(result));
    }

    [Fact]
    public async Task TryEntityFallback_KanaQuery_PlainFuzzyOnlyMatch_ReturnsNull()
    {
        var sator = new MusicArtist { Name = "Sator", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { sator }, CodesFromNames(sator));
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        var probe = new KanaGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

        SkillResponse? result = await probe.CallTryEntityFallbackAsync(
            "ビートルズ", jellyfinUser, _fx.CreateUser(), _fx.CreateSession(), _fx.CreateContext(), "en-US",
            _fx.LibraryManager.Object, _fx.UserDataManager.Object, index, "kana probe", CancellationToken.None);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // The plumb itself + the Latin byte-identical contract
    // ---------------------------------------------------------------

    [Fact]
    public void IsPhoneticFloorScore_DetectsTheFloorBoundary()
    {
        Assert.False(FuzzyMatcher.IsPhoneticFloorScore(FuzzyMatcher.ContainmentScore));
        Assert.True(FuzzyMatcher.IsPhoneticFloorScore(FuzzyMatcher.ContainmentScore + 1));
        Assert.True(FuzzyMatcher.IsPhoneticFloorScore(100));
    }

    [Fact]
    public void PassesArtistMatchAcceptance_LatinContainment_KeepsBareThresholdBehavior()
    {
        // Latin control: the containment class ("Miles" inside "miles davis live",
        // score 90) passes the bare threshold with kanaOrigin false and is refused
        // with kanaOrigin true. Byte-identical Latin behavior is JF-643's pinned
        // contract; the kana bar composes, it does not replace.
        var miles = new MusicArtist { Name = "Miles", Id = Guid.NewGuid() };
        var probe = new KanaGateProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

        Assert.True(probe.CallPassesArtistMatchAcceptance(miles, "miles davis live", _fx.CreateUser(), null, out _, kanaOrigin: false));
        Assert.False(probe.CallPassesArtistMatchAcceptance(miles, "miles davis live", _fx.CreateUser(), null, out _, kanaOrigin: true));
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();

    /// <summary>
    /// Probe handler exposing the kana-gated collaborators directly (the
    /// SharedGateProbeHandler pattern; that probe pins artistIndex null, this one
    /// needs a live index for the phonetic paths).
    /// </summary>
    private sealed class KanaGateProbeHandler : BaseHandler
    {
        public KanaGateProbeHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public Task<SkillResponse?> CallTryEntityFallbackAsync(
            string slotText,
            Jellyfin.Database.Implementations.Entities.User jellyfinUser,
            Entities.User user,
            SessionInfo session,
            Context context,
            string locale,
            ILibraryManager libraryManager,
            IUserDataManager userDataManager,
            IArtistIndex artistIndex,
            string logLabel,
            CancellationToken cancellationToken)
            => CrossMedia.TryEntityFallbackAsync(
                slotText, jellyfinUser, user, session, context, locale,
                libraryManager, userDataManager, null, artistIndex, logLabel, cancellationToken);

        public bool CallPassesArtistMatchAcceptance(
            BaseItem artist,
            string query,
            Entities.User user,
            IArtistIndex? artistIndex,
            out int score,
            bool kanaOrigin)
            => CrossMedia.PassesArtistMatchAcceptance(artist, query, user, artistIndex, out score, kanaOrigin);
    }
}
