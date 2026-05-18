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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CustomerProfileServiceTests
{
    private readonly ILoggerFactory _loggerFactory;

    public CustomerProfileServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private CustomerProfileService CreateService()
    {
        return new CustomerProfileService(_loggerFactory.CreateLogger<CustomerProfileService>());
    }

    private static Context CreateContext(string? token = "test-token")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                ApiAccessToken = token,
                ApiEndpoint = "https://api.amazonalexa.com",
                User = new global::Alexa.NET.Request.User()
            }
        };
    }

    [Fact]
    public async Task GetGivenNameAsync_NullContext_ReturnsNull()
    {
        var service = CreateService();
        string? result = await service.GetGivenNameAsync(null!, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetGivenNameAsync_MissingToken_ReturnsNull()
    {
        var service = CreateService();
        var context = CreateContext(token: null);
        string? result = await service.GetGivenNameAsync(context, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimezoneAsync_NullContext_ReturnsNull()
    {
        var service = CreateService();
        string? result = await service.GetTimezoneAsync(null!, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimezoneAsync_MissingToken_ReturnsNull()
    {
        var service = CreateService();
        var context = CreateContext(token: null);
        string? result = await service.GetTimezoneAsync(context, CancellationToken.None);
        Assert.Null(result);
    }
}

public class LaunchRequestHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public LaunchRequestHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private LaunchRequestHandler CreateHandler()
    {
        var userManagerMock = new Mock<IUserManager>();
        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        return new LaunchRequestHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            userManagerMock.Object,
            _loggerFactory);
    }

    private static LaunchRequest CreateLaunchRequest()
    {
        return new LaunchRequest { Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext(string? token = "test-token")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                ApiAccessToken = token,
                ApiEndpoint = "https://api.amazonalexa.com",
                User = new global::Alexa.NET.Request.User()
            }
        };
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    [Fact]
    public void CanHandle_LaunchRequest_ReturnsTrue()
    {
        var handler = CreateHandler();
        Assert.True(handler.CanHandle(CreateLaunchRequest()));
    }

    [Fact]
    public void CanHandle_IntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlayIntent" }, Locale = "en-US" };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_EmptyQueue_ReturnsWelcome()
    {
        var handler = CreateHandler();
        var request = CreateLaunchRequest();
        var context = CreateContext();
        var session = CreateSession();

        var response = await handler.HandleAsync(request, context, CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.False(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_EmptyQueue_ContainsWelcomeText()
    {
        var handler = CreateHandler();
        var request = CreateLaunchRequest();
        var context = CreateContext();
        var session = CreateSession();

        var response = await handler.HandleAsync(request, context, CreateUser(), session, CancellationToken.None);

        string output = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jellyfin", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NullToken_StillReturnsWelcome()
    {
        var handler = CreateHandler();
        var request = CreateLaunchRequest();
        var context = CreateContext(token: null);
        var session = CreateSession();

        var response = await handler.HandleAsync(request, context, CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        string output = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jellyfin", output, StringComparison.OrdinalIgnoreCase);
    }
}

public class PersonalizedGreetingLocaleTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("it-IT")]
    public void WelcomePersonalized_ExistsForLocale(string locale)
    {
        string value = ResponseStrings.Get("WelcomePersonalized", locale);
        Assert.NotEqual("WelcomePersonalized", value);
        Assert.Contains("{0}", value);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("it-IT")]
    public void WelcomePersonalizedSsml_ExistsForLocale(string locale)
    {
        string value = ResponseStrings.Get("WelcomePersonalizedSsml", locale);
        Assert.NotEqual("WelcomePersonalizedSsml", value);
        Assert.Contains("{0}", value);
    }
}
