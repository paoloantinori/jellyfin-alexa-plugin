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

public class PlayByDecadeIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayByDecadeIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayByDecadeIntentHandler CreateHandler()
    {
        return new PlayByDecadeIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? decade = null, string? genre = null)
    {
        var intent = new Intent { Name = IntentNames.PlayByDecade };
        intent.Slots = new Dictionary<string, Slot>();

        if (decade != null)
        {
            intent.Slots["decade"] = new Slot { Name = "decade", Value = decade };
        }

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
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

    [Fact]
    public void CanHandle_PlayByDecadeIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(decade: "80s");

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
    public async Task HandleAsync_NoDecadeSlot_ReturnsDidNotCatchMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_WithDecadeItems_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(decade: "80s");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audioItem = new Audio { Name = "80s Song", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_DecadeNotFound_ReturnsNotFoundMessage()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(decade: "80s");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_PassesYearsToQuery()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(decade: "80s");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "80s Song", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.Years);
        Assert.Equal(10, capturedQuery.Years.Length);
        Assert.Equal(1980, capturedQuery.Years[0]);
        Assert.Equal(1989, capturedQuery.Years[9]);
    }

    [Fact]
    public async Task HandleAsync_WithGenreFilter_PassesGenreToQuery()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(decade: "80s", genre: "rock");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "80s Rock Song", Id = Guid.NewGuid() } });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.NotNull(capturedQuery.Genres);
        Assert.Contains("rock", capturedQuery.Genres);
        Assert.NotNull(capturedQuery.Years);
        Assert.Equal(10, capturedQuery.Years.Length);
    }

    [Fact]
    public async Task HandleAsync_DefaultsToAudioType()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(decade: "90s");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem> { new Audio { Name = "90s Song", Id = Guid.NewGuid() } });

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
        var request = CreateIntentRequest(decade: "70s");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = new List<BaseItem>
        {
            new Audio { Name = "Song 1", Id = Guid.NewGuid() },
            new Audio { Name = "Song 2", Id = Guid.NewGuid() },
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Theory]
    [InlineData("80s", 1980, 1989)]
    [InlineData("80", 1980, 1989)]
    [InlineData("1980s", 1980, 1989)]
    [InlineData("1980", 1980, 1989)]
    [InlineData("the 80s", 1980, 1989)]
    [InlineData("the eighties", 1980, 1989)]
    [InlineData("eighties", 1980, 1989)]
    [InlineData("90s", 1990, 1999)]
    [InlineData("the nineties", 1990, 1999)]
    [InlineData("nineties", 1990, 1999)]
    [InlineData("70s", 1970, 1979)]
    [InlineData("seventies", 1970, 1979)]
    [InlineData("60s", 1960, 1969)]
    [InlineData("sixties", 1960, 1969)]
    [InlineData("50s", 1950, 1959)]
    [InlineData("fifties", 1950, 1959)]
    [InlineData("20s", 2020, 2029)]
    [InlineData("10s", 2010, 2019)]
    [InlineData("2000s", 2000, 2009)]
    [InlineData("two thousands", 2000, 2009)]
    [InlineData("twenty twenties", 2020, 2029)]
    public void ParseDecadeYears_ValidInputs_ReturnsCorrectRange(string input, int expectedStart, int expectedEnd)
    {
        int[]? result = PlayByDecadeIntentHandler.ParseDecadeYears(input);

        Assert.NotNull(result);
        Assert.Equal(10, result.Length);
        Assert.Equal(expectedStart, result[0]);
        Assert.Equal(expectedEnd, result[9]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("thirties")]
    public void ParseDecadeYears_InvalidInputs_ReturnsNull(string input)
    {
        int[]? result = PlayByDecadeIntentHandler.ParseDecadeYears(input);

        Assert.Null(result);
    }
}
