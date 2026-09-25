using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch 11 tests for the TV next-up collaborator. The series resolution's
/// fuzzy-fallback arm was characterized FIRST (green on the pre-move BaseHandler
/// code via a probe subclass, then retargeted to direct construction); the NextUp
/// core, the latest fallback, the resume announce, and the content/library gating
/// arms keep their handler-level coverage
/// (PlayNextEpisodeIntentHandlerTests / PlayEpisodeIntentHandlerTests), the
/// batch-6 scoping precedent.
/// </summary>
[Collection("Plugin")]
public class TvNextUpServiceTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory;

    public TvNextUpServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    [Fact]
    public async Task ResolveSeries_SearchTermMiss_FuzzyFallbackResolvesSeries()
    {
        var libraryManager = new Mock<ILibraryManager>();
        // The indexed SearchTerm tier misses (Jellyfin search returns nothing)...
        libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == "the office")))
            .Returns(new List<BaseItem>());
        // ...so the fuzzy tier's broad scan (no SearchTerm, Limit 500) runs and finds it.
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };
        libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == null && q.Limit == 500)))
            .Returns(new List<BaseItem> { series });

        var service = CreateService();

        var (resolved, error) = await service.ResolveSeriesForPlaybackAsync(
            libraryManager.Object,
            TestHelpers.CreateJellyfinUser(),
            TestHelpers.CreateTestUser(),
            "the office",
            "en-US",
            CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(resolved);
        Assert.Equal("The Office", resolved!.Name);
        // Both tiers ran: the miss is what arms the fallback.
        libraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == "the office")), Times.Once);
        libraryManager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == null && q.Limit == 500)), Times.Once);
    }

    // ---------------------------------------------------------------------
    // the composition seam
    // ---------------------------------------------------------------------

    [Fact]
    public void TvNextUp_Property_Wired_By_BaseHandler_Ctor()
    {
        var handler = new SharedGateProbeHandler(new Mock<ISessionManager>().Object, new PluginConfiguration(), _loggerFactory);

        Assert.NotNull(handler.TvNextUp);
        // The getter hands out ONE stable instance (not a fresh collaborator per
        // access); the ctor line above is what pins the per-handler wiring.
        Assert.Same(handler.TvNextUp, handler.TvNextUp);
    }

    /// <summary>
    /// JF-565: a next-up episode with playback progress is a resume (NextUp runs
    /// EnableResumable, and the announce already says "Resuming"), so the remux-routed
    /// launch URL must carry the stored position (?start=).
    /// </summary>
    [Fact]
    public async Task PlayNextUp_InProgressRemuxEpisode_MintsStartSliceOnLaunchUrl()
    {
        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "The Convention",
            episodeId,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        episode.RunTimeTicks = TimeSpan.FromMinutes(30).Ticks;
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };
        long resumeTicks = TimeSpan.FromMinutes(12).Ticks;

        SkillResponse response = await PlayNextUpAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = resumeTicks },
            queueSeeding: null);

        var directive = Assert.IsType<VideoAppLaunchDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains($"/alexaskill/api/video-audio/episode/{episodeId}/stream.m3u8?start={resumeTicks}&token=", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-565 + JF-581: when UserData reads 0 (the live server-side write-loss shape)
    /// the plugin-owned ItemPositionState must seed the resume slice; the fallback arm
    /// is live on this path because the episode comes from the NextUp query, not from
    /// a UserData progress scan.
    /// </summary>
    [Fact]
    public async Task PlayNextUp_UserDataWriteLoss_SeedsStartSliceFromItemPositionState()
    {
        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "The Convention",
            episodeId,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        episode.RunTimeTicks = TimeSpan.FromMinutes(30).Ticks;
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };
        long storedTicks = TimeSpan.FromMinutes(6).Ticks;

        SkillResponse response = await PlayNextUpAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 },
            queueSeeding: queue => queue.ItemPositionState[episodeId.ToString("N")] = storedTicks);

        var directive = Assert.IsType<VideoAppLaunchDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains($"?start={storedTicks}&token=", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-586: the user's primary use case ("riproduci l'ultimo episodio di morning"
    /// on an Echo Dot): on a screenless device the shared launch tail degrades to the
    /// AudioPlayer audio-only route instead of the VideoRequiresScreen refusal. An
    /// eac3 episode rides the audio-only HLS transcode with the resume position
    /// minted as ?start= (directive offset 0, JF-507), the announce survives on the
    /// final response, and the session queue seeding still applies (the JF-324
    /// auto-advance signal on the AudioPlayer path).
    /// </summary>
    [Fact]
    public async Task PlayLatestEpisode_ScreenlessDevice_DegradesToAudioPlayerTranscodeWithResume()
    {
        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Morning #42",
            episodeId,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"))
        {
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Morning", Id = Guid.NewGuid() };
        long resumeTicks = TimeSpan.FromMinutes(12).Ticks;

        var session = TestHelpers.CreateTestSession(new Mock<ISessionManager>().Object, _loggerFactory);
        SkillResponse response = await PlayLatestAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = resumeTicks },
            TestHelpers.CreateScreenlessContext(),
            session);

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains($"/alexaskill/api/video-audio/episode/{episodeId}/audio.m3u8?start={resumeTicks}&token=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession, "JF-299: the AudioPlayer play ends the session");
        Assert.Contains("Morning #42", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.DoesNotContain("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        // Queue/now-playing coherence on the audio route (feeds the JF-324 advance).
        Assert.Equal(episodeId.ToString(), session.FullNowPlayingItem?.Id.ToString());
    }

    /// <summary>
    /// JF-586 static-route arm: a decodable episode on a screenless device degrades
    /// to the plain static /Audio stream URL with NO offset and no refusal speech.
    /// </summary>
    [Fact]
    public async Task PlayNextUp_ScreenlessDevice_DecodableEpisode_UsesStaticAudioStream()
    {
        var episodeId = Guid.NewGuid();
        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "The Convention",
            Id = episodeId,
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };

        SkillResponse response = await PlayNextUpAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 },
            queueSeeding: null,
            context: TestHelpers.CreateScreenlessContext());

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains($"/Audio/{episodeId}/stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.True(response.Response.ShouldEndSession, "JF-299: the AudioPlayer play ends the session");
        Assert.DoesNotContain("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-586: AudioPlayer CAN seek a static stream (unlike the VideoApp Static
    /// route), so an in-progress decodable episode carries its resume position on the
    /// DIRECTIVE on the screenless degrade.
    /// </summary>
    [Fact]
    public async Task PlayNextUp_ScreenlessDevice_InProgressDecodableEpisode_CarriesResumeOffsetOnDirective()
    {
        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "The Convention",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };
        long resumeTicks = TimeSpan.FromMinutes(10).Ticks;

        SkillResponse response = await PlayNextUpAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = resumeTicks },
            queueSeeding: null,
            context: TestHelpers.CreateScreenlessContext());

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal((int)TimeSpan.FromMinutes(10).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains("/Audio/", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-586 clamp: a stored position at or beyond the runtime cannot be a
    /// legitimate mid-episode resume (the JF-565 fail-closed rule the VideoApp slice
    /// applies), so the degrade plays from the start instead of minting an offset
    /// the stream cannot serve.
    /// </summary>
    [Fact]
    public async Task PlayNextUp_ScreenlessDevice_StalePositionBeyondRuntime_PlaysFromStart()
    {
        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "The Convention",
            Id = Guid.NewGuid(),
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };

        SkillResponse response = await PlayNextUpAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks },
            queueSeeding: null,
            context: TestHelpers.CreateScreenlessContext());

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.DoesNotContain("start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-589: when BOTH position stores are empty but the request context still
    /// carries a residual AudioPlayer state whose token is the SAME episode (the
    /// device-counter signal that survives restarts), the fresh re-ask seeds its
    /// resume from that context offset. Audio-only content rides the AudioPlayer
    /// static route, so the offset is visible directly on the directive.
    /// </summary>
    [Fact]
    public async Task PlayLatest_EmptyStores_ContextTokenMatchesEpisode_SeedsResumeFromContextOffset()
    {
        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Morning #1287",
            episodeId,
            TestHelpers.TestStream(MediaStreamType.Audio, "mp3"))
        {
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Morning", Id = Guid.NewGuid() };
        var context = TestHelpers.CreateContextWithVideoApp();
        context.AudioPlayer = new global::Alexa.NET.Request.Type.PlaybackState
        {
            Token = episodeId.ToString(),
            OffsetInMilliseconds = 27_599,
            PlayerActivity = "STOPPED"
        };

        SkillResponse response = await PlayLatestAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 },
            context);

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Contains("/Audio/", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(27_599, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// JF-589 guard: a real stored position always wins over the residual context,
    /// which can be stale in ways the stores are not.
    /// </summary>
    [Fact]
    public async Task PlayLatest_StoredPositionPresent_StoredPositionWinsOverContext()
    {
        var episodeId = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Morning #1287",
            episodeId,
            TestHelpers.TestStream(MediaStreamType.Audio, "mp3"))
        {
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Morning", Id = Guid.NewGuid() };
        var context = TestHelpers.CreateContextWithVideoApp();
        context.AudioPlayer = new global::Alexa.NET.Request.Type.PlaybackState
        {
            Token = episodeId.ToString(),
            OffsetInMilliseconds = 27_599,
            PlayerActivity = "STOPPED"
        };

        SkillResponse response = await PlayLatestAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(10).Ticks },
            context);

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal((int)TimeSpan.FromMinutes(10).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// JF-589 guard: a residual context token for a DIFFERENT episode (the previous
    /// thing this device played) never seeds this launch; the fresh start stands.
    /// </summary>
    [Fact]
    public async Task PlayLatest_ContextTokenForDifferentEpisode_NoSeed()
    {
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Morning #1287",
            Guid.NewGuid(),
            TestHelpers.TestStream(MediaStreamType.Audio, "mp3"))
        {
            RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
        };
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Morning", Id = Guid.NewGuid() };
        var context = TestHelpers.CreateContextWithVideoApp();
        context.AudioPlayer = new global::Alexa.NET.Request.Type.PlaybackState
        {
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 27_599,
            PlayerActivity = "STOPPED"
        };

        SkillResponse response = await PlayLatestAsync(
            episode, series,
            new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 },
            context);

        var directive = Assert.IsType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>(Assert.Single(response.Response.Directives));
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// Drives <see cref="TvNextUpService.PlayNextUpEpisodeAsync"/> with the given
    /// episode stubbed as the NextUp result. <paramref name="queueSeeding"/> runs
    /// against a real DeviceQueueManager swapped into Plugin.Instance (the JF-581
    /// read side) when the ItemPositionState fallback arm is under test.
    /// </summary>
    private async Task<SkillResponse> PlayNextUpAsync(
        BaseItem episode,
        BaseItem series,
        UserItemData userData,
        Action<DeviceQueue>? queueSeeding,
        global::Alexa.NET.Request.Context? context = null)
    {
        var tv = new Mock<MediaBrowser.Controller.TV.ITVSeriesManager>();
        tv.Setup(t => t.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Returns(new QueryResult<BaseItem>(new[] { episode }));
        var library = new Mock<ILibraryManager>();
        var userDataMock = new Mock<IUserDataManager>();
        userDataMock.Setup(u => u.GetUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        var session = TestHelpers.CreateTestSession(new Mock<ISessionManager>().Object, _loggerFactory);
        var service = CreateService();

        // JF-630: the ONE swap scope (using-on-null is a no-op, so the unseeded
        // shape needs no branch).
        using IDisposable? queueSwap = queueSeeding != null ? Swap(queueSeeding) : null;
        return await PlayCore();

        IDisposable? Swap(Action<DeviceQueue> seed)
        {
            TestHelpers.EnsurePluginInstance(
                new PluginConfiguration(), _loggerFactory, _ => { }, nameof(TvNextUpServiceTests));
            DeviceQueueManager queue = TestHelpers.CreateDeviceQueueManager("tvnextup-jf565");
            seed(queue.GetOrCreateQueue("test-device"));
            return TestHelpers.SwapPluginQueueManager(queue);
        }

        async Task<SkillResponse> PlayCore()
        {
            return await service.PlayNextUpEpisodeAsync(
                tv.Object,
                library.Object,
                userDataMock.Object,
                TestHelpers.CreateJellyfinUser(),
                TestHelpers.CreateTestUser(),
                session,
                series,
                "en-US",
                context ?? TestHelpers.CreateTestContext(),
                new IntentRequest { Locale = "en-US" },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Drives <see cref="TvNextUpService.PlayLatestEpisodeAsync"/> (the JF-583
    /// recency core) with the given episode stubbed as the recency winner.
    /// </summary>
    private async Task<SkillResponse> PlayLatestAsync(
        BaseItem episode,
        BaseItem series,
        UserItemData userData,
        global::Alexa.NET.Request.Context context,
        SessionInfo? session = null)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });
        var userDataMock = new Mock<IUserDataManager>();
        userDataMock.Setup(u => u.GetUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(userData);

        session ??= TestHelpers.CreateTestSession(new Mock<ISessionManager>().Object, _loggerFactory);
        var service = CreateService();

        return await service.PlayLatestEpisodeAsync(
            library.Object,
            userDataMock.Object,
            TestHelpers.CreateJellyfinUser(),
            TestHelpers.CreateTestUser(),
            session,
            series,
            "en-US",
            context,
            new IntentRequest { Locale = "en-US" },
            CancellationToken.None);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private TvNextUpService CreateService()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        return new TvNextUpService(
            _loggerFactory.CreateLogger<TvNextUpServiceTests>(),
            new SearchService(config, _loggerFactory.CreateLogger<SearchService>(), requestTimeoutMs: 6000),
            TestHelpers.CreateLaunchBuilder(config),
            requestTimeoutMs: 6000);
    }
}
