using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AlexaSession = global::Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for FollowMeIntentHandler: cross-device playback transfer.
/// </summary>
public class FollowMeIntentHandlerTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly string _tempDir;
    private readonly DeviceQueueManager _queueManager;

    public FollowMeIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();

        // Set up user manager to return a valid Jellyfin user for any ID
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        _tempDir = Path.Combine(Path.GetTempPath(), $"followme-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var qmLogger = _loggerFactory.CreateLogger<DeviceQueueManager>();
        _queueManager = new DeviceQueueManager(_tempDir, qmLogger);
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort
        }

        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Context CreateContext(string deviceId = "test-device")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId }
            }
        };
    }

    private FollowMeIntentHandler CreateHandler()
    {
        return new FollowMeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            _queueManager);
    }

    // =====================================================================
    // CanHandle
    // =====================================================================

    [Fact]
    public void CanHandle_ReturnsTrueForFollowMeIntent()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_ReturnsFalseForOtherIntent()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_ReturnsFalseForNonIntentRequest()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();
        Assert.False(handler.CanHandle(request));
    }

    // =====================================================================
    // No other device playing
    // =====================================================================

    [Fact]
    public async Task FollowMe_NoOtherDevicePlaying_ReturnsNothingPlaying()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        // Only the current device has a queue — nothing on other devices
        _queueManager.SetQueue("device-kitchen", new List<string> { "item1", "item2" }, 0);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FollowMe_NoQueueManager_ReturnsNothingPlaying()
    {
        // Create handler without queue manager
        var handler = new FollowMeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            queueManager: null);

        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Successful follow-me transfer
    // =====================================================================

    [Fact]
    public async Task FollowMe_OtherDevicePlaying_ResumesOnCurrentDevice()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var itemId = Guid.NewGuid();
        var item = new Audio { Id = itemId, Name = "Test Song" };
        item.Artists = new List<string> { "Test Artist" };

        // Set up a queue on the living room device
        _queueManager.SetQueue("device-livingroom", new List<string> { itemId.ToString(), "other-item" }, 0);

        // Set up the library manager to return the item
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Should contain an AudioPlayer directive
        Assert.NotNull(response.Response.Directives);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);

        // Should mention the track name in the speech output
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Test Song", text);

        // The queue should now be on the kitchen device
        var kitchenQueue = _queueManager.GetOrCreateQueue("device-kitchen");
        Assert.Equal(2, kitchenQueue.ItemIds.Count);
        Assert.Equal(0, kitchenQueue.CurrentIndex);
        Assert.Equal(itemId.ToString(), kitchenQueue.ItemIds[0]);

        // The source device queue should be cleared
        Assert.Empty(_queueManager.GetAllActiveQueues("device-kitchen"));
    }

    [Fact]
    public async Task FollowMe_PicksMostRecentlyActiveQueue()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var recentItemId = Guid.NewGuid();
        var olderItemId = Guid.NewGuid();
        var recentItem = new Audio { Id = recentItemId, Name = "Recent Song" };
        var olderItem = new Audio { Id = olderItemId, Name = "Older Song" };

        // Set up two devices: older one first, then a more recent one
        _queueManager.SetQueue("device-bedroom", new List<string> { olderItemId.ToString() }, 0);

        // Slightly later, set up the living room queue
        _queueManager.SetQueue("device-livingroom", new List<string> { recentItemId.ToString() }, 0);

        _libraryManagerMock.Setup(l => l.GetItemById(recentItemId)).Returns(recentItem);
        _libraryManagerMock.Setup(l => l.GetItemById(olderItemId)).Returns(olderItem);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Should pick the most recently modified queue (livingroom)
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Recent Song", text);
    }

    [Fact]
    public async Task FollowMe_DoesNotPickCurrentDeviceQueue()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var kitchenItemId = Guid.NewGuid();
        var livingRoomItemId = Guid.NewGuid();
        var livingRoomItem = new Audio { Id = livingRoomItemId, Name = "Living Room Song" };

        // Set up queues on both the current device and another device
        _queueManager.SetQueue("device-kitchen", new List<string> { kitchenItemId.ToString() }, 0);
        _queueManager.SetQueue("device-livingroom", new List<string> { livingRoomItemId.ToString() }, 0);

        _libraryManagerMock.Setup(l => l.GetItemById(livingRoomItemId)).Returns(livingRoomItem);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Should pick the living room queue, not the kitchen's own queue
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("Living Room Song", text);
    }

    // =====================================================================
    // Edge cases
    // =====================================================================

    [Fact]
    public async Task FollowMe_SourceQueueClearedAfterTransfer()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var itemId = Guid.NewGuid();
        var item = new Audio { Id = itemId, Name = "Song" };

        _queueManager.SetQueue("device-livingroom", new List<string> { itemId.ToString() }, 0);
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Source device should have no active queue anymore
        var sourceQueues = _queueManager.GetAllActiveQueues(excludeDeviceId: "device-kitchen");
        Assert.Empty(sourceQueues);
    }

    [Fact]
    public async Task FollowMe_TransfersRepeatAndShuffleSettings()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var itemId = Guid.NewGuid();
        var item = new Audio { Id = itemId, Name = "Song" };

        _queueManager.SetQueue("device-livingroom", new List<string> { itemId.ToString() }, 0, "All", "Shuffle");
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var kitchenQueue = _queueManager.GetOrCreateQueue("device-kitchen");
        Assert.Equal("All", kitchenQueue.RepeatMode);
        Assert.Equal("Shuffle", kitchenQueue.PlaybackOrder);
    }

    [Fact]
    public async Task FollowMe_UpdatesSessionNowPlayingItem()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var itemId = Guid.NewGuid();
        var item = new Audio { Id = itemId, Name = "Song" };

        _queueManager.SetQueue("device-livingroom", new List<string> { itemId.ToString() }, 0);
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(itemId, session.FullNowPlayingItem.Id);
    }

    // =====================================================================
    // DeviceQueueManager.GetAllActiveQueues tests
    // =====================================================================

    [Fact]
    public void GetAllActiveQueues_ExcludesEmptyQueues()
    {
        _queueManager.GetOrCreateQueue("device-empty"); // empty queue
        _queueManager.SetQueue("device-active", new List<string> { "item1" }, 0);

        var result = _queueManager.GetAllActiveQueues();
        Assert.Single(result);
        Assert.Equal("device-active", result[0].DeviceId);
    }

    [Fact]
    public void GetAllActiveQueues_ExcludesSpecifiedDevice()
    {
        _queueManager.SetQueue("device-A", new List<string> { "item1" }, 0);
        _queueManager.SetQueue("device-B", new List<string> { "item2" }, 0);

        var result = _queueManager.GetAllActiveQueues(excludeDeviceId: "device-A");
        Assert.Single(result);
        Assert.Equal("device-B", result[0].DeviceId);
    }

    [Fact]
    public void GetAllActiveQueues_ExcludesQueuesWithNegativeIndex()
    {
        var queue = _queueManager.GetOrCreateQueue("device-neg");
        queue.ItemIds = new List<string> { "item1" };
        queue.CurrentIndex = -1; // no current item

        _queueManager.SetQueue("device-ok", new List<string> { "item2" }, 0);

        var result = _queueManager.GetAllActiveQueues();
        Assert.Single(result);
        Assert.Equal("device-ok", result[0].DeviceId);
    }
}
