using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for gapless playback / aggressive pre-fetch in PlaybackNearlyFinishedEventHandler.
/// Covers sequential enqueue, loop modes (RepeatOne, RepeatAll), shuffle, radio mode,
/// sleep timer, end-of-queue, and edge cases.
/// </summary>
public class GaplessPlaybackTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public GaplessPlaybackTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static Context CreateContext(string? token = null)
    {
        var context = TestHelpers.CreateTestContext();
        if (token != null)
        {
            context.AudioPlayer = new PlaybackState { Token = token, OffsetInMilliseconds = 0 };
        }

        return context;
    }

    private static AudioPlayerRequest CreateNearlyFinishedRequest(string? token = null)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackNearlyFinished",
            Token = token ?? Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0
        };
    }

    private PlaybackNearlyFinishedEventHandler CreateHandler()
    {
        return new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    // =====================================================================
    // CanHandle
    // =====================================================================

    [Fact]
    public void CanHandle_ReturnsTrueForPlaybackNearlyFinished()
    {
        var handler = CreateHandler();
        var request = CreateNearlyFinishedRequest();

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_ReturnsFalseForPlaybackStarted()
    {
        var handler = CreateHandler();
        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_ReturnsFalseForIntentRequest()
    {
        var handler = CreateHandler();
        Assert.False(handler.CanHandle(new IntentRequest()));
    }

    // =====================================================================
    // Sequential enqueue (gapless)
    // =====================================================================

    [Fact]
    public async Task SequentialQueue_EnqueuesNextTrack()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track1 = new Audio { Id = track1Id, Name = "Track 1" };
        var track2 = new Audio { Id = track2Id, Name = "Track 2" };

        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id)).Returns(track2);

        var context = CreateContext(track1Id.ToString());
        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);

        // Verify the response contains an AudioPlayerPlayDirective with Enqueue behavior
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal(track1Id.ToString(), directive.AudioItem.Stream.ExpectedPreviousToken);
    }

    [Fact]
    public async Task SequentialQueue_UsesStreamEndpoint()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track2 = new Audio { Id = track2Id, Name = "Track 2" };

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id)).Returns(track2);

        var user = TestHelpers.CreateTestUser(jellyfinToken: "my-token");
        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            user,
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Contains("/stream?static=true", directive.AudioItem.Stream.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("my-token", directive.AudioItem.Stream.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SequentialQueue_IncludesMetadata()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track2 = new Audio { Id = track2Id, Name = "Next Song" };
        track2.Artists = new List<string> { "Test Artist" };

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Current Song" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id)).Returns(track2);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.NotNull(directive.AudioItem.Metadata);
        Assert.Equal("Next Song", directive.AudioItem.Metadata.Title);
        Assert.Equal("Test Artist", directive.AudioItem.Metadata.Subtitle);
        Assert.NotNull(directive.AudioItem.Metadata.Art?.Sources);
        Assert.NotEmpty(directive.AudioItem.Metadata.Art.Sources);
    }

    // =====================================================================
    // End of queue
    // =====================================================================

    [Fact]
    public async Task EndOfQueue_NoNextItem_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        session.FullNowPlayingItem = new Audio { Id = trackId, Name = "Last Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = trackId } };

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackId.ToString()),
            CreateContext(trackId.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task EmptyQueue_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem>();

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task NextItemNotFoundInLibrary_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };

        // Simulate the item being deleted from library
        _libraryManagerMock.Setup(l => l.GetItemById(track2Id)).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        Assert.Empty(response.Response.Directives);
    }

    // =====================================================================
    // Loop mode: RepeatOne
    // =====================================================================

    [Fact]
    public async Task RepeatOne_RequeuesSameTrack()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        var track = new Audio { Id = trackId, Name = "Loop Song" };

        session.FullNowPlayingItem = track;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = trackId } };
        session.PlayState.RepeatMode = RepeatMode.RepeatOne;

        _libraryManagerMock.Setup(l => l.GetItemById(trackId)).Returns(track);

        var context = CreateContext(trackId.ToString());
        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackId.ToString()),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
        Assert.Equal(trackId.ToString(), directive.AudioItem.Stream.Token);
        // Should reference itself as expected previous
        Assert.Equal(trackId.ToString(), directive.AudioItem.Stream.ExpectedPreviousToken);
    }

    [Fact]
    public async Task RepeatOne_WithMultiItemQueue_RequeuesSameTrack()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track1 = new Audio { Id = track1Id, Name = "Track 1" };

        session.FullNowPlayingItem = track1;
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };
        session.PlayState.RepeatMode = RepeatMode.RepeatOne;

        _libraryManagerMock.Setup(l => l.GetItemById(track1Id)).Returns(track1);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // With RepeatOne, should replay track1 even though track2 is next in queue
        Assert.Equal(track1Id.ToString(), directive.AudioItem.Stream.Token);
    }

    // =====================================================================
    // Loop mode: RepeatAll
    // =====================================================================

    [Fact]
    public async Task RepeatAll_AtEndOfQueue_WrapsToFirst()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track1 = new Audio { Id = track1Id, Name = "Track 1" };

        session.FullNowPlayingItem = new Audio { Id = track2Id, Name = "Track 2" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };
        session.PlayState.RepeatMode = RepeatMode.RepeatAll;

        _libraryManagerMock.Setup(l => l.GetItemById(track1Id)).Returns(track1);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track2Id.ToString()),
            CreateContext(track2Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // Should wrap around to track1
        Assert.Equal(track1Id.ToString(), directive.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task RepeatAll_MiddleOfQueue_AdvancesNormally()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();
        var track2 = new Audio { Id = track2Id, Name = "Track 2" };

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };
        session.PlayState.RepeatMode = RepeatMode.RepeatAll;

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id)).Returns(track2);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // Should advance to track2, not wrap
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task RepeatNone_AtEndOfQueue_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track2Id, Name = "Track 2" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };
        session.PlayState.RepeatMode = RepeatMode.RepeatNone;

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track2Id.ToString()),
            CreateContext(track2Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    // =====================================================================
    // Shuffle mode
    // =====================================================================

    [Fact]
    public async Task Shuffle_PicksRandomTrackFromQueue()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();
        var track4Id = Guid.NewGuid();
        var track5Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id },
            new() { Id = track4Id },
            new() { Id = track5Id }
        };
        session.PlayState.PlaybackOrder = PlaybackOrder.Shuffle;

        // Set up library to return items for any ID
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => new Audio { Id = id, Name = $"Track {id}" });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // Should enqueue something, and it should NOT be track1 (avoid immediate repeat)
        Assert.NotEqual(track1Id.ToString(), directive.AudioItem.Stream.Token);
        // Token should be one of the queue items
        var allIds = new HashSet<string> { track1Id.ToString(), track2Id.ToString(), track3Id.ToString(), track4Id.ToString(), track5Id.ToString() };
        Assert.Contains(directive.AudioItem.Stream.Token, allIds);
    }

    [Fact]
    public async Task Shuffle_WithTwoTracks_Alternates()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };
        session.PlayState.PlaybackOrder = PlaybackOrder.Shuffle;

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id))
            .Returns(new Audio { Id = track2Id, Name = "Track 2" });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // With only 2 tracks and shuffle, should pick the other one
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task Shuffle_WithSingleTrackAndRepeatOne_RequeuesSame()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        var track = new Audio { Id = trackId, Name = "Only Song" };

        session.FullNowPlayingItem = track;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = trackId } };
        session.PlayState.PlaybackOrder = PlaybackOrder.Shuffle;
        session.PlayState.RepeatMode = RepeatMode.RepeatOne;

        _libraryManagerMock.Setup(l => l.GetItemById(trackId)).Returns(track);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackId.ToString()),
            CreateContext(trackId.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // RepeatOne takes priority and re-queues the same track
        Assert.Equal(trackId.ToString(), directive.AudioItem.Stream.Token);
    }

    [Fact]
    public async Task Shuffle_WithSingleTrackNoRepeat_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        var track = new Audio { Id = trackId, Name = "Only Song" };

        session.FullNowPlayingItem = track;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = trackId } };
        session.PlayState.PlaybackOrder = PlaybackOrder.Shuffle;
        session.PlayState.RepeatMode = RepeatMode.RepeatNone;

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackId.ToString()),
            CreateContext(trackId.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Single track + shuffle + no repeat = nothing to enqueue
        Assert.Empty(response.Response.Directives);
    }

    // =====================================================================
    // Sleep timer
    // =====================================================================

    [Fact]
    public async Task SleepTimer_Expired_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        var nextId = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = trackId, Name = "Current" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = trackId },
            new() { Id = nextId }
        };

        // Create a token with an expired sleep deadline (1 tick past epoch = definitely expired)
        string sleepToken = $"{trackId}|sleep:{DateTimeOffset.UtcNow.AddSeconds(-10).UtcTicks}";
        var context = CreateContext(sleepToken);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(sleepToken),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task SleepTimer_NotExpired_EnqueuesNormally()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var nextTrack = new Audio { Id = nextId, Name = "Next" };

        session.FullNowPlayingItem = new Audio { Id = trackId, Name = "Current" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = trackId },
            new() { Id = nextId }
        };

        // Deadline far in the future
        string sleepToken = $"{trackId}|sleep:{DateTimeOffset.UtcNow.AddHours(1).UtcTicks}";
        var context = CreateContext(sleepToken);

        _libraryManagerMock.Setup(l => l.GetItemById(nextId)).Returns(nextTrack);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(sleepToken),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(nextId.ToString(), directive.AudioItem.Stream.Token);
    }

    // =====================================================================
    // Radio mode
    // =====================================================================

    [Fact]
    public async Task RadioMode_QueueEmpty_AutoPopulatesAndEnqueues()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };

        session.FullNowPlayingItem = currentAudio;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };

        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);

        var similarId = Guid.NewGuid();
        var similarTrack = new Audio { Id = similarId, Name = "Similar Rock Song" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { similarTrack });

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        _libraryManagerMock.Setup(l => l.GetItemById(similarId)).Returns(similarTrack);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(similarId.ToString(), directive.AudioItem.Stream.Token);
        Assert.True(session.NowPlayingQueue.Count > 1);

        // Cleanup radio state
        RadioModeState.Disable(session.UserId, context.System.Device.DeviceID);
    }

    [Fact]
    public async Task RadioMode_NoSimilarTracks_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Obscure Song" };
        currentAudio.Genres = new[] { "ObscureGenre" };

        session.FullNowPlayingItem = currentAudio;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };

        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);

        // Cleanup
        RadioModeState.Disable(session.UserId, context.System.Device.DeviceID);
    }

    // =====================================================================
    // Combined: RepeatAll + Shuffle
    // =====================================================================

    [Fact]
    public async Task RepeatAllWithShuffle_AtEndOfQueue_PicksRandomFromQueue()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track3Id, Name = "Track 3" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };
        session.PlayState.RepeatMode = RepeatMode.RepeatAll;
        session.PlayState.PlaybackOrder = PlaybackOrder.Shuffle;

        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => new Audio { Id = id, Name = $"Track {id}" });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track3Id.ToString()),
            CreateContext(track3Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        // Shuffle takes priority - should pick a random track (not track3)
        Assert.NotEqual(track3Id.ToString(), directive.AudioItem.Stream.Token);
    }

    // =====================================================================
    // Edge cases
    // =====================================================================

    [Fact]
    public async Task NoCurrentItem_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();
        session.FullNowPlayingItem = null;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = Guid.NewGuid() } };

        var context = CreateContext();

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(),
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task CurrentItemNotInQueue_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var orphanId = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = orphanId, Name = "Orphan" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(orphanId.ToString()),
            CreateContext(orphanId.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task SingleTrackQueue_NoRepeat_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var trackId = Guid.NewGuid();
        session.FullNowPlayingItem = new Audio { Id = trackId, Name = "Only Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = trackId } };
        session.PlayState.RepeatMode = RepeatMode.RepeatNone;

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(trackId.ToString()),
            CreateContext(trackId.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task ThreeTrackQueue_AdvancesThroughAll()
    {
        var handler = CreateHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();

        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => new Audio { Id = id, Name = $"Track {id}" });

        // Track 1 -> Track 2
        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        var response1 = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);
        var d1 = response1.Response.Directives.OfType<AudioPlayerPlayDirective>().First();
        Assert.Equal(track2Id.ToString(), d1.AudioItem.Stream.Token);

        // Track 2 -> Track 3
        session.FullNowPlayingItem = new Audio { Id = track2Id, Name = "Track 2" };
        var response2 = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track2Id.ToString()),
            CreateContext(track2Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);
        var d2 = response2.Response.Directives.OfType<AudioPlayerPlayDirective>().First();
        Assert.Equal(track3Id.ToString(), d2.AudioItem.Stream.Token);

        // Track 3 -> end of queue
        session.FullNowPlayingItem = new Audio { Id = track3Id, Name = "Track 3" };
        var response3 = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track3Id.ToString()),
            CreateContext(track3Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);
        Assert.Empty(response3.Response.Directives);
    }
}
