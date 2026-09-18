using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-581: the device-last-played resume offer must fall back to the plugin's own
/// ItemPositionState when Jellyfin's UserData reads 0 (or the Jellyfin user does not
/// resolve), because the live 12.1 incident proved the server-side UserData writes
/// never land for Alexa sessions while the plugin-owned store is written
/// unconditionally by PlaybackStopped.
/// </summary>
[Collection("Plugin")]
public class ResumeSeedFallbackTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly DeviceQueueManager? _previousPluginQueueManager;

    public ResumeSeedFallbackTests()
    {
        _fx.Config.NativeControlsForAudio = true;
        _fx.Config.ResumeOfferEnabled = true;

        _queueManager = TestHelpers.CreateDeviceQueueManager("resume-seed-fallback", _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { c.NativeControlsForAudio = true; c.ResumeOfferEnabled = true; },
            "resume-seed-fallback");

        _previousPluginQueueManager = Jellyfin.Plugin.AlexaSkill.Plugin.Instance?.DeviceQueueManager;
        if (Jellyfin.Plugin.AlexaSkill.Plugin.Instance != null)
        {
            Jellyfin.Plugin.AlexaSkill.Plugin.Instance.DeviceQueueManager = _queueManager;
        }
    }

    public void Dispose()
    {
        if (Jellyfin.Plugin.AlexaSkill.Plugin.Instance != null)
        {
            Jellyfin.Plugin.AlexaSkill.Plugin.Instance.DeviceQueueManager = _previousPluginQueueManager;
        }

        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private LaunchRequestHandler CreateHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private static LaunchRequest LaunchRequest() => new() { Locale = "en-US" };

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = Guid.NewGuid();
        return session;
    }

    private void SeedDeviceLastPlayed(Guid itemId, long storedTicks)
    {
        _queueManager.RecordLastPlayed(DeviceId, itemId.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        _queueManager.GetOrCreateQueue(DeviceId).ItemPositionState[itemId.ToString("N")] = storedTicks;
    }

    private ResumeHelper.ResumeState ReadResumeState(SkillResponse response)
    {
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("resume_state"));
        ResumeHelper.ResumeState? state = ResumeHelper.ReadState(response.SessionAttributes);
        Assert.NotNull(state);
        return state!;
    }

    /// <summary>
    /// The live incident shape: UserData reads 0 while ItemPositionState holds the real
    /// stop position. The offer must seed offsetMs from the plugin-owned store.
    /// </summary>
    [Fact]
    public async Task LaunchRequest_DeviceLastPlayed_UserDataZero_SeedsOffsetFromItemPositionState()
    {
        var itemId = Guid.NewGuid();
        long storedTicks = 3641180000; // the incident's real stop position
        _fx.LibraryManager.Setup(l => l.GetItemById(itemId)).Returns(new Audio { Name = "Morning", Id = itemId });
        _fx.SetupUserMock();
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new MediaBrowser.Controller.Entities.UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 });
        SeedDeviceLastPlayed(itemId, storedTicks);

        SkillResponse response = await CreateHandler().HandleAsync(
            LaunchRequest(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Equal((int)TimeSpan.FromTicks(storedTicks).TotalMilliseconds, ReadResumeState(response).OffsetMs);
    }

    /// <summary>
    /// The resolve-failure variant: without a Jellyfin user the UserData read never
    /// happens, and the plugin-owned store must still seed the offer.
    /// </summary>
    [Fact]
    public async Task LaunchRequest_DeviceLastPlayed_UserDataResolveFails_SeedsOffsetFromItemPositionState()
    {
        var itemId = Guid.NewGuid();
        long storedTicks = 3641180000;
        _fx.LibraryManager.Setup(l => l.GetItemById(itemId)).Returns(new Audio { Name = "Morning", Id = itemId });
        _fx.UserManager.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Jellyfin.Database.Implementations.Entities.User?)null);
        SeedDeviceLastPlayed(itemId, storedTicks);

        SkillResponse response = await CreateHandler().HandleAsync(
            LaunchRequest(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Equal((int)TimeSpan.FromTicks(storedTicks).TotalMilliseconds, ReadResumeState(response).OffsetMs);
    }

    /// <summary>
    /// Review finding pin: a PLAYED item legitimately reads UserData 0 (completion
    /// resets it), while ItemPositionState still holds an older mid-listen position.
    /// The fallback must NOT resurrect that stale position over the finished listen.
    /// </summary>
    [Fact]
    public async Task LaunchRequest_DeviceLastPlayed_PlayedItem_DoesNotSeedFromItemPositionState()
    {
        var itemId = Guid.NewGuid();
        long staleTicks = TimeSpan.FromMinutes(60).Ticks;
        _fx.LibraryManager.Setup(l => l.GetItemById(itemId)).Returns(new Audio { Name = "Morning", Id = itemId });
        _fx.SetupUserMock();
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new MediaBrowser.Controller.Entities.UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 });
        SeedDeviceLastPlayed(itemId, staleTicks);

        SkillResponse response = await CreateHandler().HandleAsync(
            LaunchRequest(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Equal(0, ReadResumeState(response).OffsetMs);
    }

    /// <summary>
    /// Control pin: a healthy UserData position wins over the plugin store; the
    /// fallback must not overwrite it.
    /// </summary>
    [Fact]
    public async Task LaunchRequest_DeviceLastPlayed_UserDataHasTicks_KeepsUserDataPosition()
    {
        var itemId = Guid.NewGuid();
        long userDataTicks = TimeSpan.FromMinutes(10).Ticks;
        _fx.LibraryManager.Setup(l => l.GetItemById(itemId)).Returns(new Audio { Name = "Morning", Id = itemId });
        _fx.SetupUserMock();
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new MediaBrowser.Controller.Entities.UserItemData { Key = "test", Played = false, PlaybackPositionTicks = userDataTicks });
        SeedDeviceLastPlayed(itemId, TimeSpan.FromMinutes(99).Ticks);

        SkillResponse response = await CreateHandler().HandleAsync(
            LaunchRequest(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Equal((int)TimeSpan.FromTicks(userDataTicks).TotalMilliseconds, ReadResumeState(response).OffsetMs);
    }

    /// <summary>
    /// The screenless audio fallback reads UserData only, so with the server-side
    /// write loss it declines the offer entirely. With UserData flat zero it must
    /// seed from the device queue's ItemPositionState and offer the queued audio item.
    /// </summary>
    [Fact]
    public async Task LaunchRequest_ScreenlessVideoLastPlayed_UserDataLoss_OffersAudioFromItemPositionState()
    {
        var videoId = Guid.NewGuid();
        var audioId = Guid.NewGuid();
        long storedTicks = TimeSpan.FromMinutes(6).Ticks;
        _fx.LibraryManager.Setup(l => l.GetItemById(videoId)).Returns(new Episode { Name = "Show", Id = videoId });
        _fx.LibraryManager.Setup(l => l.GetItemById(audioId)).Returns(new Audio { Name = "Track", Id = audioId });
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new List<BaseItem>());
        _fx.SetupUserMock();
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new MediaBrowser.Controller.Entities.UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 });
        // SetQueue replaces the queue record, so it must run BEFORE RecordLastPlayed.
        _queueManager.SetQueue(DeviceId, new List<string> { audioId.ToString() }, 0);
        SeedDeviceLastPlayed(videoId, TimeSpan.FromMinutes(1).Ticks);
        _queueManager.GetOrCreateQueue(DeviceId).ItemPositionState[audioId.ToString("N")] = storedTicks;

        SkillResponse response = await CreateHandler().HandleAsync(
            LaunchRequest(),
            TestHelpers.CreateScreenlessContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        ResumeHelper.ResumeState state = ReadResumeState(response);
        Assert.Equal(audioId.ToString(), state.ItemId);
        Assert.Equal((int)TimeSpan.FromTicks(storedTicks).TotalMilliseconds, state.OffsetMs);
    }
}
