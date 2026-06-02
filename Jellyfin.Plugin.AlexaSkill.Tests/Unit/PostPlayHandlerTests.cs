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
    public async Task QueueExhausted_StopMode_DoesNotSetPostPlayState()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Stop;
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(Guid.NewGuid().ToString());

        session.NowPlayingQueue = new List<QueueItem>();

        await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task QueueExhausted_AutoPlayMode_SetsPostPlayState()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId);

        await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode, out var storedItemId));
        Assert.Equal(PostPlayBehavior.AutoPlay, mode);
        Assert.Equal(itemId.ToString(), storedItemId);
    }

    [Fact]
    public async Task QueueExhausted_AskMode_SetsPostPlayState()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.Ask;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId);

        await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode, out _));
        Assert.Equal(PostPlayBehavior.Ask, mode);
    }

    [Fact]
    public async Task HasNextItem_DoesNotSetPostPlayState()
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

        await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task RadioModeOn_DoesNotSetPostPlayState()
    {
        _config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackNearlyFinishedContext(itemId.ToString());

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = itemId } };
        session.FullNowPlayingItem = CreateAudioItem(itemId);
        RadioModeState.Enable(_userId, DeviceId);

        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns((JellyfinUser?)null);

        await _handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
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

    private static Audio.Audio CreateAudioItem(Guid id)
    {
        var audio = new Audio.Audio();
        typeof(BaseItem).GetProperty("Id")!.SetValue(audio, id);
        typeof(BaseItem).GetProperty("Name")!.SetValue(audio, "Test Song");
        return audio;
    }
}

/// <summary>
/// Tests for PlaybackFinished PostPlay integration.
/// </summary>
[Collection("Plugin")]
public class PlaybackFinishedPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public PlaybackFinishedPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "pf-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private PlaybackFinishedEventHandler CreateHandler()
        => new(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

    [Fact]
    public async Task NoPostPlayState_EndsSession()
    {
        var handler = CreateHandler();
        var itemId = Guid.NewGuid();
        var (request, context, user, session) = CreatePlaybackFinishedContext(itemId.ToString());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task AutoPlay_FindsTracks_PlaysAndEnablesRadio()
    {
        var itemId = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, itemId.ToString());

        var currentAudio = CreateAudioItem(itemId, "Test Song", new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        var radioTrack = CreateAudioItem(radioTrackId, "Similar Song");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        var handler = CreateHandler();
        var (request, context, user, session) = CreatePlaybackFinishedContext(itemId.ToString());
        session.FullNowPlayingItem = currentAudio;

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.Equal(PlayBehavior.ReplaceAll, playDirective.PlayBehavior);

        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task AutoPlay_NoTracksFound_EndsSession()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, itemId.ToString());

        var currentAudio = CreateAudioItem(itemId, "Test Song");
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());

        var handler = CreateHandler();
        var (request, context, user, session) = CreatePlaybackFinishedContext(itemId.ToString());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task AskMode_ReturnsPromptWithoutEndingSession()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, itemId.ToString());

        var handler = CreateHandler();
        var (request, context, user, session) = CreatePlaybackFinishedContext(itemId.ToString());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);

        // State remains for Yes/No to consume
        Assert.True(PostPlayState.TryGet(_userId, DeviceId, out var mode, out _));
        Assert.Equal(PostPlayBehavior.Ask, mode);
    }

    [Fact]
    public async Task HasQueuedNext_SkipsPostPlay()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.AutoPlay, itemId.ToString());

        var handler = CreateHandler();
        // Override context to simulate next track already queued
        var (request, _, user, session) = CreatePlaybackFinishedContext(itemId.ToString());
        var context = AlexaRequestFactory.CreateContextWithAudioPlayer(
            _userId.ToString(), DeviceId, itemId.ToString(), 0, "PLAYING");

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should keep alive, not act on PostPlay
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

    private static Audio.Audio CreateAudioItem(Guid id, string name, string[]? genres = null)
    {
        var audio = new Audio.Audio();
        typeof(BaseItem).GetProperty("Id")!.SetValue(audio, id);
        typeof(BaseItem).GetProperty("Name")!.SetValue(audio, name);
        if (genres != null)
        {
            typeof(Audio.Audio).GetProperty("Genres")!.SetValue(audio, genres);
        }

        return audio;
    }
}

