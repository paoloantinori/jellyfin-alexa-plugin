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
    /// Drives <see cref="TvNextUpService.PlayNextUpEpisodeAsync"/> with the given
    /// episode stubbed as the NextUp result. <paramref name="queueSeeding"/> runs
    /// against a real DeviceQueueManager swapped into Plugin.Instance (the JF-581
    /// read side) when the ItemPositionState fallback arm is under test.
    /// </summary>
    private async Task<SkillResponse> PlayNextUpAsync(
        BaseItem episode,
        BaseItem series,
        UserItemData userData,
        Action<DeviceQueue>? queueSeeding)
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

        DeviceQueueManager? queue = null;
        DeviceQueueManager? previous = null;
        try
        {
            if (queueSeeding != null)
            {
                TestHelpers.EnsurePluginInstance(
                    new PluginConfiguration(), _loggerFactory, _ => { }, nameof(TvNextUpServiceTests));
                queue = TestHelpers.CreateDeviceQueueManager("tvnextup-jf565");
                queueSeeding(queue.GetOrCreateQueue("test-device"));
                previous = Plugin.Instance!.DeviceQueueManager;
                Plugin.Instance!.DeviceQueueManager = queue;
            }

            return await service.PlayNextUpEpisodeAsync(
                tv.Object,
                library.Object,
                userDataMock.Object,
                TestHelpers.CreateJellyfinUser(),
                TestHelpers.CreateTestUser(),
                session,
                series,
                "en-US",
                TestHelpers.CreateTestContext(),
                new IntentRequest { Locale = "en-US" },
                CancellationToken.None);
        }
        finally
        {
            if (queue != null)
            {
                Plugin.Instance!.DeviceQueueManager = previous;
                queue.Dispose();
            }
        }
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
