#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.DynamicEntities;

/// <summary>
/// Tests for per-user library filtering in DynamicEntityBuilder.Build.
/// Verifies that the allowedLibraryIds parameter correctly filters the Jellyfin query.
/// </summary>
public class DynamicEntityBuilderFilterTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntityBuilderFilterTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private DynamicEntityBuilder CreateBuilder()
    {
        return new DynamicEntityBuilder(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory.CreateLogger<DynamicEntityBuilder>());
    }

    private void SetupUserMock(Guid userId)
    {
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns(jellyfinUser);
    }

    [Fact]
    public void Build_WithAllowedLibraryIds_PassesTopParentIdsToQuery()
    {
        // Arrange
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var libA = Guid.NewGuid();
        var libB = Guid.NewGuid();
        var allowedLibraryIds = new Guid[] { libA, libB };

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        var builder = CreateBuilder();

        // Act
        builder.Build(userId, "it-IT", allowedLibraryIds, CancellationToken.None);

        // Assert: artist, album, and last-played queries should all have TopParentIds set
        Assert.True(capturedQueries.Count >= 2, $"Expected at least 2 queries, got {capturedQueries.Count}");
        Assert.All(capturedQueries, q =>
        {
            Assert.NotNull(q.TopParentIds);
            Assert.Equal(2, q.TopParentIds.Length);
            Assert.Contains(libA, q.TopParentIds);
            Assert.Contains(libB, q.TopParentIds);
        });
    }

    [Fact]
    public void Build_WithNullAllowedLibraryIds_NoTopParentIds()
    {
        // Arrange
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        var builder = CreateBuilder();

        // Act
        builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.True(capturedQueries.Count >= 2, $"Expected at least 2 queries, got {capturedQueries.Count}");
        Assert.All(capturedQueries, q =>
        {
            Assert.Empty(q.TopParentIds);
        });
    }

    [Fact]
    public void Build_WithEmptyAllowedLibraryIds_NoTopParentIds()
    {
        // Arrange
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        var builder = CreateBuilder();

        // Act
        builder.Build(userId, "it-IT", Array.Empty<Guid>(), CancellationToken.None);

        // Assert
        Assert.True(capturedQueries.Count >= 2, $"Expected at least 2 queries, got {capturedQueries.Count}");
        Assert.All(capturedQueries, q =>
        {
            Assert.Empty(q.TopParentIds);
        });
    }

    [Fact]
    public void Build_WithoutAllowedLibraryIdsOverload_NoTopParentIds()
    {
        // Arrange
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        List<InternalItemsQuery> capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(Array.Empty<BaseItem>());

        var builder = CreateBuilder();

        // Act - passing null for allowedLibraryIds
        builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.True(capturedQueries.Count >= 2, $"Expected at least 2 queries, got {capturedQueries.Count}");
        Assert.All(capturedQueries, q =>
        {
            Assert.Empty(q.TopParentIds);
        });
    }
}
