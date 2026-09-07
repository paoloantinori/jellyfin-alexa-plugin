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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
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
[Collection("Plugin")]
public class FollowMeIntentHandlerTests : PluginTestBase, IDisposable
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

    /// <summary>
    /// Documents the by-design offset-0 limitation: follow-me resumes the current item
    /// from the beginning, NOT at the saved playback position. DeviceQueueManager tracks
    /// per-item resume position, not a cross-device transfer offset. If this assertion
    /// ever fails, either the limitation was lifted (update docs and release notes) or a
    /// regression silently added a bogus offset.
    /// </summary>
    [Fact]
    public async Task FollowMe_ResumesAtOffsetZero_ByDesign()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var itemId = Guid.NewGuid();
        var item = new Audio { Id = itemId, Name = "Song" };

        _queueManager.SetQueue("device-livingroom", new List<string> { itemId.ToString() }, 0);
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var playDirective = Assert.IsType<AudioPlayerPlayDirective>(
            response.Response.Directives!.First(d => d is AudioPlayerPlayDirective));
        Assert.Equal(0, playDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// When the source queue references an item Jellyfin can't resolve (deleted media,
    /// stale queue), the handler returns MediaNotFound rather than crashing, and does
    /// NOT clear the source queue (the transfer did not complete, so the source survives).
    /// </summary>
    [Fact]
    public async Task FollowMe_SourceItemMissing_ReturnsMediaNotFoundAndKeepsSource()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var itemId = Guid.NewGuid();
        _queueManager.SetQueue("device-livingroom", new List<string> { itemId.ToString() }, 0);
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns((BaseItem?)null);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // No AudioPlayer directive when the item can't be resolved.
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d is AudioPlayerPlayDirective);
        // Source queue must survive: the transfer did not complete.
        Assert.NotEmpty(_queueManager.GetAllActiveQueues(excludeDeviceId: "device-kitchen"));
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

    // =====================================================================
    // JF-270 acceptance criteria (spec-exact)
    //
    // Moq CANNOT intercept DeviceQueueManager (DeviceQueueManager.cs: sealed class,
    // non-virtual members), so the AC's "mock DeviceQueueManager / mock verification"
    // is implemented against the real instance: GetAllActiveQueues inputs are set up
    // through SetQueue + explicitly pinned LastModifiedUtc, and SetQueue/Clear calls
    // are verified from the manager's observable post-state (a pre-call null-check on
    // the target device's queue makes each assertion non-vacuous: only SetQueue can
    // create it, only Clear can remove it).
    // =====================================================================

    /// <summary>
    /// JF-270 AC #1: with multiple other-device queues, the handler plays the MOST
    /// RECENTLY MODIFIED queue's CURRENT item (not the first item). LastModifiedUtc is
    /// pinned explicitly (SetQueue stamps DateTime.UtcNow; back-to-back calls can
    /// collide within one clock tick).
    /// </summary>
    [Fact]
    public async Task FollowMe_MultipleOtherDeviceQueues_PlaysMostRecentlyModifiedQueueCurrentItem()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var olderItemId = Guid.NewGuid();
        var laterFirstItemId = Guid.NewGuid();
        var laterCurrentItemId = Guid.NewGuid();
        var laterCurrentItem = new Audio { Id = laterCurrentItemId, Name = "Later Current Song" };

        _queueManager.SetQueue("device-bedroom", new List<string> { olderItemId.ToString() }, 0);
        _queueManager.SetQueue("device-livingroom", new List<string> { laterFirstItemId.ToString(), laterCurrentItemId.ToString() }, 1);
        _queueManager.GetQueue("device-bedroom")!.LastModifiedUtc = DateTime.UtcNow.AddMinutes(-10);
        _queueManager.GetQueue("device-livingroom")!.LastModifiedUtc = DateTime.UtcNow;

        _libraryManagerMock.Setup(l => l.GetItemById(laterCurrentItemId)).Returns(laterCurrentItem);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var playDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!.Where(d => d is AudioPlayerPlayDirective)));

        // The played item is the LATER queue's CURRENT item (index 1), not the older
        // queue's item and not the later queue's first item.
        Assert.Equal(laterCurrentItemId.ToString(), playDirective.AudioItem.Stream.Token);
        Assert.Contains($"/Audio/{laterCurrentItemId}/stream", playDirective.AudioItem.Stream.Url, StringComparison.Ordinal);

        // Independent selection signal: only the winning source queue gets cleared.
        Assert.Null(_queueManager.GetQueue("device-livingroom"));
        Assert.NotNull(_queueManager.GetQueue("device-bedroom"));
    }

    /// <summary>
    /// JF-270 AC #2: when GetAllActiveQueues returns no OTHER-device queues (only the
    /// current device has an active queue), the response speaks the localized
    /// FollowMeNothingPlaying for the request locale. it-IT is pinned so the assert
    /// cannot pass via the en-US fallback.
    /// </summary>
    [Fact]
    public async Task FollowMe_NoOtherDeviceQueues_SpeaksLocalizedFollowMeNothingPlaying()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        // Only the CURRENT device has a queue: GetAllActiveQueues("device-kitchen") is empty.
        _queueManager.SetQueue("device-kitchen", new List<string> { Guid.NewGuid().ToString() }, 0);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" }, Locale = "it-IT" },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Equal(
            ResponseStrings.Get("FollowMeNothingPlaying", "it-IT"),
            TestHelpers.GetSpeechText(response));
    }

    /// <summary>
    /// JF-270 AC #3: a null DeviceQueueManager (the optional DI dependency was not
    /// provided) returns the localized FollowMeNothingPlaying instead of crashing.
    /// </summary>
    [Fact]
    public async Task FollowMe_NullQueueManager_SpeaksLocalizedFollowMeNothingPlaying()
    {
        var handler = new FollowMeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory,
            queueManager: null);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" }, Locale = "it-IT" },
            CreateContext("device-kitchen"),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.Equal(
            ResponseStrings.Get("FollowMeNothingPlaying", "it-IT"),
            TestHelpers.GetSpeechText(response));
        Assert.Empty(response.Response.Directives);
    }

    /// <summary>
    /// JF-270 AC #4: after a successful transfer, SetQueue is called on the CURRENT
    /// device (queue lands with the source's items, index, repeat mode and playback
    /// order) and Clear is called on the SOURCE device's queue.
    /// </summary>
    [Fact]
    public async Task FollowMe_SuccessfulTransfer_SetsQueueOnCurrentDeviceAndClearsSourceQueue()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");

        var firstItemId = Guid.NewGuid();
        var currentItemId = Guid.NewGuid();
        var item = new Audio { Id = currentItemId, Name = "Song" };

        _queueManager.SetQueue("device-livingroom", new List<string> { firstItemId.ToString(), currentItemId.ToString() }, 1, "All", "Shuffle");
        _libraryManagerMock.Setup(l => l.GetItemById(currentItemId)).Returns(item);

        // Preconditions that make the post-state assertions non-vacuous: the kitchen has
        // no queue yet (only the handler's SetQueue can create it) and the living room
        // has one (only the handler's Clear can remove it).
        Assert.Null(_queueManager.GetQueue("device-kitchen"));

        await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // SetQueue ran on the current device with the source queue's full state.
        DeviceQueue? kitchenQueue = _queueManager.GetQueue("device-kitchen");
        Assert.NotNull(kitchenQueue);
        Assert.Equal(new List<string> { firstItemId.ToString(), currentItemId.ToString() }, kitchenQueue.ItemIds);
        Assert.Equal(1, kitchenQueue.CurrentIndex);
        Assert.Equal("All", kitchenQueue.RepeatMode);
        Assert.Equal("Shuffle", kitchenQueue.PlaybackOrder);

        // Clear ran on the source device's queue.
        Assert.Null(_queueManager.GetQueue("device-livingroom"));
    }

    /// <summary>
    /// JF-270 AC #5: the success response carries an AudioPlayer.Play directive whose
    /// stream points at the source queue's CURRENT item (token plus the /Audio/{id}/stream
    /// URL with the user's api_key) AND the localized FollowMeSuccess speech with the
    /// title interpolated (SSML variant, the shape the handler builds for locales that
    /// define FollowMeSuccessSsml).
    /// </summary>
    [Fact]
    public async Task FollowMe_Success_PlaysSourceCurrentItemAndSpeaksLocalizedFollowMeSuccess()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext("device-kitchen");
        var user = TestHelpers.CreateTestUser(jellyfinToken: "test-token");

        const string title = "Current Song";
        var firstItemId = Guid.NewGuid();
        var currentItemId = Guid.NewGuid();
        var item = new Audio { Id = currentItemId, Name = title };

        _queueManager.SetQueue("device-livingroom", new List<string> { firstItemId.ToString(), currentItemId.ToString() }, 1);
        _libraryManagerMock.Setup(l => l.GetItemById(currentItemId)).Returns(item);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "FollowMeIntent" }, Locale = "en-US" },
            context,
            user,
            session,
            CancellationToken.None);

        var playDirective = Assert.IsType<AudioPlayerPlayDirective>(
            Assert.Single(response.Response.Directives!.Where(d => d is AudioPlayerPlayDirective)));

        // The directive plays the source queue's current item (index 1), not its first item.
        Assert.Equal(currentItemId.ToString(), playDirective.AudioItem.Stream.Token);
        Assert.Contains(
            $"/Audio/{currentItemId}/stream?static=true&api_key={user.JellyfinToken}",
            playDirective.AudioItem.Stream.Url,
            StringComparison.Ordinal);

        // The speech is the localized FollowMeSuccess with the title interpolated.
        // The title has no XML-reserved characters, so EscapeXml is the identity here.
        var speech = Assert.IsType<SsmlOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal(
            $"<speak>{BaseHandler.GetSsml("FollowMeSuccessSsml", "en-US", title)}</speak>",
            speech.Ssml);
        Assert.Contains(title, TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }
}
