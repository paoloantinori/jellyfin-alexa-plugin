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
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public CrossMediaTypeFallbackTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlaySongIntentHandler CreateSongHandler()
    {
        return new PlaySongIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private PlayAlbumIntentHandler CreateAlbumHandler()
    {
        return new PlayAlbumIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateSongIntent(string song, string? musician = null)
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
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static IntentRequest CreateAlbumIntent(string album, string? musician = null)
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
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
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

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: returns empty (no song titled "the strokes")
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: returns the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Artist songs fallback: ArtistIds + MediaTypes Audio
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.MediaTypes != null && q.MediaTypes.Contains(MediaType.Audio))
                    return new List<BaseItem> { song1, song2 };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("the strokes"); // no musician slot
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateSongHandler();
        var request = CreateSongIntent("xyzzyfoo"); // no musician slot
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        bool artistQueryIssued = false;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem>();

                // Artist search: returns a strong match
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.MediaTypes != null && q.MediaTypes.Contains(MediaType.Audio))
                    return new List<BaseItem> { song1 };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("strokes"); // single word, no musician slot
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Song search: returns a match
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                    return new List<BaseItem> { new Audio { Name = "Reptilia", Id = songId } };

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("reptilia"); // no musician slot
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        var artistId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Album search: returns empty
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))
                    return new List<BaseItem>();

                // Artist search: returns the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };

                // Artist songs fallback
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.MediaTypes != null && q.MediaTypes.Contains(MediaType.Audio))
                    return new List<BaseItem> { song1, song2 };

                return new List<BaseItem>();
            });

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("the strokes"); // no musician slot
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("xyzzyfoo");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>(
                new List<BaseItem> { new Audio { Name = "Is This It", Id = trackId } }));

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("is this it");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        var artistId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>(
                new List<BaseItem> { new Audio { Name = "Deep in It", Id = trackId } }));

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntent("jazz caffè"); // accent variant → exact miss, fuzzy hit
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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
}
