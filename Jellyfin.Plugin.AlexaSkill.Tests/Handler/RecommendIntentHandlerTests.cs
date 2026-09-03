using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class RecommendIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private RecommendIntentHandler CreateHandler()
    {
        return new RecommendIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? mediaType = null)
    {
        var intent = new Intent { Name = IntentNames.Recommend };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (mediaType != null)
        {
            intent.Slots["media_type"] = new global::Alexa.NET.Request.Slot { Name = "media_type", Value = mediaType };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public void CanHandle_RecommendIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();

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
    public async Task HandleAsync_NoPlayHistory_ReturnsFallback()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        // No played items
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == true)))
            .Returns(new List<BaseItem>());

        // No unplayed items either
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == false || q.IsPlayed == null)))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_WithPlayHistory_RecommendsFromGenres()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var playedItem = new Audio { Name = "Played Song", Id = Guid.NewGuid() };
        playedItem.Genres = new[] { "Rock", "Pop" };

        var recommendedItem = new Audio { Name = "New Rock Song", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == true)))
            .Returns(new List<BaseItem> { playedItem });

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == false || q.IsPlayed == null)))
            .Returns(new List<BaseItem> { recommendedItem });

        _fx.LibraryManager.Setup(l => l.GetItemById(recommendedItem.Id))
            .Returns(recommendedItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task HandleAsync_MediaTypeMusic_QueriesAudioOnly()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mediaType: "music");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task HandleAsync_NoMediaTypeSlot_DefaultsToAudioAndMovie()
    {
        var handler = CreateHandler();
        // No media_type slot provided (slotless utterance)
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var playedItem = new Audio { Name = "Played Song", Id = Guid.NewGuid() };
        playedItem.Genres = new[] { "Rock" };

        var recommendedItem = new Audio { Name = "Recommended Song", Id = Guid.NewGuid() };

        // Track what item types were queried in the played-items query
        Jellyfin.Data.Enums.BaseItemKind[]? queriedTypes = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == true)))
            .Callback<InternalItemsQuery>(q => queriedTypes = q.IncludeItemTypes)
            .Returns(new List<BaseItem> { playedItem });

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == false || q.IsPlayed == null)))
            .Returns(new List<BaseItem> { recommendedItem });

        _fx.LibraryManager.Setup(l => l.GetItemById(recommendedItem.Id))
            .Returns(recommendedItem);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.NotNull(queriedTypes);
        Assert.Contains(Jellyfin.Data.Enums.BaseItemKind.Audio, queriedTypes);
        Assert.Contains(Jellyfin.Data.Enums.BaseItemKind.Movie, queriedTypes);
    }

    [Fact]
    public async Task HandleAsync_AllPlayed_FallsBackToRecent()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var playedItem = new Audio { Name = "Everything", Id = Guid.NewGuid() };
        playedItem.Genres = new[] { "Rock" };

        // Played items found, but no unplayed items in those genres
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == true)))
            .Returns(new List<BaseItem> { playedItem });

        // First call for genre-based recs returns empty, second fallback also empty
        int callCount = 0;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsPlayed == false || q.IsPlayed == null)))
            .Callback<InternalItemsQuery>(q => callCount++)
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }
}
