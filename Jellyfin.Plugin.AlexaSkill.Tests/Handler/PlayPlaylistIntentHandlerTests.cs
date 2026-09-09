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

    // JF-526 (JF-508 sibling): BuildPlaylistPlayResponseAsync's site-level FuzzyMatch
    // pre-check (the >1-match branch) returns before HandleFuzzyMiss, so without the
    // shared gate a 2-word partial-coverage hit ("soul coffee" -> "Starfish & Coffee",
    // score 72) auto-played ungated; the gated miss now takes the HandleFuzzyMiss
    // yes/no prompt.
    [Fact]
    public async Task PlayPlaylist_TwoWordPartialCoverageFuzzyHit_PromptsInsteadOfAutoPlaying()
    {
        using var statics = StubBaseItemStatics();
        SetupTwoPlaylistCandidates();

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(playlistName: "soul coffee"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"),
            "the playlist pre-check must not auto-play a partial-coverage short query (JF-526)");
    }

    [Fact]
    public async Task PlayPlaylist_TwoWordFullCoverageFuzzyHit_StillPassesThePreCheck()
    {
        // Counter-case: full coverage keeps the pre-check acceptance. The Folder
        // stand-ins have no resolvable tracks, so the normal flow surfaces the
        // empty-playlist Tell (session-ending, no disambiguation ask), which is only
        // reachable when the pre-check accepted the query.
        using var statics = StubBaseItemStatics();
        SetupTwoPlaylistCandidates();

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateRequest(playlistName: "coffee tv"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory),
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.False(response.SessionAttributes?.ContainsKey("disambig_matches") == true);
    }

    /// <summary>
    /// The shared setup of the two candidate playlists the JF-526 pre-check tests
    /// fuzzy-match against. Tags must be non-null: BaseItem.GetInheritedTags does
    /// AddRange(Tags) with no null guard.
    /// </summary>
    private void SetupTwoPlaylistCandidates()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem>
                {
                    new Folder { Name = "Starfish & Coffee", Id = Guid.NewGuid(), Tags = Array.Empty<string>() },
                    new Folder { Name = "Coffee & TV", Id = Guid.NewGuid(), Tags = Array.Empty<string>() }
                },
                TotalRecordCount = 2
            });
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());
    }

    /// <summary>
    /// The visibility filter inside BuildPlaylistPlayResponseAsync calls
    /// BaseItem.IsVisible, which walks the STATIC BaseItem.LibraryManager and
    /// BaseItem.Logger (unset in the unit-test host; the same off-host limitation
    /// DeviceQueueManagerTests documents for the track-resolution path). Stub both
    /// for the duration of one test and restore them on dispose (the suite runs
    /// sequentially, so the transient static mutation cannot race).
    /// </summary>
    private IDisposable StubBaseItemStatics()
    {
        ILibraryManager? prevLibraryManager = BaseItem.LibraryManager;
        Microsoft.Extensions.Logging.ILogger<BaseItem>? prevLogger = BaseItem.Logger;

        _libraryManagerMock.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns(new List<Folder>());
        BaseItem.LibraryManager = _libraryManagerMock.Object;
        BaseItem.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<BaseItem>.Instance;

        return new RestoreBaseItemStatics(prevLibraryManager, prevLogger);
    }

    private sealed class RestoreBaseItemStatics : IDisposable
    {
        private readonly ILibraryManager? _libraryManager;
        private readonly Microsoft.Extensions.Logging.ILogger<BaseItem>? _logger;

        public RestoreBaseItemStatics(ILibraryManager? libraryManager, Microsoft.Extensions.Logging.ILogger<BaseItem>? logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public void Dispose()
        {
            BaseItem.LibraryManager = _libraryManager;
            BaseItem.Logger = _logger;
        }
    }
}
