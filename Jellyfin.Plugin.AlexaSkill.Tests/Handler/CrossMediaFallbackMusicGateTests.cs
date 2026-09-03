#nullable enable
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
/// JF-464: the shared cross-media fallbacks (BaseHandler.TryEntityFallbackAsync for
/// artists, TryAlbumFallbackAsync for albums) play music, so the global music flag
/// (PluginConfiguration.MusicEnabled, global-only: no per-user override exists) must
/// gate them at the SHARED entry. With music disabled, a genre miss (PlayByGenre,
/// JF-463) falls through to its own not-found: no AudioPlayer directive and no
/// fallback query issued. With music enabled, the JF-463 behavior is unchanged (the
/// artist still plays). The library mocks always ARM the would-be hit, so the
/// disabled-flag tests prove the gate stops the path, not that the library was empty.
/// JF-467 superseded the full-handler mood/song miss tests this file carried
/// (PlayMoodMusic and PlaySong are now gated at ENTRY, before any query); the
/// primary-path coverage lives in MusicPrimaryPathGateTests, and the shared gates
/// are pinned here directly via the probe handlers.
/// </summary>
[Collection("Plugin")]
public class CrossMediaFallbackMusicGateTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<InternalItemsQuery> _queries = new();

    // What the artist search (MusicArtist, no Genres) and the artist songs query
    // (ArtistIds + Audio) WOULD return if the music gate let the fallback run.
    private List<BaseItem> _artistSearchResults = new();
    private List<BaseItem> _artistSongs = new();

    // What the album cascade (MusicAlbum tiers) WOULD return if the gate let it run.
    private List<BaseItem> _albumSearchResults = new();

    public CrossMediaFallbackMusicGateTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.MusicEnabled = _config.MusicEnabled,
            "alexa-crossmedia-music-gate-test");

        // Record every issued query: the disabled-flag assertions below prove the
        // artist search never RUNS, not merely that it returned nothing.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                _queries.Add(q);
                return AnswerQuery(q);
            });
    }

    public void Dispose() => _loggerFactory.Dispose();

    // Shared gate, pinned directly with no caller wiring (probe handler pattern):
    // music disabled means TryEntityFallbackAsync returns null BEFORE any library
    // query is issued, so every current and future caller inherits the gate.
    [Fact]
    public async Task SharedGate_MusicDisabled_ReturnsNullWithoutAnyQuery()
    {
        DisableMusic();
        ArmArtist("Abbey Road");
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");

        var probe = new SharedGateProbeHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        SkillResponse? result = await probe.CallTryEntityFallbackAsync(
            "abbey road", jellyfinUser, CreateUser(), CreateSession(), CreateContext(), "en-US",
            _libraryManagerMock.Object, _userDataManagerMock.Object, "gate probe", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_queries);
    }

    // Caller 1 (JF-463 wiring): music disabled + genre miss falls through to
    // NotFoundGenre, with no playback and no artist query issued at all.
    [Fact]
    public async Task GenreMiss_MusicDisabled_FallsThroughToGenreNotFound()
    {
        DisableMusic();
        SetupUserMock();
        ArmArtist("Abbey Road");

        var handler = CreateGenreHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateGenreIntent("abbey road"), CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("abbey road", speech, StringComparison.OrdinalIgnoreCase);
        // PlayByGenre issues only its genre query before the fallback: with the gate
        // shut, no MusicArtist query of any shape may be issued.
        Assert.Empty(_queries.Where(q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true));
    }

    // Caller 2 (mood, predates JF-463) was SUPERSEDED by the JF-467 entry gate on
    // PlayMoodMusic; its coverage lives in MusicPrimaryPathGateTests.

    // Control (JF-463 behavior unchanged): music enabled + genre miss + artist
    // exists still plays the artist. Runs under a wired Plugin.Instance (unlike the
    // JF-463 suite, which leaves it null) so the gate's ENABLED read is exercised
    // too, not just its disabled short-circuit.
    [Fact]
    public async Task GenreMiss_MusicEnabled_ArtistStillPlays()
    {
        SetupUserMock();
        ArmArtist("Abbey Road");

        var handler = CreateGenreHandler();
        var session = CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateGenreIntent("abbey road"), CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
        string? speech = TestHelpers.GetSpeechTextOrNull(response);
        Assert.NotNull(speech);
        Assert.Contains("Abbey Road", speech, StringComparison.Ordinal);
    }

    private static IntentRequest CreateGenreIntent(string genre, string locale = "es-ES")
    {
        var intent = new Intent { Name = IntentNames.PlayByGenre };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["genre"] = new Slot { Name = "genre", Value = genre }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    // The album cascade (TryAlbumFallbackAsync, JF-345) carries the same leak the
    // JF-464 /simplify pass found: its payoff is playing an album of music and its
    // queries skip FilterByContentAccess. Music disabled means the cascade returns
    // null BEFORE any library query is issued. JF-467 note: the old full-handler
    // driver (a PlaySong song miss) can no longer reach the cascade with music
    // disabled because PlaySong is gated at entry; the probe pins the shared gate
    // directly instead, so every current and future caller inherits it.
    [Fact]
    public async Task AlbumCascade_MusicDisabled_ReturnsNullWithoutAnyQuery()
    {
        DisableMusic();
        _albumSearchResults = new List<BaseItem> { new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() } };

        var probe = new SharedGateProbeHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        SkillResponse? result = await probe.CallTryAlbumFallbackAsync(
            "abbey road", CreateUserJellyfin(), CreateUser(), CreateSession(), CreateContext(), "en-US",
            _libraryManagerMock.Object, _userDataManagerMock.Object, "album gate probe", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_queries);
    }

    private PlayByGenreIntentHandler CreateGenreHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();
    private static Jellyfin.Database.Implementations.Entities.User CreateUserJellyfin()
        => new("testuser", "test", "test");

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(CreateUserJellyfin());
    }

    /// <summary>
    /// Disables the global music flag on both references. BOTH writes are
    /// load-bearing: when EnsurePluginInstance created the Plugin the two configs
    /// are the same object, but when an instance pre-existed they differ (the gate
    /// reads the injected _config, other paths read Plugin.Instance.Configuration).
    /// </summary>
    private void DisableMusic()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;
    }

    /// <summary>
    /// Arms the would-be fallback hit: the artist search would return this artist
    /// and its songs, so a passing disabled-flag test proves the GATE stopped the
    /// path rather than an empty library.
    /// </summary>
    private void ArmArtist(string artistName, int songCount = 2)
    {
        var artistId = Guid.NewGuid();
        _artistSearchResults = new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };
        _artistSongs = Enumerable.Range(1, songCount)
            .Select(i => (BaseItem)new Audio { Name = $"Song {i}", Id = Guid.NewGuid() })
            .ToList();
    }

    /// <summary>
    /// Answers the recorded queries: genre/mood track searches and the mood
    /// handler's genre-scoped artist search always MISS (they arm the fallback);
    /// the fallback's artist name search and the artist-songs query return the
    /// armed results.
    /// </summary>
    private List<BaseItem> AnswerQuery(InternalItemsQuery q)
    {
        bool isArtistQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist);
        bool isAudioQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio);
        bool hasGenres = q.Genres != null && q.Genres.Count > 0;

        // Genre/mood track search (Audio + Genres): the miss that arms the fallback.
        if (hasGenres && isAudioQuery)
        {
            return new List<BaseItem>();
        }

        // Mood handler's artist-genre fallback (MusicArtist + Genres): miss.
        if (isArtistQuery && hasGenres)
        {
            return new List<BaseItem>();
        }

        // Entity fallback artist search (MusicArtist, no Genres).
        if (isArtistQuery)
        {
            return _artistSearchResults;
        }

        // Artist songs (ArtistIds + Audio).
        if (q.ArtistIds != null && q.ArtistIds.Length > 0 && isAudioQuery)
        {
            return _artistSongs;
        }

        // Album cascade tiers (MusicAlbum): the hit the gate must prevent.
        if (q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))
        {
            return _albumSearchResults;
        }

        return new List<BaseItem>();
    }
}
