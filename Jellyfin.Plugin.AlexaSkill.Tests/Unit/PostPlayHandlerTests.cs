using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Helper to deserialize AudioPlayerRequest from JSON (readonly properties
/// can't be set via object initializer).
/// </summary>
internal static class AlexaRequestFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static AudioPlayerRequest CreateAudioPlayerRequest(string type, string token, long offsetMs)
    {
        string json = $$"""
        {
            "type": "{{type}}",
            "requestId": "test-req-{{Guid.NewGuid()}}",
            "timestamp": "2024-01-01T00:00:00Z",
            "locale": "en-US",
            "token": "{{token}}",
            "offsetInMilliseconds": {{offsetMs}}
        }
        """;
        return JsonSerializer.Deserialize<AudioPlayerRequest>(json, JsonOptions)!;
    }

    internal static Context CreateContextWithAudioPlayer(string userId, string deviceId, string token, long offsetMs, string playerActivity)
    {
        string json = $$"""
        {
            "System": {
                "user": { "userId": "{{userId}}" },
                "device": { "deviceId": "{{deviceId}}" }
            },
            "AudioPlayer": {
                "token": "{{token}}",
                "offsetInMilliseconds": {{offsetMs}},
                "playerActivity": "{{playerActivity}}"
            }
        }
        """;
        return JsonSerializer.Deserialize<Context>(json, JsonOptions)!;
    }
}

/// <summary>
/// Tests for PlaybackNearlyFinished PostPlay integration.
/// </summary>
[Collection("Plugin")]
public class PlaybackNearlyFinishedPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PlaybackNearlyFinishedEventHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public PlaybackNearlyFinishedPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "pnf-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task QueueExhausted_StopMode_ReturnsEmpty()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Stop;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
    }

    [Fact]
    public async Task QueueExhausted_AutoPlayMode_EnqueuesSimilarTracks()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId, new[] { "Rock" });

        var currentAudio = CreateAudioItem(itemId, new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        var radioTrack = CreateAudioItem(radioTrackId);
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());
        _libraryManagerMock.Setup(lm => lm.GetItemById(radioTrackId)).Returns(radioTrack);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(PlayBehavior.Enqueue, playDirective.PlayBehavior);
    }

    [Fact]
    public async Task QueueExhausted_AutoPlayMode_NoTracksFound_ReturnsEmpty()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId, new[] { "Rock" });

        var currentAudio = CreateAudioItem(itemId, new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
    }

    [Fact]
    public async Task HasNextItem_DoesNotTriggerPostPlay()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(currentId.ToString());

        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentId },
            new() { Id = nextId }
        };
        session.FullNowPlayingItem = CreateAudioItem(currentId);
        _libraryManagerMock.Setup(lm => lm.GetItemById(nextId)).Returns(CreateAudioItem(nextId));

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should enqueue the next item, not trigger PostPlay
        Assert.False(RadioModeState.IsEnabled(_userId, DeviceId));
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task RadioModeOn_DoesNotTriggerPostPlay()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId);
        RadioModeState.Enable(_userId, DeviceId);

        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns((JellyfinUser?)null);

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Radio mode handles its own continuation, not PostPlay
        Assert.Empty(response.Response.Directives ?? Array.Empty<IDirective>());
    }

    private (Request request, Context context, Entities.User user, SessionInfo session)
        CreatePlaybackNearlyFinishedContext(string tokenId)
    {
        var request = AlexaRequestFactory.CreateAudioPlayerRequest(
            "AudioPlayer.PlaybackNearlyFinished", tokenId, 0);

        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, tokenId, 0, "PLAYING");

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>());
        session.UserId = _userId;

        return (request, context, user, session);
    }

    private static Audio.Audio CreateAudioItem(Guid id, string[]? genres = null)
    {
        var audio = new Audio.Audio();
        typeof(BaseItem).GetProperty("Id")!.SetValue(audio, id);
        typeof(BaseItem).GetProperty("Name")!.SetValue(audio, "Test Song");
        if (genres != null)
        {
            typeof(Audio.Audio).GetProperty("Genres")!.SetValue(audio, genres);
        }

        return audio;
    }
}

/// <summary>
/// Tests for PlaybackFinished behavior.
/// PlaybackFinished reports stop position, then ends or keeps alive based on queue state.
/// </summary>
[Collection("Plugin")]
public class PlaybackFinishedPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public PlaybackFinishedPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "pf-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private PlaybackFinishedEventHandler CreateHandler()
        => new(_sessionManagerMock.Object, _config, _loggerFactory);

    [Fact]
    public async Task QueueExhausted_EndsSession()
    {
        var handler = CreateHandler();
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackFinishedContext(itemId.ToString());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HasQueuedNext_KeepsAlive()
    {
        var handler = CreateHandler();
        var itemId = Guid.NewGuid();
        var (request, _, user, session) = CreatePlaybackFinishedContext(itemId.ToString());
        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, itemId.ToString(), 0, "PLAYING");

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Null(response.Response.ShouldEndSession);
    }

    private (Request request, Context context, Entities.User user, SessionInfo session)
        CreatePlaybackFinishedContext(string tokenId)
    {
        var request = AlexaRequestFactory.CreateAudioPlayerRequest(
            "AudioPlayer.PlaybackFinished", tokenId, 180000);

        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, tokenId, 180000, "FINISHED");

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>());
        session.UserId = _userId;

        return (request, context, user, session);
    }
}
