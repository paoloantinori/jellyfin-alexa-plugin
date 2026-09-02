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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
        // A CollectionFolder that WOULD resolve to a physical folder id: if the old
        // ApplyLibraryFilter call were still on this path, TopParentIds would be set.
        var cfId = Guid.NewGuid();
        var physicalId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser();
        user.AllowedLibraryIds = new List<string> { cfId.ToString() };

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var cf = new CollectionFolder { Id = cfId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };
        _libraryManagerMock.Setup(l => l.GetItemById(cfId)).Returns(cf);
        _libraryManagerMock.Setup(l => l.FindByPath("/data/media/music", true))
            .Returns(new Folder { Id = physicalId });

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
}
