using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-324 PlayNextEpisodeIntentHandler: NextUp resolution ("play the next episode
/// of X" / "play the latest episode of X" / "continue watching X"), the empty-NextUp
/// latest fallback, and the per-user library/content gating into the series query.
/// </summary>
[Collection("Plugin")]
public class PlayNextEpisodeIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly Mock<ITVSeriesManager> _tvSeriesManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayNextEpisodeIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _tvSeriesManagerMock = new Mock<ITVSeriesManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayNextEpisodeIntentHandler CreateHandler()
    {
        return new PlayNextEpisodeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _tvSeriesManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? seriesName = null)
    {
        var intent = new Intent { Name = IntentNames.PlayNextEpisode };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (seriesName != null)
        {
            intent.Slots["series_name"] = new global::Alexa.NET.Request.Slot { Name = "series_name", Value = seriesName };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
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
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private global::MediaBrowser.Controller.Entities.TV.Series SetupSeriesFound(string name = "The Office")
    {
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = name, Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { series });
        return series;
    }

    private global::MediaBrowser.Controller.Entities.TV.Episode SetupNextUp(string name, Guid seriesId)
    {
        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = name,
            Id = Guid.NewGuid(),
            ParentIndexNumber = 3,
            IndexNumber = 2,
            SeriesId = seriesId
        };
        _tvSeriesManagerMock
            .Setup(t => t.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Returns(new QueryResult<BaseItem>(new[] { episode }));
        return episode;
    }

    private void SetupNextUpEmpty()
    {
        _tvSeriesManagerMock
            .Setup(t => t.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Returns(new QueryResult<BaseItem>());
    }

    [Fact]
    public void CanHandle_PlayNextEpisodeIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlayEpisodeIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingSeriesName_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
    }

    [Fact]
    public async Task HandleAsync_SeriesNotFound_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "NonExistent Show");
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
    public async Task HandleAsync_NextUpFound_LaunchesVideoAppAndAnnouncesEpisode()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        var series = SetupSeriesFound();
        var episode = SetupNextUp("The Convention", series.Id);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<VideoAppLaunchDirective>();
        string announceText = TestHelpers.GetSpeechText(response);
        Assert.Contains("The Convention", announceText, StringComparison.Ordinal);
        Assert.Contains("next episode", announceText, StringComparison.Ordinal);
        // The video URL must be the Videos endpoint (VideoApp launch for an Episode).
        var directive = response.Response.Directives!.OfType<VideoAppLaunchDirective>().First();
        Assert.Contains("/Videos/", directive.VideoItem!.Source, StringComparison.Ordinal);
        Assert.Equal(episode.Id.ToString(), session.FullNowPlayingItem?.Id.ToString());
    }

    [Fact]
    public async Task HandleAsync_NextUpQueryIsScopedToUserSeriesAndResumable()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test") { Id = Guid.NewGuid() };
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns(jellyfinUser);
        var series = SetupSeriesFound();
        SetupNextUp("The Convention", series.Id);
        NextUpQuery? captured = null;
        _tvSeriesManagerMock
            .Setup(t => t.GetNextUp(It.IsAny<NextUpQuery>(), It.IsAny<DtoOptions>()))
            .Callback<NextUpQuery, DtoOptions>((q, _) => captured = q)
            .Returns(new QueryResult<BaseItem>(Array.Empty<BaseItem>()));

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Watched-state contract (AC#5): NextUp must be scoped to the resolved user
        // and series, with resumable episodes counted as "next" (the stopped-mid-
        // episode case). The per-user watched filtering itself is Jellyfin's
        // TVSeriesManager responsibility; this pins the query the handler sends.
        Assert.NotNull(captured);
        Assert.Equal(jellyfinUser.Id, captured!.User.Id);
        Assert.Equal(series.Id, captured.SeriesId);
        Assert.True(captured.EnableResumable);
        Assert.Equal(1, captured.Limit);
    }

    [Fact]
    public async Task HandleAsync_NextUpEmpty_FallsBackToLatestEpisode()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        var series = SetupSeriesFound();
        SetupNextUpEmpty();

        var latest = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Finale",
            Id = Guid.NewGuid(),
            ParentIndexNumber = 9,
            IndexNumber = 23,
            SeriesId = series.Id
        };
        InternalItemsQuery? capturedEpisodeQuery = null;
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Callback<InternalItemsQuery>(q => capturedEpisodeQuery = q)
            .Returns(new List<BaseItem> { latest });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<VideoAppLaunchDirective>();
        string announceText = TestHelpers.GetSpeechText(response);
        Assert.Contains("Finale", announceText, StringComparison.Ordinal);
        Assert.Contains("latest episode", announceText, StringComparison.Ordinal);
        // Latest fallback semantics: the most recently created episode of the series.
        Assert.NotNull(capturedEpisodeQuery);
        Assert.Equal((ItemSortBy.DateCreated, SortOrder.Descending), capturedEpisodeQuery!.OrderBy[0]);
        Assert.Contains(series.Id, capturedEpisodeQuery.AncestorIds);
    }

    [Fact]
    public async Task HandleAsync_NoNextUpAndNoEpisodes_ReturnsNoNextEpisode()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        SetupSeriesFound();
        SetupNextUpEmpty();
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
        Assert.Contains("next episode", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_ResumableNextUp_AnnouncesResume()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        var series = SetupSeriesFound();
        SetupNextUp("The Convention", series.Id);
        _userDataManagerMock
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<VideoAppLaunchDirective>();
        Assert.Contains("Resuming", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_SeriesQueryAppliesPerUserLibraryFilter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var libraryId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId.ToString() });
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock
            .Setup(l => l.GetItemById(libraryId))
            .Returns(new Folder { Id = libraryId });
        InternalItemsQuery? capturedSeriesQuery = null;
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Callback<InternalItemsQuery>(q => capturedSeriesQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Library gating (AC#5): the series search runs through ApplyLibraryFilter,
        // so a restricted user only resolves series inside their allowed libraries.
        Assert.NotNull(capturedSeriesQuery);
        Assert.NotNull(capturedSeriesQuery!.TopParentIds);
        Assert.Contains(libraryId, capturedSeriesQuery.TopParentIds);
    }

    [Fact]
    public async Task HandleAsync_VideosDisabled_ReturnsMediaTypeNotAvailable()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory, c => c.VideosEnabled = false, "alexa-playnextepisode-test");
        try
        {
            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            response.Tells();
            _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        }
        finally
        {
            TestHelpers.EnsurePluginInstance(
                _config, _loggerFactory, c => c.VideosEnabled = true, "alexa-playnextepisode-test");
        }
    }
}
