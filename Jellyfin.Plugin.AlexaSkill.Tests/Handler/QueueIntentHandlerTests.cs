using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class QueueIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public QueueIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public void ClearQueue_CanHandle_ReturnsTrue()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void ClearQueue_CanHandle_ReturnsFalseForOtherIntent()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task ClearQueue_WithPlayingItem_KeepsCurrentItem()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();

        var currentItemId = Guid.NewGuid();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentItemId, Name = "Current Song" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentItemId },
            new() { Id = Guid.NewGuid() },
            new() { Id = Guid.NewGuid() }
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(currentItemId, session.NowPlayingQueue[0].Id);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("cleared", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearQueue_WithNoPlayingItem_ClearsAll()
    {
        var handler = new ClearQueueIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = Guid.NewGuid() },
            new() { Id = Guid.NewGuid() }
        };

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ClearQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Empty(session.NowPlayingQueue);
    }

    [Fact]
    public void ListQueue_CanHandle_ReturnsTrue()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task ListQueue_EmptyQueue_ReturnsEmptyMessage()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem>();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("empty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListQueue_WithUpcomingItems_ListsNames()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);
        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        session.FullNowPlayingItem = new MediaBrowser.Controller.Entities.Audio.Audio { Id = currentId, Name = "Current" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentId },
            new() { Id = nextId }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(nextId))
            .Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = nextId, Name = "Next Song" });

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "ListQueueIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Next Song", text);
    }

    [Fact]
    public void AddToQueue_CanHandle_ReturnsTrue()
    {
        var handler = new AddToQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "AddToQueueIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void PlayNext_CanHandle_ReturnsTrue()
    {
        var handler = new PlayNextIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "PlayNextIntent" } };
        Assert.True(handler.CanHandle(request));
    }
}
