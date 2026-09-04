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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests that PlaySongIntentHandler and PlayAlbumIntentHandler fall back to artist
/// playback when no results are found for the primary media type (cross-media-type fallback).
/// This handles the case where Alexa's NLU routes an artist query to the wrong intent
/// (e.g. "mettere gli strokes" → PlaySongIntent instead of PlayArtistSongsIntent).
/// </summary>
[Collection("Plugin")]
public class CrossMediaTypeFallbackTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlaySongIntentHandler CreateSongHandler()
    {
        return new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private PlayAlbumIntentHandler CreateAlbumHandler(IArtistIndex? artistIndex = null)
    {
        return new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: artistIndex);
    }

    private static IntentRequest CreateSongIntent(string song, string? musician = null, string locale = "en-US")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    private static IntentRequest CreateAlbumIntent(string album, string? musician = null, string locale = "en-US")
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["album"] = new Slot { Name = "album", Value = album }
        };
        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    // ============================================================
    // PlaySongIntentHandler cross-media-type fallback tests
    // ============================================================

    [Fact]
    public async Task PlaySong_NoSongs_NoMusician_ArtistExists_FallsBackToArtist()
    {
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Last Nite", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Someday", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: returns empty (no song titled "the strokes")
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: returns the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Artist songs fallback: ArtistIds + MediaTypes Audio
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                    return new List<BaseItem> { song1, song2 };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("the strokes"); // no musician slot
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should return audio player directive (artist songs playback), not "not found"
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // Queue should have the artist's songs
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);

        // Should include announcement speech
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Strokes", speech);
    }

    [Fact]
    public async Task PlaySong_NoSongs_NoMusician_NoArtist_ReturnsNotFound()
    {
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateSongHandler();
        var request = CreateSongIntent("xyzzyfoo"); // no musician slot
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should NOT fall back — no artist found either
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("xyzzyfoo", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_NoSongs_NoMusician_MultiWordTitle_SkipsArtistFallback()
    {
        // JF-295: a multi-word song title that misses must NOT fall back to a
        // fuzzy artist match. Observed bug: "la ballata del genesio" matched
        // artist "Lamb" at score 75. A clean "song not found" is better than a
        // wrong artist. Even though the mock returns a plausible artist, the
        // word-count gate must short-circuit before the artist search.
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: empty (no such song)
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search would return a false-positive match — but the
                // word-count gate must prevent us from even getting here.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Lamb", Id = Guid.NewGuid() } };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("la ballata del genesio"); // 4 words, no musician slot
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should NOT fall back to artist playback — clean "song not found"
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("la ballata del genesio", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lamb", speech);
        // Queue must be empty — no artist songs enqueued
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
    }

    [Fact]
    public async Task PlaySong_NoSongs_NoMusician_MultiWordTitle_DoesNotInvokeArtistSearch()
    {
        // Companion to the above: verify the word-count gate skips the artist
        // DB query entirely (no MusicArtist query should be issued).
        _fx.SetupUserMock();

        bool artistQueryIssued = false;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    artistQueryIssued = true;
                    return new List<BaseItem> { new MusicArtist { Name = "Lamb", Id = Guid.NewGuid() } };
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        // 3 words — above the 2-word gate
        var request = CreateSongIntent("ballata del genesio");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(artistQueryIssued, "Cross-media artist search must NOT be issued for a 3+ word song title");
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
    }

    [Fact]
    public async Task PlaySong_NoSongs_NoMusician_SingleWordMisroute_StillFallsBackToArtist()
    {
        // JF-295 regression guard: the word-count gate must NOT break the
        // original purpose of the cross-media fallback — catching NLU misroutes
        // of SHORT artist names into the song slot. A single-word query like
        // "strokes" should still resolve to "The Strokes" via the fallback.
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Last Nite", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: returns a strong match
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                    return new List<BaseItem> { song1 };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("strokes"); // single word, no musician slot
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should fall back to artist playback
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Strokes", speech);
    }

    [Fact]
    public async Task PlaySong_SongsFound_NoFallbackTriggered()
    {
        var songId = Guid.NewGuid();
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: returns a match
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem> { new Audio { Name = "Reptilia", Id = songId } };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("reptilia"); // no musician slot
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should play the song directly (no fallback). The now-playing announce may speak
        // (JF-353), but the cross-media "FoundArtistInstead" text must never appear for a
        // direct song match.
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string? speech = response.Response.OutputSpeech == null
            ? null
            : Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("FoundArtistInstead", speech ?? string.Empty);
        Assert.DoesNotContain("instead", (speech ?? string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_NoSongs_WithMusicianSlot_NoFallback()
    {
        // When musician slot IS filled, the cross-media-type fallback should NOT trigger
        // because the user explicitly specified "play song X by artist Y".
        _fx.SetupUserMock();

        var artistId = Guid.NewGuid();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Artist search for musician slot
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Song search returns empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("unknown song", "the strokes"); // musician slot is filled
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should NOT fall back to artist playback — should return "not found song by artist"
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("unknown song", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_NoSongs_ArtistFound_ButNoArtistSongs_ReturnsNoSongsForArtist()
    {
        var artistId = Guid.NewGuid();
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: found
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Empty Artist", Id = artistId } };

                // Artist songs: empty (no songs for this artist)
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return new List<BaseItem>();

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("empty artist");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should tell user no songs for the artist
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Empty Artist", speech);
    }

    // ============================================================
    // PlayAlbumIntentHandler cross-media-type fallback tests
    // ============================================================

    [Fact]
    public async Task PlayAlbum_NoAlbums_NoMusician_ArtistExists_FallsBackToArtist()
    {
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Is This It", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "The Modern Age", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Album search: returns empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                    return new List<BaseItem>();

                // Artist search: returns the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                    return new List<BaseItem> { song1, song2 };

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("the strokes"); // no musician slot
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should return audio player directive (artist songs playback)
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // Queue should have the artist's songs
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);

        // Should include announcement speech
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Strokes", speech);
    }

    [Fact]
    public async Task PlayAlbum_NoAlbums_NoMusician_NoArtist_ReturnsNotFound()
    {
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("xyzzyfoo");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("xyzzyfoo", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_AlbumsFound_NoFallbackTriggered()
    {
        // When albums ARE found, the handler proceeds to album playback and never
        // reaches the cross-media fallback code path. Verify that the response
        // does NOT contain the "FoundArtistInstead" announcement.
        var albumId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Album search: returns a match
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                    return new List<BaseItem> { new MusicAlbum { Name = "Is This It", Id = albumId } };

                // Album tracks (ParentId match)
                if (q.ParentId == albumId)
                    return new List<BaseItem> { new Audio { Name = "Is This It", Id = trackId } };

                return new List<BaseItem>();
            });

        // The handler uses SafeGetItemsResult for album tracks which calls GetItemsResult internally.
        // Mock it to return a single track.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>(
                new List<BaseItem> { new Audio { Name = "Is This It", Id = trackId } }));

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("is this it");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Response should be audio playback with no artist fallback announcement. The now-playing
        // announce may speak (JF-353), but it must never carry the "FoundArtistInstead" text that
        // only the cross-media fallback path sets.
        string? speech = response.Response.OutputSpeech == null
            ? null
            : Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("FoundArtistInstead", speech ?? string.Empty);
        Assert.DoesNotContain("instead", (speech ?? string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_NoAlbums_WithMusicianSlot_NoFallback()
    {
        // When musician slot IS filled, cross-media-type fallback should NOT trigger
        _fx.SetupUserMock();

        var artistId = Guid.NewGuid();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Artist search for musician slot
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Album search returns empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                    return new List<BaseItem>();

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("unknown album", "the strokes"); // musician slot is filled
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should NOT fall back — should return "not found album by artist"
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("unknown album", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_ExactMiss_FuzzyAlbumMatch_PlaysAndAnnouncesAlbumName()
    {
        // JF-339: when the exact album search misses but the fuzzy fallback matches a
        // library album (e.g. ASR "jazz caffè" → "Jazz Cafe"), the handler plays it
        // AND speaks the matched album name so voice-only devices know what's playing.
        var albumId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isAlbumQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum);

                // Exact album search (SearchTerm set): miss
                if (isAlbumQuery && !string.IsNullOrEmpty(q.SearchTerm))
                {
                    return new List<BaseItem>();
                }

                // Fuzzy album scan (no SearchTerm): the candidate album
                if (isAlbumQuery)
                {
                    return new List<BaseItem> { new MusicAlbum { Name = "Jazz Cafe", Id = albumId } };
                }

                // Album tracks via ParentId (defensive — SafeGetItemsResult may use GetItemList)
                if (q.ParentId == albumId)
                {
                    return new List<BaseItem> { new Audio { Name = "Deep in It", Id = trackId } };
                }

                return new List<BaseItem>();
            });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>(
                new List<BaseItem> { new Audio { Name = "Deep in It", Id = trackId } }));

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("jazz caffè"); // accent variant → exact miss, fuzzy hit
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Plays the matched album
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);

        // Announces the matched album name (JF-339)
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jazz Cafe", speech);
    }

    // ============================================================
    // JF-363: cross-media artist SUGGESTION (sub-strict band [60,85))
    // When a song/album isn't found but an artist scores in [normalThreshold, 85),
    // the behavior is governed by CrossMediaArtistSuggestion (Off/Confirm/AutoServe).
    // "soul coffin" vs "Soul Coughing" is the real on-device repro (scored 63).
    // ============================================================

    /// <summary>
    /// Mocks a song search that returns nothing and an artist search that returns one
    /// artist whose name scores in the [60,85) band against the query.
    /// </summary>
    private void SetupSongMissArtistSubStrict(string query, string artistName, Guid artistId, params Audio[] artistSongs)
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: empty (no such song)
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: the plausible-but-sub-strict artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };

                // Artist songs fallback: ArtistIds + Audio
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return new List<BaseItem>(artistSongs);

                return new List<BaseItem>();
            });
    }

    /// <summary>
    /// Album-search mirror of <see cref="SetupSongMissArtistSubStrict"/>: the album
    /// search misses, the artist search returns the plausible-but-sub-strict artist,
    /// and the artist songs fallback returns the artist's songs.
    /// </summary>
    private void SetupAlbumMissArtistSubStrict(string query, string artistName, Guid artistId, params Audio[] artistSongs)
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Album search: empty (no such album)
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                    return new List<BaseItem>();

                // Artist search: the plausible-but-sub-strict artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };

                // Artist songs fallback: ArtistIds + Audio
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return new List<BaseItem>(artistSongs);

                return new List<BaseItem>();
            });
    }

    /// <summary>
    /// Simulates the user answering "no" to a cross-media offer: feeds the offer's
    /// session attributes into NoIntentHandler.
    /// </summary>
    private Task<SkillResponse> DeclineOfferAsync(SkillResponse offer)
    {
        var noHandler = new NoIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        var noRequest = new IntentRequest { Intent = new Intent { Name = IntentNames.AmazonNo }, Locale = "en-US", RequestId = "no-req" };
        return noHandler.HandleAsync(
            noRequest, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), offer.SessionAttributes, CancellationToken.None);
    }

    [Fact]
    public async Task JF363_PlaySong_SubStrict_Confirm_OffersArtistAsk()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupSongMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateSongHandler();
        var response = await handler.HandleAsync(
            CreateSongIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Confirm mode: Ask (session stays open), no playback yet, carries the artist in disambig state.
        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Soul Coughing", speech);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.Equal("artist", response.SessionAttributes["disambig_type"]);
        // JF-454 (F4): the offer must also land the JF-363 decline keys (the original
        // not-found query and its media type) via BuildAttributes' extraEntries, so
        // NoIntentHandler's "no" answers with the clean song not-found instead of the
        // generic "no more matches".
        Assert.Equal("soul coffin", response.SessionAttributes?["crossmedia_notfound_query"]);
        Assert.Equal("song", response.SessionAttributes?["crossmedia_notfound_type"]);
    }

    [Fact]
    public async Task JF363_PlaySong_SubStrict_AutoServe_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupSongMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe;

        var handler = CreateSongHandler();
        var response = await handler.HandleAsync(
            CreateSongIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // AutoServe: plays the artist (audio directive), with FoundArtistInstead announcement.
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Soul Coughing", speech);
    }

    [Fact]
    public async Task JF363_PlaySong_SubStrict_Off_ReturnsCleanNotFound()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupSongMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Off;

        var handler = CreateSongHandler();
        var response = await handler.HandleAsync(
            CreateSongIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Off: clean not-found Tell, no offer, no play.
        Assert.True(response.Response?.ShouldEndSession);
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("soul coffin", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Soul Coughing", speech);
    }

    [Fact]
    public async Task JF363_PlayAlbum_SubStrict_Confirm_OffersArtistAsk()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupAlbumMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateAlbumHandler();
        var response = await handler.HandleAsync(
            CreateAlbumIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        Assert.Contains("Soul Coughing", TestHelpers.GetSpeechText(response));
        Assert.Equal("artist", response.SessionAttributes?["disambig_type"]);
        // JF-454 (F4): same decline keys on the album offer, carrying the album media
        // type for NoIntentHandler's decline branch.
        Assert.Equal("soul coffin", response.SessionAttributes?["crossmedia_notfound_query"]);
        Assert.Equal("album", response.SessionAttributes?["crossmedia_notfound_type"]);
    }

    [Fact]
    public async Task JF363_PlaySong_PerUserOverride_TakesPrecedenceOverGlobal()
    {
        // Global says Off, per-user says Confirm -> must offer (per-user wins).
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupSongMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Off;
        var user = _fx.CreateUser();
        user.CrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateSongHandler();
        var response = await handler.HandleAsync(
            CreateSongIntent("soul coffin"), _fx.CreateContext(), user, _fx.CreateSession(), CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession != true);
        Assert.Contains("Soul Coughing", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task JF363_PlaySong_OfferDeclined_NoIntent_ReturnsCleanSongNotFound()
    {
        // MUST-FIX from code-review: a "no" to the cross-media artist offer must produce the
        // clean song not-found, NOT the generic "no more matches" (the offer had one candidate).
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupSongMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateSongHandler();
        var offer = await handler.HandleAsync(
            CreateSongIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Simulate the user's "no": feed the offer's session attributes into NoIntentHandler.
        var noResponse = await DeclineOfferAsync(offer);

        // Decline must be the song not-found, ending the session, not "no more matches"
        // and not the album not-found (crossmedia_notfound_type selects the string).
        Assert.True(noResponse.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(noResponse);
        Assert.Contains("soul coffin", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("songs", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("more matches", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Soul Coughing", speech);
    }

    [Fact]
    public async Task JF363_PlayAlbum_OfferDeclined_NoIntent_ReturnsCleanAlbumNotFound()
    {
        // JF-454 (F4): companion to the PlaySong decline test above, pinning the ALBUM
        // branch of the crossmedia_notfound_* reader (NoIntentHandler): a "no" to an
        // album offer must speak the album not-found, never the generic "no more
        // matches" and never the offered artist.
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        SetupAlbumMissArtistSubStrict("soul coffin", "Soul Coughing", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateAlbumHandler();
        var offer = await handler.HandleAsync(
            CreateAlbumIntent("soul coffin"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // Precondition: the album offer carries the decline keys (asserted directly in
        // JF363_PlayAlbum_SubStrict_Confirm_OffersArtistAsk).
        Assert.Equal("album", offer.SessionAttributes?["crossmedia_notfound_type"]);

        // Simulate the user's "no": feed the offer's session attributes into NoIntentHandler.
        var noResponse = await DeclineOfferAsync(offer);

        // Decline must be the ALBUM not-found, ending the session.
        Assert.True(noResponse.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(noResponse);
        Assert.Contains("soul coffin", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("albums", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("more matches", speech, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Soul Coughing", speech);
    }

    [Fact]
    public async Task JF363_PlaySong_MultiWordQuery_DoesNotOfferArtist()
    {
        // JF-363 code-review #3: a >2-word query must skip the artist fallback entirely
        // (word-count gate), so no spurious offer on long song titles.
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Soul Coughing", Id = artistId } };
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                    return new List<BaseItem> { song };
                return new List<BaseItem>();
            });

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateSongHandler();
        var response = await handler.HandleAsync(
            CreateSongIntent("la ballata del genesio"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        // 4-word query: word-count gate fires before any offer; clean not-found, no disambig state.
        Assert.True(response.Response?.ShouldEndSession);
        Assert.Null(response.SessionAttributes?["disambig_type"]);
        Assert.DoesNotContain("Soul Coughing", TestHelpers.GetSpeechText(response));
    }

    // ============================================================
    // JF-446: artist answers to the both-empty album elicit go through the
    // SHARED cross-media gate (TryEntityFallbackAsync), which tokenizes before
    // the word-count guard and accepts via the phonetic matcher. The inline
    // copies PlaySong/PlayAlbum carried counted RAW words and scored
    // non-phonetically, so "di pink floyd" dead-ended and "cup" for "Koop"
    // never played.
    // ============================================================

    [Fact]
    public async Task JF446_PlayAlbum_AlbumSlot_DiArtistAnswer_MusicianEmpty_PlaysArtist()
    {
        // AC#3: the JF-422 both-empty album elicit captures an artist ANSWER in the
        // album slot. "di pink floyd" is 3 RAW words but tokenizes to [pink, floyd]
        // under it-IT, so the shared gate's word-count guard must not dead-end it in
        // NotFoundAlbumByName; the artist plays instead.
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Time", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Every album query (exact search and fuzzy full-catalog scan): miss
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                {
                    return new List<BaseItem>();
                }

                // Artist search (tokenized query "pink floyd"): the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Pink Floyd", Id = artistId } };
                }

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem> { song1, song2 };
                }

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        // Mid-dialog shape: the answer to the elicit arrives with dialogState IN_PROGRESS.
        var request = CreateAlbumIntent("di pink floyd", locale: "it-IT");
        request.DialogState = "IN_PROGRESS";
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        // The artist's music plays (not NotFoundAlbumByName)
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Pink Floyd", speech);
    }

    [Fact]
    public async Task JF446_PlayAlbum_AlbumSlot_LongArtistAnswer_TokenizedGate_SkipsFallback()
    {
        // AC#4: tokenizing must not OPEN the guard for answers with >2 CONTENT words.
        // "la ballata del grande koop" tokenizes to [ballata, grande, koop] = 3 tokens,
        // so the artist fallback is skipped entirely (no artist query issued) and the
        // clean album not-found is returned.
        _fx.SetupUserMock();

        bool artistQueryIssued = false;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    artistQueryIssued = true;
                    return new List<BaseItem> { new MusicArtist { Name = "Koop", Id = Guid.NewGuid() } };
                }

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("la ballata del grande koop", locale: "it-IT");
        var context = _fx.CreateContext();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.False(artistQueryIssued, "the tokenized word-count gate must skip the artist search for a >2-content-word answer");
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("koop", speech, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // JF-479: the album-title miss DOES reach the shared cross-media
    // gate (wired since JF-446), but the word-count guard rejected the
    // device shape: KeywordMatcher.Tokenize splits the stylized name
    // "P!nk" on the exclamation mark into the tokens [p, nk], so the
    // slot "dei P!nk floyd" counted THREE content tokens against the
    // cap of two and dead-ended in NotFoundAlbumByName. The guard must
    // count spoken WORDS that carry content, not alphanumeric fragments.
    // ============================================================

    [Fact]
    public async Task JF479_PlayAlbum_AlbumSlot_DeiStylizedArtistAnswer_PlaysArtist()
    {
        // Device corr=f74eb567: 'riproduci l'album dei pink floyd' arrived as
        // album='dei P!nk floyd', musician empty. 'dei' strips as an it-IT stop word
        // and "P!nk" is ONE spoken word, so the guard sees 2 content words and the
        // shared gate searches the tokenized join "p nk floyd", which resolves Pink
        // Floyd via the PLAIN score (a one-char substitution from "pink floyd" = 90,
        // above the 85 bar; the phonetic codes do NOT collide: see
        // DoubleMetaphoneTests.Encode_StylizedPunctuationName_PinsTheRealCodes).
        // Plays with the FoundArtistInstead announcement.
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Wish You Were Here", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Time", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Every album query (exact search and fuzzy full-catalog scan): miss
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                {
                    return new List<BaseItem>();
                }

                // Artist search (tokenized query "p nk floyd"): the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Pink Floyd", Id = artistId } };
                }

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem> { song1, song2 };
                }

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("dei P!nk floyd", locale: "it-IT");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        // The artist's music plays (not the dead-end NotFoundAlbumByName)
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Pink Floyd", speech);
    }

    [Fact]
    public async Task JF479_PlayAlbum_AlbumSlot_ThreeWordAlbumTitle_CleanNotFound()
    {
        // Word-count guard discipline pin: a genuine album-shaped miss with 3 content
    // words ('magical mystery tour', absent from the library) is a poor artist query;
    // CrossMediaArtistMaxWords=2 rejects it BEFORE any artist search is issued and the
    // response is the clean album not-found speaking the user's own words.
        _fx.SetupUserMock();

        bool artistQueryIssued = false;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    artistQueryIssued = true;
                    return new List<BaseItem> { new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() } };
                }

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("magical mystery tour", locale: "it-IT");
        var context = _fx.CreateContext();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.False(artistQueryIssued, "a 3-content-word album title must not reach the artist search");
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("magical mystery tour", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JF479_PlayAlbum_AlbumGateRejection_DoesNotBlockArtistRecovery()
    {
        // Interaction pin (JF-478 x JF-479): when the fuzzy album acceptance REJECTS
    // a coincidental containment (here album 'O' interior-contained in "dei P!nk
    // floyd" via the 'o' of "floyd"), the request must still fall through to the
    // cross-media artist gate and play Pink Floyd. The album rejection may never
    // create a new dead-end (cascade ordering precedent: reject, then the caller's
    // own fallback chain).
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Breathe", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Exact album search (SearchTerm set): miss. Fuzzy full-catalog scan
                // (SearchTerm null): the degenerate 1-char album the JF-408/JF-478
                // gate must reject.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                {
                    return q.SearchTerm == null
                        ? new List<BaseItem> { new MusicAlbum { Name = "O", Id = Guid.NewGuid() } }
                        : new List<BaseItem>();
                }

                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Pink Floyd", Id = artistId } };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem> { song1 };
                }

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("dei P!nk floyd", locale: "it-IT");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Pink Floyd", speech);
        Assert.DoesNotContain("l'album O", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JF446_PlaySong_SongSlot_DiArtistAnswer_MusicianEmpty_PlaysArtist()
    {
        // PlaySong mirror of AC#3 through the same shared gate: a carrier article in
        // the song slot ("di koop", it-IT) tokenizes to [koop] and plays the artist.
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Waltz for Koop", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search (SearchTerm): miss
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return new List<BaseItem>();
                }

                // Artist search (tokenized query "koop"): the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Koop", Id = artistId } };
                }

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { song1 };
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("di koop", locale: "it-IT");
        var context = _fx.CreateContext();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Koop", speech);
    }

    [Fact]
    public async Task JF446_PlayAlbum_PhoneticAsrDrift_WithArtistIndex_PlaysArtist()
    {
        // Finding 2 (AC#2): acceptance must score through the PHONETIC matcher when
        // the artist index is available. The live JF-381/JF-446 drift case: ASR "cup"
        // for "Koop" (both Double Metaphone code KP) scores far below 85 on plain
        // Levenshtein; the phonetic overload floors the length-banded code collision
        // above the strict bar, so the artist plays instead of a not-found.
        var artistId = Guid.NewGuid();
        var song1 = new Audio { Name = "Waltz for Koop", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        var koop = new MusicArtist { Name = "Koop", Id = artistId };
        var codes = DoubleMetaphone.Encode("Koop");
        var index = new FakeArtistIndex(
            new[] { koop },
            new Dictionary<Guid, (string Primary, string? Alternate)> { [artistId] = codes });

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { koop };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0)
                {
                    return new List<BaseItem> { song1 };
                }

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler(index);
        var request = CreateAlbumIntent("cup"); // 3 chars: below MinFuzzyAlbumQueryLength, no album fuzzy scan
        var context = _fx.CreateContext();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Koop", speech);
    }

    [Fact]
    public async Task JF363_BandWinsOverWordCoverageValve_Confirm_NotAutoPlay()
    {
        // Review pin (band/valve precedence in TryEntityFallbackAsync): a word-subset
        // artist scoring in [normalThreshold, 85) is reachable by BOTH the JF-440
        // word-coverage valve (silent auto-play) and the JF-363 sub-strict band
        // (Confirm offer) when the caller enabled the band (PlaySong/PlayAlbum). The
        // band must WIN the overlap: the valve's silent auto-play would break the
        // JF-363 contract of no silent substitution in [60,85). The pair is engineered
        // into the overlap: "ac dc" is 2 content words (passes the tokenized guard)
        // and tokenizes to the same words as "AC/DC", while the artist's slash form
        // keeps the RAW strings apart so the containment floor (90) does not fire;
        // the fuzzy score is 80 (verified against FuzzyMatcher). Valve-only callers
        // (FindSong/PlayMoodMusic, notFoundMediaType=null) never enter the band
        // branch, so their valve behavior is unchanged by the reorder.
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "Highway to Hell", Id = Guid.NewGuid() };
        SetupSongMissArtistSubStrict("ac dc", "AC/DC", artistId, song);

        _fx.Config.DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm;

        var handler = CreateSongHandler();
        var session = _fx.CreateSession();
        var response = await handler.HandleAsync(
            CreateSongIntent("ac dc"), _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        // The band's Confirm offer, NOT the valve's auto-play: session stays open,
        // no playback directive, the artist is offered for yes/no.
        Assert.True(response.Response?.ShouldEndSession != true, "the Confirm offer must keep the session open");
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0, "the Confirm offer must not start playback");
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("AC/DC", speech);
        Assert.Equal("artist", response.SessionAttributes?["disambig_type"]);
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0, "no artist songs may be enqueued by the offer");
    }

    [Fact]
    public async Task JF295_ItCanonicalCase_GuardEligibleButThresholdRefuted_StaysNotFound()
    {
        // JF-295 re-check (JF-446 review): the tokenized guard makes the canonical
        // case "la ballata del genesio" GUARD-ELIGIBLE under it-IT ([ballata,
        // genesio] = 2 content words <= CrossMediaArtistMaxWords), so the JF-295
        // protection is no longer the guard layer but the THRESHOLD layer: the fair
        // length penalty keeps "Lamb" far below the 60 normal threshold for the
        // cleaned "ballata genesio" query (see also FuzzyMatcherTests.
        // FindBestMatch_LengthDisproportion_RejectsLambFromBallata), so the shared
        // gate rejects and the clean song not-found stands. This pin exists to catch
        // threshold changes: any future lowering of normalThreshold re-opens JF-295
        // and must fail here first.
        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: empty (no such song)
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: the false-positive JF-295 artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "Lamb", Id = Guid.NewGuid() } };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("la ballata del genesio", locale: "it-IT");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        // Clean "song not found": no playback, no Lamb, empty queue.
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("Lamb", speech);
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
    }
}

