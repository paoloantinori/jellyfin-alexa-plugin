using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayByGenreIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlayByGenreIntentHandler CreateHandler()
    {
        return new PlayByGenreIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? genre = null)
    {
        var intent = new Intent { Name = IntentNames.PlayByGenre };
        intent.Slots = new Dictionary<string, Slot>();

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public void CanHandle_PlayByGenreIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");

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
    public async Task HandleAsync_NoGenreSlot_ReturnsDidNotCatchMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_WithGenreItems_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audioItem = new Audio { Name = "Rock Song", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_GenreNotFound_ReturnsNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "polka");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_PassesGenreToQuery()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "jazz");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Jazz Song", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.Genres);
        Assert.Contains("jazz", capturedQuery.Genres);
    }

    [Fact]
    public async Task HandleAsync_DefaultsToAudioType()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "Rock Song", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.Single(capturedQuery.IncludeItemTypes);
        Assert.Equal(BaseItemKind.Audio, capturedQuery.IncludeItemTypes[0]);
    }

    [Fact]
    public async Task HandleAsync_SetsQueueFromResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(genre: "rock");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var items = new List<BaseItem>
        {
            new Audio { Name = "Song 1", Id = Guid.NewGuid() },
            new Audio { Name = "Song 2", Id = Guid.NewGuid() },
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task HandleAsync_MusicDisabled_ReturnsNotFound()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => c.MusicEnabled = false, "genre-music-test");
        _fx.SetupUserMock();

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateIntentRequest(genre: "rock"),
            _fx.CreateContext(),
            _fx.CreateUser(),
            _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.NotEmpty(speech);
    }
}
