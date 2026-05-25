using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Integration tests verifying that handlers correctly pass user-specific
/// AllowedLibraryIds through ApplyLibraryFilter to the InternalItemsQuery
/// sent to ILibraryManager.GetItemList.
/// </summary>
[Collection("Plugin")]
public class LibraryFilterIntegrationTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public LibraryFilterIntegrationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c => { },
            "alexa-libfilter-integration-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Creates a test user with specific AllowedLibraryIds.
    /// </summary>
    private static Entities.User CreateUserWithLibraries(params Guid[] libraryIds)
    {
        return new Entities.User
        {
            Id = Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            AllowedLibraryIds = libraryIds.Select(id => id.ToString()).ToList()
        };
    }

    /// <summary>
    /// Creates a test user without library restrictions (AllowedLibraryIds = null).
    /// </summary>
    private static Entities.User CreateUserWithoutLibraryFilter()
    {
        return new Entities.User
        {
            Id = Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            AllowedLibraryIds = null
        };
    }

    /// <summary>
    /// Sets up ILibraryManager to capture the query and returns a factory
    /// that retrieves the captured query after the handler has executed.
    /// </summary>
    private Func<InternalItemsQuery?> CaptureQueryViaLibraryManager()
    {
        InternalItemsQuery? captured = null;

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem>());

        return () => captured;
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private void SetupJellyfinUser()
    {
        var jellyfinUser = new global::Jellyfin.Database.Implementations.Entities.User(
            "testuser", "test", "test")
        {
            Id = Guid.NewGuid(),
        };
        _userManagerMock.Setup(um => um.GetUserById(It.IsAny<Guid>())).Returns(jellyfinUser);
    }

    // --- PlayRandomIntentHandler ---

    [Fact]
    public async Task PlayRandom_SetsTopParentIds_WhenUserHasLibraryFilter()
    {
        var libId1 = Guid.NewGuid();
        var libId2 = Guid.NewGuid();
        var user = CreateUserWithLibraries(libId1, libId2);
        SetupJellyfinUser();

        // Set up LibraryManager to recognize these as regular folders (not CollectionFolder)
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId1))
            .Returns(new Folder { Id = libId1 });
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId2))
            .Returns(new Folder { Id = libId2 });

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayRandomIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRandomIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Equal(2, capturedQuery.TopParentIds.Length);
        Assert.Contains(libId1, capturedQuery.TopParentIds);
        Assert.Contains(libId2, capturedQuery.TopParentIds);
    }

    [Fact]
    public async Task PlayRandom_DoesNotSetTopParentIds_WhenUserHasNoLibraryFilter()
    {
        var user = CreateUserWithoutLibraryFilter();
        SetupJellyfinUser();

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayRandomIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRandomIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        // When AllowedLibraryIds is null, ApplyLibraryFilter is a no-op.
        // InternalItemsQuery.TopParentIds defaults to an empty array.
        Assert.Empty(capturedQuery.TopParentIds);
    }

    // --- SearchMediaIntentHandler ---

    [Fact]
    public async Task SearchMedia_SetsTopParentIds_WhenUserHasLibraryFilter()
    {
        var libId = Guid.NewGuid();
        var user = CreateUserWithLibraries(libId);
        SetupJellyfinUser();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId))
            .Returns(new Folder { Id = libId });

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new SearchMediaIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest
            {
                Intent = new Intent
                {
                    Name = "SearchMediaIntent",
                    Slots = new Dictionary<string, Slot>
                    {
                        ["query"] = new Slot { Value = "test search" }
                    }
                }
            },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Single(capturedQuery.TopParentIds);
        Assert.Equal(libId, capturedQuery.TopParentIds[0]);
    }

    // --- PlayFavoritesIntentHandler ---

    [Fact]
    public async Task PlayFavorites_SetsTopParentIds_WhenUserHasLibraryFilter()
    {
        var libId = Guid.NewGuid();
        var user = CreateUserWithLibraries(libId);
        SetupJellyfinUser();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId))
            .Returns(new Folder { Id = libId });

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayFavoritesIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayFavoritesIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Single(capturedQuery.TopParentIds);
        Assert.Equal(libId, capturedQuery.TopParentIds[0]);
    }

    // --- PlayLastAddedIntentHandler ---

    [Fact]
    public async Task PlayLastAdded_SetsTopParentIds_WhenUserHasLibraryFilter()
    {
        var libId = Guid.NewGuid();
        var user = CreateUserWithLibraries(libId);
        SetupJellyfinUser();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId))
            .Returns(new Folder { Id = libId });

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayLastAddedIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayLastAddedIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Single(capturedQuery.TopParentIds);
        Assert.Equal(libId, capturedQuery.TopParentIds[0]);
    }

    // --- CollectionFolder resolution ---

    [Fact]
    public async Task PlayRandom_ResolvesCollectionFolderToPhysicalFolderId()
    {
        var cfId = Guid.NewGuid();
        var physicalId = Guid.NewGuid();
        var user = CreateUserWithLibraries(cfId);
        SetupJellyfinUser();

        // Set up LibraryManager to return a CollectionFolder with physical locations
        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };

        var physicalFolder = new Folder { Id = physicalId };

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(cfId))
            .Returns(cf);
        _libraryManagerMock
            .Setup(lm => lm.FindByPath("/data/media/music", true))
            .Returns(physicalFolder);

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayRandomIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRandomIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Single(capturedQuery.TopParentIds);
        // The query should have the resolved physical folder ID, not the CollectionFolder ID
        Assert.Equal(physicalId, capturedQuery.TopParentIds[0]);
        Assert.NotEqual(cfId, capturedQuery.TopParentIds[0]);
    }

    // --- Multiple handlers consistency check ---

    [Fact]
    public async Task SearchMedia_DoesNotSetTopParentIds_WhenUserHasNoLibraryFilter()
    {
        var user = CreateUserWithoutLibraryFilter();
        SetupJellyfinUser();

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new SearchMediaIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest
            {
                Intent = new Intent
                {
                    Name = "SearchMediaIntent",
                    Slots = new Dictionary<string, Slot>
                    {
                        ["query"] = new Slot { Value = "test search" }
                    }
                }
            },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }

    [Fact]
    public async Task PlayFavorites_DoesNotSetTopParentIds_WhenUserHasNoLibraryFilter()
    {
        var user = CreateUserWithoutLibraryFilter();
        SetupJellyfinUser();

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayFavoritesIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayFavoritesIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }

    [Fact]
    public async Task PlayLastAdded_DoesNotSetTopParentIds_WhenUserHasNoLibraryFilter()
    {
        var user = CreateUserWithoutLibraryFilter();
        SetupJellyfinUser();

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayLastAddedIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayLastAddedIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }

    // --- Multiple AllowedLibraryIds ---

    [Fact]
    public async Task SearchMedia_SetsAllTopParentIds_WhenUserHasMultipleLibraries()
    {
        var libId1 = Guid.NewGuid();
        var libId2 = Guid.NewGuid();
        var libId3 = Guid.NewGuid();
        var user = CreateUserWithLibraries(libId1, libId2, libId3);
        SetupJellyfinUser();

        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId1))
            .Returns(new Folder { Id = libId1 });
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId2))
            .Returns(new Folder { Id = libId2 });
        _libraryManagerMock
            .Setup(lm => lm.GetItemById(libId3))
            .Returns(new Folder { Id = libId3 });

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new SearchMediaIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest
            {
                Intent = new Intent
                {
                    Name = "SearchMediaIntent",
                    Slots = new Dictionary<string, Slot>
                    {
                        ["query"] = new Slot { Value = "test search" }
                    }
                }
            },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Equal(3, capturedQuery.TopParentIds.Length);
        Assert.Contains(libId1, capturedQuery.TopParentIds);
        Assert.Contains(libId2, capturedQuery.TopParentIds);
        Assert.Contains(libId3, capturedQuery.TopParentIds);
    }

    // --- Empty AllowedLibraryIds treated as unrestricted ---

    [Fact]
    public async Task PlayRandom_DoesNotSetTopParentIds_WhenAllowedLibraryIdsIsEmpty()
    {
        var user = new Entities.User
        {
            Id = Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            AllowedLibraryIds = new List<string>()
        };
        SetupJellyfinUser();

        var getCaptured = CaptureQueryViaLibraryManager();

        var handler = new PlayRandomIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRandomIntent" } },
            CreateContext(), user, CreateSession(), CancellationToken.None);

        var capturedQuery = getCaptured();
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }
}
