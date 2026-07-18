using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayEpisodeIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayEpisodeIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayEpisodeIntentHandler CreateHandler()
    {
        return new PlayEpisodeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? seriesName = null, string? seasonNumber = null, string? episodeNumber = null, string? dialogState = "COMPLETED")
    {
        var intent = new Intent { Name = IntentNames.PlayEpisode };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (seriesName != null)
        {
            intent.Slots["series_name"] = new global::Alexa.NET.Request.Slot { Name = "series_name", Value = seriesName };
        }

        if (seasonNumber != null)
        {
            intent.Slots["season_number"] = new global::Alexa.NET.Request.Slot { Name = "season_number", Value = seasonNumber };
        }

        if (episodeNumber != null)
        {
            intent.Slots["episode_number"] = new global::Alexa.NET.Request.Slot { Name = "episode_number", Value = episodeNumber };
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
    public void CanHandle_PlayEpisodeIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office", seasonNumber: "4", episodeNumber: "10");

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
    public async Task HandleAsync_MissingSeriesName_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seasonNumber: "4", episodeNumber: "10");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
    }

    [Fact]
    public async Task HandleAsync_MissingSeasonNumber_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office", episodeNumber: "10");
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
        var request = CreateIntentRequest(seriesName: "NonExistent Show", seasonNumber: "1", episodeNumber: "1");
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
    public async Task HandleAsync_EpisodeNotFound_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office", seasonNumber: "1", episodeNumber: "99");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };

        // First call returns series, second returns no matching episode
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { series });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.Tells();
    }

    [Fact]
    public async Task HandleAsync_EpisodeFound_ReturnsVideoDirective()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(seriesName: "The Office", seasonNumber: "4", episodeNumber: "10");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };
        var episode = new global::MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Fun Run",
            Id = Guid.NewGuid(),
            ParentIndexNumber = 4,
            IndexNumber = 10,
            SeriesId = series.Id
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { series });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode))))
            .Returns(new List<BaseItem> { episode });

        _libraryManagerMock.Setup(l => l.GetItemById(episode.Id))
            .Returns(episode);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<VideoAppLaunchDirective>();

        // JF-349: an episode launch now announces the title instead of launching silently,
        // matching PlayRandom/PlayVideo.
        Assert.NotNull(response.Response.OutputSpeech);
        string announceText = response.Response.OutputSpeech is SsmlOutputSpeech s
            ? s.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("Fun Run", announceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_DialogStarted_ElicitsSeriesName()
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
        var request = CreateIntentRequest(seriesName: "The Office", dialogState: "IN_PROGRESS");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }
}
