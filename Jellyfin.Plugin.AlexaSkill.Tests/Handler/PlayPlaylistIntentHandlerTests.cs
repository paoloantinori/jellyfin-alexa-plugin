#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-455: the playlist query must NOT carry the per-user library filter. Playlists
/// are user-scoped (query.User gates visibility) and native playlists live outside any
/// media library, so any TopParentIds restriction excluded them all for restricted users.
/// </summary>
[Collection("Plugin")]
public class PlayPlaylistIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayPlaylistIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayPlaylistIntentHandler CreateHandler()
    {
        return new PlayPlaylistIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateRequest(string playlistName = "road trip songs")
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.PlayPlaylist,
                Slots = new Dictionary<string, Slot>
                {
                    ["playlist"] = new Slot { Name = "playlist", Value = playlistName }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };
    }

    [Fact]
    public async Task PlayPlaylist_RestrictedUser_DoesNotSetTopParentIdsOnQuery()
    {
        // A library-restricted plugin user: if ApplyLibraryFilter were still on this
        // path (primary query or fuzzy fallback), the resolved union would land in
        // TopParentIds and exclude native playlists entirely.
        var user = TestHelpers.CreateTestUser();
        user.AllowedLibraryIds = new List<string> { Guid.NewGuid().ToString() };

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        InternalItemsQuery? primaryQuery = null;
        InternalItemsQuery? fuzzyQuery = null;
        _libraryManagerMock
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => primaryQuery = q)
            .Returns(new QueryResult<BaseItem>());
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => fuzzyQuery = q)
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(),
            TestHelpers.CreateTestContext(),
            user,
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(primaryQuery);
        Assert.Empty(primaryQuery.TopParentIds); // no library filter on the playlist query

        Assert.NotNull(fuzzyQuery);
        Assert.Empty(fuzzyQuery.TopParentIds); // nor on the fuzzy fallback
    }

    [Fact]
    public async Task PlayPlaylist_PlaylistNotVisibleToUser_IsFilteredOut()
    {
        // GetItemsResult goes straight to the repository without the IsVisible
        // post-filter GetItemList applies, so with the library filter removed the
        // primary query alone could surface another user's private playlist. The
        // handler must post-filter IsVisible and answer not-found when nothing
        // visible remains (JF-455 review hardening).
        var user = TestHelpers.CreateTestUser();

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var hidden = new Mock<Playlist> { CallBase = true };
        hidden.Object.Name = "road trip songs";
        hidden.Object.Id = Guid.NewGuid();
        hidden.Setup(p => p.IsVisible(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
                It.IsAny<bool>()))
            .Returns(false);

        _libraryManagerMock
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem> { hidden.Object }, TotalRecordCount = 1 });
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>()); // fuzzy fallback finds nothing either

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(),
            TestHelpers.CreateTestContext(),
            user,
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        // The invisible playlist must be neither played nor announced: the clean
        // not-found tell proves the post-filter dropped it before any handling.
        Assert.Equal(
            ResponseStrings.Get("NotFoundPlaylist", "en-US", "road trip songs"),
            TestHelpers.GetSpeechText(response));
    }
}
