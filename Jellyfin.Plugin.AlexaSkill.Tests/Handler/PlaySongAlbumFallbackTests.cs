#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-345: a bare "play abbey road" in the free-text locales routes to PlaySong,
/// misses, and used to dead-end in a song not-found (guaranteed in the five English
/// locales PR #15 trimmed the carriers from; a coin flip in the other 11, which
/// still ship them). The song-to-album cascade (TryAlbumFallbackAsync) must recover
/// it on a confirmed song miss AND a confirmed artist miss, with bounded queries only
/// (the f5c701c lesson: a full Audio-catalog scan cost 11s and an InvalidResponse),
/// a containment-grade threshold (stricter than the artist cascade: "play thriller"
/// must not substitute the album while a song Thriller exists), and an opt-out
/// announcement (AnnounceCrossMediaSubstitution).
/// </summary>
[Collection("Plugin")]
public class PlaySongAlbumFallbackTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlaySongAlbumFallbackTests()
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
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private static IntentRequest CreateSongIntent(string song)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    /// <summary>
    /// Mocks the library so the song search and the artist cascade both miss while the
    /// album cascade finds <paramref name="exactAlbums"/> via the indexed SearchTerm
    /// tier. Every query is recorded for shape assertions.
    /// </summary>
    private void SetupMissWithAlbums(List<BaseItem> exactAlbums, List<BaseItem> fuzzyAlbums, List<InternalItemsQuery> queries)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                queries.Add(q);

                // Song search (Audio + SearchTerm): always misses.
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem>();
                }

                // Artist cascade DB queries (MusicArtist): no artist.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem>();
                }

                // Album cascade tier 1 (MusicAlbum + SearchTerm).
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) && q.SearchTerm != null)
                {
                    return exactAlbums;
                }

                // Album cascade tier 2 (MusicAlbum, no SearchTerm): the bounded fuzzy scan.
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))
                {
                    return fuzzyAlbums;
                }

                return new List<BaseItem>();
            });
    }

    private void SetupAlbumTracks(MusicAlbum album, List<BaseItem> tracks)
    {
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                Guid playKey = q.ParentId != Guid.Empty
                    ? q.ParentId
                    : q.AlbumIds is { Length: > 0 } ? q.AlbumIds[0] : Guid.Empty;
                return playKey == album.Id
                    ? new QueryResult<BaseItem> { Items = tracks, TotalRecordCount = tracks.Count }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 };
            });
    }

    private static (MusicAlbum Album, List<BaseItem> Tracks) MakeAlbum(string name, int trackCount)
    {
        var album = new MusicAlbum { Name = name, Id = Guid.NewGuid() };
        var tracks = new List<BaseItem>();
        for (int i = 0; i < trackCount; i++)
        {
            tracks.Add(new Audio { Name = $"{name} track {i + 1}", Id = Guid.NewGuid(), ParentId = album.Id });
        }

        return (album, tracks);
    }

    private static AudioPlayerPlayDirective? GetPlayDirective(SkillResponse response)
        => response.Response?.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;

    /// <summary>
    /// The speech text of a response whose OutputSpeech may legitimately be null (a
    /// silent AudioPlayer start): null means no speech at all, which is exactly what
    /// the announce-off tests want to prove.
    /// </summary>
    private static string? GetSpeechTextOrNull(SkillResponse response)
        => (response.Response?.OutputSpeech as PlainTextOutputSpeech)?.Text;

    // AC#5a: song miss + artist miss + album hit plays the album with the
    // FoundAlbumInstead announcement.
    [Fact]
    public async Task PlaySong_SongMiss_ArtistMiss_AlbumExists_PlaysAlbumWithAnnouncement()
    {
        SetupUserMock();
        var (album, tracks) = MakeAlbum("Abbey Road", 3);
        var queries = new List<InternalItemsQuery>();
        SetupMissWithAlbums(new List<BaseItem> { album }, new List<BaseItem>(), queries);
        SetupAlbumTracks(album, tracks);

        var handler = CreateSongHandler();
        var request = CreateSongIntent("abbey road");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        // Plays the album (AudioPlayer directive + the album's tracks in the queue).
        Assert.NotNull(GetPlayDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(3, session.NowPlayingQueue.Count);
        Assert.Equal(tracks[0].Id, session.FullNowPlayingItem?.Id);

        // Announces the substitution.
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Abbey Road", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("album", speech, StringComparison.OrdinalIgnoreCase);
    }

    // AC#5b / AC#2: a bare utterance that matches a SONG plays the song; the album
    // cascade must not even be consulted (no MusicAlbum query at all).
    [Fact]
    public async Task PlaySong_SongHit_DoesNotSubstituteAlbum()
    {
        SetupUserMock();
        var song = new Audio { Name = "Thriller", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                queries.Add(q);
                return q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio)
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("thriller");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(GetPlayDirective(response));
        Assert.Equal(song.Id, session.FullNowPlayingItem?.Id);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true);
    }

    // AC#5c: a below-threshold album match must not substitute (clean song not-found).
    // The fuzzy tier is the only source of weak candidates (the indexed SearchTerm
    // tier returns albums the search index already vouched for).
    [Fact]
    public async Task PlaySong_AlbumBelowThreshold_NoSubstitution()
    {
        SetupUserMock();
        var (album, tracks) = MakeAlbum("Abbey Road", 3);
        var queries = new List<InternalItemsQuery>();
        // Exact tier misses; the fuzzy scan returns an unrelated album whose best
        // score is far below the containment-grade bar of 90.
        SetupMissWithAlbums(new List<BaseItem>(), new List<BaseItem> { album }, queries);
        SetupAlbumTracks(album, tracks);

        var handler = CreateSongHandler();
        var request = CreateSongIntent("qqqzzz plugh");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.Null(GetPlayDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("album", speech, StringComparison.OrdinalIgnoreCase);
    }

    // AC#5d / AC#4: with AnnounceCrossMediaSubstitution off the album still plays,
    // but no FoundAlbumInstead speech is emitted.
    [Fact]
    public async Task PlaySong_AnnouncementOff_PlaysAlbumSilently()
    {
        SetupUserMock();
        _config.AnnounceCrossMediaSubstitution = false;
        var (album, tracks) = MakeAlbum("Abbey Road", 3);
        var queries = new List<InternalItemsQuery>();
        SetupMissWithAlbums(new List<BaseItem> { album }, new List<BaseItem>(), queries);
        SetupAlbumTracks(album, tracks);

        var handler = CreateSongHandler();
        var request = CreateSongIntent("abbey road");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(GetPlayDirective(response));
        Assert.Equal(3, session.NowPlayingQueue?.Count);
        string? speech = GetSpeechTextOrNull(response);
        Assert.True(string.IsNullOrWhiteSpace(speech) || !speech.Contains("album", StringComparison.OrdinalIgnoreCase));
    }

    // The same flag silences the EXISTING artist-cascade announcement (task decision:
    // one flag for both cross-media substitutions); the artist still plays.
    [Fact]
    public async Task PlaySong_AnnouncementOff_ArtistFallbackStillPlaysSilently()
    {
        SetupUserMock();
        _config.AnnounceCrossMediaSubstitution = false;
        var artistId = Guid.NewGuid();
        var artistSongs = new List<BaseItem>
        {
            new Audio { Name = "Last Nite", Id = Guid.NewGuid() },
            new Audio { Name = "Someday", Id = Guid.NewGuid() }
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "The Strokes", Id = artistId } };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return artistSongs;
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("the strokes");

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(GetPlayDirective(response));
        string? speech = GetSpeechTextOrNull(response);
        Assert.True(string.IsNullOrWhiteSpace(speech) || !speech.Contains("artist", StringComparison.OrdinalIgnoreCase));
    }

    // AC#3 (testable half): the cascade issues only bounded queries. At most two
    // MusicAlbum queries (indexed SearchTerm tier, then at most one cheap-DTO fuzzy
    // scan), and NO unbounded Audio query anywhere on the path (the f5c701c lesson:
    // a full Audio-catalog scan cost 11s and blew Alexa's response window).
    [Fact]
    public async Task PlaySong_AlbumCascade_UsesOnlyBoundedQueries()
    {
        SetupUserMock();
        var (album, tracks) = MakeAlbum("Abbey Road", 3);
        var queries = new List<InternalItemsQuery>();
        SetupMissWithAlbums(new List<BaseItem>(), new List<BaseItem> { album }, queries);
        SetupAlbumTracks(album, tracks);

        var handler = CreateSongHandler();
        var request = CreateSongIntent("abbey roade"); // exact album tier misses, fuzzy tier finds it

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);
        Assert.NotNull(GetPlayDirective(response));

        var albumQueries = queries.Where(q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true).ToList();
        Assert.True(albumQueries.Count <= 2, $"album cascade issued {albumQueries.Count} MusicAlbum queries");

        foreach (InternalItemsQuery q in albumQueries)
        {
            if (q.SearchTerm != null)
            {
                // Tier 1: the indexed exact lookup.
                continue;
            }

            // Tier 2: the one bounded fuzzy scan must carry the cheap DTO shape
            // (no images, no userdata, no current program).
            Assert.NotNull(q.DtoOptions);
            Assert.False(q.DtoOptions.EnableImages);
            Assert.False(q.DtoOptions.EnableUserData);
            Assert.False(q.DtoOptions.AddCurrentProgram);
        }

        // No Audio query may be an unbounded full-catalog scan: every one must be
        // scoped by SearchTerm (song search), ParentId/AlbumIds (album tracks), or
        // ArtistIds (artist songs).
        var audioQueries = queries.Where(q => q.IncludeItemTypes?.Contains(BaseItemKind.Audio) == true).ToList();
        Assert.NotEmpty(audioQueries);
        Assert.All(audioQueries, q =>
            Assert.True(
                q.SearchTerm != null
                || q.ParentId != Guid.Empty
                || (q.AlbumIds is { Length: > 0 })
                || (q.ArtistIds is { Length: > 0 }),
                "unbounded Audio query issued on the PlaySong miss path"));
    }

    // PRECEDENCE pin: when the artist cascade accepts, the album tier must not even be
    // consulted (a bare "play metallica" keeps playing the ARTIST, never flipping to a
    // self-titled album).
    [Fact]
    public async Task PlaySong_ArtistFound_AlbumCascadeNotQueried()
    {
        SetupUserMock();
        var artistId = Guid.NewGuid();
        var queries = new List<InternalItemsQuery>();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                queries.Add(q);
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Metallica", Id = artistId } };
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem> { new Audio { Name = "Master of Puppets", Id = Guid.NewGuid() } };
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("metallica");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(GetPlayDirective(response));
        Assert.Contains("Metallica", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true);
    }

    // The shared 2-content-word guard: a longer album title is a poor cross-media
    // query; the cascade must skip the album search entirely (conservative recall
    // scope, same trade-off the artist cascade makes).
    [Fact]
    public async Task PlaySong_MultiWordQuery_SkipsAlbumCascade()
    {
        SetupUserMock();
        var (album, tracks) = MakeAlbum("The Dark Side of the Moon", 3);
        var queries = new List<InternalItemsQuery>();
        SetupMissWithAlbums(new List<BaseItem> { album }, new List<BaseItem> { album }, queries);
        SetupAlbumTracks(album, tracks);

        var handler = CreateSongHandler();
        var request = CreateSongIntent("the dark side of the moon");

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.Null(GetPlayDirective(response));
        Assert.DoesNotContain(queries, q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true);
    }

    // JF-408 pin: a coincidental interior containment (album "O" via the 'o' in
    // "walls for cup") reaches the bar via the containment score but must NOT
    // substitute. The exact tier misses; the fuzzy scan returns the album.
    [Fact]
    public async Task PlaySong_InteriorContainmentAlbum_NotSubstituted()
    {
        SetupUserMock();
        var album = new MusicAlbum { Name = "O", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        SetupMissWithAlbums(new List<BaseItem>(), new List<BaseItem> { album }, queries);
        SetupAlbumTracks(album, new List<BaseItem> { new Audio { Name = "track 1", Id = Guid.NewGuid(), ParentId = album.Id } });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("walls for cup");

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.Null(GetPlayDirective(response));
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("album", speech, StringComparison.OrdinalIgnoreCase);
    }
}
