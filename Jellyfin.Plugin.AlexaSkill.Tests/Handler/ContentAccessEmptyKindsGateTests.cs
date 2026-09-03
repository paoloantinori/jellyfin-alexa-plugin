#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-466: Jellyfin treats an EMPTY (length 0) IncludeItemTypes as NO type filter,
/// not as match-nothing (verified against Jellyfin 10.11.11 BaseItemRepository and
/// probed live: IncludeItemTypes= on a movie-only genre returned the full 275-item
/// count, identical to omitting the parameter). So when FilterByContentAccess
/// empties the kind set (every requested category disabled), handing that array to
/// a query WIDENS it to all types: PlayByGenre on a genre shared with movies would
/// queue movies through the audio stream URL. These tests pin the hard zeros: with
/// the applicable flags off, each affected handler issues ZERO library queries and
/// answers the shared disabled-type response. The library mocks always ARM the
/// would-be widening hit, so a pass proves the gate stopped the path, not that the
/// library was empty. Callers already structurally safe (SearchMedia: its kind sets
/// always contain Playlist, which IsTypeAllowed never removes) are not retested here.
/// </summary>
[Collection("Plugin")]
public class ContentAccessEmptyKindsGateTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new();
    private readonly List<InternalItemsQuery> _queries = new();

    public ContentAccessEmptyKindsGateTests()
    {
        TestHelpers.EnsurePluginInstance(
            _fx.Config, _fx.LoggerFactory,
            c =>
            {
                c.MusicEnabled = _fx.Config.MusicEnabled;
                c.VideosEnabled = _fx.Config.VideosEnabled;
                c.BooksEnabled = _fx.Config.BooksEnabled;
            },
            "alexa-content-access-empty-kinds-test");

        // Record every issued query: the disabled-flag assertions below prove the
        // path never RUNS, not merely that it returned nothing. The armed answer
        // returns a non-audio item for ANY query, the exact widening payload the
        // bug would have played.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                _queries.Add(q);
                return new List<BaseItem> { new Movie { Name = "Action Movie", Id = Guid.NewGuid() } };
            });
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();

    // Caller 1: PlayByGenre. Music disabled; the genre is shared with movies (the
    // armed library would return a movie for the genre query). The JF-466 entry
    // gate must speak the disabled-type response and issue no query at all.
    [Fact]
    public async Task PlayByGenre_MusicDisabled_GenreSharedWithMovies_NoQueryDisabledTell()
    {
        DisableMusic();
        _fx.SetupUserMock();

        var handler = CreateGenreHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayByGenre, ("genre", "action")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // Caller 2a: PlayRandom, no slot. Default kinds are Movie+Episode; videos
    // disabled empties the set, so the random query must not run (it would return
    // the armed movie, or worse anything in the library) and the handler speaks
    // the shared disabled-type response.
    [Fact]
    public async Task PlayRandom_VideosDisabled_NoSlot_NoQueryDisabledTell()
    {
        DisableVideos();
        _fx.SetupUserMock();

        var handler = CreateRandomHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayRandom),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // Caller 2b: PlayRandom, slot "audio". Music disabled empties the mapped kind
    // set ([Audio]); same hard zero and shared disabled-type response, even with
    // a genre slot present.
    [Fact]
    public async Task PlayRandom_MusicDisabled_AudioSlotWithGenre_NoQueryDisabledTell()
    {
        DisableMusic();
        _fx.SetupUserMock();

        var handler = CreateRandomHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayRandom, ("media_type", "audio"), ("genre", "rock")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // PlayRandom control: no flags off means the query still runs, with a
    // NON-EMPTY IncludeItemTypes (the widening shape must never appear).
    [Fact]
    public async Task PlayRandom_AllEnabled_IssuesQueryWithNonEmptyKinds()
    {
        _fx.SetupUserMock();

        var handler = CreateRandomHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayRandom),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotEmpty(_queries);
        Assert.All(_queries, q => Assert.NotEmpty(q.IncludeItemTypes));
        Assert.NotNull(response);
    }

    // Caller 3: Recommend. Both default kinds (Audio, Movie) disabled empties the
    // set; the history/genre/fallback queries must not run.
    [Fact]
    public async Task Recommend_MusicAndVideosDisabled_NoQueryDisabledTell()
    {
        DisableMusic();
        DisableVideos();
        _fx.SetupUserMock();

        var handler = CreateRecommendHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.Recommend),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response?.ShouldEndSession ?? true, "the disabled response must end the session (Tell)");
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // Caller 4: BrowseLibrary, category path. Books disabled empties the kind set
    // for "libri" (AudioBook); the primary query AND the fuzzy fallback (which
    // re-queries the raw kind) must both be skipped.
    [Fact]
    public async Task BrowseLibrary_BooksDisabled_CategoryKind_NoQueryDisabledTell()
    {
        DisableBooks();
        _fx.SetupUserMock();

        var handler = CreateBrowseHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.BrowseLibrary, ("browse_category", "libri")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response?.ShouldEndSession ?? true, "the disabled response must end the session (Tell)");
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // Caller 5: BrowseLibrary, genre path. Audio and Movie are the only browsable
    // kinds there; both disabled empties the set, so the genre query must not run.
    [Fact]
    public async Task BrowseLibrary_MusicAndVideosDisabled_GenreQuery_NoQueryDisabledTell()
    {
        DisableMusic();
        DisableVideos();
        _fx.SetupUserMock();

        var handler = CreateBrowseHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.BrowseLibrary, ("browse_category", "generi"), ("filter", "rock")),
            _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response?.ShouldEndSession ?? true, "the disabled response must end the session (Tell)");
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // Shared choke point (covers Resume, ContinueWatching, StartOver): an empty
    // content-type set returns (null, 0) WITHOUT querying, instead of issuing the
    // widened query that would resume an item of any type.
    [Fact]
    public void FindLastPlayedItemWithProgress_EmptyContentTypes_NoQueryNullItem()
    {
        var probe = new FindProgressProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

        var (item, ticks) = probe.CallFindLastPlayedItemWithProgress(
            new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"),
            _fx.LibraryManager.Object,
            _fx.UserDataManager.Object,
            _fx.CreateUser(),
            Array.Empty<BaseItemKind>(),
            _fx.LoggerFactory.CreateLogger<FindProgressProbeHandler>());

        Assert.Null(item);
        Assert.Equal(0, ticks);
        Assert.Empty(_queries);
    }

    // Choke-point control: a non-empty set still queries (the guard is a skip, not
    // a blanket refusal).
    [Fact]
    public void FindLastPlayedItemWithProgress_NonEmptyContentTypes_Queries()
    {
        var probe = new FindProgressProbeHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

        var (item, _) = probe.CallFindLastPlayedItemWithProgress(
            new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"),
            _fx.LibraryManager.Object,
            _fx.UserDataManager.Object,
            _fx.CreateUser(),
            new[] { BaseItemKind.Audio },
            _fx.LoggerFactory.CreateLogger<FindProgressProbeHandler>());

        // The armed library returns a Movie; the helper only accepts items with
        // server-side progress, of which the mock has none, so the item is null
        // but the QUERY ran.
        Assert.Null(item);
        Assert.Single(_queries);
    }

    private static IntentRequest CreateIntent(string intentName, params (string Name, string Value)[] slots)
    {
        var intent = new Intent { Name = intentName };
        if (slots.Length > 0)
        {
            intent.Slots = new Dictionary<string, Slot>();
            foreach ((string name, string value) in slots)
            {
                intent.Slots[name] = new Slot { Name = name, Value = value };
            }
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private PlayByGenreIntentHandler CreateGenreHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private PlayRandomIntentHandler CreateRandomHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    private RecommendIntentHandler CreateRecommendHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private BrowseLibraryIntentHandler CreateBrowseHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory);

    // Each helper writes the flag on BOTH references (load-bearing: when
    // EnsurePluginInstance created the Plugin the two configs are the same object,
    // but when an instance pre-existed they differ).
    private void DisableMusic()
    {
        _fx.Config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;
    }

    private void DisableVideos()
    {
        _fx.Config.VideosEnabled = false;
        Plugin.Instance!.Configuration.VideosEnabled = false;
    }

    private void DisableBooks()
    {
        _fx.Config.BooksEnabled = false;
        Plugin.Instance!.Configuration.BooksEnabled = false;
    }
}

/// <summary>
/// JF-466 probe: minimal concrete BaseHandler exposing the protected shared
/// resume helper for direct testing (same pattern as SharedGateProbeHandler).
/// </summary>
internal sealed class FindProgressProbeHandler : BaseHandler
{
    public FindProgressProbeHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => true;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(ResponseBuilder.Tell("test"));

    public (BaseItem? Item, long PositionTicks) CallFindLastPlayedItemWithProgress(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Entities.User pluginUser,
        BaseItemKind[] contentTypes,
        ILogger? logger = null)
        => FindLastPlayedItemWithProgress(jellyfinUser, libraryManager, userDataManager, pluginUser, contentTypes, logger);
}
