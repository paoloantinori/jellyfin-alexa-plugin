using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-581: the stop handler's UserData fill must self-verify after the session
/// report and write the position directly when it did not land, resolving the
/// Jellyfin user from the SESSION id. The pre-JF-581 code resolved through the
/// plugin identity id (user.Id, the LWA token GUID), which is not a Jellyfin user
/// id, so the cross-client fill silently never ran in production.
/// JF-812: the same save block's position write stamps the stopped item's KIND
/// beside the position (DeviceQueue.ItemPositionKinds), so the JF-797 deep-resume
/// valve on the book head can skip song-shaped entries; those pins live here too
/// (same block, same fixture).
/// </summary>
[Collection("Plugin")]
public class StoppedSaveSelfVerificationTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly Guid _jellyfinUserId = Guid.NewGuid();
    private readonly Jellyfin.Database.Implementations.Entities.User _jellyfinUser;
    private readonly Entities.User _pluginUser;

    public StoppedSaveSelfVerificationTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("stopped-save-self-verification", _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());
        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "stopped-save-self-verification");

        _jellyfinUser = TestHelpers.CreateJellyfinUser();
        _pluginUser = TestHelpers.CreateTestUser();

        // Resolve ONLY the session-resolved Jellyfin id: a lookup through the plugin
        // identity id must fail, mirroring the production user store.
        _fx.UserManager.Setup(u => u.GetUserById(_jellyfinUserId)).Returns(_jellyfinUser);
        _fx.UserManager.Setup(u => u.GetUserById(It.Is<Guid>(g => g != _jellyfinUserId)))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _jellyfinUserId;
        return session;
    }

    private PlaybackStoppedEventHandler CreateHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LoggerFactory,
            _queueManager,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object);

    /// <summary>
    /// The incident shape: the report path (ISessionManager no-op here, a black hole
    /// on the live 12.1 box) leaves UserData at 0, so the handler writes the real
    /// stop position directly via SaveUserData, with the session-resolved user.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_SessionReportLost_WritesUserDataDirectlyWithSessionResolvedUser()
    {
        var id = Guid.NewGuid();
        long realTicks = 3641180000;
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(new Audio { Name = "Morning", Id = id });
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 });
        UserItemData? saved = null;
        _fx.UserDataManager
            .Setup(u => u.SaveUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()))
            .Callback((Jellyfin.Database.Implementations.Entities.User user, BaseItem _, UserItemData data, UserDataSaveReason _, CancellationToken _) => saved = data);

        await CreateHandler().HandleAsync(
            new AudioPlayerRequest { Type = "AudioPlayer.PlaybackStopped", Token = id.ToString(), OffsetInMilliseconds = 364118 },
            TestHelpers.CreateTestContext(DeviceId),
            _pluginUser,
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(realTicks, saved!.PlaybackPositionTicks);
        _fx.UserManager.Verify(u => u.GetUserById(_jellyfinUserId), Times.Once);
        _fx.UserManager.Verify(u => u.GetUserById(_pluginUser.Id), Times.Never);
    }

    /// <summary>
    /// Review finding pin (stale-nonzero): on a write-loss server the first stop
    /// direct-writes X; the NEXT stop at Y must overwrite X, not read the non-zero
    /// X (our own earlier write) and skip, which would pin UserData forever.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_StaleNonzeroFromEarlierDirectWrite_OverwritesWithThisStopPosition()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(new Audio { Name = "Morning", Id = id });
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 600_000_000 }); // 60s: an earlier listen's write
        UserItemData? saved = null;
        _fx.UserDataManager
            .Setup(u => u.SaveUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()))
            .Callback((Jellyfin.Database.Implementations.Entities.User user, BaseItem _, UserItemData data, UserDataSaveReason _, CancellationToken _) => saved = data);

        await CreateHandler().HandleAsync(
            new AudioPlayerRequest { Type = "AudioPlayer.PlaybackStopped", Token = id.ToString(), OffsetInMilliseconds = 364118 },
            TestHelpers.CreateTestContext(DeviceId),
            _pluginUser,
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(3641180000, saved!.PlaybackPositionTicks);
    }

    /// <summary>
    /// Control pin: when the report path DID persist a nonzero position, the
    /// handler must not overwrite it.
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_UserDataAlreadyPersisted_DoesNotOverwrite()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(new Audio { Name = "Morning", Id = id });
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 3641180000 });

        await CreateHandler().HandleAsync(
            new AudioPlayerRequest { Type = "AudioPlayer.PlaybackStopped", Token = id.ToString(), OffsetInMilliseconds = 364118 },
            TestHelpers.CreateTestContext(DeviceId),
            _pluginUser,
            CreateSession(),
            CancellationToken.None);

        _fx.UserDataManager.Verify(
            u => u.SaveUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================
    // Position kind stamps (JF-812)
    // =====================================================================

    /// <summary>
    /// JF-812: an AudioBook stopped item (the chapter-leaf or single-file book
    /// shape) stamps Book beside its stored position, so the deep-resume valve
    /// releases. The stamp comes from the ONE item resolution the save block
    /// makes (GetItemById, classified through AudiobookItems.IsAudioBookOrChapter).
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_AudioBookStoppedItem_StampsBookKind()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id))
            .Returns(new AudioBook { Name = "The Hobbit", Id = id });

        await CreateHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackStopped", id.ToString(), 364_118),
            TestHelpers.CreateTestContext(DeviceId),
            _pluginUser,
            CreateSession(),
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(DeviceQueueManager.PositionKindBook, queue.ItemPositionKinds[id.ToString("N")]);
        Assert.True(_queueManager.HasAnyBookShapedStoredPosition(DeviceId));
    }

    /// <summary>
    /// JF-812 tightening: a plain resolved song stamps Other, so the valve skips
    /// it (household song playback no longer permanently releases the book
    /// head's deep-resume fetch).
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_PlainSongStoppedItem_StampsOtherKind()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id))
            .Returns(new Audio { Name = "Morning", Id = id });

        await CreateHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackStopped", id.ToString(), 364_118),
            TestHelpers.CreateTestContext(DeviceId),
            _pluginUser,
            CreateSession(),
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(DeviceQueueManager.PositionKindOther, queue.ItemPositionKinds[id.ToString("N")]);
        Assert.False(_queueManager.HasAnyBookShapedStoredPosition(DeviceId));
    }

    /// <summary>
    /// JF-812 conservative default: an item that does not resolve (no
    /// LibraryManager row for the stopped id) still writes the position but
    /// stores it KINDLESS; the entry keeps the valve's conservative release,
    /// never a song-shaped skip the failed lookup could not justify
    /// (IsAudioBookOrChapter answers false for null, which alone would have
    /// stamped it Other).
    /// </summary>
    [Fact]
    public async Task PlaybackStopped_UnresolvableStoppedItem_StoresKindless()
    {
        var id = Guid.NewGuid();
        // No GetItemById setup: the mock answers null (the unresolved-row shape).

        await CreateHandler().HandleAsync(
            TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackStopped", id.ToString(), 364_118),
            TestHelpers.CreateTestContext(DeviceId),
            _pluginUser,
            CreateSession(),
            CancellationToken.None);

        DeviceQueue queue = _queueManager.GetOrCreateQueue(DeviceId);
        string key = id.ToString("N");
        Assert.Equal(364_118L * TimeSpan.TicksPerMillisecond, queue.ItemPositionState[key]);
        Assert.DoesNotContain(key, queue.ItemPositionKinds.Keys);
        Assert.True(_queueManager.HasAnyBookShapedStoredPosition(DeviceId));
    }
}
