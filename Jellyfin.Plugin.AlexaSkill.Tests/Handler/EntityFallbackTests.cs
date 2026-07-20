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

/// <summary>
/// Tests the entity fallback logic wired into PlayMoodMusicIntentHandler:
/// when mood/genre search misses, the handler strips locale stop-words from
/// the slot text and tries it as an artist name via the phonetic search pipeline.
/// </summary>
[Collection("Plugin")]
public class EntityFallbackTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public EntityFallbackTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayMoodMusicIntentHandler CreateHandler()
    {
        return new PlayMoodMusicIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateMoodIntent(string mood, string locale = "en-US")
    {
        var intent = new Intent { Name = IntentNames.PlayMoodMusic };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["mood"] = new Slot { Name = "mood", Value = mood }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    /// <summary>
    /// Configures the library mock so genre/artist-genre searches return empty
    /// (triggering the entity fallback), and the artist SearchTerm query returns
    /// the given artist, followed by artist songs via ArtistIds.
    /// </summary>
    private void SetupArtistFallback(Guid artistId, string artistName, params Audio[] songs)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isArtistQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist);
                bool isAudioQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                bool hasGenres = q.Genres != null && q.Genres.Count > 0;

                // Genre track search → empty
                if (hasGenres && isAudioQuery)
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem>();
                }

                // Artist-genre search → empty
                if (hasGenres && isArtistQuery)
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem>();
                }

                // Entity fallback: artist search via SearchTerm (no Genres)
                if (q.SearchTerm != null && isArtistQuery && !hasGenres)
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };
                }

                // Artist songs (ArtistIds + MediaTypes Audio)
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem>(songs);
                }

                return (IReadOnlyList<BaseItem>)new List<BaseItem>();
            });
    }

    [Fact]
    public async Task ItalianMood_DiMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("di miles davis", "it-IT");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task EnglishMood_ByMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("by miles davis", "en-US");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task GermanMood_VonMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("von miles davis", "de-DE");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task NoMatch_ReturnsNull_CallerShowsNotFoundMood()
    {
        SetupUserMock();

        // All searches return empty — no genre tracks, no artist-genre, no artist fallback
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var request = CreateMoodIntent("relaxing", "en-US");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // No directives — NotFoundMood tell
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("relaxing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpanishMood_DeMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("de miles davis", "es-ES");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task PortugueseMood_DoMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("do miles davis", "pt-BR");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task MaxWordsGuard_LongMood_DoesNotFallbackToArtist()
    {
        // An artist IS available, but a >CrossMediaArtistMaxWords-word slot is a poor artist
        // query — the guard must skip the fallback (a wrong-artist false positive is worse
        // than a clean not-found). Same guard PlaySong's cross-media fallback uses.
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("miles davis john coltrane quartet", "en-US");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // No AudioPlayer.Play — the guard prevented the artist fallback despite the artist being available.
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
    }
}
