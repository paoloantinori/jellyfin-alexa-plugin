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

using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for the DynamicEntityBuilder output cache.
/// Verifies that repeated Build() calls with identical parameters return cached results
/// without hitting the database, and that cache invalidation/eviction works correctly.
/// </summary>
public class DynamicEntityBuilderCacheTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntityBuilderCacheTests()
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

    private void SetupUserAndLibrary(Guid userId, List<BaseItem> artists, List<BaseItem> albums)
    {
        var jellyfinUser = new User("testuser", "test", "test");
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns(jellyfinUser);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(artists);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(albums);
    }

    [Fact]
    public void Build_SecondCallWithinTtl_ReturnsCachedResult_WithoutDbQuery()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        SetupUserAndLibrary(userId, artists, []);

        using var builder = CreateBuilder();
        builder.InvalidateCache(); // Ensure clean state

        // Act — first call (cache miss, hits DB)
        var result1 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Act — second call (cache hit, no DB query)
        var result2 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);

        // Same reference — returned from cache
        Assert.Same(result1, result2);

        // DB was queried exactly once for artists (the first call).
        // The second call returns cached result without any DB call.
        _libraryManagerMock.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))),
            Times.Once);
    }

    [Fact]
    public void Build_DifferentParameters_SeparateCacheEntries()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        SetupUserAndLibrary(userId, artists, []);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act — different locales should produce separate cache entries
        var resultIt = builder.Build(userId, "it-IT", null, CancellationToken.None);
        var resultEn = builder.Build(userId, "en-US", null, CancellationToken.None);

        // Assert
        Assert.NotNull(resultIt);
        Assert.NotNull(resultEn);

        // Different directive objects (different cache keys)
        Assert.NotSame(resultIt, resultEn);

        // Artist DB was called twice (once per locale)
        _libraryManagerMock.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))),
            Times.Exactly(2));
    }

    [Fact]
    public void Build_DifferentIncludeFlags_SeparateCacheEntries()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        SetupUserAndLibrary(userId, artists, []);

        // Also setup Series query to return results for the includeSeries=true call
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.Series))))
            .Returns(new List<BaseItem>
            {
                new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Breaking Bad", Id = Guid.NewGuid() }
            });

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act — includeSeries=false vs true should be different cache keys
        var resultBase = builder.Build(userId, "it-IT", null, false, false, CancellationToken.None);
        var resultSeries = builder.Build(userId, "it-IT", null, true, false, CancellationToken.None);

        // Assert
        Assert.NotNull(resultBase);
        Assert.NotNull(resultSeries);
        Assert.NotSame(resultBase, resultSeries);
    }

    [Fact]
    public void InvalidateCache_SubsequentBuildQueriesDbAgain()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        SetupUserAndLibrary(userId, artists, []);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act — first call
        var result1 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Invalidate cache
        builder.InvalidateCache();

        // Second call should query DB again
        var result2 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);

        // Different objects (cache was invalidated)
        Assert.NotSame(result1, result2);

        // Artist DB was queried twice
        _libraryManagerMock.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))),
            Times.Exactly(2));
    }

    [Fact]
    public void Build_DifferentUsers_SeparateCacheEntries()
    {
        // Arrange
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };

        var jellyfinUser1 = new User("user1", "test", "test");
        var jellyfinUser2 = new User("user2", "test", "test");

        _userManagerMock.Setup(um => um.GetUserById(user1)).Returns(jellyfinUser1);
        _userManagerMock.Setup(um => um.GetUserById(user2)).Returns(jellyfinUser2);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(artists);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act
        var result1 = builder.Build(user1, "it-IT", null, CancellationToken.None);
        var result2 = builder.Build(user2, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotSame(result1, result2);
    }

    [Fact]
    public void Build_NullResult_NotCached()
    {
        // Arrange — empty library returns null
        var userId = Guid.NewGuid();
        SetupUserAndLibrary(userId, [], []);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act — first call returns null (no items)
        var result1 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Second call — should query DB again since nulls aren't cached
        var result2 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.Null(result1);
        Assert.Null(result2);

        // Artist DB was queried twice (null result not cached)
        _libraryManagerMock.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))),
            Times.Exactly(2));
    }

    [Fact]
    public void Build_DbCallCount_FirstCallHitsDb_SecondUsesCache()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        var albums = new List<BaseItem>
        {
            new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() }
        };
        SetupUserAndLibrary(userId, artists, albums);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act — first Build(): should query DB for artists, albums, and last-played
        var result1 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Count DB calls from first Build
        int firstCallCount = _libraryManagerMock.Invocations.Count(i =>
            i.Method.Name == "GetItemList");

        // Second Build(): should NOT query DB (cache hit)
        var result2 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Count total DB calls after second Build
        int totalCallCount = _libraryManagerMock.Invocations.Count(i =>
            i.Method.Name == "GetItemList");

        // Assert
        Assert.NotNull(result1);
        Assert.Same(result1, result2);

        // Before: first Build() made N DB calls (artists + albums + last-played >= 3)
        Assert.True(firstCallCount >= 3,
            $"First Build() should make at least 3 DB calls, made {firstCallCount}");

        // After: second Build() made 0 additional DB calls
        Assert.Equal(firstCallCount, totalCallCount);
    }

    [Fact]
    public void Build_CacheHitReturnsSameDirectiveContent()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artistId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Pink Floyd", Id = artistId }
        };
        SetupUserAndLibrary(userId, artists, []);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act
        var result1 = builder.Build(userId, "it-IT", null, CancellationToken.None);
        var result2 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert — cached result has identical content
        Assert.NotNull(result1);
        Assert.Same(result1, result2);
        Assert.Equal("AMAZON.Musician", result2.Types[0].Name);
        Assert.Equal("Pink Floyd", result2.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void Build_LibraryChangeEvent_InvalidatesCache()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        SetupUserAndLibrary(userId, artists, []);

        using var builder = CreateBuilder();
        builder.InvalidateCache();

        // Act — first call (populates cache)
        var result1 = builder.Build(userId, "it-IT", null, CancellationToken.None);
        Assert.NotNull(result1);

        // Simulate a library change event (e.g., an album was added)
        var eventArgs = new ItemChangeEventArgs
        {
            Item = new MusicAlbum { Name = "New Album", Id = Guid.NewGuid() }
        };
        _libraryManagerMock.Raise(
            lm => lm.ItemAdded += null,
            _libraryManagerMock.Object,
            eventArgs);

        // Second call — cache should have been invalidated by the event
        var result2 = builder.Build(userId, "it-IT", null, CancellationToken.None);

        // Assert
        Assert.NotNull(result2);
        Assert.NotSame(result1, result2);

        // DB was queried twice (cache invalidated by library event)
        _libraryManagerMock.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))),
            Times.Exactly(2));
    }
}
