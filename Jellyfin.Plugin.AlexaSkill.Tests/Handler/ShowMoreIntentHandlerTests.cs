using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Assertions;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class ShowMoreIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ShowMoreIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private ShowMoreIntentHandler CreateHandler()
    {
        return new ShowMoreIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _loggerFactory);
    }

    private YesIntentHandler CreateYesHandler()
    {
        return new YesIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static IntentRequest CreateShowMoreIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = "ShowMoreIntent" },
            Locale = "en-US"
        };
    }

    private static IntentRequest CreateYesIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.YesIntent" },
            Locale = "en-US"
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private Dictionary<string, object> CreatePaginationAttrs(
        ListPaginationHelper.ListType type,
        string[] itemIds,
        int offset,
        int pageSize)
    {
        var attrs = new Dictionary<string, object>();
        ListPaginationHelper.WriteState(attrs, type, itemIds, offset, pageSize);
        return attrs;
    }

    private void SetupLibraryItems(List<Audio> items)
    {
        foreach (var item in items)
        {
            _libraryManagerMock.Setup(l => l.GetItemById(item.Id))
                .Returns(item);
        }
    }

    // ================================================================
    // ShowMoreIntentHandler tests
    // ================================================================

    [Fact]
    public void CanHandle_ShowMoreIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "ShowMoreIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoSessionState_ReturnsNoSessionMessage()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateShowMoreIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("previous list", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithState_NoMoreItems_ReturnsNoMoreItems()
    {
        var handler = CreateHandler();

        // 5 items, offset = 5 (all already read), pageSize = 5
        // CurrentOffset >= ItemIds.Length => no more items
        var items = new List<Audio>();
        for (int i = 0; i < 5; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        SetupLibraryItems(items);

        var attrs = CreatePaginationAttrs(
            ListPaginationHelper.ListType.BrowseLibrary,
            items.Select(i => i.Id.ToString()).ToArray(),
            5,  // offset = total items = nothing left
            5);

        var response = await handler.HandleAsync(
            CreateShowMoreIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("all", speech.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task HandleAsync_WithState_HasMoreItems_ReturnsNextPage()
    {
        var handler = CreateHandler();

        // 10 items, offset = 5 (first page read), pageSize = 5
        // This will show items 6-10, which is the last page
        var items = new List<Audio>();
        for (int i = 0; i < 10; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        SetupLibraryItems(items);

        var attrs = CreatePaginationAttrs(
            ListPaginationHelper.ListType.BrowseLibrary,
            items.Select(i => i.Id.ToString()).ToArray(),
            5,  // offset = first 5 already shown
            5);

        var response = await handler.HandleAsync(
            CreateShowMoreIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(response);
        string speechText = TestHelpers.GetSpeechText(response);

        // Should contain items from page 2 (Track 6 through Track 10)
        Assert.Contains("Track 6", speechText);
        Assert.Contains("Track 10", speechText);
        // This is the last page (5 + 5 = 10 = total), so "remaining" text
        Assert.Contains("remaining", speechText, StringComparison.OrdinalIgnoreCase);
        // Last page ends session
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task HandleAsync_WithState_ManyPages_KeepsSessionOpen()
    {
        var handler = CreateHandler();

        // 15 items, offset = 5, pageSize = 5 => page 2 shows 6-10, still page 3 remaining
        var items = new List<Audio>();
        for (int i = 0; i < 15; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        SetupLibraryItems(items);

        var attrs = CreatePaginationAttrs(
            ListPaginationHelper.ListType.BrowseLibrary,
            items.Select(i => i.Id.ToString()).ToArray(),
            5,  // first 5 shown
            5);

        var response = await handler.HandleAsync(
            CreateShowMoreIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(response);
        string speechText = TestHelpers.GetSpeechText(response);

        // Should contain page 2 items
        Assert.Contains("Track 6", speechText);
        Assert.Contains("Track 10", speechText);
        // Should NOT contain page 3 items
        Assert.DoesNotContain("Track 11", speechText);
        // Should contain show more prompt
        Assert.Contains("show more", speechText, StringComparison.OrdinalIgnoreCase);
        // Session should be kept open
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
        // Should have updated pagination state in response
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("pagination_state"));
    }

    [Fact]
    public async Task HandleAsync_WithState_LastPage_EndsSession()
    {
        var handler = CreateHandler();

        // 7 items, offset = 5, pageSize = 5
        var items = new List<Audio>();
        for (int i = 0; i < 7; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        SetupLibraryItems(items);

        var attrs = CreatePaginationAttrs(
            ListPaginationHelper.ListType.BrowseLibrary,
            items.Select(i => i.Id.ToString()).ToArray(),
            5,  // first 5 shown
            5);

        var response = await handler.HandleAsync(
            CreateShowMoreIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(response);
        string speechText = TestHelpers.GetSpeechText(response);

        // Should contain remaining items (6, 7)
        Assert.Contains("Track 6", speechText);
        Assert.Contains("Track 7", speechText);
        // Should end session (last page)
        Assert.True(response.Response.ShouldEndSession == true);
        // No pagination state in response
        Assert.Null(response.SessionAttributes);
    }

    // ================================================================
    // YesIntent delegation tests
    // ================================================================

    [Fact]
    public async Task YesIntent_WithPaginationState_DelegatesToShowMore()
    {
        var handler = CreateYesHandler();

        // 15 items so there are pages after offset=5
        var items = new List<Audio>();
        for (int i = 0; i < 15; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        SetupLibraryItems(items);

        var attrs = CreatePaginationAttrs(
            ListPaginationHelper.ListType.BrowseLibrary,
            items.Select(i => i.Id.ToString()).ToArray(),
            5,  // first 5 shown
            5);

        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(response);
        string speechText = TestHelpers.GetSpeechText(response);

        // Should show page 2 items, not "not sure what you" (unexpected yes)
        Assert.Contains("Track 6", speechText);
        Assert.DoesNotContain("not sure what you", speechText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task YesIntent_WithPaginationNoMoreItems_ReturnsNoMoreItems()
    {
        var handler = CreateYesHandler();

        var items = new List<Audio>();
        for (int i = 0; i < 5; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        SetupLibraryItems(items);

        var attrs = CreatePaginationAttrs(
            ListPaginationHelper.ListType.BrowseLibrary,
            items.Select(i => i.Id.ToString()).ToArray(),
            5,  // all items already shown
            5);

        var response = await handler.HandleAsync(
            CreateYesIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(response);
        string speechText = TestHelpers.GetSpeechText(response);
        Assert.Contains("all", speechText, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================
    // ListPaginationHelper tests
    // ================================================================

    [Fact]
    public void ListPaginationHelper_WriteAndRead_RoundTrips()
    {
        var attrs = new Dictionary<string, object>();
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };

        ListPaginationHelper.WriteState(attrs, ListPaginationHelper.ListType.BrowseLibrary, ids, 5, 5);

        var state = ListPaginationHelper.ReadState(attrs);
        Assert.NotNull(state);
        Assert.Equal(ListPaginationHelper.ListType.BrowseLibrary, state.Type);
        Assert.Equal(2, state.ItemIds.Length);
        Assert.Equal(5, state.CurrentOffset);
        Assert.Equal(5, state.PageSize);
    }

    [Fact]
    public void ListPaginationHelper_ClearState_RemovesKey()
    {
        var attrs = new Dictionary<string, object>();
        ListPaginationHelper.WriteState(attrs, ListPaginationHelper.ListType.Queue, new[] { "id1" }, 0, 5);

        Assert.True(ListPaginationHelper.HasPaginationState(attrs));
        ListPaginationHelper.ClearState(attrs);
        Assert.False(ListPaginationHelper.HasPaginationState(attrs));
    }

    [Fact]
    public void ListPaginationHelper_ReadState_NullAttrs_ReturnsNull()
    {
        var state = ListPaginationHelper.ReadState(null);
        Assert.Null(state);
    }

    [Fact]
    public void ListPaginationHelper_ReadState_EmptyAttrs_ReturnsNull()
    {
        var state = ListPaginationHelper.ReadState(new Dictionary<string, object>());
        Assert.Null(state);
    }

    [Fact]
    public void ListPaginationHelper_ReadState_InvalidJson_ReturnsNull()
    {
        var attrs = new Dictionary<string, object>
        {
            ["pagination_state"] = "not-valid-json"
        };
        var state = ListPaginationHelper.ReadState(attrs);
        Assert.Null(state);
    }

    [Fact]
    public void ListPaginationHelper_ReadState_EmptyIds_ReturnsNull()
    {
        var attrs = new Dictionary<string, object>();
        ListPaginationHelper.WriteState(attrs, ListPaginationHelper.ListType.BrowseLibrary, Array.Empty<string>(), 0, 5);

        var state = ListPaginationHelper.ReadState(attrs);
        Assert.Null(state);
    }
}
