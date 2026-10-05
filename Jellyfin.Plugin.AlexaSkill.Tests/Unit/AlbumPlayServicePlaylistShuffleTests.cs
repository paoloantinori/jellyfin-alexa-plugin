#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-713 pins for the PlayPlaylist shuffle arm's derive-then-commit reorder in
/// <see cref="AlbumPlayService.BuildPlaylistPlayResponseAsync"/>: the shuffled
/// order is DERIVED once before the launch build (the first track is picked from
/// the snapshot) and <see cref="DeviceQueueManager.CommitShuffledQueue"/> stores
/// the SAME snapshot only after a successful build, so a refused shuffle start
/// leaves the device queue untouched (the JF-699 residual this task closes) and a
/// successful one stores exactly the order the launch built from.
/// DRIVE-THE-SERVICE, not the handler: the pins call the service method directly
/// (the sanctioned JF-713 evidence shape; the handler shell adds nothing to the
/// write ordering under test). The former "hard to pin end-to-end" limitation
/// (Playlist.GetManageableItems non-virtual, DB-coupled) is bypassed WITHOUT
/// touching production seams: a real <see cref="Playlist"/> whose
/// <see cref="Folder.LinkedChildren"/> carry the track ids resolves through the
/// stubbed static <c>BaseItem.LibraryManager</c> (GetLinkedChild/ResolveLinkedChildren
/// ask GetItemById, or on the 12.x ref GetItemList(ItemIds)), the same
/// StubBaseItemStatics shape PlayPlaylistIntentHandlerTests uses for IsVisible.
/// The builder seam is the REAL refusal (config-driven): seek mode with an empty
/// StreamTokenSecret makes BuildAudioPlayerResponse throw
/// <see cref="StreamTokenNotConfiguredException"/> from the token-delivery guard,
/// the same shape MusicPathLaunchRefusalTests pins for the artist/song/album paths.
/// RED PROOF: with SetShuffledQueue back above the launch build (the pre-JF-713
/// order) the refusal pin fails on the sentinel-queue assertions.
/// </summary>
[Collection("Plugin")]
public class AlbumPlayServicePlaylistShuffleTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IUserManager> _userManagerMock = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    private static PluginConfiguration SeekModeBrokenConfig()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        config.NativeControlsForAudio = true;
        config.StreamTokenSecret = string.Empty;
        return config;
    }

    private static PluginConfiguration WorkingConfig()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        return config;
    }

    private AlbumPlayService CreateService(PluginConfiguration config)
    {
        ILogger logger = _loggerFactory.CreateLogger<AlbumPlayServicePlaylistShuffleTests>();
        var launch = new PlaybackLaunchBuilder(config, logger, (_, _, _) => Task.FromResult(false));
        var search = new SearchService(config, logger, requestTimeoutMs: 6000);
        var crossMedia = new CrossMediaFallback(config, logger, launch, requestTimeoutMs: 6000);
        return new AlbumPlayService(
            config, logger, launch, search, crossMedia, requestTimeoutMs: 6000,
            TestHelpers.FuzzyMissNotFound);
    }

    /// <summary>
    /// One resolvable five-track playlist: a real <see cref="Playlist"/> whose
    /// LinkedChildren point at the Audio tracks, one exact server hit, and the
    /// library mock resolving ids (the linked-child path and the service's own
    /// first-track lookup share it).
    /// </summary>
    private (Playlist Playlist, List<Audio> Tracks) SetupPlaylist(string name = "road trip songs")
    {
        var tracks = Enumerable.Range(0, 5)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i}", Tags = Array.Empty<string>() })
            .ToList();

        var playlist = new Playlist { Name = name, Id = Guid.NewGuid(), Tags = Array.Empty<string>() };
        playlist.LinkedChildren = tracks.Select(t => new LinkedChild { ItemId = t.Id }).ToArray();

        var byId = tracks.ToDictionary(t => t.Id, t => (BaseItem)t);
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => byId.TryGetValue(id, out BaseItem? item) ? item : null!);
        // The 12.x ref resolves linked children through a batched ItemIds query
        // (GetItemList); 10.11 never asks. Returning the tracks for any list query
        // is inert on the net9 path (the flow's own queries go to GetItemsResult).
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(tracks.Cast<BaseItem>().ToList());

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { playlist },
                TotalRecordCount = 1
            });

        return (playlist, tracks);
    }

    // ------------------------------------------------------------------
    // Refused shuffle start: the device queue stays untouched
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlaylistShuffle_SeekModeEmptySecret_Refuses_DeviceQueueUntouched()
    {
        using var statics = StubBaseItemStatics();
        SetupPlaylist();
        var config = SeekModeBrokenConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-shuffle-refused");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-shuffle-refused");
        // A PRE-EXISTING ordered queue: the strongest form of "untouched". The old
        // order (SetShuffledQueue before the build) replaced it with the shuffled
        // ids of a launch that then refused; the reorder must leave it exactly as
        // it was, so a later resume/browse answers the real previous queue.
        List<string> sentinel = new() { "sentinel-1", "sentinel-2", "sentinel-3" };
        queueManager.SetQueue(deviceId, sentinel, 0);

        TestHelpers.EnsurePluginInstance(config, _loggerFactory, c => { }, "playlist-shuffle-refused");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => svc.BuildPlaylistPlayResponseAsync(
                _libraryManagerMock.Object,
                _userManagerMock.Object,
                queueManager,
                "road trip songs",
                context,
                TestHelpers.CreateTestUser(jellyfinToken: "tok"),
                session,
                "en-US",
                shuffle: true,
                new Random(42),
                kanaOrigin: false,
                CancellationToken.None));

        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);
        Assert.Equal(sentinel, q.ItemIds);              // un-shuffled, un-replaced
        Assert.Equal("Default", q.PlaybackOrder);
        Assert.Null(q.OriginalItemIds);                 // no shuffle state either

        Assert.Null(session.FullNowPlayingItem);
        Assert.Empty(session.NowPlayingQueue);
        Assert.Null(queueManager.GetLastPlayedItemId(deviceId));
        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    // ------------------------------------------------------------------
    // Successful shuffle: the commit stores the order the launch built from
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlaylistShuffle_SuccessfulLaunch_CommitsTheSnapshotTheLaunchBuiltFrom()
    {
        using var statics = StubBaseItemStatics();
        var (playlist, tracks) = SetupPlaylist();
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-shuffle-success");
        string deviceId = context.System.Device.DeviceID!;
        List<string> orderedIds = tracks.Select(t => t.Id.ToString()).ToList();

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-shuffle-success");
        TestHelpers.EnsurePluginInstance(config, _loggerFactory, c => { }, "playlist-shuffle-success");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        SkillResponse response = await svc.BuildPlaylistPlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "road trip songs",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            shuffle: true,
            new Random(42),
            kanaOrigin: false,
            CancellationToken.None);

        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);

        // The snapshot agreement: the directive the device received names the FIRST
        // item of the committed order (a re-derive at commit would re-shuffle and
        // disagree; rng 42 over 5 ids makes the mismatch all but certain).
        Guid launched = Guid.Parse(q.ItemIds[0]);
        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"/Audio/{launched}/stream", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(launched, session.FullNowPlayingItem!.Id);

        // Shuffle state + original-order snapshot, exactly as the atomic shape stored.
        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.Equal(0, q.CurrentIndex);
        Assert.Equal(orderedIds, q.OriginalItemIds);
        Assert.Equal(new HashSet<string>(orderedIds), new HashSet<string>(q.ItemIds));

        // The session queue mirrors the COMMITTED shuffled order (metadata kept).
        Assert.Equal(launched, session.NowPlayingQueue[0].Id);
        Assert.Equal(orderedIds.Count, session.NowPlayingQueue.Count);
        Assert.All(session.NowPlayingQueue, qi => Assert.Equal(playlist.Id.ToString(), qi.PlaylistItemId));

        // The builder's last-played ledger (written pre-commit on the swapped
        // manager) survives the queue replacement via CopySurvivingStores (JF-693).
        Assert.Equal(launched.ToString(), queueManager.GetLastPlayedItemId(deviceId));
    }

    /// <summary>
    /// The shared BaseItem statics stub scope (TestHelpers.StubBaseItemStatics,
    /// hoisted there on this file's third-copy construction, JF-713).
    /// </summary>
    private IDisposable StubBaseItemStatics()
        => TestHelpers.StubBaseItemStatics(_libraryManagerMock);
}
