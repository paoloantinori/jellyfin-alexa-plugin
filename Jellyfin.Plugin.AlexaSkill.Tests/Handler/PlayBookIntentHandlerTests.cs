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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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
public class PlayBookIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;

    public PlayBookIntentHandlerTests()
    {
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        TestHelpers.EnsurePluginInstance(
            _fx.Config, _fx.LoggerFactory, c => { }, "playbook-tests");
    }

    private PlayBookIntentHandler CreateHandler()
    {
        return new PlayBookIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string? bookName = null)
    {
        var intent = new Intent { Name = IntentNames.PlayBook };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (bookName != null)
        {
            intent.Slots["book"] = new global::Alexa.NET.Request.Slot { Name = "book", Value = bookName };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    [Fact]
    public void CanHandle_PlayBookIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlayAlbumIntent" },
            Locale = "en-US",
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoBookSlot_AsksForBookName()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("book", speech, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_BookNotFound_ReturnsNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Nonexistent Book");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("nonexistent book", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_FeatureDisabled_ReturnsFeatureDisabled()
    {
        Plugin.Instance!.Configuration.BooksEnabled = false;
        try
        {
            var handler = CreateHandler();
            var request = CreateIntentRequest(bookName: "The Hobbit");
            var context = _fx.CreateContext();
            var user = _fx.CreateUser();
            var session = CreateSession();

            SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

            Assert.NotNull(response);
            Assert.True(response.Response.ShouldEndSession);
            string speech = TestHelpers.GetSpeechText(response);
            Assert.Contains("disabled", speech, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Plugin.Instance!.Configuration.BooksEnabled = true;
        }
    }

    [Fact]
    public async Task HandleAsync_SingleBookFound_PlaysAudio()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio
        {
            Name = "The Hobbit",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?[0] as AudioPlayerPlayDirective;
        Assert.NotNull(audioDirective);
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_SingleBookFound_NativeControls_FreshStart_AnnouncesTitle()
    {
        // F2: a fresh-start audiobook via VideoApp (NativeControlsForBooks, no resume position)
        // must announce the book title instead of launching silently.
        _fx.Config.NativeControlsForBooks = true;
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "The Hobbit");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        var bookItem = new Audio { Name = "The Hobbit", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        var trackItem = new Audio { Name = "Chapter 1", Id = Guid.NewGuid() };
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = new[] { trackItem },
                TotalRecordCount = 1
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.Directives?.FirstOrDefault(d => d.GetType().Name.Contains("VideoApp")));
        Assert.NotNull(response.Response.OutputSpeech);
        string announceText = response.Response.OutputSpeech is SsmlOutputSpeech ss
            ? ss.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("The Hobbit", announceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_BookWithNoTracks_ReturnsNoContentMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(bookName: "Empty Book");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = CreateSession();

        _fx.SetupUserMock();

        // Use a Folder-based item (not Audio) so MediaType != Audio,
        // triggering the "no content" path after our single-file audiobook fix.
        var bookItem = new CollectionFolder
        {
            Name = "Empty Book",
            Id = Guid.NewGuid()
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new MediaBrowser.Model.Querying.QueryResult<BaseItem>
            {
                Items = Array.Empty<BaseItem>(),
                TotalRecordCount = 0
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("empty book", speech, StringComparison.OrdinalIgnoreCase);
    }
}
