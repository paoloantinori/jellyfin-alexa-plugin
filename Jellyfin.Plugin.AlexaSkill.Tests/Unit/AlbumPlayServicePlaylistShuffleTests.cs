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
    /// The ONE resolvable-playlist fixture (TestHelpers.SetupPlaylist, the JF-808
    /// hoist of this class's former private copy: one exact server hit, LinkedChildren
    /// resolving through the stubbed BaseItem statics, the library mock answering ids).
    /// </summary>
    private (Playlist Playlist, List<Audio> Tracks) SetupPlaylist(string name = "road trip songs", int trackCount = 5)
        => TestHelpers.SetupPlaylist(_libraryManagerMock, _userManagerMock, name, trackCount);

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

    // ------------------------------------------------------------------
    // JF-927 (issue #44): the shuffle scope is the WHOLE playlist, not the
    // progressive window
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlaylistShuffle_BigPlaylist_ShufflesTheWholeListNotTheWindow()
    {
        using var statics = StubBaseItemStatics();
        var (_, tracks) = SetupPlaylist(trackCount: 12);
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-shuffle-biglist");
        string deviceId = context.System.Device.DeviceID!;
        List<string> orderedIds = tracks.Select(t => t.Id.ToString()).ToList();

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-shuffle-biglist");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "playlist-shuffle-biglist");

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

        // THE JF-927 bar: a 12-track playlist (window 5) commits a 12-entry
        // shuffled queue. Pre-fix this read 5: the derive call reused the
        // progressive window's idList, so only the first five songs ever
        // shuffled and the continuation appended tracks 6..12 in source order.
        Assert.Equal(12, q.ItemIds.Count);
        Assert.Equal(orderedIds, q.OriginalItemIds);
        Assert.Equal(new HashSet<string>(orderedIds), new HashSet<string>(q.ItemIds));
        Assert.Equal("Shuffle", q.PlaybackOrder);

        // The order must actually BE shuffled: a degenerate identity shuffle
        // (Fisher-Yates broken to a no-op) would pass every assertion above.
        // Seed 42 over 12 distinct ids is non-identity with overwhelming
        // probability, so this pin is deterministic in practice.
        Assert.NotEqual(orderedIds, q.ItemIds);

        // The launch still builds from the snapshot's first item.
        Guid launched = Guid.Parse(q.ItemIds[0]);
        AudioPlayerPlayDirective? directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"/Audio/{launched}/stream", directive.AudioItem.Stream.Url, StringComparison.Ordinal);

        // The session mirror carries the FULL shuffled order.
        Assert.Equal(12, session.NowPlayingQueue.Count);
        Assert.Equal(launched, session.NowPlayingQueue[0].Id);

        // The full queue is already complete: no continuation may be seeded for
        // it (a seeded entry would append the source order's tail after the
        // shuffled queue on PlaybackNearlyFinished).
        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    [Fact]
    public async Task PlaylistShuffle_BigPlaylist_ClearsStaleContinuationFromEarlierOrderedPlay()
    {
        using var statics = StubBaseItemStatics();
        SetupPlaylist(trackCount: 12);
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-shuffle-stale-cont");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-shuffle-stale-cont");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "playlist-shuffle-stale-cont");

        // A stale continuation left by an earlier ORDERED play of the same
        // playlist on the same device: the shuffle launch replaces the queue
        // wholesale, so the old entry's source-order tail must not survive to
        // be appended after the shuffled queue.
        QueueContinuationStore.Set(session.UserId, deviceId, new QueueContinuation
        {
            SourceType = "Playlist",
            StartIndex = 5,
            TotalCount = 12,
        });

        await svc.BuildPlaylistPlayResponseAsync(
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

        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    [Fact]
    public async Task PlaylistOrdered_BigPlaylist_StillWindowsTheQueueAndSeedsContinuation()
    {
        using var statics = StubBaseItemStatics();
        SetupPlaylist(trackCount: 12);
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-ordered-biglist");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-ordered-biglist");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "playlist-ordered-biglist");

        await svc.BuildPlaylistPlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "road trip songs",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            shuffle: false,
            new Random(42),
            kanaOrigin: false,
            CancellationToken.None);

        // The ordered arm is UNCHANGED by JF-927: progressive window in the
        // device queue, continuation seeded from the window edge.
        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);
        Assert.Equal(5, q.ItemIds.Count);
        Assert.Equal("Default", q.PlaybackOrder);
        QueueContinuation? cont = QueueContinuationStore.Get(session.UserId, deviceId);
        Assert.NotNull(cont);
        Assert.Equal(5, cont.StartIndex);
        Assert.Equal(12, cont.TotalCount);
    }

    [Fact]
    public async Task PlaylistShuffle_DuplicateTrackInPlaylist_CommitsEachTrackOnce()
    {
        using var statics = StubBaseItemStatics();
        var (playlist, tracks) = SetupPlaylist(trackCount: 6);
        // The same track listed twice (PlaylistTrackResolver preserves
        // duplicates verbatim): a duplicate id in the committed queue would
        // make the first-match queue advance resolve the second occurrence to
        // the first's position and cycle the inter-duplicate segment forever
        // (review finding 1).
        playlist.LinkedChildren = tracks
            .Select(t => new LinkedChild { ItemId = t.Id })
            .Append(new LinkedChild { ItemId = tracks[0].Id })
            .ToArray();
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-shuffle-dupe");
        string deviceId = context.System.Device.DeviceID!;
        List<string> distinctIds = tracks.Select(t => t.Id.ToString()).ToList();

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-shuffle-dupe");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "playlist-shuffle-dupe");

        await svc.BuildPlaylistPlayResponseAsync(
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

        // 7 linked children, 6 distinct tracks: the committed shuffle (and its
        // original-order snapshot) carry each id exactly once.
        Assert.Equal(6, q.ItemIds.Count);
        Assert.Equal(distinctIds.Count, q.ItemIds.Distinct().Count());
        Assert.Equal(new HashSet<string>(distinctIds), new HashSet<string>(q.ItemIds));
        Assert.All(q.OriginalItemIds, id => Assert.Single(q.OriginalItemIds, o => o == id));
    }

    [Fact]
    public async Task PlaylistOrdered_SmallPlaylist_ReplacingTheQueueDropsStaleContinuation()
    {
        using var statics = StubBaseItemStatics();
        SetupPlaylist(trackCount: 3);
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("playlist-ordered-small-stale");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("playlist-ordered-small-stale");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "playlist-ordered-small-stale");

        // A stale continuation from an EARLIER play of a bigger source: the
        // small ordered launch replaces the device queue wholesale, so the old
        // entry must not survive (the JF-674 gate is subset-shaped and cannot
        // be relied on when the new queue happens to contain the minted ids;
        // review finding 2).
        QueueContinuationStore.Set(session.UserId, deviceId, new QueueContinuation
        {
            SourceType = "Playlist",
            StartIndex = 5,
            TotalCount = 12,
        });

        await svc.BuildPlaylistPlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "road trip songs",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            shuffle: false,
            new Random(42),
            kanaOrigin: false,
            CancellationToken.None);

        // 3 tracks fit one window (no InitialPageHasMore): nothing re-seeds,
        // and the stale entry is gone rather than left to fire.
        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    /// <summary>
    /// The shared BaseItem statics stub scope (TestHelpers.StubBaseItemStatics,
    /// hoisted there on this file's third-copy construction, JF-713).
    /// </summary>
    private IDisposable StubBaseItemStatics()
        => TestHelpers.StubBaseItemStatics(_libraryManagerMock);
}
