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
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class VoiceIdentificationTests
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    private static PluginConfiguration CreateConfigWithUser(out Entities.User user)
    {
        var config = new PluginConfiguration();
        user = new Entities.User
        {
            Id = Guid.NewGuid(),
            JellyfinToken = "test-token",
            InvocationName = "jellyfin"
        };
        config.AddUser(user);
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        return config;
    }

    private static Context CreateContextWithPerson(string personId)
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" },
                User = new global::Alexa.NET.Request.User
                {
                    UserId = "test-user",
                    AccessToken = Guid.NewGuid().ToString()
                },
                Person = new Person { PersonId = personId }
            }
        };
    }

    private static Context CreateContextWithoutPerson()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" },
                User = new global::Alexa.NET.Request.User
                {
                    UserId = "test-user",
                    AccessToken = Guid.NewGuid().ToString()
                }
            }
        };
    }

    [Fact]
    public void GetUserByPersonId_WithMapping_ReturnsUser()
    {
        var config = CreateConfigWithUser(out Entities.User user);
        user.AlexaPersonId = "person-123";

        Entities.User? found = config.GetUserByPersonId("person-123");
        Assert.NotNull(found);
        Assert.Equal(user.Id, found.Id);
    }

    [Fact]
    public void GetUserByPersonId_NoMapping_ReturnsNull()
    {
        var config = CreateConfigWithUser(out Entities.User _);

        Entities.User? found = config.GetUserByPersonId("person-unknown");
        Assert.Null(found);
    }

    [Fact]
    public void GetUserByPersonId_EmptyPersonId_ReturnsNull()
    {
        var config = CreateConfigWithUser(out Entities.User user);
        user.AlexaPersonId = "person-123";

        Assert.Null(config.GetUserByPersonId(string.Empty));
        Assert.Null(config.GetUserByPersonId(null!));
    }

    [Fact]
    public async Task LearnMyVoice_WithPersonId_SetsAlexaPersonId()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User user);

        var handler = new LearnMyVoiceIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        var context = CreateContextWithPerson("person-abc");
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.LearnMyVoice },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.Equal("person-abc", user.AlexaPersonId);
    }

    [Fact]
    public async Task LearnMyVoice_WithDifferentPersonId_UpdatesMapping()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User user);
        user.AlexaPersonId = "person-old";

        var handler = new LearnMyVoiceIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        var context = CreateContextWithPerson("person-new");
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.LearnMyVoice },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("person-new", user.AlexaPersonId);
    }

    [Fact]
    public async Task LearnMyVoice_WithoutPersonId_ReturnsError()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User user);

        var handler = new LearnMyVoiceIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        var context = CreateContextWithoutPerson();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.LearnMyVoice },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(user.AlexaPersonId);
    }

    [Fact]
    public async Task WhoAmI_WithRecognizedVoice_ReturnsResponse()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User user);
        user.AlexaPersonId = "person-xyz";

        var handler = new WhoAmIIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        var context = CreateContextWithPerson("person-xyz");
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.WhoAmI },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task WhoAmI_WithoutPersonId_ReturnsUnknownResponse()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User user);

        var handler = new WhoAmIIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        var context = CreateContextWithoutPerson();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.WhoAmI },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task WhoAmI_WithMismatchedPersonId_ReturnsUnknownResponse()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User user);
        user.AlexaPersonId = "person-abc";

        var handler = new WhoAmIIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        // Different person speaking
        var context = CreateContextWithPerson("person-different");
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.WhoAmI },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleRequestAsync_WithPersonId_UsesMappedUser()
    {
        // Set up Plugin.Instance for HandleRequestAsync which needs it
        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-voice-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(System.IO.Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());

        var userManager = new Mock<IUserManager>();
        userManager.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        _ = new Plugin(appPaths.Object, xmlSerializer.Object, _loggerFactory, userManager.Object);

        var sessionManagerMock = new Mock<ISessionManager>();
        var config = CreateConfigWithUser(out Entities.User voiceUser);
        voiceUser.AlexaPersonId = "voice-person-1";
        voiceUser.JellyfinToken = "voice-jellyfin-token";

        var handler = new WhoAmIIntentHandler(
            sessionManagerMock.Object, config, _loggerFactory);

        var context = CreateContextWithPerson("voice-person-1");

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.WhoAmI },
            Locale = "en-US"
        };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, _loggerFactory);
        sessionManagerMock.Setup(s => s.GetSessionByAuthenticationToken(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(session);

        SkillResponse response = await handler.HandleRequestAsync(request, context, CancellationToken.None);

        Assert.NotNull(response);

        // Verify the session was looked up with the voice-mapped user's token
        sessionManagerMock.Verify(
            s => s.GetSessionByAuthenticationToken("voice-jellyfin-token", "test-device", It.IsAny<string>()),
            Times.Once);
    }
}
