#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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
/// JF-974 pins for <see cref="AlbumPlayService.BuildAlbumShufflePlayResponseAsync"/>:
/// the album sibling of the playlist shuffle arm carries the SAME derive-commit tail
/// invariants the JF-713/JF-927 playlist suite pins (snapshot launch, commit of the
/// SAME snapshot after the build, full-list deduped derive, unconditional continuation
/// clear, never a seeded continuation) plus this arm's OWN hard constraint: the launch
/// passes no collectionParentId/collectionStartTicks, so even in seek mode the album
/// plays per-track (the single-item video-audio URL keyed by the TRACK id), never the
/// JF-625 source-order album concat. DRIVE-THE-SERVICE, the JF-713 evidence shape:
/// the handler shell adds nothing to the write ordering under test (the handler
/// branch routing is pinned in ShufflePlayIntentHandlerTests). The builder seam is the
/// REAL refusal (seek mode with an empty StreamTokenSecret makes the launch throw
/// <see cref="StreamTokenNotConfiguredException"/> from the token-delivery guard, the
/// same shape the playlist shuffle suite pins).
/// </summary>
[Collection("Plugin")]
public class AlbumPlayServiceAlbumShuffleTests : PluginTestBase
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

    private static PluginConfiguration SeekModeWorkingConfig()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        config.NativeControlsForAudio = true;
        config.StreamTokenSecret = "unit-test-secret";
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
        ILogger logger = _loggerFactory.CreateLogger<AlbumPlayServiceAlbumShuffleTests>();
        var launch = new PlaybackLaunchBuilder(config, logger, (_, _, _) => Task.FromResult(false));
        var search = new SearchService(config, logger, requestTimeoutMs: 6000);
        var crossMedia = new CrossMediaFallback(config, logger, launch, requestTimeoutMs: 6000);
        return new AlbumPlayService(
            config, logger, launch, search, crossMedia, requestTimeoutMs: 6000,
            TestHelpers.FuzzyMissNotFound);
    }

    /// <summary>
    /// The shared album fixture (hoisted to TestHelpers in the JF-974 simplify
    /// round): the duplicate-row leg pins the JF-927 dedupe; the fixture's own
    /// contract lives on the TestHelpers helper.
    /// </summary>
    private (MusicAlbum Album, List<Audio> Tracks) SetupAlbum(string name = "thriller", int trackCount = 5, bool duplicateFirstTrack = false)
        => TestHelpers.SetupAlbum(_libraryManagerMock, _userManagerMock, name, trackCount, duplicateFirstTrack);

    /// <summary>THE PRE-SEED PIN (JF-974 code review round): the
    /// session-queue pre-seed before MirrorQueueToSession is LOAD-BEARING.
    /// The mirror's preserve loop appends every item already in
    /// session.NowPlayingQueue that is not in the new device queue ("the
    /// playable queue never shrinks"), so the pre-seed must REPLACE the stale
    /// queue with the album's own tracks; an earlier simplify pass removed it
    /// as redundant and the previous play session's tracks would have
    /// survived behind the shuffled album.</summary>
    [Fact]
    public async Task AlbumShuffle_PreSeed_ReplacesTheStaleSessionQueue_NotAppendsToIt()
    {
        using var statics = StubBaseItemStatics();
        var (_, tracks) = SetupAlbum();
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("album-shuffle-preseed");
        using var queueManager = TestHelpers.CreateDeviceQueueManager("album-shuffle-preseed");
        TestHelpers.EnsurePluginInstance(config, _loggerFactory, c => { }, "album-shuffle-preseed");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        // The stale prior play: a track from ANOTHER album still sitting in the
        // session queue when the shuffle launch runs.
        var stale = new Audio { Id = Guid.NewGuid(), Name = "stale prior track", Tags = Array.Empty<string>() };
        session.NowPlayingQueue = new List<MediaBrowser.Model.Session.QueueItem> { new() { Id = stale.Id } };

        await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "thriller",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            new Random(7),
            kanaOrigin: false,
            CancellationToken.None);

        // The mirrored session queue is EXACTLY the shuffled album: the stale
        // track must not survive behind it.
        var albumIds = tracks.Select(t => t.Id).ToHashSet();
        Assert.Equal(albumIds.Count, session.NowPlayingQueue.Count);
        Assert.All(session.NowPlayingQueue, item => Assert.Contains(item.Id, albumIds));
    }

    // ------------------------------------------------------------------
    // Successful shuffle: the commit stores the order the launch built from
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_SuccessfulLaunch_CommitsTheSnapshotTheLaunchBuiltFrom()
    {
        using var statics = StubBaseItemStatics();
        var (_, tracks) = SetupAlbum();
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("album-shuffle-success");
        string deviceId = context.System.Device.DeviceID!;
        List<string> orderedIds = tracks.Select(t => t.Id.ToString()).ToList();

        using var queueManager = TestHelpers.CreateDeviceQueueManager("album-shuffle-success");
        TestHelpers.EnsurePluginInstance(config, _loggerFactory, c => { }, "album-shuffle-success");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        SkillResponse response = await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "thriller",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
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

        // The session queue mirrors the COMMITTED shuffled order.
        Assert.Equal(launched, session.NowPlayingQueue[0].Id);
        Assert.Equal(orderedIds.Count, session.NowPlayingQueue.Count);

        // The builder's last-played ledger (written pre-commit on the swapped
        // manager) survives the queue replacement via CopySurvivingStores (JF-693).
        Assert.Equal(launched.ToString(), queueManager.GetLastPlayedItemId(deviceId));

        // The committed shuffled queue is the FULL play order: no continuation.
        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    // ------------------------------------------------------------------
    // Duplicate rows: the derive dedupes first occurrence (the queue advance
    // resolves by first-match index; a duplicate id would cycle forever)
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_DuplicateTrackRows_CommitsEachTrackOnce()
    {
        using var statics = StubBaseItemStatics();
        // The same track listed twice in the track rows (a duplicated library row):
        // a duplicate id in the committed queue would make the first-match queue
        // advance resolve the second occurrence to the first's position and cycle
        // the inter-duplicate segment forever (the JF-927 review finding 1 shape).
        var (_, tracks) = SetupAlbum(trackCount: 6, duplicateFirstTrack: true);
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("album-shuffle-dupe");
        string deviceId = context.System.Device.DeviceID!;
        List<string> distinctIds = tracks.Select(t => t.Id.ToString()).ToList();

        using var queueManager = TestHelpers.CreateDeviceQueueManager("album-shuffle-dupe");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "album-shuffle-dupe");

        await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "thriller",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            new Random(42),
            kanaOrigin: false,
            CancellationToken.None);

        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);

        // 7 rows, 6 distinct tracks: the committed shuffle (and its original-order
        // snapshot) carry each id exactly once.
        Assert.Equal(6, q.ItemIds.Count);
        Assert.Equal(distinctIds.Count, q.ItemIds.Distinct().Count());
        Assert.Equal(new HashSet<string>(distinctIds), new HashSet<string>(q.ItemIds));
        Assert.All(q.OriginalItemIds, id => Assert.Single(q.OriginalItemIds, o => o == id));
    }

    // ------------------------------------------------------------------
    // Continuation lifecycle: cleared unconditionally, never seeded
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_ClearsStaleContinuationFromEarlierPlay()
    {
        using var statics = StubBaseItemStatics();
        SetupAlbum();
        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("album-shuffle-stale-cont");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("album-shuffle-stale-cont");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "album-shuffle-stale-cont");

        // A stale continuation left by an earlier play on the same device: the
        // shuffle launch replaces the queue wholesale, so the old entry's
        // source-order tail must not survive to be appended after the shuffled
        // queue.
        QueueContinuationStore.Set(session.UserId, deviceId, new QueueContinuation
        {
            SourceType = "Album",
            StartIndex = 3,
            TotalCount = 5,
        });

        await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "thriller",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            new Random(42),
            kanaOrigin: false,
            CancellationToken.None);

        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    // ------------------------------------------------------------------
    // Refused shuffle start: the device queue stays untouched
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_SeekModeEmptySecret_Refuses_DeviceQueueUntouched()
    {
        using var statics = StubBaseItemStatics();
        SetupAlbum();
        var config = SeekModeBrokenConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("album-shuffle-refused");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("album-shuffle-refused");
        // A PRE-EXISTING ordered queue: the strongest form of "untouched" (the
        // JF-713 playlist-suite twin's sentinel shape).
        List<string> sentinel = new() { "sentinel-1", "sentinel-2", "sentinel-3" };
        queueManager.SetQueue(deviceId, sentinel, 0);

        TestHelpers.EnsurePluginInstance(config, _loggerFactory, c => { }, "album-shuffle-refused");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => svc.BuildAlbumShufflePlayResponseAsync(
                _libraryManagerMock.Object,
                _userManagerMock.Object,
                queueManager,
                "thriller",
                context,
                TestHelpers.CreateTestUser(jellyfinToken: "tok"),
                session,
                "en-US",
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
    // THE JF-974 hard constraint: per-track launch, never the album concat
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_SeekMode_LaunchesPerTrack_NeverTheAlbumConcat()
    {
        using var statics = StubBaseItemStatics();
        var (album, tracks) = SetupAlbum();
        var config = SeekModeWorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateContextWithVideoApp("album-shuffle-seek");
        string deviceId = context.System.Device.DeviceID!;

        using var queueManager = TestHelpers.CreateDeviceQueueManager("album-shuffle-seek");
        using var queueScope = TestHelpers.SwapPluginLedgerScope(config, _loggerFactory, queueManager, "album-shuffle-seek");

        SkillResponse response = await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager,
            "thriller",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            new Random(42),
            kanaOrigin: false,
            CancellationToken.None);

        DeviceQueue q = queueManager.GetQueue(deviceId)!;
        Assert.NotNull(q);
        Assert.Equal("Shuffle", q.PlaybackOrder);

        // The launch took the native-controls VideoApp route (seek mode) but the
        // SINGLE-ITEM stream keyed by the launched TRACK: no collectionParentId was
        // passed, so the JF-625 source-order album concat (the /audiobook/-keyed
        // endpoint the ordered album flow launches in seek mode) is never minted for
        // a shuffled album.
        Guid launched = Guid.Parse(q.ItemIds[0]);
        var launch = Assert.IsType<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(Assert.Single(response.Response.Directives!));
        Assert.Contains($"/video-audio/{launched}/stream.m3u8", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("/audiobook/", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain(album.Id.ToString(), launch.VideoItem.Source, StringComparison.Ordinal);

        // The tail still ran on the seek route: the session mirrors the committed
        // shuffled order.
        Assert.Equal(launched, session.NowPlayingQueue[0].Id);
        Assert.Equal(tracks.Count, session.NowPlayingQueue.Count);
    }

    // ------------------------------------------------------------------
    // Multi-match: the playlist arm's disambiguation machinery on the album
    // media type
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_MultiMatch_NoFullCoverage_AsksFirstMatchOnAlbumType()
    {
        using var statics = StubBaseItemStatics();
        // Two albums whose names both contain the query's core word, with the
        // 2-token query carrying a token NEITHER name covers ("takes"), so the
        // JF-526 short-query pre-check declines and the HandleFuzzyMiss NotFound
        // outcome (the injected delegate) falls into AskFirstMatch. The query
        // stays at 2 tokens: HasFullKeywordCoverage only judges queries within
        // ShortQueryFullCoverageMaxTokens (longer ones auto-pass).
        var first = new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid(), Tags = Array.Empty<string>() };
        var second = new MusicAlbum { Name = "Abbey Road Sessions", Id = Guid.NewGuid(), Tags = Array.Empty<string>() };
        var track = new Audio { Id = Guid.NewGuid(), Name = "track", ParentId = first.Id, Tags = Array.Empty<string>() };
        var byId = new Dictionary<Guid, BaseItem> { [first.Id] = first, [second.Id] = second, [track.Id] = track };
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => byId.TryGetValue(id, out BaseItem? item) ? item : null!);
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes != null && q.IncludeItemTypes.Length > 0 && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { first, second });
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes != null && q.IncludeItemTypes.Length > 0 && q.IncludeItemTypes.Contains(BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { track });
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());

        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        var context = TestHelpers.CreateTestContext("album-shuffle-multimatch");

        SkillResponse response = await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager: null,
            "road takes",
            context,
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            rng: null,
            kanaOrigin: false,
            CancellationToken.None);

        // The yes/no first-match ASK on the ALBUM media type (the PlayAlbum
        // disambiguation contract), never a play and never a committed queue.
        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.NotEqual(true, response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.Equal(DisambiguationHelper.MediaTypeAlbum, response.SessionAttributes!["disambig_type"]!.ToString());
        Assert.Empty(session.NowPlayingQueue);
    }

    // ------------------------------------------------------------------
    // Miss and empty-album tells
    // ------------------------------------------------------------------

    [Fact]
    public async Task AlbumShuffle_AlbumNotFound_SpeaksTheAlbumNotFoundTell()
    {
        using var statics = StubBaseItemStatics();
        // Every GetItemList shape answers empty: the exact tier, the fuzzy
        // fallback's scan, and the track queries all miss.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());

        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager: null,
            "xyzzyfoo",
            TestHelpers.CreateTestContext("album-shuffle-notfound"),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            rng: null,
            kanaOrigin: false,
            CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains(
            ResponseStrings.Get("NotFoundAlbumByName", "en-US", "xyzzyfoo"),
            TestHelpers.GetSpeechText(response),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public async Task AlbumShuffle_NoTracks_SpeaksTheNoSongsTell()
    {
        using var statics = StubBaseItemStatics();
        var (album, _) = SetupAlbum();
        // The album resolves, but neither track arm returns rows (the malformed
        // album whose JF-338 retry also misses).
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes != null && q.IncludeItemTypes.Length > 0 && q.IncludeItemTypes.Contains(BaseItemKind.Audio))))
            .Returns(new List<BaseItem>());

        var config = WorkingConfig();
        AlbumPlayService svc = CreateService(config);

        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await svc.BuildAlbumShufflePlayResponseAsync(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            queueManager: null,
            "thriller",
            TestHelpers.CreateTestContext("album-shuffle-nosongs"),
            TestHelpers.CreateTestUser(jellyfinToken: "tok"),
            session,
            "en-US",
            rng: null,
            kanaOrigin: false,
            CancellationToken.None);

        TestHelpers.AssertNoAudioPlayDirective(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains(
            ResponseStrings.Get("NoSongsInAlbum", "en-US", album.Name),
            TestHelpers.GetSpeechText(response),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shared BaseItem statics stub scope (TestHelpers.StubBaseItemStatics).
    /// </summary>
    private IDisposable StubBaseItemStatics()
        => TestHelpers.StubBaseItemStatics(_libraryManagerMock);
}
