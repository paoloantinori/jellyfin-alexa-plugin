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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;
using MediaType = Jellyfin.Data.Enums.MediaType;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class PlayPodcastIntentHandlerTests
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
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
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

        Assert.NotNull(response);
        response.Tells();
    }

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

        var podcast = new global::MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { podcast });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
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

        var podcast = new global::MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
            SeriesId = podcast.Id
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { podcast });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Returns(new List<BaseItem> { episode });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
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

        var podcast = new global::MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var oldEpisode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
            SeriesId = podcast.Id,
            DateCreated = DateTime.UtcNow.AddDays(-10)
        };

        var newEpisode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 12",
            Id = Guid.NewGuid(),
            SeriesId = podcast.Id,
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { podcast });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Returns(new List<BaseItem> { oldEpisode, newEpisode });

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

        var podcast = new global::MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Serial",
            Id = Guid.NewGuid()
        };

        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Episode 1",
            Id = Guid.NewGuid(),
            SeriesId = podcast.Id
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { podcast });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
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
        Assert.True(response.Response.ShouldEndSession);
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

        InternalItemsQuery? capturedSeriesQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Callback<InternalItemsQuery>(q => capturedSeriesQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedSeriesQuery);
        Assert.NotNull(capturedSeriesQuery.MediaTypes);
        Assert.Contains(MediaType.Audio, capturedSeriesQuery.MediaTypes);
        Assert.Equal("Serial", capturedSeriesQuery.SearchTerm);
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

        var podcast1 = new global::MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "The Daily",
            Id = Guid.NewGuid()
        };

        var podcast2 = new global::MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "Daily Tech News",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { podcast1, podcast2 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.True(response.SessionAttributes.ContainsKey("disambig_type"));
    }
}
