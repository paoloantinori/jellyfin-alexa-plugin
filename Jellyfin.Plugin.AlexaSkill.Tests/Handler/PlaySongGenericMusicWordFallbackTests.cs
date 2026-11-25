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
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlaySongIntentHandler CreateHandler()
    {
        return new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
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

    [Fact]
    public async Task GenericMusicWord_Musica_FallsBackToArtistSongs()
    {
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Don't Know Why", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Come Away With Me", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        // Track queries in order: artist search → song search → artist songs fallback
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should return audio player directive (artist songs playback), not "not found"
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);

        // Queue should have the artist's songs
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Theory]
    [InlineData("on music")]        // simulate capture 2026-10-20
    [InlineData("put on music")]    // e2e harness capture 2026-10-21 (corr 0109f99f)
    [InlineData("Give Me Music")]   // JF-962 (decision 1.i, 2026-11-25): the ER's delivered title casing
    public async Task ObservedWholePhraseFill_FallsBackToArtistSongs(string fill)
    {
        // The observed whole-phrase fills (ObservedWholePhraseFills) reach the
        // artist-songs fallback end to end; the captures' register and rationale
        // live on the table in MultiValueErDisambiguation. The "Give Me Music"
        // row pins the JF-962 fill's delivered ER casing at the delivery layer;
        // the probe-level casings ride the membership theory below.
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Pink Floyd", Id = artistId } };
                // The title search for the residue fill finds nothing (the
                // incident shape: no song titled "on music"; review round 3 F3).
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                    return new List<BaseItem> { song1, song2 };
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots(fill, "pink floyd");
        request.Locale = "en-US";
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task ObservedFill_SwallowsTheExactlyTitledSong_WhenAMusicianIsNamed()
    {
        // JF-962 accepted loss, pinned: the whole-phrase fill and a REAL song
        // titled exactly "Give Me Music" are string-indistinguishable on this
        // path, so with a musician present the artist's catalog plays even when
        // the library holds the song (the same trade-off the bare "music" word
        // has carried since JF-697; the probed live capture outranks the
        // hypothetical title). The queue-count discriminator: the artist path
        // installs the catalog (2 songs), a title play would install exactly 1.
        var artistId = Guid.NewGuid();
        var theSong = new Audio { Name = "Give Me Music", Id = Guid.NewGuid() };
        var otherSong = new Audio { Name = "Another Rambler Tune", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Ramblers", Id = artistId } };
                // The title search WOULD find the exactly-titled song: the
                // swallow is the point.
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem> { theSong };
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                    return new List<BaseItem> { theSong, otherSong };
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("Give Me Music", "the ramblers");
        request.Locale = "en-US";
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The artist's catalog plays; the exactly-titled song is swallowed.
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task RealTitleWithMusician_StillPlaysTheSong()
    {
        // JF-962 boundary: a real title with a musician keeps the song play.
        // "yellow" carries no generic music word, so the tolerance must not
        // fire; the queue carries the one song, not the artist's catalog.
        var artistId = Guid.NewGuid();
        var theSong = new Audio { Name = "Yellow", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        var searchTermQueries = new List<string>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q =>
            {
                if (q.SearchTerm != null)
                    searchTermQueries.Add(q.SearchTerm);
            })
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Coldplay", Id = artistId } };

                if (q.SearchTerm != null && q.SearchTerm.Contains("yellow", StringComparison.OrdinalIgnoreCase))
                    return new List<BaseItem> { theSong };

                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("yellow", "coldplay");
        request.Locale = "en-US";
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The title search ran with the real title and the song played.
        Assert.Contains("yellow", searchTermQueries, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(theSong.Id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task GenericMusicWord_NoMusician_KeepsExistingPath()
    {
        // JF-962 boundary: the generic word with NO musician slot keeps today's
        // path. The artist-play bypass requires a musician; without one the song
        // search misses and the miss cascade (artist, album, video, not-found)
        // answers, unchanged.
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        // A null musician VALUE and an absent musician slot take the same path:
        // the slot read and every consumer branch gate on IsNullOrWhiteSpace, so
        // the null value here pins the no-musician shape the NLU delivers when
        // the utterance carries no artist.
        var request = CreateIntentWithBothSlots("music", null);
        request.Locale = "en-US";
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // No artist playback: the not-found Tell (no directives), no queue items
        // (the session fixture starts NowPlayingQueue as an empty list).
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public async Task UnobservedResidueFill_OnTrack_KeepsTheTitleSearch()
    {
        // JF-917 review round 3 F1: the bypass matches only the OBSERVED whole
        // phrase; an "on <generic word>" TITLE ("On Track" by Tame Impala) must
        // keep the normal title search, never the artist bypass.
        var artistId = Guid.NewGuid();
        var theSong = new Audio { Name = "On Track", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Tame Impala", Id = artistId } };
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem> { theSong };
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("on track", "tame impala");
        request.Locale = "en-US";
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The TITLE search found the song: the queue carries the song, not the
        // artist's catalog (an artist bypass would enqueue many tracks).
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
    }

    [Fact]
    public async Task GenericMusicWord_EnglishMusic_FallsBackToArtistSongs()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "Yesterday", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

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
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Norah Jones", Id = artistId } };

                // "sunrise" is a real song name, not a generic music word — always empty
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("sunrise", "norah jones");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

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
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("musica", "unknown artist");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("unknown artist", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task GenericMusicWord_NoArtistSongs_ReturnsNoSongsForArtist()
    {
        var artistId = Guid.NewGuid();
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Norah Jones", Id = artistId } };

                // Everything else empty — no songs for this artist
                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("musica", "norah jones");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

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
            Assert.True(MultiValueErDisambiguation.GenericMusicWords.Contains(word),
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
            Assert.True(MultiValueErDisambiguation.GenericMusicWords.Contains(word),
                $"Expected '{word}' to be in GenericMusicWords");
        }
    }

    [Fact]
    public void GenericMusicWords_CaseInsensitive()
    {
        Assert.Contains("MUSIC", MultiValueErDisambiguation.GenericMusicWords);
        Assert.Contains("Musica", MultiValueErDisambiguation.GenericMusicWords);
        Assert.Contains("BRANO", MultiValueErDisambiguation.GenericMusicWords);
        Assert.Contains("Chanson", MultiValueErDisambiguation.GenericMusicWords);
        Assert.Contains("LIED", MultiValueErDisambiguation.GenericMusicWords);
    }

    [Fact]
    public void GenericMusicWords_DoesNotContainStructuralWords()
    {
        // Verbs, prepositions, articles should NOT be in the set
        var excludedWords = new[] { "play", "suona", "riproduci", "di", "dei", "von", "from", "the", "gli", "les" };

        foreach (var word in excludedWords)
        {
            Assert.False(MultiValueErDisambiguation.GenericMusicWords.Contains(word),
                $"Did NOT expect structural word '{word}' to be in GenericMusicWords");
        }
    }

    // Shared artist-in-library fixture: the artist search resolves artistName, the
    // song search (SearchTerm) is empty, the artist-songs query returns song.
    private void SetupArtistWithOneSong(string artistName, Audio song)
    {
        var artistId = Guid.NewGuid();
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };

                if (q.SearchTerm != null)
                    return new List<BaseItem>();

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return new List<BaseItem> { song };

                return new List<BaseItem>();
            });
    }

    [Fact]
    public async Task GenericMusicWord_ArticledLaMusica_FallsBackToArtistSongs()
    {
        // JF-697 live evidence: "suonare la musica di pink" delivered song="la musica"
        // (article + generic word) and answered not-found because the gate was an
        // exact Contains. The article form must reach the artist-songs fallback.
        var song = new Audio { Name = "Just Like a Pill", Id = Guid.NewGuid() };
        SetupArtistWithOneSong("P!nk", song);

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("la musica", "pink");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Single(session.NowPlayingQueue);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task GenericMusicWord_ArticledLaMusique_FallsBackToArtistSongs()
    {
        // JF-697: the same leak class in the other covered languages; fr article form.
        var song = new Audio { Name = "La Bohème", Id = Guid.NewGuid() };
        SetupArtistWithOneSong("Aznavour", song);

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("la musique", "aznavour");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Single(session.NowPlayingQueue);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ArticleTitledRealSong_StillPlaysAsSong()
    {
        // JF-697 boundary: the article strip is a gate-membership probe ONLY.
        // A real song whose title starts with an article still plays as the song.
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "La Vie En Rose", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        var searchTermQueries = new List<string>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q =>
            {
                if (q.SearchTerm != null)
                    searchTermQueries.Add(q.SearchTerm);
            })
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Louis Armstrong", Id = artistId } };

                if (q.SearchTerm != null && q.SearchTerm.Contains("vie", StringComparison.OrdinalIgnoreCase))
                    return new List<BaseItem> { song };

                return new List<BaseItem>();
            });

        var handler = CreateHandler();
        var request = CreateIntentWithBothSlots("la vie en rose", "louis armstrong");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The song search ran with the RAW slot (the strip never touched it)
        Assert.Contains("la vie en rose", searchTermQueries, StringComparer.OrdinalIgnoreCase);
        // And the titled song played, not a generic artist-songs fallback
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue[0].Id);
    }

    [Theory]
    [InlineData("la musica", true)]
    [InlineData("la musique", true)]
    [InlineData("die Musik", true)]
    [InlineData("die Musik ", true)]
    [InlineData("the music", true)]
    [InlineData("a música", true)]
    [InlineData("le titre", true)]
    [InlineData("les chansons", true)]
    [InlineData("ein lied", true)]
    [InlineData("eine musik", true)]
    [InlineData("il brano", true)]
    [InlineData("musica", true)]
    [InlineData("on music", true)]       // JF-917 observed wrapper residue
    [InlineData("put on music", true)]   // JF-917 second observed fill (per-call variance)
    [InlineData("give me music", true)]  // JF-962 observed hallucinated entity fill (decision 1.i, 2026-11-25)
    [InlineData("Give Me Music", true)]  // JF-962: the ER-delivered title casing (case-insensitive whole-phrase match)
    [InlineData("on track", false)]      // JF-917 boundary: "on <generic>" TITLES never match (whole-phrase only)
    [InlineData("on some music", false)] // JF-917 unobserved shape stays on the normal path
    [InlineData("give me the music", false)] // JF-962 boundary: unobserved carrier variants stay on the normal path
    [InlineData("sweet music", false)]   // JF-962 boundary: a real title containing a generic word never matches (the rejected containment shape)
    [InlineData("la vie en rose", false)]
    [InlineData("a day in the life", false)]
    [InlineData("der hugenottenfestmarsch", false)]
    public void IsGenericMusicQuery_MembershipTable(string query, bool expected)
    {
        // JF-715: the membership test went private with the fold; the table is
        // probed through the ONE raw-slot entry (IsGenericSongConstraint, the
        // composite's constraint probe). Every row below is a raw-slot shape:
        // the pipeline (carrier strip, romanize, article strip) is identity on
        // all of them except the article forms, which the article strip handles.
        Assert.Equal(expected, MultiValueErDisambiguation.IsGenericSongConstraint(query));
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

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        var continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(continuation);
        Assert.Equal("Artist", continuation.SourceType);
        Assert.True(response.Response.ShouldEndSession);
    }
}
