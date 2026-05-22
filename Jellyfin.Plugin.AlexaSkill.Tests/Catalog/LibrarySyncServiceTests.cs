#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// Integration tests verifying that LibrarySyncService applies per-user
/// library filtering when querying Jellyfin items for catalog sync.
/// </summary>
public class LibrarySyncServiceTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly CatalogManager _catalogManager;
    private readonly ILoggerFactory _loggerFactory;

    public LibrarySyncServiceTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _loggerFactory = LoggerFactory.Create(b => { });

        // Create a CatalogManager with a mocked IHttpClientFactory that returns
        // an HttpClient that will throw for any real HTTP calls.
        var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
        var httpMessageHandler = new MockHttpMessageHandler();
        httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(httpMessageHandler));
        _catalogManager = new CatalogManager(httpClientFactoryMock.Object, _loggerFactory.CreateLogger<CatalogManager>());
    }

    private LibrarySyncService CreateService()
    {
        return new LibrarySyncService(
            _libraryManagerMock.Object,
            _catalogManager,
            _loggerFactory.CreateLogger<LibrarySyncService>());
    }

    private static Entities.User CreatePluginUser(Guid? id = null, List<string>? allowedLibraryIds = null)
    {
        return new Entities.User
        {
            Id = id ?? Guid.NewGuid(),
            InvocationName = "test",
            JellyfinToken = "test-token",
            SmapiDeviceToken = new DeviceToken("access-token", "refresh-token", "Bearer", 9999999999),
            UserSkill = new UserSkill { SkillId = "amzn1.ask.skill.test-id" },
            VendorId = "test-vendor-id",
            AllowedLibraryIds = allowedLibraryIds
        };
    }

    /// <summary>
    /// Verifies that when a user has AllowedLibraryIds configured, the LibrarySyncService
    /// passes those IDs as TopParentIds to ILibraryManager.GetItemList.
    /// The test returns empty items from the library, so no catalog operations are attempted.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithAllowedLibraryIds_SetsTopParentIdsOnQuery()
    {
        // Arrange
        var musicLibId = Guid.NewGuid();
        var user = CreatePluginUser(allowedLibraryIds: new List<string> { musicLibId.ToString() });
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        var service = CreateService();

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        // Act
        var result = await service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        // SyncUserLibraryAsync makes two parallel calls to SyncCatalogAsync (artists + albums).
        // Both should have TopParentIds set.
        Assert.Equal(2, capturedQueries.Count);
        Assert.All(capturedQueries, q =>
        {
            Assert.NotNull(q.TopParentIds);
            Assert.Single(q.TopParentIds);
            Assert.Equal(musicLibId, q.TopParentIds[0]);
        });
    }

    /// <summary>
    /// Verifies that when a user has no AllowedLibraryIds, the LibrarySyncService
    /// does not set TopParentIds on the query.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithNullAllowedLibraryIds_DoesNotSetTopParentIds()
    {
        // Arrange
        var user = CreatePluginUser(allowedLibraryIds: null);
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        var service = CreateService();

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        // Act
        var result = await service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.Equal(2, capturedQueries.Count);
        Assert.All(capturedQueries, q =>
        {
            // TopParentIds is not set; InternalItemsQuery initializes it to empty array.
            Assert.Empty(q.TopParentIds);
        });
    }

    /// <summary>
    /// Verifies that an empty AllowedLibraryIds list is treated the same as null.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithEmptyAllowedLibraryIds_DoesNotSetTopParentIds()
    {
        // Arrange
        var user = CreatePluginUser(allowedLibraryIds: new List<string>());
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        var service = CreateService();

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        // Act
        var result = await service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert
        Assert.Equal(2, capturedQueries.Count);
        Assert.All(capturedQueries, q =>
        {
            // TopParentIds is not set; InternalItemsQuery initializes it to empty array.
            Assert.Empty(q.TopParentIds);
        });
    }

    /// <summary>
    /// Verifies that InternalItemsQuery.Limit is set to MaxCatalogValues (50000)
    /// so the database limits the result set instead of materializing all rows.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_SetsLimitOnQuery()
    {
        // Arrange
        const int ExpectedLimit = 50000;
        var user = CreatePluginUser();
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        var service = CreateService();

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        // Act
        await service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert — two parallel catalog queries (artists + albums)
        Assert.Equal(2, capturedQueries.Count);
        Assert.All(capturedQueries, q =>
        {
            Assert.Equal(ExpectedLimit, q.Limit);
        });
    }

    /// <summary>
    /// Verifies that SyncUserLibraryAsync returns early (no queries made) when
    /// the user has no SMAPI device token.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithoutSmapiToken_SkipsSync()
    {
        // Arrange
        var user = CreatePluginUser();
        user.SmapiDeviceToken = null;
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        var service = CreateService();

        // Act
        var result = await service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert - no library queries should have been made
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        Assert.False(result.Success);
    }

    /// <summary>
    /// Verifies that SyncUserLibraryAsync returns early (no queries made) when
    /// the user has no vendor ID.
    /// </summary>
    [Fact]
    public async Task SyncUserLibraryAsync_WithoutVendorId_SkipsSync()
    {
        // Arrange
        var user = CreatePluginUser();
        user.VendorId = null;
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        var service = CreateService();

        // Act
        var result = await service.SyncUserLibraryAsync(user, jellyfinUser, CancellationToken.None);

        // Assert - no library queries should have been made
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        Assert.False(result.Success);
    }

    /// <summary>
    /// Mock HTTP message handler that throws for any request.
    /// Prevents real HTTP calls during tests that return empty items
    /// (and thus skip catalog operations).
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // This should never be reached when GetItemList returns empty items
            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Unexpected HTTP call in test"));
        }
    }
}
