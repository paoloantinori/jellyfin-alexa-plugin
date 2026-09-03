using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Alexa.NET.Assertions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for StartOverIntentHandler: restarts currently playing or last-played item from the beginning.
/// </summary>
[Collection("Plugin")]
public class StartOverIntentHandlerTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");
    private readonly JellyfinUser _jellyfinUser;
    private readonly Guid _sessionUserId;

    public StartOverIntentHandlerTests()
    {
        _jellyfinUser = new JellyfinUser("testuser", "test", "test");
        _sessionUserId = Guid.NewGuid();

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "startover-tests");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private StartOverIntentHandler CreateHandler()
    {
        return new StartOverIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateStartOverRequest()
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.StartOverIntent" }
        };
    }

    private SessionInfo CreateSessionWithNowPlaying(BaseItem item)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _sessionUserId;
        session.FullNowPlayingItem = item;
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns(_jellyfinUser);
        return session;
    }

    private SessionInfo CreateEmptySession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _sessionUserId;
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns(_jellyfinUser);
        return session;
    }

    [Fact]
    public void CanHandle_StartOverIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.ResumeIntent" }
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlaying_RestartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var audioItem = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        var session = CreateSessionWithNowPlaying(audioItem);

        // Set up existing progress
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(2).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, audioItem))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);

        // Should return AudioPlayer directive with offset 0 (restart from beginning)
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);

        // Should have cleared the progress by saving with position = 0
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                _jellyfinUser,
                audioItem,
                It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackProgress,
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlayingMovie_RestartsWithVideoApp()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Test Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/test.mkv"
        };

        var session = CreateSessionWithNowPlaying(movie);

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, movie))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);

        // Output speech should mention restarting
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("starting", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Test Movie", speech);

        // Should have cleared progress
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                _jellyfinUser,
                movie,
                It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackProgress,
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_WithServerProgress_RestartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        // Empty session (FullNowPlayingItem is null)
        var session = CreateEmptySession();

        // Server has an item with progress
        var audioItem = new Audio
        {
            Name = "Last Played Song",
            Id = Guid.NewGuid(),
            Path = "/music/last.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(3).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Should play from beginning (offset 0)
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Should have cleared progress before playing
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                _jellyfinUser,
                It.IsAny<BaseItem>(),
                It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackProgress,
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_NoServerProgress_ReturnsNoMediaToRestart()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        // Empty session (FullNowPlayingItem is null)
        var session = CreateEmptySession();

        // No items with progress on the server
        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing to restart", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NullSession_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        // null session -> handler returns NoMediaPlaying
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlaying_NoExistingUserData_StillPlays()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var audioItem = new Audio
        {
            Name = "New Song",
            Id = Guid.NewGuid(),
            Path = "/music/new.mp3"
        };

        var session = CreateSessionWithNowPlaying(audioItem);

        // No user data exists for this item
        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, audioItem))
            .Returns((UserItemData?)null);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Should still play from beginning even without existing progress to clear
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // SaveUserData should NOT be called when there is no existing userData
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                It.IsAny<JellyfinUser>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_UserNotFound_ReturnsUserNotFoundError()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var audioItem = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        var session = CreateSessionWithNowPlaying(audioItem);

        // Override: user manager returns null (user not found)
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns((JellyfinUser?)null);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("user not found", speech, StringComparison.OrdinalIgnoreCase);
    }
}
