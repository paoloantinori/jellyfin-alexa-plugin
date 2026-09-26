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
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayPodcastIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayPodcastIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayPodcastIntentHandler CreateHandler()
    {
        return new PlayPodcastIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? podcastName = null, string? dialogState = "COMPLETED")
    {
        var intent = new Intent { Name = IntentNames.PlayPodcast };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (podcastName != null)
        {
            intent.Slots["podcast_name"] = new global::Alexa.NET.Request.Slot { Name = "podcast_name", Value = podcastName };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req", DialogState = dialogState };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
    }

    [Fact]
    public void CanHandle_PlayPodcastIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingPodcastName_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        TestHelpers.AssertElicitsSlot(response, "podcast_name", IntentNames.PlayPodcast);    }

    [Fact]
    public async Task HandleAsync_PodcastNotFound_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "NonExistent Podcast");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
    }

    [Fact]
    public async Task HandleAsync_PodcastFound_NoEpisodes_ReturnsNoEpisodes()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var podcast = new MusicAlbum
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        SetupShapeQueries(new List<BaseItem> { podcast }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
    }

    [Fact]
    public async Task HandleAsync_EpisodeFound_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var podcast = new MusicAlbum
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var episode = new Audio
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
        };

        SetupShapeQueries(new List<BaseItem> { podcast }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    /// <summary>
    /// JF-636: the standing podcast rate redirects the fresh play to the atempo
    /// speed endpoint (no ?start=, from-zero encode); a null preference keeps the
    /// plain codec-routed/static launch byte-identical to the pre-JF-636 path.
    /// </summary>
    [Fact]
    public async Task HandleAsync_StandingSpeedPreference_LaunchesTheSpeedEndpoint()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        user.PodcastSpeedPerMille = 1500;
        var session = CreateSession();

        SetupUserMock();

        var podcast = new MusicAlbum
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var episode = new Audio
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
        };

        SetupShapeQueries(new List<BaseItem> { podcast }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().Single();
        Assert.Contains($"/alexaskill/api/audio-speed/{episode.Id}/1500/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>The null-preference twin: no speed endpoint in the launch URL.</summary>
    [Fact]
    public async Task HandleAsync_NoStandingSpeedPreference_KeepsThePlainLaunch()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        user.PodcastSpeedPerMille = null;
        var session = CreateSession();

        SetupUserMock();

        var podcast = new MusicAlbum
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var episode = new Audio
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
        };

        SetupShapeQueries(new List<BaseItem> { podcast }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().Single();
        Assert.DoesNotContain("audio-speed", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_MultipleEpisodes_PicksMostRecent()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var podcast = new MusicAlbum
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var oldEpisode = new Audio
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-10)
        };

        var newEpisode = new Audio
        {
            Name = "Episode 12",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        SetupShapeQueries(new List<BaseItem> { podcast }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { newEpisode, oldEpisode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(newEpisode.Id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task HandleAsync_SetsQueueAndNowPlayingItem()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var podcast = new MusicAlbum
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var episode = new Audio
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
        };

        SetupShapeQueries(new List<BaseItem> { podcast }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { episode });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(episode.Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(episode, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task HandleAsync_DialogStarted_ElicitsPodcastName()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(dialogState: "STARTED");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        TestHelpers.AssertSessionOpen(response, "a question must keep the session open or the mic never listens");
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        Assert.NotNull(elicit);
        Assert.Equal("podcast_name", elicit!.SlotToElicit);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task HandleAsync_DialogInProgress_ElicitsMissingInfo()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial", dialogState: "IN_PROGRESS");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task HandleAsync_PodcastQueryFiltersByAudioMediaType()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // Capture the FIRST query (podcast discovery). With the fix this must target
        // MusicAlbum, NOT Series, and must NOT apply a MediaTypes=Audio filter
        // (a MusicAlbum rollup is MediaType=Unknown in Jellyfin, so that filter would
        // exclude every album, which is the production bug this test now guards against).
        InternalItemsQuery? capturedDiscoveryQuery = null;
        int captureCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => { if (captureCount++ == 0) capturedDiscoveryQuery = q; })
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedDiscoveryQuery);
        Assert.NotNull(capturedDiscoveryQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.MusicAlbum, capturedDiscoveryQuery.IncludeItemTypes);
        Assert.DoesNotContain(BaseItemKind.Series, capturedDiscoveryQuery.IncludeItemTypes);
        // No MediaType filter on the album rollup (the old bug).
        Assert.True(capturedDiscoveryQuery.MediaTypes == null || capturedDiscoveryQuery.MediaTypes.Length == 0);
        Assert.Equal("Serial", capturedDiscoveryQuery.SearchTerm);
    }

    /// <summary>
    /// Guards the episode-selection query shape: it must fetch Audio children scoped to
    /// the matched album via ParentId, sorted newest-first, NOT query Episode/MediaTypes=Audio
    /// (the old dead path). Returns a real album so the handler reaches the episode query.
    /// </summary>
    [Fact]
    public async Task HandleAsync_EpisodeQueryTargetsAudioChildrenOfAlbum()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var album = new MusicAlbum { Name = "Serial", Id = Guid.NewGuid() };

        // Two distinct Setups by IncludeItemTypes, matching the sibling-test pattern:
        // album discovery (MusicAlbum) returns the album; episode query (Audio) captures itself.
        SetupShapeQueries(new List<BaseItem> { album }, new List<BaseItem>());

        InternalItemsQuery? capturedEpisodeQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Callback<InternalItemsQuery>(q => capturedEpisodeQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Episode 1", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedEpisodeQuery);
        Assert.NotNull(capturedEpisodeQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Audio, capturedEpisodeQuery.IncludeItemTypes);
        Assert.DoesNotContain(BaseItemKind.Episode, capturedEpisodeQuery.IncludeItemTypes);
        Assert.Equal(album.Id, capturedEpisodeQuery.ParentId);
    }

    /// <summary>
    /// Wires the JF-640 both-shapes discovery mocks: the MusicAlbum query returns
    /// <paramref name="albums"/> and the Series query returns <paramref name="series"/>
    /// (both queries always run since JF-640).
    /// </summary>
    private void SetupShapeQueries(IReadOnlyList<BaseItem> albums, IReadOnlyList<BaseItem> series)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))))
            .Returns(albums);

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(series);
    }

    /// <summary>
    /// Wires the JF-640 fuzzy-fallback mocks: both SearchTerm shape queries miss,
    /// and the SearchItemsFuzzyAsync query (no SearchTerm, both kinds) returns
    /// <paramref name="fuzzyCandidates"/>.
    /// </summary>
    private void SetupSearchTermMiss_FuzzyHits(IReadOnlyList<BaseItem> fuzzyCandidates)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.SearchTerm == null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum))))
            .Returns(fuzzyCandidates);
    }

    /// <summary>
    /// Wires the JF-599 series-shape mocks: album discovery misses, the Series
    /// fallback matches <paramref name="series"/>, and the episode query returns
    /// <paramref name="episodes"/> (queries appended to <paramref name="capturedQueries"/>
    /// when non-null, for shape assertions).
    /// </summary>
    private void SetupSeriesFallback(MediaBrowser.Controller.Entities.TV.Series series, IReadOnlyList<BaseItem> episodes, List<InternalItemsQuery>? capturedQueries = null)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicAlbum) && !q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { series });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Callback<InternalItemsQuery>(q => capturedQueries?.Add(q))
            .Returns(episodes);
    }

    /// <summary>
    /// JF-599: the IlPost storage shape. When the MusicAlbum query misses, the handler
    /// must fall back to a Series query and play the newest Episode descendant
    /// (episodes nest under season folders, so the series is an ancestor, not the parent).
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumMiss_SeriesMatch_PlaysNewestEpisode()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "generazione");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Generazione",
            Id = Guid.NewGuid()
        };

        var oldEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-10)
        };

        var newEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 12",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        SetupSeriesFallback(series, new List<BaseItem> { newEpisode, oldEpisode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(newEpisode.Id, session.NowPlayingQueue[0].Id);
    }

    /// <summary>
    /// JF-599: for a matched Series the episode query must scope by AncestorIds (season
    /// folders sit between the series and its episodes) with no ParentId, and accept
    /// only Episode children (JF-611: no real storage shape puts an Audio item under
    /// a Series ancestor). The album shape keeps ParentId (sibling test).
    /// </summary>
    [Fact]
    public async Task HandleAsync_SeriesShape_EpisodeQueryUsesAncestorIdsNotParentId()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "generazione");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Generazione",
            Id = Guid.NewGuid()
        };

        var episodeQueries = new List<InternalItemsQuery>();
        SetupSeriesFallback(
            series,
            new List<BaseItem> { new MediaBrowser.Controller.Entities.TV.Episode { Name = "Episode 1", Id = Guid.NewGuid() } },
            episodeQueries);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        InternalItemsQuery episodeQuery = Assert.Single(episodeQueries);
        Assert.NotNull(episodeQuery.AncestorIds);
        Assert.Contains(series.Id, episodeQuery.AncestorIds);
        Assert.Equal(Guid.Empty, episodeQuery.ParentId);
        Assert.NotNull(episodeQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Episode, episodeQuery.IncludeItemTypes);
        // JF-611: Audio is deliberately absent from the series shape (no real
        // storage shape puts an Audio item under a Series ancestor).
        Assert.DoesNotContain(BaseItemKind.Audio, episodeQuery.IncludeItemTypes);
    }

    /// <summary>
    /// JF-640 contract (replaces the JF-599 "never queries Series" pin): BOTH shape
    /// queries always run, even when the MusicAlbum query matched. The old
    /// conditional fallback made the Series shape unreachable whenever any album
    /// matched the search term, so the user's exactly-named 'Morning' podcast series
    /// was never even queried (the live JF-640 incident).
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumMatch_StillQueriesSeries()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var album = new MusicAlbum { Name = "Serial", Id = Guid.NewGuid() };

        SetupShapeQueries(new List<BaseItem> { album }, new List<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Returns(new List<BaseItem> { new Audio { Name = "Episode 1", Id = Guid.NewGuid() } });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        _libraryManagerMock.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)),
            Times.Once);
    }

    /// <summary>
    /// JF-599: a matched Series with zero playable children answers the NoEpisodes
    /// Tell instead of crashing or silently falling through to not-found.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SeriesMatch_NoEpisodes_ReturnsNoEpisodes()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "generazione");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Generazione",
            Id = Guid.NewGuid()
        };

        SetupSeriesFallback(series, new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
    }

    [Fact]
    public async Task HandleAsync_MultiplePodcasts_ReturnsDisambiguation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "Daily");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var podcast1 = new MusicAlbum
        {
            Name = "Morning Edition",
            Id = Guid.NewGuid()
        };

        var podcast2 = new MusicAlbum
        {
            Name = "All Things Considered",
            Id = Guid.NewGuid()
        };

        SetupShapeQueries(new List<BaseItem> { podcast1, podcast2 }, new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.True(response.SessionAttributes.ContainsKey("disambig_type"));
    }

    /// <summary>
    /// JF-640 (the live incident shape): the album query returns music albums AND the
    /// series query returns the exactly-named podcast; asking 'morning' must play the
    /// SERIES, never fuzzy-accept an album (live: 'Euphoria Morning' scored 90 and
    /// played a song). Proven via the episode query shape: the series container
    /// resolves episodes by AncestorIds, never ParentId.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ExactSeriesName_WinsOverAlbumMatches_PlaysTheSeries()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "morning");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var album = new MusicAlbum { Name = "Euphoria Morning", Id = Guid.NewGuid() };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "Morning", Id = Guid.NewGuid() };
        var newestEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Morning Weekend",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        SetupShapeQueries(new List<BaseItem> { album }, new List<BaseItem> { series });

        InternalItemsQuery? capturedEpisodeQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Callback<InternalItemsQuery>(q => capturedEpisodeQuery = q)
            .Returns(new List<BaseItem> { newestEpisode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(newestEpisode.Id, session.NowPlayingQueue[0].Id);
        // The played container is the SERIES: episodes resolve by ancestor, not parent.
        Assert.NotNull(capturedEpisodeQuery);
        Assert.NotNull(capturedEpisodeQuery.AncestorIds);
        Assert.Contains(series.Id, capturedEpisodeQuery.AncestorIds);
        Assert.Equal(Guid.Empty, capturedEpisodeQuery.ParentId);
    }

    /// <summary>
    /// JF-640 cross-type fuzzy guard (single-candidate shape): when the fuzzy
    /// fallback's best is a MusicAlbum ('Euphoria Morning' at the live score 90 for
    /// 'morning', both SearchTerm shape queries missed), the handler must PROMPT
    /// instead of auto-playing a music album for a podcast query. The yes/no confirm
    /// rides the podcast disambiguation state (YesIntentHandler plays it on "yes").
    /// </summary>
    [Fact]
    public async Task HandleAsync_AlbumOnlyFuzzyHit_PromptsInsteadOfAutoPlaying()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "morning");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var album = new MusicAlbum { Name = "Euphoria Morning", Id = Guid.NewGuid() };

        SetupSearchTermMiss_FuzzyHits(new List<BaseItem> { album });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.Equal("podcast", response.SessionAttributes["disambig_type"]?.ToString());
        Assert.Empty(response.Response.Directives?.OfType<AudioPlayerPlayDirective>() ?? Enumerable.Empty<AudioPlayerPlayDirective>());
        Assert.Empty(session.NowPlayingQueue ?? new List<QueueItem>());
    }

    /// <summary>
    /// JF-640 community-plugin shape boundary pin: an EXACT album name still
    /// auto-plays even when a second album fuzzy-matches the same query (the exact
    /// pass picks the exact name before any fuzzy or guard logic runs). Lowercase
    /// slot value proves the comparison is OrdinalIgnoreCase.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ExactAlbumName_StillAutoPlays()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "serial");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var exactAlbum = new MusicAlbum { Name = "Serial", Id = Guid.NewGuid() };
        var otherAlbum = new MusicAlbum { Name = "Something Serial", Id = Guid.NewGuid() };

        SetupShapeQueries(new List<BaseItem> { exactAlbum, otherAlbum }, new List<BaseItem>());

        InternalItemsQuery? capturedEpisodeQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))))
            .Callback<InternalItemsQuery>(q => capturedEpisodeQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Episode 1", Id = Guid.NewGuid() } });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        // The EXACT album played (ParentId scoping), not the fuzzy alternative.
        Assert.NotNull(capturedEpisodeQuery);
        Assert.Equal(exactAlbum.Id, capturedEpisodeQuery.ParentId);
    }

    /// <summary>
    /// JF-640: a SERIES fuzzy hit keeps the >= 90 auto-accept (only MusicAlbum hits
    /// downgrade to the confirm prompt). Search-term miss, fuzzy finds the series.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SeriesFuzzyHit_StillAutoAccepts()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "GENERAZIONE");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "Generazione", Id = Guid.NewGuid() };
        var newestEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 12",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        SetupSearchTermMiss_FuzzyHits(new List<BaseItem> { series });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Returns(new List<BaseItem> { newestEpisode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(newestEpisode.Id, session.NowPlayingQueue[0].Id);
    }

    /// <summary>
    /// JF-640 cross-type fuzzy guard (multi-candidate shape, the live incident minus
    /// the series): several music albums match the search term with no exact name;
    /// the fuzzy best would have auto-accepted at the containment score (90,
    /// 'Euphoria Morning' for 'morning') but must now confirm instead of silently
    /// playing an album.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MultipleAlbumMatches_NoExact_ConfirmsInsteadOfAutoPlaying()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "morning");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var euphoria = new MusicAlbum { Name = "Euphoria Morning", Id = Guid.NewGuid() };
        var phase = new MusicAlbum { Name = "Morning Phase", Id = Guid.NewGuid() };

        SetupShapeQueries(new List<BaseItem> { euphoria, phase }, new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.Equal("podcast", response.SessionAttributes["disambig_type"]?.ToString());
        Assert.Empty(response.Response.Directives?.OfType<AudioPlayerPlayDirective>() ?? Enumerable.Empty<AudioPlayerPlayDirective>());
        Assert.Empty(session.NowPlayingQueue ?? new List<QueueItem>());
    }

    /// <summary>
    /// JF-640 guard boundary: with a MusicAlbum in the candidate set, a SERIES that
    /// wins the fuzzy scoring keeps the auto-accept (the guard targets albums only).
    /// </summary>
    [Fact]
    public async Task HandleAsync_MultiCandidate_SeriesBest_StillAutoAccepts()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(podcastName: "daily");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var album = new MusicAlbum { Name = "Euphoria Morning", Id = Guid.NewGuid() };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "The Daily", Id = Guid.NewGuid() };
        var newestEpisode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 12",
            Id = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        SetupShapeQueries(new List<BaseItem> { album }, new List<BaseItem> { series });

        InternalItemsQuery? capturedEpisodeQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Callback<InternalItemsQuery>(q => capturedEpisodeQuery = q)
            .Returns(new List<BaseItem> { newestEpisode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(newestEpisode.Id, session.NowPlayingQueue[0].Id);
        // The SERIES won the fuzzy pick (ancestor-scoped episode query), not the album.
        Assert.NotNull(capturedEpisodeQuery);
        Assert.NotNull(capturedEpisodeQuery.AncestorIds);
        Assert.Contains(series.Id, capturedEpisodeQuery.AncestorIds);
    }
}
