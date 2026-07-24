using System;
using System.Collections.Generic;
using System.Linq;
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
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests that PlaySongIntentHandler falls back to artist-songs playback when the
/// song slot contains a generic music word (e.g. "musica"/"music") and the musician
/// slot has a valid artist.
/// </summary>
[Collection("Plugin")]
public class PlaySongGenericMusicWordFallbackTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlaySongGenericMusicWordFallbackTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlaySongIntentHandler CreateHandler()
    {
        return new PlaySongIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentWithBothSlots(string song, string musician)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song },
            ["musician"] = new Slot { Name = "musician", Value = musician }
        };
        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public async Task GenericMusicWord_Musica_FallsBackToArtistSongs()
    {
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Don't Know Why", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Come Away With Me", Id = Guid.NewGuid() };

        SetupUserMock();

        // Track queries in order: artist search → song search → artist songs fallback
        var queries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => queries.Add(q))
            .Returns<InternalItemsQuery>(q =>
            {
                // Artist search: returns the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Norah Jones", Id = artistId } };

                // Song search with SearchTerm: returns empty (no song titled "musica")
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist songs fallback: ArtistIds + MediaTypes Audio, no SearchTerm
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                    return new List<BaseItem> { song1, song2 };

                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("musica", "norah jones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should return audio player directive (artist songs playback), not "not found"
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);

        // Queue should have the artist's songs
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task GenericMusicWord_EnglishMusic_FallsBackToArtistSongs()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "Yesterday", Id = Guid.NewGuid() };

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Beatles", Id = artistId } };

                if (q.SearchTerm != null)
                    return new List<BaseItem>();

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return new List<BaseItem> { song };

                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("music", "beatles");
        request.Locale = "en-US";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Single(session.NowPlayingQueue);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task NonGenericSongWord_DoesNotFallback_ReturnsNotFound()
    {
        var artistId = Guid.NewGuid();
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Norah Jones", Id = artistId } };

                // "sunrise" is a real song name, not a generic music word — always empty
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("sunrise", "norah jones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should NOT fall back — "sunrise" is not a generic music word
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("sunrise", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task GenericMusicWord_NoArtistFound_ReturnsNotFoundByArtist()
    {
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("musica", "unknown artist");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("unknown artist", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task GenericMusicWord_NoArtistSongs_ReturnsNoSongsForArtist()
    {
        var artistId = Guid.NewGuid();
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Norah Jones", Id = artistId } };

                // Everything else empty — no songs for this artist
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("musica", "norah jones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Norah Jones", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void GenericMusicWords_ContainsAllOriginalWords()
    {
        // Original 14 words must still be present
        var originalWords = new[]
        {
            "music", "songs",
            "musica", "canzoni", "brani",
            "musik", "lieder",
            "música", "canciones",
            "chansons", "musique",
            "muziek", "liedjes",
            "canções",
        };

        foreach (var word in originalWords)
        {
            Assert.True(PlaySongIntentHandler.GenericMusicWords.Contains(word),
                $"Expected '{word}' to be in GenericMusicWords");
        }
    }

    [Fact]
    public void GenericMusicWords_ContainsNewlyAddedWords()
    {
        // Representative new words from each language
        var newWords = new[]
        {
            // English
            "song", "track", "tracks", "tune", "tunes",
            // Italian
            "brano", "canzone", "pezzo", "traccia",
            // German
            "lied", "titel",
            // Spanish
            "tema", "temas", "canción", "cancion",
            // French
            "chanson", "morceau", "titre", "titres",
            // Dutch
            "liedje", "nummer", "nummers",
            // Portuguese
            "músicas", "musicas", "faixa", "faixas", "cancoes",
        };

        foreach (var word in newWords)
        {
            Assert.True(PlaySongIntentHandler.GenericMusicWords.Contains(word),
                $"Expected '{word}' to be in GenericMusicWords");
        }
    }

    [Fact]
    public void GenericMusicWords_CaseInsensitive()
    {
        Assert.Contains("MUSIC", PlaySongIntentHandler.GenericMusicWords);
        Assert.Contains("Musica", PlaySongIntentHandler.GenericMusicWords);
        Assert.Contains("BRANO", PlaySongIntentHandler.GenericMusicWords);
        Assert.Contains("Chanson", PlaySongIntentHandler.GenericMusicWords);
        Assert.Contains("LIED", PlaySongIntentHandler.GenericMusicWords);
    }

    [Fact]
    public void GenericMusicWords_DoesNotContainStructuralWords()
    {
        // Verbs, prepositions, articles should NOT be in the set
        var excludedWords = new[] { "play", "suona", "riproduci", "di", "dei", "von", "from", "the", "gli", "les" };

        foreach (var word in excludedWords)
        {
            Assert.False(PlaySongIntentHandler.GenericMusicWords.Contains(word),
                $"Did NOT expect structural word '{word}' to be in GenericMusicWords");
        }
    }

    [Fact]
    public async Task GenericMusicWord_SetsProgressiveQueueContinuation()
    {
        var artistId = Guid.NewGuid();
        var songs = new List<BaseItem>();
        for (int i = 0; i < 50; i++)
        {
            songs.Add(new Audio { Name = $"Song {i}", Id = Guid.NewGuid() });
        }

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Norah Jones", Id = artistId } };

                if (q.SearchTerm != null)
                    return new List<BaseItem>();

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return songs;

                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("musica", "norah jones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        var continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(continuation);
        Assert.Equal("Artist", continuation.SourceType);
        Assert.True(response.Response.ShouldEndSession);
    }
}
