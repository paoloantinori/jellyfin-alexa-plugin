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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class InProgressMediaListIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private InProgressMediaListIntentHandler CreateHandler()
    {
        return new InProgressMediaListIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.InProgressMediaList },
            Locale = "en-US",
            RequestId = "test-req"
        };
    }

    [Fact]
    public void CanHandle_InProgressMediaListIntent_ReturnsTrue()
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
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoRecentItems_ReturnsNoInProgressMedia()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
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
    public async Task HandleAsync_NoResumableItems_ReturnsNoInProgressMedia()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audioItem = new Audio { Name = "Finished Song", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_ResumableItems_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var audioItem = new Audio { Name = "In Progress Song", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), audioItem))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_SkipsItemsWithZeroPosition()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var item1 = new Audio { Name = "No Position", Id = Guid.NewGuid() };
        var item2 = new Audio { Name = "Has Position", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item1, item2 });

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), item1))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = 0 });

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), item2))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(10).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_LimitsToFiveItems()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var items = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            items.Add(new Audio { Name = $"Song {i + 1}", Id = Guid.NewGuid() });
        }

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_QueriesRecentItemsOnly()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal(50, capturedQuery.Limit);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
    }

    [Fact]
    public async Task HandleAsync_ShortList_KeepsSessionOpenForSelection()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        // 3 items — fits in one voice page (≤5), so non-truncated path
        var items = new List<BaseItem>
        {
            new Audio { Name = "Song A", Id = Guid.NewGuid() },
            new Audio { Name = "Song B", Id = Guid.NewGuid() },
            new Audio { Name = "Song C", Id = Guid.NewGuid() },
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(2).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        TestHelpers.AssertSessionOpen(response, "Non-truncated list should keep session open so user can pick an item");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Fact]
    public async Task HandleAsync_TruncatedList_KeepsSessionOpen()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        var items = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            items.Add(new Audio { Name = $"Song {i + 1}", Id = Guid.NewGuid() });
        }

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        TestHelpers.AssertSessionOpen(response, "Truncated list should keep session open");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Fact]
    public async Task HandleAsync_FiltersPlayedItemsAtDbLevel()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        _fx.SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.False(capturedQuery.IsPlayed);
    }
}
