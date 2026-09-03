using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Alexa.NET.Assertions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayVideoIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public PlayVideoIntentHandlerTests()
    {
        _fx.UserManager
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private PlayVideoIntentHandler CreateHandler()
    {
        return new PlayVideoIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreatePlayVideoRequest(string? title = "The Matrix")
    {
        var slots = new Dictionary<string, Slot>();
        if (title != null)
        {
            slots["title"] = new Slot { Value = title };
        }

        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayVideoIntent",
                Slots = slots
            }
        };
    }

    private static BaseItem CreateTestItem(string name, Guid? id = null)
    {
        var item = new Movie { Name = name, Id = id ?? Guid.NewGuid() };
        return item;
    }

    [Theory]
    [InlineData("PlayVideoIntent", true)]
    [InlineData("PlaySongIntent", false)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_NoTitleSlot_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayVideoIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        var response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(""),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_NoResults_ReturnsNotFound()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Unknown Movie"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundMovie_ReturnsVideoAppDirective()
    {
        var movie = CreateTestItem("The Matrix");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // JF-349: a fresh video launch (no resume position) now announces the title instead of
        // launching silently, matching PlayRandom/PlayEpisode. Resume-position launches still use
        // the "ResumingVideo" speech (unchanged if-branch).
        Assert.NotNull(response.Response.OutputSpeech);
        string announceText = response.Response.OutputSpeech is SsmlOutputSpeech ss
            ? ss.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("The Matrix", announceText, StringComparison.Ordinal);
        response.HasDirective<VideoAppLaunchDirective>();
    }

    [Fact]
    public async Task Handle_FoundMultipleResults_ReturnsDisambiguationPrompt()
    {
        var movie1 = CreateTestItem("Inception");
        var movie2 = CreateTestItem("Interstellar");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie1, movie2 });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Nolan"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        Assert.False(response.Response.ShouldEndSession);

        string speechText = response.Response.OutputSpeech is SsmlOutputSpeech ssml
            ? ssml.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;

        // With default Confirm behavior, should get "did you mean?" prompt suggesting the closest match
        Assert.True(
            speechText.Contains("Inception", StringComparison.Ordinal) ||
            speechText.Contains("Interstellar", StringComparison.Ordinal),
            "Expected a fuzzy suggestion for one of the candidate movies");
    }

    [Fact]
    public async Task Handle_NullTitleSlotValue_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(null),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task Handle_WhitespaceTitle_ReturnsPrompt(string title)
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(title),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundMovie_DirectiveContainsSourceAndMetadata()
    {
        var id = Guid.NewGuid();
        var movie = CreateTestItem("The Matrix", id);

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.NotNull(directive.VideoItem);
        Assert.Contains(id.ToString(), directive.VideoItem.Source);
        Assert.Contains("Videos", directive.VideoItem.Source);
        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("The Matrix", directive.VideoItem.Metadata.Title);
    }

    [Fact]
    public async Task Handle_FoundMovie_SetsSessionQueue()
    {
        var id = Guid.NewGuid();
        var movie = CreateTestItem("The Matrix", id);
        var session = _fx.CreateSession();

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        await handler.HandleAsync(CreatePlayVideoRequest("The Matrix"), _fx.CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_VideoResponse_ShouldEndSessionIsNull()
    {
        var movie = CreateTestItem("Test Video");

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Test Video"),
            _fx.CreateContext(),
            TestHelpers.CreateTestUser(),
            _fx.CreateSession(), CancellationToken.None);

        // VideoApp.Launch must NOT include shouldEndSession — Alexa rejects it
        Assert.Null(response.Response.ShouldEndSession);
    }
}
