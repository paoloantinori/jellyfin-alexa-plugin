using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class QueryRecentlyAddedIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public QueryRecentlyAddedIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private QueryRecentlyAddedIntentHandler CreateHandler()
    {
        return new QueryRecentlyAddedIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.QueryRecentlyAdded },
            Locale = "en-US",
            RequestId = "test-req"
        };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    /// <summary>
    /// Ensure APL visuals are enabled so "WithApl" tests pass regardless of
    /// static Plugin.Instance state left by other test classes running in parallel.
    /// </summary>
    private static void EnsureVisualsEnabled()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.AplVisualsEnabled = true;
        }
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public void CanHandle_QueryRecentlyAddedIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoItems_ReturnsEmptyMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("recently added", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithAudioItems_ReturnsNumberedList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audioItem = new Audio
        {
            Name = "Abbey Road",
            Id = Guid.NewGuid(),
            Artists = new List<string> { "The Beatles" }
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Abbey Road", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The Beatles", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.", speech);
    }

    [Fact]
    public async Task HandleAsync_WithVideoItems_ReturnsListWithoutArtist()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var movie = new Movie
        {
            Name = "Inception",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Inception", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_MixedAudioAndVideo_ReturnsCombinedList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio
        {
            Name = "Song A",
            Id = Guid.NewGuid(),
            Artists = new List<string> { "Artist X" }
        };
        var movie = new Movie
        {
            Name = "Movie B",
            Id = Guid.NewGuid()
        };
        var episode = new Episode
        {
            Name = "Episode C",
            Id = Guid.NewGuid(),
            SeriesName = "Series Y"
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio, movie, episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Song A", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Artist X", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Movie B", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Episode C", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.", speech);
        Assert.Contains("2.", speech);
        Assert.Contains("3.", speech);
    }

    [Fact]
    public async Task HandleAsync_ContainsPromptToPickNumber()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Artists = new List<string> { "Test Artist" }
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("number", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ShortList_KeepsSessionOpenForSelection()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // 3 items — fits in one voice page (≤5), so non-truncated path
        var audio = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Artists = new List<string> { "Test Artist" }
        };
        var movie = new Movie
        {
            Name = "Test Movie",
            Id = Guid.NewGuid()
        };
        var episode = new Episode
        {
            Name = "Test Episode",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio, movie, episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // List prompts user to "say the number to play one" — session must stay open
        TestHelpers.AssertSessionOpen(response, "Non-truncated list should keep session open so user can pick an item");
        Assert.NotNull(response.Response?.Reprompt);
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_TruncatedList_KeepsSessionOpen()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            items.Add(new Audio { Name = $"Song {i + 1}", Id = Guid.NewGuid(), Artists = new List<string> { $"Artist {i + 1}" } });
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        TestHelpers.AssertSessionOpen(response, "Truncated list should keep session open");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Fact]
    public async Task HandleAsync_QueriesByDateCreatedDescending()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.OrderBy);
        Assert.Equal(10, capturedQuery.Limit);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.True(capturedQuery.IncludeItemTypes.Length >= 3);
    }

    [Fact]
    public async Task HandleAsync_WithResults_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();

        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = TestHelpers.CreateContextWithApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var movie = new Movie { Name = "Test Movie", Id = Guid.NewGuid() };
        var episode = new Episode { Name = "Test Episode", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie, episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task HandleAsync_WithResults_WithoutApl_NoAplDirective()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var movie = new Movie { Name = "Test Movie", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }
}
