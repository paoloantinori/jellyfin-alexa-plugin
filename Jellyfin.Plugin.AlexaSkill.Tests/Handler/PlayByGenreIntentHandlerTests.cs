using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayByGenreIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlayByGenreIntentHandler CreateHandler()
    {
        return new PlayByGenreIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? genre = null)
    {
        var intent = new Intent { Name = IntentNames.PlayByGenre };
        intent.Slots = new Dictionary<string, Slot>();

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public void CanHandle_PlayByGenreIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoGenreSlot_ReturnsDidNotCatchMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_WithGenreItems_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audioItem = new Audio { Name = "Rock Song", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_GenreNotFound_ReturnsNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "polka");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_PassesGenreToQuery()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "jazz");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Jazz Song", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.Genres);
        Assert.Contains("jazz", capturedQuery.Genres);
    }

    [Fact]
    public async Task HandleAsync_DefaultsToAudioType()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Rock Song", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.Single(capturedQuery.IncludeItemTypes);
        Assert.Equal(BaseItemKind.Audio, capturedQuery.IncludeItemTypes[0]);
    }

    [Fact]
    public async Task HandleAsync_SetsQueueFromResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var items = new List<BaseItem>
        {
            new Audio { Name = "Song 1", Id = Guid.NewGuid() },
            new Audio { Name = "Song 2", Id = Guid.NewGuid() },
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task HandleAsync_MusicDisabled_ReturnsNotFound()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => c.MusicEnabled = false, "genre-music-test");
        _fx.SetupUserMock();

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateIntentRequest(genre: "rock"),
            _fx.CreateContext(),
            _fx.CreateUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.NotEmpty(speech);
    }

    // --- JF-643: katakana genre slot vs Latin genre tags ---

    /// <summary>
    /// Mock dispatcher for the genre flow: audio-kind genre queries resolve
    /// through the genre table below (exact Genres equality, empty = miss); the
    /// genre-vocabulary query (Genre/MusicGenre kinds) returns the vocabulary
    /// rows. The resolver only reads Name/Id from vocabulary rows, so any
    /// BaseItem stand-in is faithful to what the mock returns.
    /// </summary>
    private static IReadOnlyList<BaseItem> DispatchGenreFlow(InternalItemsQuery q, Dictionary<string, Audio> genreTable, List<string> audioQueriesSeen)
    {
        if (q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Genre)
            || q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicGenre))
        {
            return new List<BaseItem>
            {
                new MusicArtist { Name = "Jazz", Id = Guid.NewGuid() },
                new MusicArtist { Name = "Rock", Id = Guid.NewGuid() },
                new MusicArtist { Name = "Pop", Id = Guid.NewGuid() }
            };
        }

        string? genre = q.Genres?.FirstOrDefault();
        if (genre != null)
        {
            audioQueriesSeen.Add(genre);
            return genreTable.TryGetValue(genre, out Audio? song)
                ? new List<BaseItem> { song }
                : new List<BaseItem>();
        }

        return new List<BaseItem>();
    }

    [Fact]
    public async Task HandleAsync_KatakanaGenreSlot_ResolvesToLatinTag_AndPlays_JF643()
    {
        // 'ジャズ' romanizes to 'jazu'; the exact Genres filter misses, the
        // kana-gated resolution tier matches the vocabulary phonetically
        // ('jazu' and 'Jazz' share the Double Metaphone code), and the re-query
        // with the canonical tag 'Jazz' returns playable audio.
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "ジャズ");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        var jazzSong = new Audio { Name = "Jazz Song", Id = Guid.NewGuid() };
        var genreTable = new Dictionary<string, Audio> { ["Jazz"] = jazzSong };
        var audioQueries = new List<string>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => DispatchGenreFlow(q, genreTable, audioQueries));

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Equal(new[] { "jazu", "Jazz" }, audioQueries);
    }

    [Fact]
    public async Task HandleAsync_LatinGenreSlot_NeverRunsVocabularyResolution_JF643()
    {
        // The kana gate: a Latin genre query keeps its exact-match behavior and
        // must not even fetch the genre vocabulary.
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "jazz");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        bool vocabularyQueried = false;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Genre)
                    || q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicGenre))
                {
                    vocabularyQueried = true;
                }

                return new List<BaseItem> { new Audio { Name = "Jazz Song", Id = Guid.NewGuid() } };
            });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(vocabularyQueried);
    }

    [Fact]
    public async Task HandleAsync_LatinGenreTypo_StillNotFound_NoResolution_JF643()
    {
        // A Latin MISS ('jazzz', no kana) keeps the pre-JF-643 behavior: no
        // vocabulary fetch, no phonetic rescue, the plain NotFoundGenre tell.
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "jazzz");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();
        _fx.SetupUserMock();

        bool vocabularyQueried = false;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Genre)
                    || q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicGenre))
                {
                    vocabularyQueried = true;
                }

                return new List<BaseItem>();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(vocabularyQueried);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }
}
