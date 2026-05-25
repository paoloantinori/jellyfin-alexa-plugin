using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
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
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayFavoritesIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayFavoritesIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayFavoritesIntentHandler CreateHandler()
    {
        return new PlayFavoritesIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? username = null)
    {
        var intent = new Intent { Name = IntentNames.PlayFavorites };
        intent.Slots = new Dictionary<string, Slot>();

        if (username != null)
        {
            intent.Slots["username"] = new Slot { Name = "username", Value = username };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
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
    public void CanHandle_PlayFavoritesIntent_ReturnsTrue()
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
    public async Task HandleAsync_NoUsernameSlot_PlaysAuthenticatedUserFavorites()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(); // no username slot
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audioItem = CreateTestAudio("Favorite Song", Guid.NewGuid());
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();

        // Verify the query used the session user (not a different user)
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.IsFavorite == true)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NoFavoriteItems_ReturnsNoFavoriteItemsMessage()
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
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("favorite", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithExactUsernameMatch_PlaysThatUserFavorites()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(username: "Paolo");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Setup plugin user "Paolo" in config
        var paoloPluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(paoloPluginUser);

        var paoloJellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(paoloPluginUser.Id))
            .Returns(paoloJellyfinUser);

        var audioItem = CreateTestAudio("Paolo's Favorite Song", Guid.NewGuid());
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();

        // Verify the query used Paolo's Jellyfin user (exact match)
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.User == paoloJellyfinUser && q.IsFavorite == true)), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithFuzzyUsernameMatch_PlaysMatchedUserFavorites()
    {
        var handler = CreateHandler();
        // "Paulo" is a close fuzzy match for "Paolo"
        var request = CreateIntentRequest(username: "Paulo");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Setup plugin user "Paolo" in config
        var paoloPluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(paoloPluginUser);

        var paoloJellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(paoloPluginUser.Id))
            .Returns(paoloJellyfinUser);

        var audioItem = CreateTestAudio("Paolo's Favorite Song", Guid.NewGuid());
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_WithUnknownUsername_ReturnsUserNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(username: "NonExistentUser");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Setup a plugin user but with a very different name
        var otherPluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(otherPluginUser);

        var otherJellyfinUser = new Jellyfin.Database.Implementations.Entities.User("CompletelyDifferentName", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(otherPluginUser.Id))
            .Returns(otherJellyfinUser);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("NonExistentUser", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithUsernameNoPluginUsers_ReturnsUserNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(username: "Someone");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // No plugin users configured
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("Someone", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithCaseInsensitiveMatch_PlaysThatUserFavorites()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(username: "paolo"); // lowercase
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Setup plugin user with capitalized name
        var paoloPluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(paoloPluginUser);

        var paoloJellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(paoloPluginUser.Id))
            .Returns(paoloJellyfinUser);

        var audioItem = CreateTestAudio("Favorite Song", Guid.NewGuid());
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();

        // Verify exact match was used (case-insensitive)
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.User == paoloJellyfinUser)), Times.Once);
    }

    [Fact]
    public void ResolveUserByName_ExactMatch_ReturnsUser()
    {
        var handler = CreateHandler();

        var pluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(pluginUser);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(jellyfinUser);

        var result = handler.ResolveUserByName("Paolo");

        Assert.NotNull(result);
        Assert.Equal("Paolo", result.Username);
    }

    [Fact]
    public void ResolveUserByName_CaseInsensitiveMatch_ReturnsUser()
    {
        var handler = CreateHandler();

        var pluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(pluginUser);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(jellyfinUser);

        var result = handler.ResolveUserByName("paolo");

        Assert.NotNull(result);
        Assert.Equal("Paolo", result.Username);
    }

    [Fact]
    public void ResolveUserByName_FuzzyMatch_ReturnsUser()
    {
        var handler = CreateHandler();

        var pluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(pluginUser);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(jellyfinUser);

        var result = handler.ResolveUserByName("Paulo");

        Assert.NotNull(result);
        Assert.Equal("Paolo", result.Username);
    }

    [Fact]
    public void ResolveUserByName_NoMatch_ReturnsNull()
    {
        var handler = CreateHandler();

        var pluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        _config.Users.Add(pluginUser);

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("Paolo", "auth", "provider");
        _userManagerMock.Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(jellyfinUser);

        var result = handler.ResolveUserByName("Xyzqwerty");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveUserByName_NoUsers_ReturnsNull()
    {
        var handler = CreateHandler();

        var result = handler.ResolveUserByName("Paolo");

        Assert.Null(result);
    }

    private static Audio CreateTestAudio(string name, Guid id)
    {
        return new Audio
        {
            Name = name,
            Id = id,
        };
    }
}
