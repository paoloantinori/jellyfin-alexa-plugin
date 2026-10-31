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
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-918 funnel pin (upstream issue #43, the Sonos split): on hardware whose
/// voice endpoint and playback endpoint are two different Alexa deviceIds, the
/// album queue lives under the voice device while AudioPlayer events arrive
/// from the playback device. The funnel must canonicalize the event's device to
/// the stream's launch device BEFORE session resolution, so the event resolves
/// the launch device's Jellyfin session (the one holding the album queue) and
/// the NearlyFinished handler can enqueue the next track. Drives the real
/// funnel (BaseHandler.HandleRequestAsync), not HandleAsync directly: the
/// canonicalization lives in the funnel, exactly the JF-327 lesson (a handler-
/// level green cannot see a dead funnel seam).
/// </summary>
[Collection("Plugin")]
public class DeviceAliasFunnelTests : PluginTestBase, IDisposable
{
    private readonly DeviceQueueManager _manager;
    private readonly IDisposable _swap;

    public DeviceAliasFunnelTests()
    {
        // The funnel reads Plugin.Instance.Configuration.ServerAddress deep inside
        // the session lookup; a real Plugin instance is required.
        TestHelpers.EnsureRealPlugin();
        _manager = TestHelpers.CreateDeviceQueueManager("device-alias-test");
        _swap = TestHelpers.SwapPluginQueueManager(_manager);
    }

    public void Dispose()
    {
        // The swap scope owns the manager's disposal (restore-then-dispose).
        _swap.Dispose();
        GC.SuppressFinalize(this);
    }

    private (Entities.User User, PluginConfiguration Config) CreateUserWithConfig()
    {
        var user = new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = Guid.NewGuid(),
            JellyfinToken = "alias-token-" + Guid.NewGuid().ToString("N"),
        };
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        config.Users.Add(user);
        return (user, config);
    }

    private static Context CreateEventContext(string deviceId, string token, Guid userId)
        => new()
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId },
                User = new global::Alexa.NET.Request.User { AccessToken = userId.ToString() },
            },
            AudioPlayer = new PlaybackState { Token = token, OffsetInMilliseconds = 0 },
        };

    [Fact]
    public async Task HandleRequestAsync_SplitBrainEventDevice_ResolvesLaunchDevicesSession_AndEnqueuesNext_JF918()
    {
        var (user, config) = CreateUserWithConfig();
        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track1 = new Audio { Id = track1Id, Name = "Track 1" };
        var track2 = new Audio { Id = track2Id, Name = "Track 2" };

        // The launch device's session carries the album queue; the playback
        // device must NEVER be asked for a session (a session created under it
        // would be the empty-session bug this fix closes).
        var sessionManager = new Mock<ISessionManager>();
        var launchSession = TestHelpers.CreateTestSession(sessionManager.Object, LoggerFactory.Create(b => { }));
        launchSession.PlayState = new PlayerStateInfo();
        launchSession.FullNowPlayingItem = track1;
        launchSession.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
        };
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "voice-dev", It.IsAny<string?>()))
            .ReturnsAsync(launchSession);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemById(track2Id)).Returns(track2);
        libraryManager.Setup(l => l.GetItemById(track1Id)).Returns(track1);

        // The voice device's launch: the album queue plus the directive that
        // created track 1's stream (the ownership RecordLaunchBase writes).
        _manager.SetQueue("voice-dev", new List<string> { track1Id.ToString(), track2Id.ToString() }, currentIndex: 0);
        _manager.RecordLaunchBase("voice-dev", track1Id.ToString(), 0, enqueued: false);

        var handler = new PlaybackNearlyFinishedEventHandler(
            sessionManager.Object,
            config,
            libraryManager.Object,
            new Mock<IUserManager>().Object,
            LoggerFactory.Create(b => { }),
            _manager);

        string token = track1Id.ToString();
        var request = TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", token);
        var context = CreateEventContext("playback-dev", token, user.Id);

        SkillResponse response = await handler.HandleRequestAsync(request, context, alexaSession: null, CancellationToken.None);

        // The canonicalized device is visible to every downstream consumer.
        Assert.Equal("voice-dev", context.System.Device.DeviceID);

        // The next album track is enqueued (the issue #43 symptom is gone).
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);

        // The session resolved under the LAUNCH device's id, never the event device's.
        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "voice-dev", It.IsAny<string?>()),
            Times.Once);
        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);

        // The chain survives the boundary: the enqueue's directive recorded
        // track 2's ownership under the launch device too, so track 2's own
        // NearlyFinished (arriving from the playback device) still aliases.
        Assert.Equal("voice-dev", _manager.ResolveDeviceAlias("playback-dev", track2Id.ToString()));
    }

    /// <summary>
    /// The no-alias guarantee for same-device playback (every normal Echo): the
    /// funnel leaves the device id untouched and resolves the session under the
    /// device's own id. This is the byte-identical leg of JF-918: the fix must
    /// change nothing when the event device IS the launch device.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_SameDeviceEvent_NoAlias_SessionResolvedUnderOwnId_JF918()
    {
        var (user, config) = CreateUserWithConfig();
        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track1 = new Audio { Id = track1Id, Name = "Track 1" };
        var track2 = new Audio { Id = track2Id, Name = "Track 2" };

        var sessionManager = new Mock<ISessionManager>();
        var session = TestHelpers.CreateTestSession(sessionManager.Object, LoggerFactory.Create(b => { }));
        session.PlayState = new PlayerStateInfo();
        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
        };
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "echo-dev", It.IsAny<string?>()))
            .ReturnsAsync(session);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemById(track2Id)).Returns(track2);
        libraryManager.Setup(l => l.GetItemById(track1Id)).Returns(track1);

        _manager.SetQueue("echo-dev", new List<string> { track1Id.ToString(), track2Id.ToString() }, currentIndex: 0);
        _manager.RecordLaunchBase("echo-dev", track1Id.ToString(), 0, enqueued: false);

        var handler = new PlaybackNearlyFinishedEventHandler(
            sessionManager.Object,
            config,
            libraryManager.Object,
            new Mock<IUserManager>().Object,
            LoggerFactory.Create(b => { }),
            _manager);

        string token = track1Id.ToString();
        var request = TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", token);
        var context = CreateEventContext("echo-dev", token, user.Id);

        SkillResponse response = await handler.HandleRequestAsync(request, context, alexaSession: null, CancellationToken.None);

        Assert.Equal("echo-dev", context.System.Device.DeviceID);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);

        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "echo-dev", It.IsAny<string?>()),
            Times.Once);
        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// The INTENT-path NEGATIVE pin (code-review JF-918 finding 1): an intent
    /// whose context playback state echoes another device's stream token must
    /// NOT be canonicalized, even when the requester is stateless. Intents
    /// create per-device state, and a group-member Echo echoing the group's
    /// token would otherwise have its play written over the launch device's
    /// live queue. The canonicalization is for control-shaped traffic only
    /// (AudioPlayer events, PlaybackController taps).
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_IntentRequestWithContextToken_NotCanonicalized_JF918()
    {
        var (user, config) = CreateUserWithConfig();
        string item = Guid.NewGuid().ToString();

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "playback-dev", It.IsAny<string?>()))
            .ReturnsAsync(TestHelpers.CreateTestSession(sessionManager.Object, LoggerFactory.Create(b => { })));

        var handler = new RecordingIntentHandler(sessionManager.Object, config, LoggerFactory.Create(b => { }));

        _manager.SetQueue("voice-dev", new List<string> { item }, currentIndex: 0);
        _manager.RecordLaunchBase("voice-dev", item, 0, enqueued: false);

        var request = new IntentRequest { Type = "IntentRequest", Intent = new Intent { Name = "AnyIntent" } };
        var context = CreateEventContext("playback-dev", item, user.Id);

        await handler.HandleRequestAsync(request, context, alexaSession: null, CancellationToken.None);

        // The device id is untouched and the session resolved under the
        // requester's OWN id (the mock is only stubbed for playback-dev).
        Assert.Equal("playback-dev", context.System.Device.DeviceID);
        sessionManager.Verify(
            s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "playback-dev", It.IsAny<string?>()),
            Times.Once);
    }

    /// <summary>
    /// The PlaybackController POSITIVE arm: a tap command (control-shaped, no
    /// state creation) from the playback endpoint carries the stream token in
    /// its context playback state and IS canonicalized, so Sonos tap controls
    /// reach the queue they control.
    /// </summary>
    [Fact]
    public async Task HandleRequestAsync_PlaybackControllerCommand_WithContextToken_Canonicalized_JF918()
    {
        var (user, config) = CreateUserWithConfig();
        string item = Guid.NewGuid().ToString();

        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(user.JellyfinToken, "voice-dev", It.IsAny<string?>()))
            .ReturnsAsync(TestHelpers.CreateTestSession(sessionManager.Object, LoggerFactory.Create(b => { })));

        var handler = new RecordingIntentHandler(sessionManager.Object, config, LoggerFactory.Create(b => { }));

        _manager.SetQueue("voice-dev", new List<string> { item }, currentIndex: 0);
        _manager.RecordLaunchBase("voice-dev", item, 0, enqueued: false);

        var request = new PlaybackControllerRequest
        {
            Type = "PlaybackController.NextCommandIssued",
        };
        var context = CreateEventContext("playback-dev", item, user.Id);

        await handler.HandleRequestAsync(request, context, alexaSession: null, CancellationToken.None);

        Assert.Equal("voice-dev", context.System.Device.DeviceID);
    }

    /// <summary>
    /// Minimal concrete handler for the intent-path funnel pin (the JF-327
    /// RecordingHandler shape: the test asserts the funnel's canonicalization,
    /// not handler behavior).
    /// </summary>
    private sealed class RecordingIntentHandler : BaseHandler
    {
        public RecordingIntentHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => request is IntentRequest;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Empty());
    }
}
