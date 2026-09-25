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
            (_, _, _, _, _, _, _, _) => Task.FromResult((BaseHandler.FuzzyMissOutcome.NotFound, (global::Alexa.NET.Response.SkillResponse?)null)));
    }

    private static void SetupLibrary(Mock<ILibraryManager> library, MusicAlbum album, List<Audio> tracks)
    {
        library.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(1, tracks.Count, tracks.ToList()));
        library.Setup(l => l.GetItemById(album.Id)).Returns(album);
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
        var tracker = TestHelpers.CreatePositionTracker("vehicle-album-resume");
        for (int seg = 1; seg <= 31; seg++)
        {
            tracker.RecordSegment(album.Id.ToString(), seg);
        }

        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);

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
}
