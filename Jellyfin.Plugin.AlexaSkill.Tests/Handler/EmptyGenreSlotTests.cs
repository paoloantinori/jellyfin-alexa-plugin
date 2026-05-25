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
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for truncated utterances where the genre slot arrives as empty/whitespace string.
/// Same class of bug as EmptyMusicianSlotTests — IsNullOrEmpty instead of IsNullOrWhiteSpace
/// on an optional slot that modifies a search query.
/// </summary>
[Collection("Plugin")]
public class EmptyGenreSlotTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public EmptyGenreSlotTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    // ── PlayRandomIntentHandler ──────────────────────────────────────────────

    private PlayRandomIntentHandler CreateRandomHandler()
    {
        return new PlayRandomIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateRandomIntentRequest(string? genre)
    {
        var intent = new Intent { Name = IntentNames.PlayRandom };
        intent.Slots = new Dictionary<string, Slot>();

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public async Task PlayRandom_EmptyGenreSlot_SkipsGenreFilter()
    {
        var song = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateRandomHandler();
        var request = CreateRandomIntentRequest(genre: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should play results without genre filtering
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // Verify Genres was NOT set on the query
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.Genres == null || q.Genres.Count == 0)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PlayRandom_WhitespaceGenreSlot_SkipsGenreFilter()
    {
        var song = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateRandomHandler();
        var request = CreateRandomIntentRequest(genre: "   ");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.Genres == null || q.Genres.Count == 0)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PlayRandom_ValidGenreSlot_AppliesGenreFilter()
    {
        var song = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateRandomHandler();
        var request = CreateRandomIntentRequest(genre: "rock");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.Genres != null && q.Genres.Any(g => g == "rock"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PlayRandom_EmptyGenreSlot_NotFound_ReportsGenericNotFound()
    {
        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateRandomHandler();
        var request = CreateRandomIntentRequest(genre: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        // Should NOT mention a genre name
        Assert.DoesNotContain("  ", speech);
    }

    // ── PlayByDecadeIntentHandler ────────────────────────────────────────────

    private PlayByDecadeIntentHandler CreateDecadeHandler()
    {
        return new PlayByDecadeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateDecadeIntentRequest(string decade, string? genre)
    {
        var intent = new Intent { Name = IntentNames.PlayByDecade };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["decade"] = new Slot { Name = "decade", Value = decade }
        };

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public async Task PlayByDecade_EmptyGenreSlot_SkipsGenreFilter()
    {
        var song = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateDecadeHandler();
        var request = CreateDecadeIntentRequest("the nineties", genre: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.Genres == null || q.Genres.Count == 0)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PlayByDecade_WhitespaceGenreSlot_SkipsGenreFilter()
    {
        var song = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateDecadeHandler();
        var request = CreateDecadeIntentRequest("the eighties", genre: "   ");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.Genres == null || q.Genres.Count == 0)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PlayByDecade_ValidGenreSlot_AppliesGenreFilter()
    {
        var song = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateDecadeHandler();
        var request = CreateDecadeIntentRequest("the nineties", genre: "rock");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(
            q => q.Genres != null && q.Genres.Any(g => g == "rock"))), Times.AtLeastOnce);
    }
}
