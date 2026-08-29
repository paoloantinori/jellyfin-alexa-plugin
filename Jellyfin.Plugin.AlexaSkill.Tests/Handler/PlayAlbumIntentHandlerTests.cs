using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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

[Collection("Plugin")]
public class PlayAlbumIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;

    public PlayAlbumIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, c => { }, "playalbum-tests");
    }

    private PlayAlbumIntentHandler CreateHandler()
    {
        return new PlayAlbumIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string? album = null, string? musician = null)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (album != null)
        {
            intent.Slots["album"] = new global::Alexa.NET.Request.Slot { Name = "album", Value = album };
        }

        if (musician != null)
        {
            intent.Slots["musician"] = new global::Alexa.NET.Request.Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private void SetupAlbumsAndTracks(List<BaseItem> albums, QueryResult<BaseItem>? tracks = null)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(albums);
        if (tracks != null)
        {
            _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
                .Returns(tracks);
        }
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_PromptsDisambiguation()
    {
        // JF-341: distinct-name multi-match (e.g. "Greatest Hits" vs "Biggest Hits") must
        // prompt the user (AskFirstMatch), not silently auto-play the best-scoring one.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(new List<BaseItem> { album1, album2 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // AskFirstMatch keeps the session open + does NOT auto-play.
        Assert.False(response.Response.ShouldEndSession);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_AutoPlayUser_AutoPlaysFirst()
    {
        // JF-341 review: an AutoPlay user (opted out of disambiguation prompts) auto-plays
        // even for distinct-name collisions.
        var user = TestHelpers.CreateTestUser();
        user.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_MultipleSameNameAlbums_AutoPlaysFirst()
    {
        // JF-341: same-name duplicates (e.g. two "Jazz Cafe" disc-albums) auto-play the first
        // -- a "Jazz Cafe or Jazz Cafe?" prompt would be useless.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "jazz cafe");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AutoPlays()
    {
        // Regression guard (AC#5): single-match albums still auto-play.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_InteriorContainmentFuzzyMatch_DoesNotAutoPlay()
    {
        // JF-408 incident replay (live 2026-08-28): query "walls for cup" (ASR for "Waltz for
        // Koop") fuzzy-matched album "O" at ContainmentScore because the query contains an 'o'
        // inside "for", and auto-played it on-device. The fuzzy match is returned by the recall
        // layer; the auto-play decision must reject an interior-only containment.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "walls for cup");
        SetupUserMock();

        // Exact search (SearchTerm set) must miss; the fuzzy fallback's full-catalog scan
        // (SearchTerm null) returns the degenerate 1-char album.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => q.SearchTerm == null
                ? new List<BaseItem> { new MusicAlbum { Name = "O", Id = Guid.NewGuid() } }
                : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.Null(playDirective);
    }

    [Fact]
    public async Task HandleAsync_MusicianOnly_PlaysArtistsAlbum()
    {
        // JF-411: "un disco dei Koop" (indefinite album-by-artist) fills only the musician
        // slot. The handler must resolve the artist's album and play it, not discard the
        // artist behind an album-name reprompt. The album resolution must filter on
        // ALBUM artists (AlbumArtistIds): ArtistIds matches any album CONTAINING a track
        // by the artist, which on the live library picked a compilation featuring Koop
        // over Koop's own album.
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "koop");
        SetupUserMock();

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Baby", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                return new List<BaseItem> { album };
            });
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);

        // The indefinite-resolution query (the MusicAlbum query with no SearchTerm) must
        // filter on album artists.
        InternalItemsQuery? resolutionQuery = queries.FirstOrDefault(q =>
            q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true && q.SearchTerm == null);
        Assert.NotNull(resolutionQuery);
        Assert.NotNull(resolutionQuery.AlbumArtistIds);
        Assert.Contains(artist.Id, resolutionQuery.AlbumArtistIds);
    }

    [Fact]
    public async Task HandleAsync_BothSlotsEmpty_ElicitsMusicianViaDialogDirective()
    {
        // JF-411 on-device reproduction (3x: 20:23, 20:56): the device ASR swallows the
        // short foreign artist name, so "un disco dei Koop" arrives as PlayAlbumIntent
        // with BOTH slots empty (profile-nlu: "un disco di/dei" reproduces the shape).
        // With no information at all, the useful question is WHICH ARTIST (the phrase
        // shape is album-by-artist); eliciting the musician feeds the JF-411 resolution
        // which plays an album without ever needing a title.
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.Reprompt);
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        Assert.NotNull(elicit);
        Assert.Equal("musician", elicit.SlotToElicit);
        Assert.Equal("PlayAlbumIntent", elicit.UpdatedIntent.Name);
        // Amazon requires updatedIntent to declare EVERY intent slot (live INVALID_RESPONSE
        // 2026-08-28 21:17: "All slots must be defined... Missing: album").
        Assert.Equal(new[] { "album", "musician" }, elicit.UpdatedIntent.Slots.Keys.OrderBy(k => k).ToArray());
        // JF-398: the elicit owns no flow state, so every OTHER flow's keys must be
        // marked for removal (no stale resume/disambiguation/pagination rides along).
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey(Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.SessionAttributeRemoval.MarkerKey), "elicit must mark other flows inactive");
    }

    [Fact]
    public async Task HandleAsync_DialogInProgressWithMusician_ElicitsAlbumViaDialogDirective()
    {
        // Delegated dialog mid-flow: the musician is known and the phrasing implied an
        // album title, so elicit the ALBUM slot (context preserved via Dialog.ElicitSlot).
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "queen");
        request.DialogState = "IN_PROGRESS";
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        Assert.NotNull(elicit);
        Assert.Equal("album", elicit.SlotToElicit);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AnnounceAudioPlaysOffByDefault_SilentLaunch()
    {
        // JF-353 AC#4 / JF-352.4: audio plays are silent by default. Even with the video-launch
        // toggle (DefaultAnnounceNowPlaying) on, a music play must stay silent unless the separate
        // AnnounceAudioPlays flag is opted in. This guards "no behavior change for existing users"
        // on the most frequent play paths.
        _config.DefaultAnnounceNowPlaying = true; // video/book toggle on
        _config.AnnounceAudioPlays = false; // audio opt-in default
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AnnounceAudioPlaysOn_SpeaksNowPlaying()
    {
        // When the user opts into audio announces (AnnounceAudioPlays = true), a successful
        // album play attaches the now-playing announce to OutputSpeech.
        _config.AnnounceAudioPlays = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        // The now-playing announce speaks the track that starts playing ("Track 1"), since the
        // AudioPlayer token is the first track of the album, not the album container itself.
        Assert.Contains("Track 1", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AnnounceAudioPlaysOff_SilentLaunch()
    {
        // With AnnounceAudioPlays explicitly off, the launch is silent (no OutputSpeech)
        // but playback still starts (AudioPlayer.Play directive present).
        _config.AnnounceAudioPlays = false;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }
}
