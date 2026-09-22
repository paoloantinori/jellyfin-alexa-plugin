using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for pause/resume playback state preservation via DeviceQueue.
/// Verifies that PlaybackStoppedEventHandler saves position to DeviceQueue
/// and ResumeIntentHandler falls back to it when Alexa context is empty.
/// </summary>
[Collection("Plugin")]
public class PauseResumeStateTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");
    private readonly string _tempDir;
    private readonly DeviceQueueManager _queueManager;
    private static readonly string DeviceId = "test-device";
    private static readonly string TestItemId = Guid.NewGuid().ToString();

    public PauseResumeStateTests()
    {
        _tempDir = TestHelpers.CreateRegisteredTempDir("pause-resume-tests");
        var queueLogger = _fx.LoggerFactory.CreateLogger<DeviceQueueManager>();
        _queueManager = new DeviceQueueManager(_tempDir, queueLogger);

        // Ensure Plugin.Instance is available for BuildAudioPlayerResponse (APL checks)
        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "pause-resume-tests");
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
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSessionWithNowPlaying(string itemId)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();

        // Create a mock audio item as the now-playing item
        var audio = new Audio
        {
            Id = Guid.Parse(itemId),
            Name = "Test Song",
            Path = "/music/test.mp3"
        };
        session.FullNowPlayingItem = audio;

        return session;
    }

    /// <summary>
    /// Registers the TestItemId audio in the mock library: since the 2026-09-22
    /// hardening, the ResumeIntent DeviceQueue fallback materializes the queued id
    /// through ILibraryManager.GetItemById before adopting it (a deleted item falls
    /// through), so queue-based tests must serve the item.
    /// </summary>
    private void SeedTestItemInLibrary()
    {
        var audio = new Audio
        {
            Id = Guid.Parse(TestItemId),
            Name = "Test Song",
            Path = "/music/test.mp3"
        };
        _fx.LibraryManager.Setup(m => m.GetItemById(audio.Id)).Returns(audio);
    }

    private static Context CreateContext(string? audioPlayerToken = null, long audioPlayerOffset = 0)
    {
        var context = TestHelpers.CreateTestContext();
        if (audioPlayerToken != null)
        {
            context.AudioPlayer = new PlaybackState
            {
                Token = audioPlayerToken,
                OffsetInMilliseconds = audioPlayerOffset,
                PlayerActivity = "IDLE"
            };
        }
        else
        {
            context.AudioPlayer = new PlaybackState
            {
                PlayerActivity = "IDLE"
            };
        }

        return context;
    }

    private static AudioPlayerRequest CreateStoppedRequest(string token, long offsetMs)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStopped",
            Token = token,
            OffsetInMilliseconds = offsetMs
        };
    }

    // ---- DeviceQueue property tests ----

    [Fact]
    public void DeviceQueue_DefaultPositionTicks_IsZero()
    {
        var queue = _queueManager.GetOrCreateQueue("new-device");
        Assert.Equal(0L, queue.CurrentPositionTicks);
        Assert.Null(queue.CurrentItemId);
    }

    [Fact]
    public void DeviceQueue_CanStoreAndRetrievePosition()
    {
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(45).Ticks;

        var retrieved = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(TestItemId, retrieved.CurrentItemId);
        Assert.Equal(TimeSpan.FromSeconds(45).Ticks, retrieved.CurrentPositionTicks);
    }

    // ---- PlaybackStoppedEventHandler saves to DeviceQueue ----

    [Fact]
    public async Task PlaybackStopped_SavesPositionToDeviceQueue()
    {
        var handler = new PlaybackStoppedEventHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _queueManager,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

        long offsetMs = 30000; // 30 seconds
        var request = CreateStoppedRequest(TestItemId, offsetMs);
        var context = CreateContext();
        var session = CreateSessionWithNowPlaying(TestItemId);

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(TestItemId, queue.CurrentItemId);
        Assert.Equal(TimeSpan.FromMilliseconds(offsetMs).Ticks, queue.CurrentPositionTicks);
    }

    [Fact]
    public async Task PlaybackStopped_WithZeroOffset_StillSavesItemId()
    {
        var handler = new PlaybackStoppedEventHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _queueManager,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

        var request = CreateStoppedRequest(TestItemId, 0);
        var context = CreateContext();
        var session = CreateSessionWithNowPlaying(TestItemId);

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(TestItemId, queue.CurrentItemId);
        Assert.Equal(0L, queue.CurrentPositionTicks);
    }

    [Fact]
    public async Task PlaybackStopped_WithoutQueueManager_DoesNotThrow()
    {
        var handler = new PlaybackStoppedEventHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _queueManager,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

        var request = CreateStoppedRequest(TestItemId, 5000);
        var context = CreateContext();
        var session = CreateSessionWithNowPlaying(TestItemId);

        // Should not throw even without DeviceQueueManager
        var response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
    }

    // ---- ResumeIntentHandler uses DeviceQueue fallback ----

    [Fact]
    public async Task ResumeIntent_UsesAudioPlayerContext_WhenAvailable()
    {
        // Pre-populate DeviceQueue with different position (60s)
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(60).Ticks;

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        // AudioPlayer context has offset = 10s, should prefer that over DeviceQueue's 60s
        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 10000);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);

        // Verify the offset used is from AudioPlayer context (10s), not DeviceQueue (60s)
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(10000, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_FallsBackToDeviceQueue_WhenContextOffsetIsZero()
    {
        // Pre-populate DeviceQueue with position
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(45).Ticks;
        SeedTestItemInLibrary();

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        // AudioPlayer context has token but offset = 0 (cleared after pause)
        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 0);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);

        // Verify the offset used is from DeviceQueue (45s)
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(45000, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_FallsBackToDeviceQueue_WhenTokenAndSessionItemAreBothNull()
    {
        // Live incident 2026-09-22: a one-shot resume arriving after PlaybackStopped
        // carries a null AudioPlayer token AND a null session now-playing item. The
        // queue lookup used to sit inside the item_id!=null guard, so this shape fell
        // straight to server-side progress and resumed a stale unrelated episode.
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromMilliseconds(14763).Ticks;
        SeedTestItemInLibrary();

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);

        // The queue's item plays at the queue's position, not a server-progress item
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(TestItemId, directive.AudioItem.Stream.Token);
        Assert.Equal(14763, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_FresherLastPlayedRecord_WinsOverStaleQueuePointer()
    {
        // JF-619 scenario: music X was stopped on this Echo (queue pointer, 10 min
        // ago), then a VideoApp play recorded item M (now). Bare "riprendi" with
        // empty context must resume M, the SAME item the LaunchRequest offer would
        // seed (both entry points route through GetDeviceResumePointer).
        var stoppedId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();

        var watched = new Audio { Id = watchedId, Name = "Watched Episode Seed", Path = "/tv/e.mp4" };
        _fx.LibraryManager.Setup(m => m.GetItemById(watchedId)).Returns(watched);

        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        var sessionUserId = Guid.NewGuid();
        _fx.UserManager.Setup(x => x.GetUserById(sessionUserId)).Returns(jellyfinUser);
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks, Played = false });

        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = stoppedId.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        queue.CurrentItemWrittenAt = DateTime.UtcNow.AddMinutes(-10);
        _queueManager.RecordLastPlayed(DeviceId, watchedId.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = sessionUserId;
        session.PlayState = new PlayerStateInfo();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(watchedId.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal((int)TimeSpan.FromMinutes(5).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task ResumeIntent_LastPlayedVideoKind_LeftToFallback4VideoAppResume()
    {
        // JF-619 review K1: a Movie/Episode from the LastPlayed arm is a VideoApp
        // play; this fallback's tail builds an audio-only launch, so the video kind
        // must fall through (fallback 4 owns the VideoApp resume with the ?start=
        // slice), not resume as sound-only.
        var movieId = Guid.NewGuid();
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = movieId, Name = "Test Movie" };
        _fx.LibraryManager.Setup(m => m.GetItemById(movieId)).Returns(movie);

        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = null;
        _queueManager.RecordLastPlayed(DeviceId, movieId.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Fallback 4 (empty mock library) has nothing to offer: a Tell, never an
        // audio-only AudioPlayer launch of the movie.
        Assert.DoesNotContain(response.Response.Directives ?? new System.Collections.Generic.List<IDirective>(), d => d is AudioPlayerPlayDirective);
    }

    [Fact]
    public async Task ResumeIntent_UnusableLastPlayedWinner_RetriesQueuePointerLoser()
    {
        // JF-619 review K2: the resolver's winner (a launch that never started, no
        // position anywhere) must not discard the loser queue pointer's good
        // item-absolute position.
        var songId = Guid.NewGuid();
        SeedTestItemInLibrary();
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromMinutes(5).Ticks;
        queue.CurrentItemWrittenAt = DateTime.UtcNow.AddMinutes(-10);

        var neverStarted = Guid.NewGuid();
        _queueManager.RecordLastPlayed(DeviceId, neverStarted.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        // The winner has no library item (never started): the loser must win.

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(TestItemId, directive.AudioItem.Stream.Token);
        Assert.Equal((int)TimeSpan.FromMinutes(5).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task ResumeIntent_IgnoresDeviceQueue_WhenQueueItemWasDeleted()
    {
        // Review hardening (2026-09-22): a queue pointer to a since-deleted item must
        // not mint a dead stream URL; the resume falls through to the next fallback
        // (here: nothing playing -> NoMediaPlaying).
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(45).Ticks;
        // No GetItemById setup: the mock library does not know the item.

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives, d => d is AudioPlayerPlayDirective);
    }

    [Fact]
    public async Task ResumeIntent_DoesNotUseDeviceQueue_WhenTokenMismatch()
    {
        // Pre-populate DeviceQueue with a DIFFERENT item
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = "other-item-id";
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(45).Ticks;

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        // AudioPlayer context has a different token and offset = 0
        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 0);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);

        // Offset should be 0 (from context), NOT from DeviceQueue (token mismatch)
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_WithoutQueueManager_WorksNormally()
    {
        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 5000);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_ReturnsNoMedia_WhenSessionHasNoNowPlayingItem()
    {
        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();
        // FullNowPlayingItem is null by default

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_ReturnsEmpty_WhenAlreadyPlaying()
    {
        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 1000);
        context.AudioPlayer.PlayerActivity = "PLAYING";
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    // ---- Integration: Stop then Resume flow ----

    [Fact]
    public async Task StopThenResume_PreservesPosition_ViaDeviceQueue()
    {
        var stoppedHandler = new PlaybackStoppedEventHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _queueManager,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);
        var resumeHandler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        // Simulate playback stopped at 45 seconds
        long stoppedOffsetMs = 45000;
        var stoppedRequest = CreateStoppedRequest(TestItemId, stoppedOffsetMs);
        var context = CreateContext();
        var session = CreateSessionWithNowPlaying(TestItemId);
        SeedTestItemInLibrary();

        await stoppedHandler.HandleAsync(stoppedRequest, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Verify DeviceQueue has the position
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        Assert.Equal(TestItemId, queue.CurrentItemId);
        Assert.Equal(TimeSpan.FromMilliseconds(stoppedOffsetMs).Ticks, queue.CurrentPositionTicks);

        // Simulate resume with no AudioPlayer offset (device context cleared)
        context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 0);

        var response = await resumeHandler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);

        // Verify the resume uses the DeviceQueue position
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(45000, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession);
    }

    // ---- PlayBehavior and PlaybackController tests ----

    [Fact]
    public async Task ResumeIntent_UsesReplaceAllPlayBehavior()
    {
        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 10000);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void ResumeIntent_CanHandle_PlaybackControllerPlayCommand()
    {
        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

        // PlaybackControllerRequest.PlaybackRequestType is read-only;
        // deserialize from JSON to set it (TestHelpers.CreatePlayCommand)
        var request = TestHelpers.CreatePlayCommand();

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void ResumeIntent_CannotHandle_PlaybackControllerPauseCommand()
    {
        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object);

        var json = @"{""requestId"":""test"",""type"":""PlaybackController.PauseCommandIssued"",""timestamp"":""2024-01-01T00:00:00Z"",""locale"":""en-US"",""playbackRequestMethod"":""PAUSE""}";
        var request = Newtonsoft.Json.JsonConvert.DeserializeObject<PlaybackControllerRequest>(json);

        Assert.NotNull(request);
        Assert.False(handler.CanHandle(request!));
    }

    // ---- DeviceQueue persistence ----

    [Fact]
    public void DeviceQueue_PositionPersistedAcrossDispose()
    {
        // Write position
        var queue = _queueManager.GetOrCreateQueue(DeviceId);
        queue.CurrentItemId = TestItemId;
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        _queueManager.PersistAll();

        // Create a new DeviceQueueManager with the same data directory
        var queueLogger2 = _fx.LoggerFactory.CreateLogger<DeviceQueueManager>();
        using var queueManager2 = new DeviceQueueManager(_tempDir, queueLogger2);

        var restored = queueManager2.GetOrCreateQueue(DeviceId);
        Assert.Equal(TestItemId, restored.CurrentItemId);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, restored.CurrentPositionTicks);
    }

    // ---- M5: Proactive position announcement on resume ----

    [Fact]
    public async Task ResumeIntent_AnnouncePositionEnabled_AnnouncesPosition()
    {
        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = true
        });

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 90000); // 1m 30s
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("1 minutes", speech.Text);
        Assert.Contains("30 seconds", speech.Text);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_AnnouncePositionDisabled_NoAnnouncement()
    {
        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 90000);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context, user, session, CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_AnnouncePositionEnabled_ZeroOffset_NoAnnouncement()
    {
        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = true
        });

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 0);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context, user, session, CancellationToken.None);

        // Offset is 0, so no announcement even with the flag enabled
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task ResumeIntent_AnnouncePositionEnabled_UserNotInConfig_NoAnnouncement()
    {
        // User exists but is NOT in plugin config.Users
        var user = TestHelpers.CreateTestUser();

        var handler = new ResumeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
            _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
            _queueManager);

        var context = CreateContext(audioPlayerToken: TestItemId, audioPlayerOffset: 90000);
        var session = CreateSessionWithNowPlaying(TestItemId);

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context, user, session, CancellationToken.None);

        // No config entry for this user → no announcement
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }
}
