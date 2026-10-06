using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-625: on the VideoApp route the album announce must ride the PROGRESSIVE-response
/// vehicle (the fast-start player steals the audio channel before a final-response
/// speech finishes, the JF-501 observation); on vehicle failure the speech falls back
/// onto the final response. The pin: a capturing sendProgressiveResponse observes the
/// ALBUM name, and the final response is speechless on success / speech-carrying on
/// failure, with the whole-album concat directive in both cases.
/// </summary>
[Collection("Plugin")]
public class AlbumAnnounceVehicleTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    public AlbumAnnounceVehicleTests()
    {
        TestHelpers.EnsurePluginInstance(new PluginConfiguration(), _loggerFactory, c => { }, "album-announce-vehicle");
    }

    private static MusicAlbum Album()
        => new() { Name = "Temple of the Dog", Id = Guid.NewGuid() };

    private static List<Audio> Tracks()
        => Enumerable.Range(1, 3).Select(i => new Audio
        {
            Name = $"Track {i}",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        }).ToList();

    private SessionInfo Session()
        => new(
            new Mock<ISessionManager>().Object,
            _loggerFactory.CreateLogger<SessionInfo>())
        {
            UserId = Guid.NewGuid(),
            DeviceId = "test-device",
        };

    private AlbumPlayService CreateService(PluginConfiguration config, List<string> capturedSpeech, Func<bool> vehicleResult)
    {
        ILogger logger = _loggerFactory.CreateLogger<AlbumAnnounceVehicleTests>();
        var launch = new PlaybackLaunchBuilder(
            config,
            logger,
            (_, _, speech) =>
            {
                capturedSpeech.Add(speech);
                return Task.FromResult(vehicleResult());
            });
        var search = new SearchService(config, logger, requestTimeoutMs: 6000);
        var crossMedia = new CrossMediaFallback(config, logger, launch, requestTimeoutMs: 6000);
        return new AlbumPlayService(
            config, logger, launch, search, crossMedia, requestTimeoutMs: 6000,
            TestHelpers.FuzzyMissNotFound);
    }

    private static void SetupLibrary(Mock<ILibraryManager> library, MusicAlbum album, List<Audio> tracks)
    {
        library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(1, tracks.Count, tracks.ToList()));
        library.Setup(l => l.GetItemById(album.Id)).Returns(album);
    }

    /// <summary>
    /// Warms a swapped-in position tracker to 5 minutes into the album (31
    /// segments recorded; the conservative high-water minus one lands 300s), the
    /// shared warm-up of both criterion-3 pins. Dispose the returned scope.
    /// </summary>
    private static IDisposable WarmTrackerFiveMinutesIn(Guid albumId)
    {
        var tracker = TestHelpers.CreatePositionTracker("vehicle-album-tracker");
        for (int seg = 1; seg <= 31; seg++)
        {
            tracker.RecordSegment(albumId.ToString(), seg);
        }

        return TestHelpers.SwapPluginPositionTracker(tracker);
    }

    [Fact]
    public async Task SeekModeAlbumPlay_AnnounceRidesTheVehicleWithTheAlbumName()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true, AnnounceAudioPlays = true };
        var captured = new List<string>();
        var svc = CreateService(config, captured, vehicleResult: () => true);
        var album = Album();
        var tracks = Tracks();
        var library = new Mock<ILibraryManager>();
        SetupLibrary(library, album, tracks);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var response = await svc.BuildAlbumPlayResponseAsync(
            album, jellyfinUser, TestHelpers.CreateTestUser(), Session(),
            TestHelpers.CreateContextWithVideoApp(), "it-IT",
            library.Object, new Mock<IUserDataManager>().Object, null, "AlbumAnnounceVehicle",
            request: new IntentRequest());

        Assert.Contains(captured, s => s.Contains("Temple of the Dog", StringComparison.Ordinal));
        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"/video-audio/audiobook/{album.Id}/stream.m3u8", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task VehicleFailure_AnnounceFallsBackOntoTheFinalResponse()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true, AnnounceAudioPlays = true };
        var captured = new List<string>();
        var svc = CreateService(config, captured, vehicleResult: () => false);
        var album = Album();
        var tracks = Tracks();
        var library = new Mock<ILibraryManager>();
        SetupLibrary(library, album, tracks);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var response = await svc.BuildAlbumPlayResponseAsync(
            album, jellyfinUser, TestHelpers.CreateTestUser(), Session(),
            TestHelpers.CreateContextWithVideoApp(), "it-IT",
            library.Object, new Mock<IUserDataManager>().Object, null, "AlbumAnnounceVehicle",
            request: new IntentRequest());

        Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.Contains("Temple of the Dog", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRequest_AnnounceStaysOnTheFinalResponse()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true, AnnounceAudioPlays = true };
        var captured = new List<string>();
        var svc = CreateService(config, captured, vehicleResult: () => true);
        var album = Album();
        var tracks = Tracks();
        var library = new Mock<ILibraryManager>();
        SetupLibrary(library, album, tracks);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        var response = await svc.BuildAlbumPlayResponseAsync(
            album, jellyfinUser, TestHelpers.CreateTestUser(), Session(),
            TestHelpers.CreateContextWithVideoApp(), "it-IT",
            library.Object, new Mock<IUserDataManager>().Object, null, "AlbumAnnounceVehicle",
            request: null);

        Assert.Empty(captured);
        Assert.NotNull(response.Response.OutputSpeech);
    }

    // ---------------------------------------------------------------------
    // JF-625 criterion 3: the seek-mode album resume reads the position tracker
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SeekModeAlbumResume_TrackerPosition_MapsOntoTrackAndOffset()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true, AnnounceAudioPlays = true };
        var captured = new List<string>();
        var svc = CreateService(config, captured, vehicleResult: () => true);
        var album = Album();
        var tracks = Tracks(); // 3 tracks x 4 min = 720s total
        var library = new Mock<ILibraryManager>();
        SetupLibrary(library, album, tracks);
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        // Tracker: 5 minutes into the album (30 segments x 10s) = track 2, 60s in.
        using var trackerSwap = WarmTrackerFiveMinutesIn(album.Id);

        var response = await svc.BuildAlbumPlayResponseAsync(
            album, jellyfinUser, TestHelpers.CreateTestUser(), Session(),
            TestHelpers.CreateContextWithVideoApp(), "it-IT",
            library.Object, new Mock<IUserDataManager>().Object, null, "AlbumAnnounceVehicle",
            request: new IntentRequest());

        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        // Offset = track 1 runtime (4 min) + the 60s in-track partial.
        Assert.Contains($"start={TimeSpan.FromMinutes(4).Ticks + TimeSpan.FromSeconds(60).Ticks}", launch.VideoItem.Source, StringComparison.Ordinal);
        // The metadata names the resume TRACK (track 2), not track 1.
        Assert.Equal("Track 2", launch.VideoItem.Metadata?.Title);
    }

    // JF-796 companion pin: the deep-resume block's tracker veto. A warm tracker on
    // the seek route stays the resume truth (JF-625 criterion 3) even when UserData
    // progress sits BEYOND the initial page (the JF-796 defect shape on a 26-track
    // album): the tracker position maps onto the PAGE track and the launch keeps the
    // tracker's offset, with no re-slice at the UserData track (the queue keeps the
    // page window, so the walk's prefix sum stays the album-absolute timeline).
    [Fact]
    public async Task SeekModeAlbumResume_WarmTracker_VetoesTheDeepUserDataResume()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true, AnnounceAudioPlays = true };
        var captured = new List<string>();
        var svc = CreateService(config, captured, vehicleResult: () => true);
        var album = Album();

        // 26 tracks x 4 min, paging-honoring tracks mock (page 5, deep fetch all).
        List<Audio> tracks = Enumerable.Range(1, 26).Select(i => new Audio
        {
            Name = $"Track {i:00}",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        }).ToList();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => new QueryResult<BaseItem>
            {
                Items = tracks.Skip(q.StartIndex ?? 0).Take(q.Limit ?? tracks.Count).ToList(),
                TotalRecordCount = tracks.Count
            });
        library.Setup(l => l.GetItemById(album.Id)).Returns(album);

        // Deep UserData progress on track 22 (index 21), the JF-796 defect shape.
        var inProgress = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks
        };
        var userData = new Mock<IUserDataManager>();
        userData.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                item.Id == tracks[21].Id ? inProgress : null);

        // Tracker: 5 minutes into the album (31 segments x 10s, conservative
        // high-water minus one) = PAGE track 2 (index 1), 60s in.
        using var trackerSwap = WarmTrackerFiveMinutesIn(album.Id);

        SessionInfo session = Session();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        var response = await svc.BuildAlbumPlayResponseAsync(
            album, jellyfinUser, TestHelpers.CreateTestUser(), session,
            TestHelpers.CreateContextWithVideoApp(), "it-IT",
            library.Object, userData.Object, null, "AlbumAnnounceVehicle",
            request: new IntentRequest());

        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        // The tracker's mapping wins: track 1 runtime + the 60s in-track partial,
        // NOT the UserData track's absolute prefix.
        Assert.Contains($"start={TimeSpan.FromMinutes(4).Ticks + TimeSpan.FromSeconds(60).Ticks}", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.Equal("Track 02", launch.VideoItem.Metadata?.Title);

        // No re-slice happened: the queue keeps the page window starting at the
        // tracker's track (index 1), not the deep UserData track (index 21).
        Assert.Equal(tracks[1].Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(tracks[1].Id, session.NowPlayingQueue[0].Id);
    }

    // JF-796 companion pin (the code-review F3 gap): the COLD-tracker seek route is
    // the leg deepResumePrefixTicks exists for. A VideoApp device with no tracked
    // position and UserData progress beyond the initial page takes the deep resume
    // on the video route too: the concat must slice at the ABSOLUTE album prefix
    // (the runtimes of the 21 tracks before the position-holding one), not at 0.
    [Fact]
    public async Task SeekModeAlbumResume_ColdTracker_DeepResumeSlicesTheConcatAtTheAbsolutePrefix()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/", NativeControlsForAudio = true, AnnounceAudioPlays = true };
        var captured = new List<string>();
        var svc = CreateService(config, captured, vehicleResult: () => true);
        var album = Album();

        // 26 tracks x 4 min, paging-honoring tracks mock (page 5, deep fetch all).
        List<Audio> tracks = Enumerable.Range(1, 26).Select(i => new Audio
        {
            Name = $"Track {i:00}",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(4).Ticks,
        }).ToList();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => new QueryResult<BaseItem>
            {
                Items = tracks.Skip(q.StartIndex ?? 0).Take(q.Limit ?? tracks.Count).ToList(),
                TotalRecordCount = tracks.Count
            });
        library.Setup(l => l.GetItemById(album.Id)).Returns(album);

        // Deep UserData progress on track 22 (index 21); NO tracker swap, so the
        // plugin instance's tracker stays cold (unassigned in the test host).
        var inProgress = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks
        };
        var userData = new Mock<IUserDataManager>();
        userData.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                item.Id == tracks[21].Id ? inProgress : null);

        SessionInfo session = Session();
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        var response = await svc.BuildAlbumPlayResponseAsync(
            album, jellyfinUser, TestHelpers.CreateTestUser(), session,
            TestHelpers.CreateContextWithVideoApp(), "it-IT",
            library.Object, userData.Object, null, "AlbumAnnounceVehicle",
            request: new IntentRequest());

        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        // The absolute prefix of the 21 tracks before track 22 (21 x 4 min): the
        // album-absolute slice the concat timeline needs, not the page-relative 0.
        Assert.Contains($"start={TimeSpan.FromMinutes(84).Ticks}", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.Equal("Track 22", launch.VideoItem.Metadata?.Title);
        Assert.Equal(tracks[21].Id, session.FullNowPlayingItem!.Id);
        Assert.Equal(tracks[21].Id, session.NowPlayingQueue[0].Id);
    }
}
