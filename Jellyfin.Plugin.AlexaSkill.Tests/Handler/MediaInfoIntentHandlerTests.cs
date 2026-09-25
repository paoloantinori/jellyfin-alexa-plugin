using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class MediaInfoIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public MediaInfoIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private MediaInfoIntentHandler CreateHandler(Jellyfin.Plugin.AlexaSkill.Alexa.Playback.DeviceQueueManager? queueManager = null)
    {
        return new MediaInfoIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            queueManager: queueManager);
    }

    private static IntentRequest CreateMediaInfoRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "MediaInfoIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };
    }

    private static IntentRequest CreateMediaInfoRequest(string infoType)
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "MediaInfoIntent",
                Slots = new Dictionary<string, Slot>
                {
                    ["media_info_type"] = new Slot { Value = infoType }
                }
            }
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    // --- Shared current-item resolver shapes (JF-629, mirroring RateItemIntentHandlerTests) ---

    /// <summary>
    /// The token-survives-PlaybackStopped shape (JF-629): Jellyfin clears the
    /// session's now-playing DTO when playback stops, but the device's
    /// AudioPlayer token survives. The pre-migration handler read ONLY the DTO
    /// and answered "nothing playing" here; the shared resolver resolves the
    /// track from the token and the answer speaks it (per-medium Audio shape,
    /// track by artist, from the projection).
    /// </summary>
    [Fact]
    public async Task Handle_StoppedClearedDto_TokenResolves_ReportsTokenTrack()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        var song = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Super Bon Bon",
            Id = Guid.NewGuid(),
            Path = "/music/s.mp3",
            Album = "Irresistible Bliss",
            AlbumArtists = new List<string> { "Soul Coughing" }
        };
        _libraryManagerMock.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        var handler = CreateHandler();
        var session = CreateSession();
        Assert.Null(session.NowPlayingItem);

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), "mediainfo-stopped-device"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Super Bon Bon", text);
        Assert.Contains("Soul Coughing", text);
    }

    /// <summary>
    /// The VideoApp displacement shape (JF-629, the Repeat/RateItem precedent):
    /// the stale music token names the last SONG while a VideoApp-routed movie
    /// plays. The video is current, so the answer speaks the MOVIE in its
    /// per-medium shape (title with year), never the stale song the session DTO
    /// still names.
    /// </summary>
    [Fact]
    public async Task Handle_StaleTokenVideoDisplacement_ReportsTheVideo()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        var queueManager = TestHelpers.CreateDeviceQueueManager("mediainfo-displace");
        var song = new MediaBrowser.Controller.Entities.Audio.Audio
        {
            Name = "Old Song",
            Id = Guid.NewGuid(),
            Path = "/music/o.mp3"
        };
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Current Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/c.mkv",
            ProductionYear = 2024
        };
        _libraryManagerMock.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _libraryManagerMock.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        string deviceId = "mediainfo-displace-device";
        queueManager.RecordLastPlayed(
            deviceId, movie.Id.ToString(), Jellyfin.Plugin.AlexaSkill.Alexa.Playback.DeviceQueueManager.LaunchRoute.VideoApp);
        var handler = CreateHandler(queueManager);
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = song.Id,
            Name = "Old Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Some Artist"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(),
            TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Current Movie", text);
        Assert.Contains("2024", text);
        Assert.DoesNotContain("Old Song", text);
    }

    /// <summary>
    /// Ensure APL visuals are enabled so "WithApl" tests pass regardless of
    /// static Plugin.Instance state left by other test classes running in parallel.
    /// </summary>
    private static void EnsureVisualsEnabled()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.AplVisualsEnabled = true;
        }
    }

    private static string GetSpeechText(SkillResponse response) => TestHelpers.GetSpeechText(response);


    /// <summary>
    /// Live incident 2026-09-23: the artist-info line spoke the raw genre entries,
    /// whose first item itself carried embedded semicolons from library tagging
    /// («Alternative Rock;Indie Rock;Post-Grunge;Rock, Folk Rock, Rock»), and the
    /// TTS read the punctuation aloud. The list must be split, trimmed, deduped.
    /// </summary>
    [Fact]
    public async Task Handle_AudioItem_SemicolonGenreSoup_IsNormalizedForSpeech()
    {
        SetupArtistLookup("Three Fish", null, new[] { "Alternative Rock;Indie Rock;Post-Grunge;Rock", "Folk Rock", "Rock" });
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Can I Come Along",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Three Fish",
            Album = "no title"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Alternative Rock, Indie Rock", text, StringComparison.Ordinal);
        Assert.DoesNotContain(";", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Post-Grunge;Rock", text, StringComparison.Ordinal);
    }

    private void SetupArtistLookup(string artistName, string? overview, string[]? genres)
    {
        var artistItem = new MusicArtist { Name = artistName, Id = Guid.NewGuid() };
        artistItem.Overview = overview;
        artistItem.Genres = genres ?? Array.Empty<string>();

        var artistList = new List<BaseItem> { artistItem };
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Length > 0 && q.IncludeItemTypes[0] == BaseItemKind.MusicArtist)
                {
                    return artistList;
                }

                return new List<BaseItem>();
            });
    }

    [Theory]
    [InlineData("MediaInfoIntent", true)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_NoMediaPlaying_ReturnsNothingPlaying()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains("Nothing is currently playing", GetSpeechText(response));
    }

    [Fact]
    public async Task Handle_AudioItem_ReportsTrackArtistAndAlbum()
    {
        SetupArtistLookup("Queen", "Queen are a British rock band.", new[] { "Rock" });
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Queen",
            Album = "A Night at the Opera"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Bohemian Rhapsody", text);
        Assert.Contains("Queen", text);
        Assert.Contains("A Night at the Opera", text);
    }

    [Fact]
    public async Task Handle_AudioItem_NoAlbum_ReportsTrackAndArtist()
    {
        SetupArtistLookup("The Beatles", null, null);
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Yesterday",
            Type = BaseItemKind.Audio,
            AlbumArtist = "The Beatles"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Yesterday", text);
        Assert.Contains("The Beatles", text);
    }

    [Fact]
    public async Task Handle_AudioItem_NoArtistOrAlbum_ReportsTrackOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Mystery Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Mystery Track", text);
    }

    [Fact]
    public async Task Handle_AudioItem_WithArtistBio_IncludesBioInResponse()
    {
        SetupArtistLookup("Queen", "Queen are a British rock band formed in London in 1970. They are one of the most commercially successful bands.", new[] { "Rock", "Classic Rock" });
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Queen",
            Album = "A Night at the Opera"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Bohemian Rhapsody", text);
        Assert.Contains("Queen", text);
        Assert.Contains("British rock band", text);
    }

    [Fact]
    public async Task Handle_AudioItem_WithGenresOnly_IncludesGenreInResponse()
    {
        SetupArtistLookup("Radiohead", null, new[] { "Alternative", "Experimental" });
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Karma Police",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Radiohead",
            Album = "OK Computer"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Alternative", text);
    }

    [Fact]
    public async Task Handle_AudioItem_NoArtistMetadata_FallsBackToBasicInfo()
    {
        // No artist found in library
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Unknown Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Unknown Artist",
            Album = "Unknown Album"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        // Should still report track info even without artist metadata
        Assert.Contains("Unknown Song", text);
        Assert.Contains("Unknown Artist", text);
    }

    [Fact]
    public async Task Handle_EpisodeItem_ReportsSeriesSeasonEpisode()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Pilot",
            Type = BaseItemKind.Episode,
            SeriesName = "Breaking Bad",
            ParentIndexNumber = 1,
            IndexNumber = 1
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Breaking Bad", text);
        Assert.Contains("season 1", text);
        Assert.Contains("episode 1", text);
        Assert.Contains("Pilot", text);
    }

    [Fact]
    public async Task Handle_EpisodeItem_NoSeriesName_ReportsEpisodeOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Unknown Episode",
            Type = BaseItemKind.Episode
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Unknown Episode", text);
    }

    [Fact]
    public async Task Handle_MovieItem_ReportsTitleAndYear()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "The Matrix",
            Type = BaseItemKind.Movie,
            ProductionYear = 1999
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("The Matrix", text);
        Assert.Contains("1999", text);
    }

    [Fact]
    public async Task Handle_MovieItem_NoYear_ReportsTitleOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Old Movie",
            Type = BaseItemKind.Movie
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Old Movie", text);
        Assert.DoesNotContain("(", text);
    }

    [Fact]
    public async Task Handle_UnknownType_ReportsName()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Some Media",
            Type = BaseItemKind.Photo
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Some Media", text);
    }

    [Fact]
    public async Task Handle_WithPositionAndRuntime_ReportsPosition()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(2).Ticks
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("2 minutes", text);
        Assert.Contains("4 minutes", text);
    }

    [Fact]
    public async Task Handle_WithPositionNoRuntime_ReportsPositionOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(1).Ticks
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("1 minutes", text);
        Assert.DoesNotContain("of", text);
    }

    [Fact]
    public async Task Handle_NullPlayState_NoPositionReported()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist"
        };
        session.PlayState = null;

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.DoesNotContain("Position", text);
    }

    [Fact]
    public async Task Handle_BioTruncation_TruncatesToTwoSentences()
    {
        string longBio = "Queen are a British rock band formed in London in 1970. Their classic lineup was Freddie Mercury, Brian May, Roger Taylor, and John Deacon. The band has sold over 300 million records worldwide.";
        SetupArtistLookup("Queen", longBio, new[] { "Rock" });

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Queen",
            Album = "A Night at the Opera"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        // Should contain first two sentences but not the third
        Assert.Contains("British rock band", text);
        Assert.DoesNotContain("300 million", text);
    }

    // --- Slot-based specific info query tests ---

    [Fact]
    public async Task Handle_SlotTitle_ReturnsTitleAndArtist()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Queen"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("title"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Bohemian Rhapsody", text);
        Assert.Contains("Queen", text);
    }

    [Fact]
    public async Task Handle_SlotTitle_NoArtist_ReturnsTitleOnly()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Mystery Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("title"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Mystery Track", text);
    }

    [Fact]
    public async Task Handle_SlotAlbum_ReturnsAlbumName()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            Album = "A Night at the Opera"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("album"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("A Night at the Opera", text);
    }

    [Fact]
    public async Task Handle_SlotAlbum_NoAlbum_ReturnsUnavailable()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("album"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("album", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Handle_SlotArtist_ReturnsArtistName()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Daft Punk"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("artist"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Daft Punk", text);
    }

    [Fact]
    public async Task Handle_SlotArtist_NoArtist_ReturnsUnavailable()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("artist"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("artist", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Handle_SlotYear_ReturnsProductionYear()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Song",
            Type = BaseItemKind.Audio,
            ProductionYear = 1975
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("year"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("1975", text);
    }

    [Fact]
    public async Task Handle_SlotYear_NoYear_ReturnsUnavailable()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("year"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("year", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Handle_SlotDuration_ReturnsFormattedDuration()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Song",
            Type = BaseItemKind.Audio,
            RunTimeTicks = TimeSpan.FromMinutes(3).Ticks + TimeSpan.FromSeconds(45).Ticks
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("duration"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("3 minutes", text);
    }

    [Fact]
    public async Task Handle_SlotDuration_LongTrack_ReportsHoursAndMinutes()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Audiobook",
            Type = BaseItemKind.Audio,
            RunTimeTicks = TimeSpan.FromHours(2).Ticks + TimeSpan.FromMinutes(30).Ticks
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("duration"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("2 hours", text);
        Assert.Contains("30 minutes", text);
    }

    [Fact]
    public async Task Handle_SlotDuration_NoRuntime_ReturnsUnavailable()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("duration"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("duration", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Handle_SlotGenre_ReturnsGenre()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Song",
            Type = BaseItemKind.Audio,
            Genres = new[] { "Rock", "Classic Rock" }
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("genre"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Rock", text);
    }

    [Fact]
    public async Task Handle_SlotGenre_MultipleGenres_ReturnsUpToThree()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Song",
            Type = BaseItemKind.Audio,
            Genres = new[] { "Rock", "Pop", "Jazz", "Classical" }
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("genre"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Rock", text);
        Assert.Contains("Pop", text);
        Assert.Contains("Jazz", text);
        Assert.DoesNotContain("Classical", text);
    }

    [Fact]
    public async Task Handle_SlotGenre_NoGenre_ReturnsUnavailable()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Track",
            Type = BaseItemKind.Audio,
            Genres = Array.Empty<string>()
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("genre"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("genre", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Handle_SlotBiography_ReturnsArtistInfo()
    {
        SetupArtistLookup("Queen", "Queen are a British rock band formed in London.", new[] { "Rock" });
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Bohemian Rhapsody",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Queen"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("biography"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Queen", text);
        Assert.Contains("British rock band", text);
    }

    [Fact]
    public async Task Handle_SlotBiography_NoArtistName_ReturnsUnavailable()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Track",
            Type = BaseItemKind.Audio
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("biography"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("artist", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Handle_SlotBiography_NoBioData_ReturnsUnavailable()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Unknown Artist"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("biography"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Unknown Artist", text);
    }

    [Fact]
    public async Task Handle_SlotUnknown_FallsBackToFullInfo()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest("unknown_slot"), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Test Song", text);
    }

    [Fact]
    public async Task Handle_NoSlot_PreservesBackwardCompatibility()
    {
        // Without a slot, the handler should behave exactly as before
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist",
            Album = "Album"
        };

        var text = GetSpeechText(await handler.HandleAsync(
            CreateMediaInfoRequest(), CreateContext(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None));

        Assert.Contains("Test Song", text);
        Assert.Contains("Artist", text);
        Assert.Contains("Album", text);
    }

    // --- APL visual card tests ---

    [Fact]
    public async Task Handle_DefaultNowPlaying_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Super Bon Bon",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Soul Coughing",
            Album = "Irresistible Bliss"
        };

        var context = TestHelpers.CreateContextWithApl();
        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), context,
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task Handle_DefaultNowPlaying_WithoutApl_NoAplDirective()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Super Bon Bon",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Soul Coughing",
            Album = "Irresistible Bliss"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), context,
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task Handle_SpecificTitle_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Super Bon Bon",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Soul Coughing"
        };

        var context = TestHelpers.CreateContextWithApl();
        var response = await handler.HandleAsync(
            CreateMediaInfoRequest("title"), context,
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task Handle_SpecificTitle_WithoutApl_NoAplDirective()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Super Bon Bon",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Soul Coughing"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var response = await handler.HandleAsync(
            CreateMediaInfoRequest("title"), context,
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task Handle_DefaultNowPlaying_EmptyItemId_NoAplDirective()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.Empty,
            Name = "Unknown",
            Type = BaseItemKind.Audio
        };

        var context = TestHelpers.CreateContextWithApl();
        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), context,
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    // --- Position display tests (M4: SeekEnabled-gated StandardCard + APL progress) ---

    [Fact]
    public async Task Handle_SeekEnabled_WithProgress_IncludesStandardCard()
    {
        _config.SeekEnabled = true;
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.SeekEnabled = true,
            "mediainfo-seek-card");

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(2).Ticks
        };

        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), TestHelpers.CreateContextWithoutApl(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response.Response.Card);
        var card = Assert.IsType<StandardCard>(response.Response.Card);
        Assert.Contains("Test Song", card.Content);
        Assert.Contains("/", card.Content); // "elapsed / total" format
    }

    [Fact]
    public async Task Handle_SeekDisabled_NoStandardCard()
    {
        _config.SeekEnabled = false;
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.SeekEnabled = false,
            "mediainfo-no-seek-card");

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(2).Ticks
        };

        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), TestHelpers.CreateContextWithoutApl(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Null(response.Response.Card);
    }

    [Fact]
    public async Task Handle_SeekEnabled_NoProgress_IncludesCardWithoutProgress()
    {
        _config.SeekEnabled = true;
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.SeekEnabled = true,
            "mediainfo-seek-no-progress");

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist"
        };
        // No PlayState → no progress

        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), TestHelpers.CreateContextWithoutApl(),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // StandardCard is attached by SeekEnabled path even without progress,
        // but no elapsed/total line. Card should still be present with just the name.
        Assert.NotNull(response.Response.Card);
        var card = Assert.IsType<StandardCard>(response.Response.Card);
        Assert.Contains("Test Song", card.Content);
        Assert.DoesNotContain("/", card.Content);
    }

    [Fact]
    public async Task Handle_SeekEnabled_WithAplAndProgress_IncludesAplDirective()
    {
        EnsureVisualsEnabled();
        _config.SeekEnabled = true;
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => { c.SeekEnabled = true; c.AplVisualsEnabled = true; },
            "mediainfo-seek-apl");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingItem = new BaseItemDto
        {
            Id = Guid.NewGuid(),
            Name = "Test Song",
            Type = BaseItemKind.Audio,
            AlbumArtist = "Artist",
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks
        };
        session.PlayState = new PlayerStateInfo
        {
            PositionTicks = TimeSpan.FromMinutes(2).Ticks
        };

        var context = TestHelpers.CreateContextWithApl();
        var response = await handler.HandleAsync(
            CreateMediaInfoRequest(), context,
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }
}
