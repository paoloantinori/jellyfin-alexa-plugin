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
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for ResumeIntentHandler fallback tier 4: Jellyfin server-side progress
/// when no AudioPlayer token, no session FullNowPlayingItem, and no DeviceQueue state exists.
/// </summary>
[Collection("Plugin")]
public class ResumeIntentHandlerServerProgressTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");
    private readonly JellyfinUser _jellyfinUser;
    private readonly Guid _sessionUserId;

    public ResumeIntentHandlerServerProgressTests()
    {
        _jellyfinUser = TestHelpers.CreateJellyfinUser();
        _sessionUserId = Guid.NewGuid();

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "resume-server-progress-tests");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingResumeHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
        : ResumeIntentHandler(sessionManager, config, loggerFactory, libraryManager, userManager, userDataManager)
    {
        public ProgressiveSpeechCapture Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(global::Alexa.NET.Request.Context context, global::Alexa.NET.Request.Type.Request request, string message)
            => Progressive.Record(context, request, message);
    }

    private RecordingResumeHandler CreateHandler()
    {
        return new RecordingResumeHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LoggerFactory,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object);
    }

    private static IntentRequest CreateResumeRequest()
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent { Name = "AMAZON.ResumeIntent" }
        };
    }

    private static Context CreateContextNoAudioPlayer()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new global::Alexa.NET.Request.Device { DeviceID = "test-device" },
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                ApiAccessToken = "test-token",
                ApiEndpoint = "https://api.amazonalexa.com"
            },
            AudioPlayer = new PlaybackState
            {
                PlayerActivity = "IDLE"
            }
        };
    }

    private SessionInfo CreateEmptySession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.UserId = _sessionUserId;
        // FullNowPlayingItem is null by default -- triggers server-side fallback
        _fx.UserManager.Setup(x => x.GetUserById(_sessionUserId)).Returns(_jellyfinUser);
        return session;
    }

    [Fact]
    public async Task ServerProgressFallback_AudioBook_ReturnsAudioWithOffset()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Server has an audiobook chapter with progress at 5 minutes
        var audioItem = new Audio
        {
            Name = "Chapter 7",
            Id = Guid.NewGuid(),
            Path = "/audiobooks/book/chapter7.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        long progressTicks = TimeSpan.FromMinutes(5).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);

        // Should return AudioPlayer directive with correct offset
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);

        int expectedOffsetMs = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
        Assert.Equal(expectedOffsetMs, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task ServerProgressFallback_Movie_ReturnsVideoAppResponse()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Server has a movie with progress at 30 minutes
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Test Movie",
            Id = Guid.NewGuid(),
            Path = "/movies/test.mkv"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        long progressTicks = TimeSpan.FromMinutes(30).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);

        // JF-501: the announce rides the progressive-response vehicle; the final launch
        // response carries the directive ONLY. The progressive speech contains the movie
        // name and position information.
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(handler.Progressive.Contains("Test Movie"), "progressive announce must speak the movie title");
        Assert.True(handler.Progressive.Contains("30m"), "progressive announce must speak the resume position");
    }

    /// <summary>
    /// JF-565: the server-progress fallback is a RESUME ask, so an episode routed to
    /// the HLS remux must carry the stored position on the launch URL (?start=):
    /// VideoApp has no offset parameter, the slice IS the resume mechanism.
    /// </summary>
    [Fact]
    public async Task ServerProgressFallback_RemuxEpisode_MintsStartSliceOnLaunchUrl()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "The Convention",
            episodeId,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        episode.RunTimeTicks = TimeSpan.FromMinutes(30).Ticks;

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });

        long resumeTicks = TimeSpan.FromMinutes(10).Ticks;
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", PlaybackPositionTicks = resumeTicks, Played = false });

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var directive = Assert.IsType<VideoAppLaunchDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains($"/alexaskill/api/video-audio/episode/{episodeId}/stream.m3u8?start={resumeTicks}&token=", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-586: the server-progress resume of an EPISODE on a SCREENLESS device (an
    /// Echo Dot) degrades to the AudioPlayer audio-only launch instead of the
    /// screen-required refusal; a decodable episode keeps the static /Audio stream
    /// with the stored position on the DIRECTIVE (AudioPlayer can seek a static
    /// stream; the VideoApp Static route cannot).
    /// </summary>
    [Fact]
    public async Task ServerProgressFallback_Episode_ScreenlessDevice_DegradesToAudioPlayer()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = TestHelpers.CreateScreenlessContext();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        var episodeId = Guid.NewGuid();
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "The Convention",
            Id = episodeId,
            Path = "/tv/the-office/s03e02.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });

        long resumeTicks = TimeSpan.FromMinutes(20).Ticks;
        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", PlaybackPositionTicks = resumeTicks, Played = false });

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var directive = Assert.IsType<AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains($"/Audio/{episodeId}/stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal((int)TimeSpan.FromMinutes(20).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession, "JF-299: the AudioPlayer play ends the session");
        Assert.DoesNotContain("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerProgressFallback_NoProgress_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // No items with progress on the server
        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_AnnouncesPosition_WhenEnabled()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = true
        });

        var session = CreateEmptySession();

        // Server has an audio item with progress at 2 minutes 30 seconds
        var audioItem = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Path = "/music/test.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        long progressTicks = TimeSpan.FromMinutes(2).Ticks + TimeSpan.FromSeconds(30).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        // Output speech should contain position announcement (from ResumingAtPosition string)
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("resuming", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_SkipsPlayedItems()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        var inProgressItem = new Audio
        {
            Name = "In Progress Song",
            Id = Guid.NewGuid(),
            Path = "/music/inprogress.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == false)))
            .Returns(new List<BaseItem> { inProgressItem });

        var inProgressUserData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), inProgressItem))
            .Returns(inProgressUserData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // Should play the in-progress item, not the played one
        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Contains(inProgressItem.Id.ToString(), audioDirective.AudioItem.Stream.Url);
        Assert.Equal((int)TimeSpan.FromMinutes(1).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task ServerProgressFallback_SkipsZeroProgressItems()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Item with zero progress should be skipped
        var zeroProgressItem = new Audio
        {
            Name = "Zero Progress Song",
            Id = Guid.NewGuid(),
            Path = "/music/zero.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { zeroProgressItem });

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = 0,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_NullSession_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();
        var user = TestHelpers.CreateTestUser();

        // null session should return NoMediaPlaying (handler checks session == null)
        var response = await handler.HandleAsync(request, context, user, null!, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing is currently playing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerProgressFallback_Episode_ReturnsVideoAppResponse()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        // Server has a TV episode with progress
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Test Episode",
            Id = Guid.NewGuid(),
            Path = "/tv/show/s01e01.mkv"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });

        long progressTicks = TimeSpan.FromMinutes(15).Ticks;
        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = progressTicks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
    }

    /// <summary>
    /// JF-563: with NativeControlsForBooks on, fallback-4 resumes an audiobook through
    /// the same sliced VideoApp HLS playlist PlayBook uses (tracker position first), not
    /// the flat /Audio stream.
    /// </summary>
    [Fact]
    public async Task ServerProgressFallback_AudioBook_NativeControlsOn_ResumesViaSlicedHlsPlaylist()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("resume-ab");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);
        var ledger = TestHelpers.CreateDeviceQueueManager("resume-ab-ledger");
        using var pluginQueueSwap = TestHelpers.SwapPluginQueueManager(ledger);
        try
        {
            var handler = CreateHandler();
            var request = CreateResumeRequest();
            var context = CreateContextNoAudioPlayer();

            var user = TestHelpers.CreateTestUser();
            _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = user.Id,
                AnnouncePositionOnResume = false
            });

            var session = CreateEmptySession();

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Chapter 7",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter7.mp3"
            };

            _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { chapter });

            // Tracker holds segment 31 (conservative resume position 30 * 10s = 5 min);
            // server-side progress says 2 min. The tracker must win (PlayBook's order).
            tracker.RecordSegment(bookFolderId.ToString(), 31);
            var userData = new UserItemData
            {
                Key = "test",
                PlaybackPositionTicks = TimeSpan.FromMinutes(2).Ticks,
                Played = false
            };

            _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
                .Returns(userData);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                Assert.Single(response.Response.Directives));
            Assert.Empty(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
            Assert.Contains(
                $"alexaskill/api/video-audio/audiobook/{bookFolderId}/stream.m3u8?start={TimeSpan.FromMinutes(5).Ticks}&token=",
                videoDirective.VideoItem!.Source,
                StringComparison.Ordinal);

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
    /// JF-563: cold tracker (fresh restart) keeps the sliced launch; the slice falls back
    /// to the server-side progress ticks, the same fallback PlayBook applies.
    /// </summary>
    [Fact]
    public async Task ServerProgressFallback_AudioBook_NativeControlsOn_ColdTracker_FlatChapterResume()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        try
        {
            var handler = CreateHandler();
            var request = CreateResumeRequest();
            var context = CreateContextNoAudioPlayer();

            var user = TestHelpers.CreateTestUser();
            _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = user.Id,
                AnnouncePositionOnResume = false
            });

            var session = CreateEmptySession();

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Chapter 7",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter7.mp3"
            };

            _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { chapter });

            var userData = new UserItemData
            {
                Key = "test",
                PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks,
                Played = false
            };

            _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
                .Returns(userData);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            // JF-567 review major: server progress is CHAPTER-relative and must never
            // slice the whole-book concat timeline; with a cold tracker the book
            // flat-resumes at the chapter offset (no VideoApp directive).
            var audioDirective = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(
                Assert.Single(response.Response.Directives!));
            Assert.Equal(
                (int)TimeSpan.FromMinutes(5).TotalMilliseconds,
                audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        }
        finally
        {
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }

    /// <summary>
    /// JF-563 flag-off pin: the AudioBook type alone must not flip the path; the flat
    /// AudioPlayer stream with the progress offset is unchanged.
    /// </summary>
    [Fact]
    public async Task ServerProgressFallback_AudioBook_NativeControlsOff_KeepsFlatAudioStream()
    {
        var handler = CreateHandler();
        var request = CreateResumeRequest();
        var context = CreateContextNoAudioPlayer();

        var user = TestHelpers.CreateTestUser();
        _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = user.Id,
            AnnouncePositionOnResume = false
        });

        var session = CreateEmptySession();

        var chapter = new AudioBook
        {
            Name = "Chapter 7",
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            Path = "/audiobooks/book/chapter7.mp3"
        };

        _fx.LibraryManager.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { chapter });

        var userData = new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks,
            Played = false
        };

        _fx.UserDataManager.Setup(x => x.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Equal((int)TimeSpan.FromMinutes(5).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains($"/Audio/{chapter.Id}/stream?static=true", audioDirective.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-563: the TAIL path (item resolved from the session, the shape a VideoApp book
    /// launch leaves behind because it never sets the AudioPlayer token) must ride the
    /// same sliced playlist, not flat-launch the chapter and lose the seek bar.
    /// </summary>
    [Fact]
    public async Task SessionHeldBook_NativeControlsOn_ResumesViaSlicedHlsPlaylist()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("resume-ab-tail");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);
        try
        {
            var handler = CreateHandler();
            var request = CreateResumeRequest();
            var context = CreateContextNoAudioPlayer();

            var user = TestHelpers.CreateTestUser();
            _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = user.Id,
                AnnouncePositionOnResume = false
            });

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Chapter 3",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter3.mp3"
            };

            var session = CreateEmptySession();
            session.FullNowPlayingItem = chapter;

            tracker.RecordSegment(bookFolderId.ToString(), 61); // conservative position: 60 * 10s = 10 min

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                Assert.Single(response.Response.Directives));
            Assert.Empty(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
            Assert.Contains(
                $"alexaskill/api/video-audio/audiobook/{bookFolderId}/stream.m3u8?start={TimeSpan.FromMinutes(10).Ticks}&token=",
                videoDirective.VideoItem!.Source,
                StringComparison.Ordinal);

            // JF-501: the announce rides the progressive vehicle; the final launch
            // response carries the directive ONLY.
            Assert.Null(response.Response.OutputSpeech);
            Assert.True(handler.Progressive.Contains("Chapter 3"), "progressive announce must speak the chapter title");
        }
        finally
        {
            // Config flag only (not resource teardown): the tracker teardown lives
            // in the swap scope above (JF-633).
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }

    /// <summary>
    /// JF-563 fresh tail shape: a session-held book with NO position anywhere (cold
    /// tracker, no session progress) launches fresh through the VideoApp HLS entry with
    /// no start slice instead of flat-launching.
    /// </summary>
    [Fact]
    public async Task SessionHeldBook_NativeControlsOn_NoPosition_LaunchesFreshWithoutSlice()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        try
        {
            var handler = CreateHandler();
            var request = CreateResumeRequest();
            var context = CreateContextNoAudioPlayer();

            var user = TestHelpers.CreateTestUser();
            _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = user.Id,
                AnnouncePositionOnResume = false
            });

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Chapter 3",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter3.mp3"
            };

            var session = CreateEmptySession();
            session.FullNowPlayingItem = chapter;

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            var videoDirective = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
                Assert.Single(response.Response.Directives));
            Assert.Contains(
                $"alexaskill/api/video-audio/audiobook/{bookFolderId}/stream.m3u8?token=",
                videoDirective.VideoItem!.Source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("start=", videoDirective.VideoItem!.Source, StringComparison.Ordinal);
        }
        finally
        {
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }

    /// <summary>
    /// JF-563: a DISPLACED AudioPlayer token (a different item actually playing) stays
    /// authoritative; the session-held book must not hijack that resume.
    /// </summary>
    [Fact]
    public async Task SessionHeldBook_NativeControlsOn_DisplacedToken_KeepsTokenItemFlat()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("resume-ab-displaced");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);
        try
        {
            var handler = CreateHandler();
            var request = CreateResumeRequest();
            var songId = Guid.NewGuid();
            var context = new Context
            {
                System = new global::Alexa.NET.Request.AlexaSystem
                {
                    Device = new global::Alexa.NET.Request.Device { DeviceID = "test-device" },
                    User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                    ApiAccessToken = "test-token",
                    ApiEndpoint = "https://api.amazonalexa.com"
                },
                AudioPlayer = new PlaybackState
                {
                    PlayerActivity = "IDLE",
                    Token = songId.ToString(),
                    OffsetInMilliseconds = 90_000
                }
            };

            var user = TestHelpers.CreateTestUser();
            _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = user.Id,
                AnnouncePositionOnResume = false
            });

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Chapter 3",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter3.mp3"
            };

            var session = CreateEmptySession();
            session.FullNowPlayingItem = chapter;

            tracker.RecordSegment(bookFolderId.ToString(), 61);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            // The token's item keeps the flat AudioPlayer path; no VideoApp directive.
            var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
            Assert.Contains($"/Audio/{songId}/stream?static=true", audioDirective.AudioItem.Stream.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("audiobook/", audioDirective.AudioItem.Stream.Url, StringComparison.Ordinal);
        }
        finally
        {
            // Config flag only (not resource teardown): the tracker teardown lives
            // in the swap scope above (JF-633).
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }

    /// <summary>
    /// JF-563 review: on a screenless device the book resume degrades to the flat
    /// AudioPlayer path, and the book-absolute tracker position is CLAMPED to the
    /// chapter runtime so the directive never carries an offset past the stream it plays.
    /// </summary>
    [Fact]
    public async Task SessionHeldBook_NativeControlsOn_ScreenlessDevice_DegradesToClampedFlatResume()
    {
        Plugin.Instance!.Configuration.NativeControlsForBooks = true;
        var tracker = TestHelpers.CreatePositionTracker("resume-ab-screenless");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);
        try
        {
            var handler = CreateHandler();
            var request = CreateResumeRequest();
            var context = TestHelpers.CreateScreenlessContext();

            var user = TestHelpers.CreateTestUser();
            _fx.Config.AddUser(new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = user.Id,
                AnnouncePositionOnResume = false
            });

            Guid bookFolderId = Guid.NewGuid();
            var chapter = new AudioBook
            {
                Name = "Chapter 3",
                Id = Guid.NewGuid(),
                ParentId = bookFolderId,
                Path = "/audiobooks/book/chapter3.mp3",
                RunTimeTicks = TimeSpan.FromMinutes(5).Ticks
            };

            var session = CreateEmptySession();
            session.FullNowPlayingItem = chapter;

            // Tracker says 10 min on the book timeline; the chapter is only 5 min long.
            tracker.RecordSegment(bookFolderId.ToString(), 61);

            var response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);

            var audioDirective = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
            Assert.Empty(response.Response.Directives.OfType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>());
            Assert.Equal((int)TimeSpan.FromMinutes(5).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
            Assert.Contains($"/Audio/{chapter.Id}/stream?static=true", audioDirective.AudioItem.Stream.Url, StringComparison.Ordinal);
        }
        finally
        {
            // Config flag only (not resource teardown): the tracker teardown lives
            // in the swap scope above (JF-633).
            Plugin.Instance.Configuration.NativeControlsForBooks = false;
        }
    }
}
