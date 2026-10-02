#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-699 item 5 pins: the modal MUSIC play paths refuse cleanly in seek mode with
/// an empty <see cref="PluginConfiguration.StreamTokenSecret"/>. The static
/// GetStreamUrl launch delegates to the token-gated video-audio URL inside the
/// BuildAudioPlayerResponse chokepoint when NativeControlsForAudio is on and the
/// device is VideoApp-capable (the default test context has no
/// SupportedInterfaces, which DeviceSupportsVideoApp reads as capable - its
/// documented fail-open), so the JF-699 item 1 refusal fires from the builder as
/// the typed exception, and the reordered paths (launch build FIRST, then the
/// session/SetQueue/QueueContinuation/RadioModeState writes) leave NO phantom
/// state behind: no now-playing MediaInfo can answer, no stale continuation
/// survives, radio mode is not armed. The reorder is safe on the success path
/// only because DeviceQueueManager.CopySurvivingStores carries the last-played
/// record the builder writes (the JF-693 finding 1 fix, pinned in
/// PlayBookIntentHandlerTests.HandleAsync_TrackedResume_LaunchLedgerSurvivesTheQueueReset).
/// RED PROOFS: reverting a reorder (moving a write back above the builder call)
/// flips that path's pin to a written-state assertion failure.
/// The PLAYLIST path's twin reorder is not pinned end-to-end:
/// <c>Playlist.GetManageableItems()</c> is non-virtual and DB-coupled (the same
/// limitation DeviceQueueManagerTests documents), so the playlist fixture cannot
/// reach the launch; the reorder there is the same mechanical pattern.
/// </summary>
[Collection("Plugin")]
public class MusicPathLaunchRefusalTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    private PluginConfiguration SeekModeBrokenConfig()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        config.NativeControlsForAudio = true;
        config.StreamTokenSecret = string.Empty;
        return config;
    }

    /// <summary>The two service-level tests share one construction (the refusal
    /// throws before any progressive send, so the delegate flavor is immaterial).</summary>
    private CrossMediaFallback CreateCrossMedia(PluginConfiguration config)
        => new(
            config,
            _loggerFactory.CreateLogger<MusicPathLaunchRefusalTests>(),
            TestHelpers.CreateLaunchBuilder(config),
            requestTimeoutMs: 6000);

    // ------------------------------------------------------------------
    // CrossMediaFallback.BuildArtistSongsResponseAsync (the artist path; also
    // the shape the cross-media cascade serves)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ArtistSongs_SeekModeEmptySecret_Refuses_NoPhantomState()
    {
        var config = SeekModeBrokenConfig();
        var svc = CreateCrossMedia(config);

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Waltz for Koop", Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser(jellyfinToken: "tok");
        var queueManager = TestHelpers.CreateDeviceQueueManager("music-refusal-artist");
        TestHelpers.EnsurePluginInstance(config, _fx.LoggerFactory, c => { }, "music-refusal-artist");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => svc.BuildArtistSongsResponseAsync(
                artist.Id, artist.Name!, TestHelpers.CreateJellyfinUser(), user, session, context, "en-US",
                library.Object, _fx.UserDataManager.Object, queueManager, "TestArtistPlay"));

        Assert.Null(session.FullNowPlayingItem);
        Assert.Empty(session.NowPlayingQueue);

        // The queue may exist as the EMPTY entry the resume-index read synthesizes
        // (ResumeMath.FindResumeTrackIndex's GetOrCreateQueue, a pre-existing read
        // path); what a refused launch must never leave is queue CONTENT (SetQueue
        // runs after the launch build since the reorder).
        Assert.Empty(queueManager.GetQueue(context.System.Device.DeviceID!)?.ItemIds ?? new List<string>());
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));

        // JF-699 code-review finding 3: the refusal must not flip the device
        // last-played ledger either (the Audio-route record moved AFTER the
        // native-controls delegation, whose callee records only past its own guard).
        Assert.Null(queueManager.GetLastPlayedItemId(context.System.Device.DeviceID!));
    }

    // ------------------------------------------------------------------
    // CrossMediaFallback.BuildSingleSongResponse (the JF-440 ONE single-song
    // shape, shared with PlaySongIntentHandler)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SingleSong_SeekModeEmptySecret_Refuses_NoPhantomState_SeedsSurvive()
    {
        var config = SeekModeBrokenConfig();
        var svc = CreateCrossMedia(config);

        var song = new Audio { Name = "Magnolia", Id = Guid.NewGuid() };
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        var context = TestHelpers.CreateTestContext();
        var user = TestHelpers.CreateTestUser(jellyfinToken: "tok");

        // A pre-existing artist continuation belongs to the queue that is REALLY
        // still current: the refused single-song play must not clear it (the Remove
        // moved after the launch build).
        QueueContinuationStore.Set(
            session.UserId, context.System.Device.DeviceID!,
            new QueueContinuation { SourceType = "Artist", StartIndex = 10, TotalCount = 20 });
        try
        {
            Assert.Throws<StreamTokenNotConfiguredException>(
                () => svc.BuildSingleSongResponse(song, user, session, context, "en-US"));

            Assert.Null(session.FullNowPlayingItem);
            Assert.Empty(session.NowPlayingQueue);
            Assert.NotNull(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));
        }
        finally
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID!);
        }
    }

    // ------------------------------------------------------------------
    // PlaySongIntentHandler (the inline single-match path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlaySong_SeekModeEmptySecret_Refuses_NoPhantomState()
    {
        _fx.Config.NativeControlsForAudio = true;
        _fx.Config.StreamTokenSecret = string.Empty;
        TestHelpers.SetServerAddress(_fx.Config, "https://test.example.com");

        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var song = new Audio { Name = "Magnolia", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>();
        intent.Slots["song"] = new Slot { Name = "song", Value = "magnolia" };
        var request = new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };

        var session = _fx.CreateSession();

        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None));

        Assert.Null(session.FullNowPlayingItem);
        Assert.Empty(session.NowPlayingQueue);
    }

    // ------------------------------------------------------------------
    // PlayAlbumIntentHandler (the AlbumPlayService album path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlayAlbum_SeekModeEmptySecret_Refuses_NoPhantomState()
    {
        _fx.Config.NativeControlsForAudio = true;
        _fx.Config.StreamTokenSecret = string.Empty;
        TestHelpers.SetServerAddress(_fx.Config, "https://test.example.com");

        var queueManager = TestHelpers.CreateDeviceQueueManager("music-refusal-album");
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "music-refusal-album");
        using var queueSwap = TestHelpers.SwapPluginQueueManager(queueManager);

        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            queueManager);

        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot>();
        intent.Slots["musician"] = new Slot { Name = "musician", Value = "koop" };
        var request = new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };

        var artist = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid(), ProductionYear = 1997 };
        var track = new Audio { Name = "Waltz First", Id = Guid.NewGuid(), Album = "Waltz for Koop", ParentId = album.Id };
        _fx.SetupUserMock();
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            new List<BaseItem> { track },
            new Dictionary<Guid, BaseItem> { [album.Id] = track });

        var session = _fx.CreateSession();
        var context = _fx.CreateContext();

        await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
            () => handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None));

        Assert.Null(session.FullNowPlayingItem);
        Assert.Empty(session.NowPlayingQueue);

        // See the artist-path pin: the empty synthesized entry is read-path noise;
        // the CONTENT is what a refused launch must never leave.
        Assert.Empty(queueManager.GetQueue(context.System.Device.DeviceID!)?.ItemIds ?? new List<string>());
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID!));

        // JF-699 code-review finding 3: see the artist-path pin.
        Assert.Null(queueManager.GetLastPlayedItemId(context.System.Device.DeviceID!));
    }

    // ------------------------------------------------------------------
    // PlayRadioIntentHandler.StartRadioPlayback (the context-seeded genre tier):
    // RadioModeState.Enable is the worst phantom of the family (an armed-but-dead
    // radio mode drives later PlaybackNearlyFinished continuation decisions).
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlayRadio_SeekModeEmptySecret_Refuses_RadioModeNotArmed()
    {
        // The fixture's mocks cover session/library/user; only the live-TV resolver
        // is radio-specific.
        var resolver = new Mock<ILiveTvStreamResolver>();
        var handler = new PlayRadioIntentHandler(
            _fx.SessionManager.Object, SeekModeBrokenConfig(), _fx.LibraryManager.Object, _fx.UserManager.Object, resolver.Object, _fx.LoggerFactory);

        var session = _fx.CreateSession();
        var currentAudio = new Audio { Id = Guid.NewGuid(), Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        session.FullNowPlayingItem = currentAudio;

        var context = TestHelpers.CreateTestContext();
        context.AudioPlayer = new PlaybackState
        {
            Token = currentAudio.Id.ToString(),
            OffsetInMilliseconds = 42_000,
            PlayerActivity = "PLAYING"
        };

        // The context-seeded radio start's shared mock pair (the RadioModeTests
        // StartsRadioMode family shape): any library query returns one similar
        // track, and the Jellyfin user resolves.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "Similar Rock Song" } });
        _fx.SetupUserMock();

        // A slot-less PlayRadio ask while something is playing: the context-seeded
        // path (no station resolution needed) reaches StartRadioPlayback directly.
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.PlayRadio },
            Locale = "en-US",
            RequestId = "test-req"
        };

        RadioModeState.Disable(session.UserId, context.System.Device.DeviceID!);
        try
        {
            await Assert.ThrowsAsync<StreamTokenNotConfiguredException>(
                () => handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None));

            Assert.False(
                RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID!),
                "a refused radio start must not arm radio mode");
            Assert.Empty(session.NowPlayingQueue);
        }
        finally
        {
            RadioModeState.Disable(session.UserId, context.System.Device.DeviceID!);
        }
    }
}
