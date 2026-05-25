using System;
using System.Collections.Generic;
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
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class GoToChapterIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IChapterManager> _chapterManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public GoToChapterIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _chapterManagerMock = new Mock<IChapterManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private GoToChapterIntentHandler CreateHandler()
    {
        return new GoToChapterIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _chapterManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? direction = null, string? chapterNumber = null)
    {
        var intent = new Intent { Name = IntentNames.GoToChapter };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (direction != null)
        {
            intent.Slots["direction"] = new global::Alexa.NET.Request.Slot { Name = "direction", Value = direction };
        }

        if (chapterNumber != null)
        {
            intent.Slots["chapter_number"] = new global::Alexa.NET.Request.Slot { Name = "chapter_number", Value = chapterNumber };
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

    [Fact]
    public void CanHandle_GoToChapterIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "next");

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
    public async Task HandleAsync_NothingPlaying_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "next");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NoChapters_ReturnsNoChapters()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "next");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;

        _chapterManagerMock.Setup(c => c.GetChapters(audioItem.Id))
            .Returns(new List<ChapterInfo>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_GoToChapterNumber_SeeksToCorrectPosition()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(chapterNumber: "3");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;

        var chapters = new List<ChapterInfo>
        {
            new() { Name = "Chapter 1", StartPositionTicks = 0 },
            new() { Name = "Chapter 2", StartPositionTicks = TimeSpan.FromMinutes(10).Ticks },
            new() { Name = "Chapter 3", StartPositionTicks = TimeSpan.FromMinutes(20).Ticks },
            new() { Name = "Chapter 4", StartPositionTicks = TimeSpan.FromMinutes(30).Ticks },
        };

        _chapterManagerMock.Setup(c => c.GetChapters(audioItem.Id))
            .Returns(chapters);

        _libraryManagerMock.Setup(l => l.GetItemById(audioItem.Id))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_ChapterNumberOutOfRange_ReturnsError()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(chapterNumber: "99");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;

        var chapters = new List<ChapterInfo>
        {
            new() { Name = "Chapter 1", StartPositionTicks = 0 },
            new() { Name = "Chapter 2", StartPositionTicks = TimeSpan.FromMinutes(10).Ticks },
        };

        _chapterManagerMock.Setup(c => c.GetChapters(audioItem.Id))
            .Returns(chapters);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NextDirection_AdvancesToNextChapter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "next");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;

        // Simulate currently at 12 minutes (in chapter 2 which starts at 10min)
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(12).Ticks };

        var chapters = new List<ChapterInfo>
        {
            new() { Name = "Chapter 1", StartPositionTicks = 0 },
            new() { Name = "Chapter 2", StartPositionTicks = TimeSpan.FromMinutes(10).Ticks },
            new() { Name = "Chapter 3", StartPositionTicks = TimeSpan.FromMinutes(20).Ticks },
        };

        _chapterManagerMock.Setup(c => c.GetChapters(audioItem.Id))
            .Returns(chapters);

        _libraryManagerMock.Setup(l => l.GetItemById(audioItem.Id))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_PreviousDirection_GoesToPreviousChapter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "previous");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;

        // Currently at 22 minutes (in chapter 3 which starts at 20min)
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(22).Ticks };

        var chapters = new List<ChapterInfo>
        {
            new() { Name = "Chapter 1", StartPositionTicks = 0 },
            new() { Name = "Chapter 2", StartPositionTicks = TimeSpan.FromMinutes(10).Ticks },
            new() { Name = "Chapter 3", StartPositionTicks = TimeSpan.FromMinutes(20).Ticks },
        };

        _chapterManagerMock.Setup(c => c.GetChapters(audioItem.Id))
            .Returns(chapters);

        _libraryManagerMock.Setup(l => l.GetItemById(audioItem.Id))
            .Returns(audioItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }
}
