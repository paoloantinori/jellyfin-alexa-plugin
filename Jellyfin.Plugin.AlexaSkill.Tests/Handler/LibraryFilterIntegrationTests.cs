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
[Collection("Plugin")]
public class LibraryFilterIntegrationTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture(configure: c => c.AsrCompoundWordFixEnabled = false);

    private PlaySongIntentHandler CreatePlaySongHandler()
    {
        return new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private SearchMediaIntentHandler CreateSearchMediaHandler()
    {
        return new SearchMediaIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
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

    [Fact]
    public async Task PlaySongHandler_WithAllowedLibraryIds_SetsTopParentIdsOnQuery()
    {
        // Arrange
        var musicLibId = Guid.NewGuid();
        var user = CreateUserWithLibraries(new List<string> { musicLibId.ToString() });
        var handler = CreatePlaySongHandler();
        var request = CreatePlaySongRequest("Bohemian Rhapsody");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audio = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };
        InternalItemsQuery? capturedQuery = null;

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { audio });

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert (membership, not exact length: ResolveTopParentIds may union in
        // physical folder ids, JF-456 item 9)
        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.TopParentIds);
        Assert.Contains(musicLibId, capturedQuery.TopParentIds);
    }

    [Fact]
    public async Task SearchMediaHandler_WithAllowedLibraryIds_SetsTopParentIdsOnLibraryScopedQuery()
    {
        // Arrange
        var musicLibId = Guid.NewGuid();
        var movieLibId = Guid.NewGuid();
        var user = CreateUserWithLibraries(new List<string> { musicLibId.ToString(), movieLibId.ToString() });
        var handler = CreateSearchMediaHandler();
        var request = CreateSearchMediaRequest("Star Wars");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Star Wars",
            Id = Guid.NewGuid()
        };
        var capturedQueries = new List<InternalItemsQuery>();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(new List<BaseItem> { movie });

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: at least one library-scoped query carries the restriction (the
        // movie result keeps the count above the artist-fallback threshold; membership,
        // not exact length, JF-456 item 9). The out-of-library sibling (playlists,
        // JF-456) must NOT carry TopParentIds.
        Assert.Contains(capturedQueries, q =>
            q.TopParentIds?.Contains(musicLibId) == true
            && q.TopParentIds.Contains(movieLibId));
        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Playlist)
            && (q.TopParentIds == null || q.TopParentIds.Length == 0));
    }

    [Fact]
    public async Task PlaySongHandler_WithNullAllowedLibraryIds_DoesNotSetTopParentIds()
    {
        // Arrange
        var user = CreateUserWithLibraries(null);
        var handler = CreatePlaySongHandler();
        var request = CreatePlaySongRequest("Test Song");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
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
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        // Act
        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert - TopParentIds is not set; InternalItemsQuery initializes it to empty array.
        Assert.NotNull(capturedQuery);
        Assert.Empty(capturedQuery.TopParentIds);
    }
}
