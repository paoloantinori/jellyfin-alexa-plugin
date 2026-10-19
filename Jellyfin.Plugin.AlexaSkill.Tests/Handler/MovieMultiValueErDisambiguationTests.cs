using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Alexa.NET.Assertions;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-876/JF-877: the multi-value ER arbitration on the MOVIE path. The
/// MovieTitle catalog's partial-name synonyms (widened to Movie in JF-874)
/// make a spoken token ER-match SEVERAL catalog entries when two movies share
/// a first substantive word (live it-IT 2026-10-19: «il film gruffalò» ->
/// ER_SUCCESS [«Il Gruffalò e la sua piccolina», «Il Gruffalò»], Amazon's
/// rank-1 naming the SEQUEL). The plain rank-1 canonical read auto-played
/// Amazon's pick with no prompt; these suites pin the gate that asks which
/// movie to play when the ER ambiguity is REAL in the library, the
/// stale-catalog survivor adoption that plays the single resolved movie, the
/// unchanged single-value and zero-resolve paths around the gate, and the
/// JF-876 song-carrier delegation that still fires on a multi-valued slot.
/// The movie leg ALWAYS asks on two or more distinct movies: the artist
/// path's JF-885 rank-1 dominance bypass is deliberately not ported (the
/// maintainer call recorded on the task).
/// </summary>
[Collection("Plugin")]
public class MovieMultiValueErDisambiguationTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public MovieMultiValueErDisambiguationTests()
    {
        _fx.SetupUserMock();
    }

    private PlayVideoIntentHandler CreateHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _fx.CreateSongHandler());

    /// <summary>
    /// The library mock of the shared-first-word family: each canonical's
    /// exact-name query returns its movie; everything else (the would-be
    /// search terms, the song path's probes) returns nothing.
    /// </summary>
    private void SetupMoviesByName(params BaseItem[] movies)
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => movies.FirstOrDefault(m => q.SearchTerm == m.Name) is { } hit
                ? new List<BaseItem> { hit }
                : new List<BaseItem>());
    }

    // ---------------------------------------------------------------
    // The multi-value shape: both ER candidates real in the library
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayMovie_MultiValueEr_BothInLibrary_AsksWhichMovie()
    {
        // The live shape (2026-10-19): «mettere il film gruffalò» with the
        // partial synonym matching BOTH Gruffalò films, Amazon ranking the
        // SEQUEL first. Pre-JF-877 the rank-1-only read silently auto-played
        // the sequel; the gate must ask instead. No JF-885 dominance bypass
        // exists on this leg, so the ask fires even with rank-1 exactness
        // available (the design decision recorded on the task).
        var sequel = TestHelpers.CreateMovie("Il Gruffalò e la sua piccolina");
        var original = TestHelpers.CreateMovie("Il Gruffalò");
        SetupMoviesByName(sequel, original);

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("il gruffalò", "movie", sequel.Name, original.Name)),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // The ask: session open, no launch, the VIDEO disambiguation state
        // carrying BOTH movies in Amazon's rank order (rank-1 is what a plain
        // "yes" plays), cursor 0, and the existing DisambiguatePrompt naming
        // the first candidate.
        TestHelpers.AssertSessionOpen(response, "the ask keeps the session open");
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        var state = DisambiguationHelper.ReadState(response.SessionAttributes);
        Assert.NotNull(state);
        Assert.Equal(DisambiguationHelper.MediaTypeVideo, state!.Value.MediaType);
        Assert.Equal(0, state.Value.Index);
        Assert.Equal(new[] { sequel.Id.ToString(), original.Id.ToString() }, state.Value.Matches.Select(m => m.Id));
        Assert.Equal(new[] { sequel.Name, original.Name }, state.Value.Matches.Select(m => m.Name));
        Assert.Contains(sequel.Name, TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayMovie_MultiValueErAsk_YesConfirms_PlaysRank1Movie()
    {
        // The stored ids must be REAL library ids: the confirm leg resolves
        // the candidate by GetItemById (raw ER names are dead on "yes"), and a
        // plain "yes" at cursor 0 plays Amazon's rank-1 (the winner-first
        // contract the gate's rank ordering provides).
        var sequel = TestHelpers.CreateMovie("Il Gruffalò e la sua piccolina");
        var original = TestHelpers.CreateMovie("Il Gruffalò");
        SetupMoviesByName(sequel, original);
        _fx.LibraryManager.Setup(l => l.GetItemById(sequel.Id)).Returns(sequel);

        var handler = CreateHandler();
        SkillResponse ask = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("il gruffalò", "movie", sequel.Name, original.Name)),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var yesHandler = new YesIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        SkillResponse confirmed = await yesHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonYes }, Locale = "it-IT", RequestId = "test-yes" },
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(),
            ask.SessionAttributes, CancellationToken.None);

        var directive = confirmed.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains(sequel.Id.ToString(), directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(original.Id.ToString(), directive.VideoItem.Source, StringComparison.Ordinal);
        // JF-387: the terminal play carries no disambiguation state.
        TestHelpers.AssertNoDisambiguationState(confirmed);
    }

    [Fact]
    public async Task PlayMovie_MultiValueErAsk_NoAdvances_ToTheSecondMovie()
    {
        // The shared yes/no cycling state machine (AskNextMatch via
        // NoIntentHandler) walks the movie list exactly as it walks the video
        // search path's: the first "no" presents rank-2 and advances the
        // cursor.
        var sequel = TestHelpers.CreateMovie("Il Gruffalò e la sua piccolina");
        var original = TestHelpers.CreateMovie("Il Gruffalò");
        SetupMoviesByName(sequel, original);

        var handler = CreateHandler();
        SkillResponse ask = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("il gruffalò", "movie", sequel.Name, original.Name)),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var noHandler = new NoIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
        SkillResponse next = await noHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonNo }, Locale = "it-IT", RequestId = "test-no" },
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(),
            ask.SessionAttributes, CancellationToken.None);

        TestHelpers.AssertSessionOpen(next, "each cycling advance keeps the session open");
        Assert.Contains(original.Name, TestHelpers.GetSpeechText(next), StringComparison.OrdinalIgnoreCase);
        var state = DisambiguationHelper.ReadState(next.SessionAttributes);
        Assert.NotNull(state);
        Assert.Equal(1, state!.Value.Index);
        Assert.Equal(new[] { sequel.Id.ToString(), original.Id.ToString() }, state.Value.Matches.Select(m => m.Id));
    }

    [Fact]
    public async Task PlayMovie_MultiValueEr_FourInLibrary_AsksTheStateCap_PaysNoDiscardedQuery()
    {
        // The JF-735/JF-877 truncation verdict, pinned (code-review F4): the
        // video-family ask state caps at FirstMatchStateCap (3), so a
        // franchise-sized family (the JF-874 same-first-word shape) asks the
        // first three and ranks past the cap are unreachable through the
        // cycle. The resolver therefore STOPS at the cap: the fourth
        // canonical's query is never paid (code-review F3, the fresh-budget
        // bound), pinned here by the query capture.
        var rocky1 = TestHelpers.CreateMovie("Rocky");
        var rocky2 = TestHelpers.CreateMovie("Rocky II");
        var rocky3 = TestHelpers.CreateMovie("Rocky III");
        var rocky4 = TestHelpers.CreateMovie("Rocky IV");
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => new[] { rocky1, rocky2, rocky3, rocky4 }
                .FirstOrDefault(m => q.SearchTerm == m.Name) is { } hit
                ? new List<BaseItem> { hit }
                : new List<BaseItem>());

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed(
                "rocky", "movie", rocky1.Name, rocky2.Name, rocky3.Name, rocky4.Name)),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        TestHelpers.AssertSessionOpen(response, "the ask keeps the session open");
        var state = DisambiguationHelper.ReadState(response.SessionAttributes);
        Assert.NotNull(state);
        Assert.Equal(
            new[] { rocky1.Id.ToString(), rocky2.Id.ToString(), rocky3.Id.ToString() },
            state!.Value.Matches.Select(m => m.Id));
        Assert.All(queries, q => Assert.Equal(new[] { BaseItemKind.Movie }, q.IncludeItemTypes));
        Assert.Equal(3, queries.Count);
    }

    // ---------------------------------------------------------------
    // Stale catalog: only ONE ER candidate exists in the library
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayMovie_MultiValueEr_OnlyRank2Resolves_PlaysTheSurvivor_NoAsk()
    {
        // Rank-1 is stale (movie left the library, weekly sync lag): the
        // ambiguity is NOT real in this library, so no two-name prompt (a
        // stale name would offer a dead end), and the gate does NOT throw its
        // answer away: the single proven library movie plays (the JF-690
        // survivor adoption; pre-change this leg searched the stale rank-1
        // canonical and answered not-found with the survivor sitting
        // resolved).
        var sequelName = "Il Gruffalò e la sua piccolina";
        var original = TestHelpers.CreateMovie("Il Gruffalò");
        SetupMoviesByName(original);

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("il gruffalò", "movie", sequelName, original.Name)),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains(original.Id.ToString(), directive.VideoItem.Source, StringComparison.Ordinal);
        Assert.Null(response.Response.ShouldEndSession);
        TestHelpers.AssertNoDisambiguationState(response);
    }

    [Fact]
    public async Task PlayMovie_MultiValueEr_OnlyRank1Resolves_PlaysIt_NoAsk()
    {
        // The mirror leg: when the SOLE library-resident candidate is rank-1
        // itself, the gate stays closed and the exact hit plays as before
        // (the JF-420.1 shape wearing a multi-value ER list).
        var sequel = TestHelpers.CreateMovie("Il Gruffalò e la sua piccolina");
        SetupMoviesByName(sequel);

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("il gruffalò", "movie", sequel.Name, "Il Gruffalò")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains(sequel.Id.ToString(), directive.VideoItem.Source, StringComparison.Ordinal);
        TestHelpers.AssertNoDisambiguationState(response);
    }

    // ---------------------------------------------------------------
    // Zero resolves / no multi-value: the existing chain is unchanged
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayMovie_MultiValueEr_NothingResolves_KeepsRank1FallbackChain()
    {
        // Zero resolves closes the gate: the existing fallback chain (the
        // rank-1 canonical feeding the search, then the miss tail) stays
        // byte-identical. The gate's own per-canonical queries are Movie-only;
        // the chain's search keeps its Movie+Episode shape, so the two are
        // distinguishable in the capture.
        var rank1 = "Alien: Covenant";
        var rank2 = "Alien";
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("alien", "movie", rank1, rank2)),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // The gate paid one Movie-only exact-name query per canonical, in
        // rank order, BEFORE the chain's own search ran on the rank-1 term.
        Assert.Equal(new[] { rank1, rank2 }, queries.Take(2).Select(q => q.SearchTerm));
        Assert.All(queries.Take(2), q => Assert.Equal(new[] { BaseItemKind.Movie }, q.IncludeItemTypes));
        Assert.Contains(queries, q => q.SearchTerm == rank1 && q.IncludeItemTypes.Contains(BaseItemKind.Episode));

        // The honest miss keeps the NotFoundVideo Tell naming the rank-1 term.
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains(rank1, TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayMovie_SingleValueEr_GateClosed_ExistingSearchOnly()
    {
        // The ordinary single-catalog-movie shape (< 2 ER values) must not pay
        // a single extra query: the gate is closed and the existing search
        // runs exactly as before.
        var movie = TestHelpers.CreateMovie("Sunny Dancer");
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(queries)))
            .Returns((InternalItemsQuery q) => q.SearchTerm == movie.Name ? new List<BaseItem> { movie } : new List<BaseItem>());

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlot("sunny dancer", movie.Name, slotName: "movie")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Single(queries);
        Assert.Contains(BaseItemKind.Episode, queries[0].IncludeItemTypes);
    }

    // ---------------------------------------------------------------
    // JF-876 regression pin: the song-carrier delegation runs BEFORE the gate
    // ---------------------------------------------------------------

    [Fact]
    public async Task PlayMovie_SongCarrierFill_MultiValueEr_StillDelegatesToSongPath()
    {
        // The entry guard's delegation reads the RANK-1 canonical only and
        // runs at the TOP of HandleAsync (before the multi-value gate below
        // it): a carrier-leading fill must reach the song path even when the
        // movie slot's ER list is multi-valued, and no movie-shaped query may
        // be issued on the way.
        var song = TestHelpers.CreateSong("Sunny Dancer");
        var movieQueries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(Capture.In(movieQueries)))
            .Returns((InternalItemsQuery q) => q.SearchTerm == "sunny dancer"
                ? (q.IncludeItemTypes.Contains(BaseItemKind.Audio)
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>())
                : new List<BaseItem>());

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            TestHelpers.CreatePlayMovieIntent(TestHelpers.ResolvedSlotMultiValueNamed("il brano sunny dancer", "movie", "Sunny Dancer", "Sunny Dancer II")),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var play = Assert.Single(response.Response.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Contains("/Audio/", play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.DoesNotContain(movieQueries, q => q.IncludeItemTypes.Contains(BaseItemKind.Movie));
    }
}
