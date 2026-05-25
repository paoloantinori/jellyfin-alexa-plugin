using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
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
/// Tests that PlaySongIntentHandler uses ASR compound-word fallback via SearchWithAsrFallbackAsync.
/// Simulates the scenario where "lazy bones" (two words from ASR) finds nothing but the
/// joined variant "lazybones" returns a result.
/// </summary>
[Collection("Plugin")]
public class PlaySongAsrFallbackTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlaySongAsrFallbackTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlaySongIntentHandler CreateHandler()
    {
        return new PlaySongIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateSongIntentRequest(string song)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public async Task PlaySong_AsrFallback_JoinedVariantFindsSong_ReturnsPlayback()
    {
        // Arrange: "lazy bones" returns empty, but "lazybones" returns a song
        var song = new Audio { Name = "Lazybones", Id = Guid.NewGuid() };

        SetupUserMock();

        var searchTerms = new List<string>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns<InternalItemsQuery>(q =>
                string.Equals(q.SearchTerm, "lazybones", StringComparison.OrdinalIgnoreCase)
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>());

        _config.AsrCompoundWordFixEnabled = true;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazy bones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: playback started (has AudioPlayer directive)
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);

        // The handler tried "lazy bones" first, then "lazybones" via ASR fallback
        Assert.True(searchTerms.Count >= 2,
            $"Expected at least 2 search calls, got {searchTerms.Count}: {string.Join(", ", searchTerms)}");
        Assert.Equal("lazy bones", searchTerms[0]);
        Assert.Equal("lazybones", searchTerms[1]);
    }

    [Fact]
    public async Task PlaySong_AsrFallbackDisabled_OriginalNotFound_ReturnsNotFound()
    {
        // Arrange: feature disabled — should NOT try variants
        SetupUserMock();

        var searchTerms = new List<string>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns(new List<BaseItem>());

        _config.AsrCompoundWordFixEnabled = false;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazy bones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: song not found, session ended
        Assert.True(response.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("lazybones", speech);

        // Only one search call (original "lazy bones"), no variants
        Assert.Single(searchTerms);
        Assert.Equal("lazy bones", searchTerms[0]);
    }

    [Fact]
    public async Task PlaySong_AsrFallback_OriginalAlreadyFound_NoVariantsTried()
    {
        // Arrange: original query finds the song — no ASR fallback needed
        var song = new Audio { Name = "Lazy Bones", Id = Guid.NewGuid() };

        SetupUserMock();

        var searchTerms = new List<string>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns(new List<BaseItem> { song });

        _config.AsrCompoundWordFixEnabled = true;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazy bones");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: playback started
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // Only the original search — no ASR variants attempted
        Assert.Single(searchTerms);
        Assert.Equal("lazy bones", searchTerms[0]);
    }
}
