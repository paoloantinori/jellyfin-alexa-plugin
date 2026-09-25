using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Alexa.NET.Assertions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for StartOverIntentHandler: restarts currently playing or last-played item from the beginning.
/// </summary>
[Collection("Plugin")]
public class StartOverIntentHandlerTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");
    private readonly JellyfinUser _jellyfinUser;
    private readonly Guid _sessionUserId;
    private readonly Mock<global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.ILiveTvStreamResolver> _resolverMock;

    public StartOverIntentHandlerTests()
    {
        _jellyfinUser = TestHelpers.CreateJellyfinUser();
        _sessionUserId = Guid.NewGuid();
        // By default the resolver returns a direct-remote stream so channel-restart
        // tests reach the VideoApp.Launch path; individual tests override this.
        _resolverMock = new Mock<global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.ILiveTvStreamResolver>();
        _resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<BaseItem>(), It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.LiveTvStream("https://remote.example/playlist.m3u8"));

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "startover-tests");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingStartOverHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.ILiveTvStreamResolver streamResolver,
        ILoggerFactory loggerFactory)
        : StartOverIntentHandler(sessionManager, config, libraryManager, userManager, userDataManager, streamResolver, loggerFactory)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingStartOverHandler CreateHandler()
    {
        return new RecordingStartOverHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _resolverMock.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateStartOverRequest()
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.StartOverIntent" }
        };
    }

    private SessionInfo CreateSessionWithNowPlaying(BaseItem item)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _sessionUserId;
        session.FullNowPlayingItem = item;
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns(_jellyfinUser);
        return session;
    }

    private SessionInfo CreateEmptySession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _sessionUserId;
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns(_jellyfinUser);
        return session;
    }

    [Fact]
    public void CanHandle_StartOverIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.ResumeIntent" }
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlaying_RestartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var audioItem = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        var session = CreateSessionWithNowPlaying(audioItem);

        // Set up existing progress
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(2).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, audioItem))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);

        // Should return AudioPlayer directive with offset 0 (restart from beginning)
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);

        // Should have cleared the progress by saving with position = 0
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                _jellyfinUser,
                audioItem,
                It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackProgress,
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlayingMovie_RestartsWithVideoApp()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Test Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/test.mkv"
        };

        var session = CreateSessionWithNowPlaying(movie);

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, movie))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);

        // JF-501: the restart announce rides the progressive-response vehicle; the final
        // launch response carries the directive ONLY.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Starting"), "progressive announce must mention restarting");
        Assert.True(handler.Progressive.Contains("Test Movie"), "progressive announce must speak the title");

        // Should have cleared progress
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                _jellyfinUser,
                movie,
                It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackProgress,
                CancellationToken.None),
            Times.Once);
    }

    /// <summary>
    /// JF-586: restarting an EPISODE on a SCREENLESS device (an Echo Dot) degrades to
    /// the AudioPlayer audio-only launch instead of the screen-required refusal; the
    /// restart cleared the progress, so the degrade plays from the beginning.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CurrentlyPlayingEpisode_ScreenlessDevice_DegradesToAudioPlayer()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = TestHelpers.CreateScreenlessContext();
        var user = TestHelpers.CreateTestUser();

        var episodeId = Guid.NewGuid();
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "The Convention",
            Id = episodeId,
            Path = "/tv/the-office/s03e02.mkv"
        };

        var session = CreateSessionWithNowPlaying(episode);

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, episode))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Contains($"/Audio/{episodeId}/stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession, "JF-299: the AudioPlayer play ends the session");
        Assert.Contains("The Convention", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.DoesNotContain("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-501 plain-text arm: RestartingContent is a PlainTextOutputSpeech announce, so the
    /// progressive vehicle must <c>&lt;speak&gt;</c>-wrap it AND XML-escape the title (only
    /// the SSML arm had an escaping assert). Alexa rejects a malformed progressive payload
    /// outright, so escaping is load-bearing.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CurrentlyPlayingMovie_PlainTextAnnounce_SpeakWrappedAndXmlEscaped()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Rock & Roll <Live>",
            Id = Guid.NewGuid(),
            Path = "/movies/test.mkv"
        };

        var session = CreateSessionWithNowPlaying(movie);

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, movie))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // JF-501: the restart announce rides the progressive-response vehicle; the final
        // launch response carries the directive ONLY.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(
            handler.Progressive.Contains("<speak>Starting Rock &amp; Roll &lt;Live&gt; from the beginning.</speak>"),
            "the plain-text announce must be speak-wrapped and XML-escaped; captured: " + handler.Progressive.AllText);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_WithServerProgress_RestartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        // Empty session (FullNowPlayingItem is null)
        var session = CreateEmptySession();

        // Server has an item with progress
        var audioItem = new Audio
        {
            Name = "Last Played Song",
            Id = Guid.NewGuid(),
            Path = "/music/last.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(3).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Should play from beginning (offset 0)
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Should have cleared progress before playing
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                _jellyfinUser,
                It.IsAny<BaseItem>(),
                It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackProgress,
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_NoServerProgress_ReturnsNoMediaToRestart()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        // Empty session (FullNowPlayingItem is null)
        var session = CreateEmptySession();

        // No items with progress on the server
        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing to restart", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NullSession_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        // null session -> handler returns NoMediaPlaying
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlaying_NoExistingUserData_StillPlays()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var audioItem = new Audio
        {
            Name = "New Song",
            Id = Guid.NewGuid(),
            Path = "/music/new.mp3"
        };

        var session = CreateSessionWithNowPlaying(audioItem);

        // No user data exists for this item
        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, audioItem))
            .Returns((UserItemData?)null);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Should still play from beginning even without existing progress to clear
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // SaveUserData should NOT be called when there is no existing userData
        _fx.UserDataManager.Verify(
            x => x.SaveUserData(
                It.IsAny<JellyfinUser>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_UserNotFound_ReturnsUserNotFoundError()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var audioItem = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        var session = CreateSessionWithNowPlaying(audioItem);

        // Override: user manager returns null (user not found)
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns((JellyfinUser?)null);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("user not found", speech, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// JF-563: with NativeControlsForBooks on, StartOver of a book relaunches from 0
    /// through the same VideoApp HLS concat entry PlayBook uses (no start slice) and
    /// drops the tracker's high-water position so the next resume cannot jump back.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CurrentlyPlayingBook_NativeControlsOn_RestartsFromZeroViaHlsConcat()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("startover-ab");
        var ledger = TestHelpers.CreateDeviceQueueManager("startover-ab-ledger");
        using var pluginQueueSwap = TestHelpers.SwapPluginQueueManager(ledger);
        // Declaration order is deliberate (the JF-633 review): reverse-declaration
        // disposal runs the TRACKER first, the ledger second, the old literal order.
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);
        try
        {
            var handler = CreateHandler();
            var request = CreateStartOverRequest();
            var context = _fx.CreateContext();
            var user = TestHelpers.CreateTestUser();

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Test Book Chapter 7",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter7.mp3"
            };

            var session = CreateSessionWithNowPlaying(chapter);

            var userData = new UserItemData
            {
                Key = "test",
                PlaybackPositionTicks = TimeSpan.FromMinutes(42).Ticks,
                Played = false
            };

            _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, chapter))
                .Returns(userData);

            // A stale tracked position must not survive the restart.
            tracker.RecordSegment(bookFolderId.ToString(), 250);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                Assert.Single(response.Response.Directives));
            Assert.Empty(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
            Assert.Contains(
                $"alexaskill/api/video-audio/audiobook/{bookFolderId}/stream.m3u8?token=",
                videoDirective.VideoItem!.Source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("start=", videoDirective.VideoItem!.Source, StringComparison.Ordinal);

            // Restart from 0: server-side progress AND the tracked high-water mark are gone.
            _fx.UserDataManager.Verify(
                x => x.SaveUserData(
                    _jellyfinUser,
                    chapter,
                    It.Is<UserItemData>(d => d.PlaybackPositionTicks == 0),
                    UserDataSaveReason.PlaybackProgress,
                    CancellationToken.None),
                Times.Once);
            Assert.Equal(0, tracker.GetPositionTicks(bookFolderId.ToString("N")));

            // JF-501: the restart announce rides the progressive-response vehicle; the
            // final launch response carries the directive ONLY.
            Assert.Null(response.Response.OutputSpeech);
            Assert.True(handler.Progressive.Contains("Starting"), "progressive announce must mention restarting");
            Assert.True(handler.Progressive.Contains("Test Book Chapter 7"), "progressive announce must speak the title");

            // JF-563 review: the VideoApp launch bypasses the AudioPlayer chokepoint, so
            // the builder must have recorded the device last-played ledger itself.
            Assert.Equal(chapter.Id.ToString(), ledger.GetLastPlayedItemId("test-device"));
        }
        finally
        {
            // Config flag only (not resource teardown): the tracker and ledger
            // teardown lives in the swap scopes above (JF-633 chained all resource
            // teardown through the usings).
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }

    /// <summary>
    /// JF-563 single-chapter shape: a book item with no parent folder relaunches through
    /// the per-item video-audio endpoint (the entry whose controller re-mints the
    /// chapter-scoped token), never a hand-built audiobook concat URL.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CurrentlyPlayingSingleFileBook_NativeControlsOn_UsesPerItemEndpoint()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        try
        {
            var handler = CreateHandler();
            var request = CreateStartOverRequest();
            var context = _fx.CreateContext();
            var user = TestHelpers.CreateTestUser();

            var book = new AudioBook
            {
                Name = "Single File Book",
                Id = Guid.NewGuid(),
                Path = "/audiobooks/single.mp3"
            };

            var session = CreateSessionWithNowPlaying(book);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                Assert.Single(response.Response.Directives));
            Assert.Contains(
                $"alexaskill/api/video-audio/{book.Id}/stream.m3u8?token=",
                videoDirective.VideoItem!.Source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("audiobook/", videoDirective.VideoItem!.Source, StringComparison.Ordinal);
        }
        finally
        {
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }

    /// <summary>
    /// JF-563 flag-off pin: the AudioBook type alone must not flip the path; the flat
    /// AudioPlayer restart from 0 is unchanged.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CurrentlyPlayingBook_NativeControlsOff_KeepsFlatAudioStream()
    {
        var handler = CreateHandler();
        var request = CreateStartOverRequest();
        var context = _fx.CreateContext();
        var user = TestHelpers.CreateTestUser();

        var chapter = new AudioBook
        {
            Name = "Test Book Chapter 7",
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Path = "/audiobooks/book/chapter7.mp3"
        };

        var session = CreateSessionWithNowPlaying(chapter);

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(42).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(_jellyfinUser, chapter))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains($"/Audio/{chapter.Id}/stream?static=true", audioDirective.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    // --- JF-564: StartOver of a live TV channel must REJOIN the live stream via the
    // same resolver/VideoApp entry PlayChannel uses. The audio branch's static
    // /Audio/{id}/stream URL returns HTTP 500 for a live source. ---

    private static MediaBrowser.Controller.LiveTv.LiveTvChannel CreateChannel()
        => new()
        {
            Name = "CNN",
            Id = Guid.NewGuid()
        };

    [Fact]
    public async Task HandleAsync_CurrentlyPlayingLiveTvChannel_RejoinsViaVideoAppResolver()
    {
        // The entry set is load-bearing: other suites in the collection (e.g.
        // LiveTvFeatureFlagTests) flip the shared flag false WITHOUT restoring it.
        // Every test here restores true so the disabled-flag test cannot leak either.
        Plugin.Instance!.Configuration.LiveTvEnabled = true;
        try
        {
            var handler = CreateHandler();
            var request = CreateStartOverRequest();
            var context = _fx.CreateContext();
            var user = TestHelpers.CreateTestUser();

            var channel = CreateChannel();
            var session = CreateSessionWithNowPlaying(channel);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            // The rejoin is a VideoApp.Launch carrying the RESOLVER's URL, never the
            // 500-ing static audio stream.
            var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                Assert.Single(response.Response.Directives));
            Assert.Equal("https://remote.example/playlist.m3u8", videoDirective.VideoItem!.Source);
            Assert.Empty(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
            Assert.Equal("CNN", videoDirective.VideoItem.Metadata?.Title);
            // VideoApp.Launch must NOT include shouldEndSession.
            Assert.Null(response.Response.ShouldEndSession);

            // Same launch-block side effects as PlayChannel: the session queue pins the
            // channel and the launch announces the channel name progressively.
            Assert.NotNull(session.NowPlayingQueue);
            Assert.Single(session.NowPlayingQueue);
            Assert.Equal(channel.Id, session.NowPlayingQueue[0].Id);
            Assert.True(handler.Progressive.Contains("CNN"), "the rejoin must announce the channel name");
        }
        finally
        {
            Plugin.Instance!.Configuration.LiveTvEnabled = true;
        }
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlayingLiveTvChannel_ResolverNull_ReturnsNotAvailableTell()
    {
        Plugin.Instance!.Configuration.LiveTvEnabled = true;
        _resolverMock
            .Setup(r => r.ResolveAsync(It.IsAny<BaseItem>(), It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.LiveTvStream?)null);
        try
        {
            var handler = CreateHandler();
            var request = CreateStartOverRequest();
            var context = _fx.CreateContext();
            var user = TestHelpers.CreateTestUser();

            var session = CreateSessionWithNowPlaying(CreateChannel());

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            // Unresolvable stream: the same honest not-available Tell PlayChannel speaks,
            // and no directive is emitted.
            Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
            Assert.True(response.Response.ShouldEndSession);
            Assert.Empty(response.Response.Directives ?? new List<IDirective>());
        }
        finally
        {
            Plugin.Instance!.Configuration.LiveTvEnabled = true;
        }
    }

    [Fact]
    public async Task HandleAsync_CurrentlyPlayingLiveTvChannel_LiveTvDisabled_ReturnsFeatureDisabled()
    {
        Plugin.Instance!.Configuration.LiveTvEnabled = false;
        try
        {
            var handler = CreateHandler();
            var request = CreateStartOverRequest();
            var context = _fx.CreateContext();
            var user = TestHelpers.CreateTestUser();

            var session = CreateSessionWithNowPlaying(CreateChannel());

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response.Response.OutputSpeech);
            Assert.True(response.Response.ShouldEndSession);
            Assert.Empty(response.Response.Directives ?? new List<IDirective>());
            _resolverMock.Verify(
                r => r.ResolveAsync(It.IsAny<BaseItem>(), It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            Plugin.Instance!.Configuration.LiveTvEnabled = true;
        }
    }
}