/// <summary>
/// Tests for YesIntent PostPlay integration.
/// </summary>
[Collection("Plugin")]
public class YesIntentPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly YesIntentHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public YesIntentPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new YesIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "yes-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task NoPostPlayState_FallsThroughToUnexpectedYes()
    {
        var (request, context, user, session) = CreateYesContext();
        var attrs = new Dictionary<string, object>();

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        Assert.Contains("not sure what you'd like me to play", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task PostPlayAskState_YesFindsTracksAndPlays()
    {
        var itemId = Guid.NewGuid();
        var radioTrackId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, itemId.ToString());

        var currentAudio = CreateAudioItem(itemId, "Test Song", new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        var radioTrack = CreateAudioItem(radioTrackId, "Similar Song");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        var (request, context, user, session) = CreateYesContext();
        var attrs = new Dictionary<string, object>();

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
        Assert.True(RadioModeState.IsEnabled(_userId, DeviceId));
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task PostPlayAskState_YesNoTracksFound_EndsSession()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, itemId.ToString());

        var currentAudio = CreateAudioItem(itemId, "Test Song");
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());

        var (request, context, user, session) = CreateYesContext();
        var attrs = new Dictionary<string, object>();

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task PostPlayState_TakesPriorityOverResume()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, itemId.ToString());

        var currentAudio = CreateAudioItem(itemId, "Test Song", new[] { "Rock" });
        _libraryManagerMock.Setup(lm => lm.GetItemById(itemId)).Returns(currentAudio);

        var jellyfinUser = new JellyfinUser("test", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);

        var radioTrack = CreateAudioItem(Guid.NewGuid(), "Radio Track");
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { radioTrack }.AsReadOnly());

        var (request, context, user, session) = CreateYesContext();
        var attrs = new Dictionary<string, object>
        {
            { "resumeItemId", "some-other-item" },
            { "resumeOffsetMs", "5000" }
        };

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        var playDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(playDirective);
    }

    private (IntentRequest request, Context context, Entities.User user, SessionInfo session)
        CreateYesContext()
    {
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.YesIntent" },
            Locale = "en-US"
        };

        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { UserId = _userId.ToString() },
                Device = new Device { DeviceID = DeviceId }
            }
        };

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>());
        session.UserId = _userId;

        return (request, context, user, session);
    }

    private static Audio.Audio CreateAudioItem(Guid id, string name, string[]? genres = null)
    {
        var audio = new Audio.Audio();
        typeof(BaseItem).GetProperty("Id")!.SetValue(audio, id);
        typeof(BaseItem).GetProperty("Name")!.SetValue(audio, name);
        if (genres != null)
        {
            typeof(Audio.Audio).GetProperty("Genres")!.SetValue(audio, genres);
        }

        return audio;
    }
}

/// <summary>
/// Tests for NoIntent PostPlay integration.
/// </summary>
[Collection("Plugin")]
public class NoIntentPostPlayTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NoIntentHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();
    private const string DeviceId = "test-device";

    public NoIntentPostPlayTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new NoIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "no-postplay-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    [Fact]
    public async Task NoPostPlayState_FallsThroughToUnexpectedYes()
    {
        var (request, context, user, session) = CreateNoContext();
        var attrs = new Dictionary<string, object>();

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        Assert.Contains("not sure what you'd like me to play", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task PostPlayAskState_No_ClearsStateAndResponds()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, itemId.ToString());

        var (request, context, user, session) = CreateNoContext();
        var attrs = new Dictionary<string, object>();

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    [Fact]
    public async Task PostPlayState_TakesPriorityOverResume()
    {
        var itemId = Guid.NewGuid();
        PostPlayState.Set(_userId, DeviceId, PostPlayBehavior.Ask, itemId.ToString());

        var (request, context, user, session) = CreateNoContext();
        var attrs = new Dictionary<string, object>
        {
            { "resumeItemId", "some-item" },
            { "resumeOffsetMs", "5000" }
        };

        SkillResponse response = await _handler.HandleAsync(request, context, user, session, attrs, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.False(PostPlayState.TryGet(_userId, DeviceId, out _, out _));
    }

    private (IntentRequest request, Context context, Entities.User user, SessionInfo session)
        CreateNoContext()
    {
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.NoIntent" },
            Locale = "en-US"
        };

        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { UserId = _userId.ToString() },
                Device = new Device { DeviceID = DeviceId }
            }
        };

        var user = new Entities.User { Id = _userId, JellyfinToken = "test-token" };
        var session = new SessionInfo(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>());
        session.UserId = _userId;

        return (request, context, user, session);
    }
}
