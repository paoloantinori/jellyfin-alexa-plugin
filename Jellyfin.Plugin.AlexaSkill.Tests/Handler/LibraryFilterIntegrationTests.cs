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
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Integration tests verifying that per-user library filtering (AllowedLibraryIds)
/// correctly sets TopParentIds on Jellyfin InternalItemsQuery objects in intent handlers.
/// </summary>
public class LibraryFilterIntegrationTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public LibraryFilterIntegrationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlaySongIntentHandler CreatePlaySongHandler()
    {
        return new PlaySongIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private SearchMediaIntentHandler CreateSearchMediaHandler()
    {
        return new SearchMediaIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreatePlaySongRequest(string song = "Test Song")
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
        {
            ["song"] = new global::Alexa.NET.Request.Slot { Name = "song", Value = song }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static IntentRequest CreateSearchMediaRequest(string query = "test")
    {
        var intent = new Intent { Name = IntentNames.SearchMedia };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
        {
            ["query"] = new global::Alexa.NET.Request.Slot { Name = "query", Value = query }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUserWithLibraries(List<string>? allowedLibraryIds)
    {
        return new Entities.User
        {
            Id = Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            AllowedLibraryIds = allowedLibraryIds
        };
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public async Task PlaySongHandler_WithAllowedLibraryIds_SetsTopParentIdsOnQuery()
    {
        // Arrange
        var musicLibId = Guid.NewGuid();
        var user = CreateUserWithLibraries(new List<string> { musicLibId.ToString() });
        var handler = CreatePlaySongHandler();
        var request = CreatePlaySongRequest("Bohemian Rhapsody");
        var context = CreateContext();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };
        InternalItemsQuery? capturedQuery = null;

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { audio });

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Single(capturedQuery.TopParentIds);
        Assert.Equal(musicLibId, capturedQuery.TopParentIds[0]);
    }

    [Fact]
    public async Task SearchMediaHandler_WithAllowedLibraryIds_SetsTopParentIdsOnQuery()
    {
        // Arrange
        var musicLibId = Guid.NewGuid();
        var movieLibId = Guid.NewGuid();
        var user = CreateUserWithLibraries(new List<string> { musicLibId.ToString(), movieLibId.ToString() });
        var handler = CreateSearchMediaHandler();
        var request = CreateSearchMediaRequest("Star Wars");
        var context = CreateContext();
        var session = CreateSession();

        SetupUserMock();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Star Wars",
            Id = Guid.NewGuid()
        };
        InternalItemsQuery? capturedQuery = null;

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { movie });

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Equal(2, capturedQuery.TopParentIds.Length);
        Assert.Contains(musicLibId, capturedQuery.TopParentIds);
        Assert.Contains(movieLibId, capturedQuery.TopParentIds);
    }

    [Fact]
    public async Task PlaySongHandler_WithNullAllowedLibraryIds_DoesNotSetTopParentIds()
    {
        // Arrange
        var user = CreateUserWithLibraries(null);
        var handler = CreatePlaySongHandler();
        var request = CreatePlaySongRequest("Test Song");
        var context = CreateContext();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert - TopParentIds is not set; InternalItemsQuery initializes it to empty array.
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }

    [Fact]
    public async Task PlaySongHandler_WithEmptyAllowedLibraryIds_DoesNotSetTopParentIds()
    {
        // Arrange
        var user = CreateUserWithLibraries(new List<string>());
        var handler = CreatePlaySongHandler();
        var request = CreatePlaySongRequest("Test Song");
        var context = CreateContext();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert - TopParentIds is not set; InternalItemsQuery initializes it to empty array.
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }
}
