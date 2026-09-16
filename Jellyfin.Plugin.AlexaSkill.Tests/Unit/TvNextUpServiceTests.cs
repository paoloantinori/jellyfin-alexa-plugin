using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private TvNextUpService CreateService()
    {
        var config = new PluginConfiguration();
        return new TvNextUpService(
            _loggerFactory.CreateLogger<TvNextUpServiceTests>(),
            new SearchService(config, _loggerFactory.CreateLogger<SearchService>(), requestTimeoutMs: 6000),
            TestHelpers.CreateLaunchBuilder(config),
            requestTimeoutMs: 6000);
    }
}
