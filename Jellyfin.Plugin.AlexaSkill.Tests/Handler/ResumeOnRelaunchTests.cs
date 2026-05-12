using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AlexaSession = global::Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for resume-on-relaunch flow: LaunchRequestHandler, YesIntentHandler, NoIntentHandler.
/// </summary>
public class ResumeOnRelaunchTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public ResumeOnRelaunchTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Context CreateContextWithoutAudio(string deviceId = "test-device")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId }
            }
        };
    }

    private static Context CreateContextWithAudio(string token, long offsetMs = 0, string deviceId = "test-device")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new global::Alexa.NET.Request.Device { DeviceID = deviceId }
            },
            AudioPlayer = new PlaybackState
            {
                Token = token,
                OffsetInMilliseconds = offsetMs
            }
        };
    }

    private LaunchRequestHandler CreateLaunchHandler()
    {
        return new LaunchRequestHandler(
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

    private NoIntentHandler CreateNoHandler()
    {
        return new NoIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    // =====================================================================
    // LaunchRequestHandler
    // =====================================================================

    [Fact]
    public async Task LaunchRequest_NoPriorPlayback_ReturnsWelcome()
    {
        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithoutAudio();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Welcome", speech);
    }

    [Fact]
    public async Task LaunchRequest_WithPriorPlayback_ReturnsResumePrompt()
    {
        var itemId = Guid.NewGuid();
        var item = new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = itemId
        };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithAudio(itemId.ToString(), 45000);
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Should keep session open for Yes/No
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Bohemian Rhapsody", speech);

        // Should have resume_state session attribute
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("resume_state"));
    }

    [Fact]
    public async Task LaunchRequest_WithPriorPlayback_UnknownItem_StillOffersResume()
    {
        var itemId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns((BaseItem?)null);

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithAudio(itemId.ToString(), 30000);
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("resume_state"));

        // Should use UnknownMedia fallback for the title
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Unknown media", speech);
    }

    // =====================================================================
    // YesIntentHandler - Resume
    // =====================================================================

    [Fact]
    public async Task YesIntent_WithResumeState_ResumesPlayback()
    {
        var itemId = Guid.NewGuid();
        var item = new Audio
        {
            Name = "Stairway to Heaven",
            Id = itemId
        };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        var handler = CreateYesHandler();
        var request = new IntentRequest
        {
            Locale = "en-US",
            Intent = new Intent { Name = "AMAZON.YesIntent" }
        };
        var context = CreateContextWithoutAudio();
        var session = CreateSession();

        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = itemId.ToString(),
            OffsetMs = 60000
        };
        var sessionAttributes = new Dictionary<string, object>
        {
            ["resume_state"] = Newtonsoft.Json.JsonConvert.SerializeObject(resumeState)
        };

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, sessionAttributes, CancellationToken.None);

        // Should have AudioPlayer directive
        Assert.NotEmpty(response.Response.Directives);
        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(response.Response.Directives[0]);
        Assert.Equal(itemId.ToString(), audioDirective.AudioItem.Stream.Token);
        Assert.Equal(60000, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(PlayBehavior.ReplaceAll, audioDirective.PlayBehavior);

        // Should have speech announcement
        Assert.NotNull(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task YesIntent_WithResumeState_ItemNotFound_ReturnsMediaNotFound()
    {
        var itemId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns((BaseItem?)null);

        var handler = CreateYesHandler();
        var request = new IntentRequest
        {
            Locale = "en-US",
            Intent = new Intent { Name = "AMAZON.YesIntent" }
        };
        var context = CreateContextWithoutAudio();
        var session = CreateSession();

        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = itemId.ToString(),
            OffsetMs = 30000
        };
        var sessionAttributes = new Dictionary<string, object>
        {
            ["resume_state"] = Newtonsoft.Json.JsonConvert.SerializeObject(resumeState)
        };

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, sessionAttributes, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("could not find", speech);
    }

    [Fact]
    public async Task YesIntent_WithoutSessionAttributes_ReturnsUnexpectedYes()
    {
        var handler = CreateYesHandler();
        var request = new IntentRequest
        {
            Locale = "en-US",
            Intent = new Intent { Name = "AMAZON.YesIntent" }
        };
        var context = CreateContextWithoutAudio();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("not sure what", speech);
    }

    // =====================================================================
    // NoIntentHandler - Resume rejection
    // =====================================================================

    [Fact]
    public async Task NoIntent_WithResumeState_ReturnsFreshStart()
    {
        var handler = CreateNoHandler();
        var request = new IntentRequest
        {
            Locale = "en-US",
            Intent = new Intent { Name = "AMAZON.NoIntent" }
        };
        var context = CreateContextWithoutAudio();
        var session = CreateSession();

        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = Guid.NewGuid().ToString(),
            OffsetMs = 30000
        };
        var sessionAttributes = new Dictionary<string, object>
        {
            ["resume_state"] = Newtonsoft.Json.JsonConvert.SerializeObject(resumeState)
        };

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, sessionAttributes, CancellationToken.None);

        // Should keep session open for next command
        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Starting fresh", speech);
    }

    [Fact]
    public async Task NoIntent_WithoutSessionAttributes_ReturnsUnexpectedYes()
    {
        var handler = CreateNoHandler();
        var request = new IntentRequest
        {
            Locale = "en-US",
            Intent = new Intent { Name = "AMAZON.NoIntent" }
        };
        var context = CreateContextWithoutAudio();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("not sure what", speech);
    }

    // =====================================================================
    // ResumeHelper
    // =====================================================================

    [Fact]
    public void ResumeHelper_ReadState_ValidJson_ReturnsState()
    {
        var state = new ResumeHelper.ResumeState
        {
            ItemId = "abc-123",
            OffsetMs = 45000
        };
        var attrs = new Dictionary<string, object>
        {
            ["resume_state"] = Newtonsoft.Json.JsonConvert.SerializeObject(state)
        };

        ResumeHelper.ResumeState? result = ResumeHelper.ReadState(attrs);
        Assert.NotNull(result);
        Assert.Equal("abc-123", result!.ItemId);
        Assert.Equal(45000, result.OffsetMs);
    }

    [Fact]
    public void ResumeHelper_ReadState_NullAttributes_ReturnsNull()
    {
        ResumeHelper.ResumeState? result = ResumeHelper.ReadState(null);
        Assert.Null(result);
    }

    [Fact]
    public void ResumeHelper_ReadState_EmptyItemId_ReturnsNull()
    {
        var state = new ResumeHelper.ResumeState { ItemId = "", OffsetMs = 100 };
        var attrs = new Dictionary<string, object>
        {
            ["resume_state"] = Newtonsoft.Json.JsonConvert.SerializeObject(state)
        };

        ResumeHelper.ResumeState? result = ResumeHelper.ReadState(attrs);
        Assert.Null(result);
    }

    [Fact]
    public void ResumeHelper_HasResumeState_Present_ReturnsTrue()
    {
        var attrs = new Dictionary<string, object>
        {
            ["resume_state"] = "{}"
        };
        Assert.True(ResumeHelper.HasResumeState(attrs));
    }

    [Fact]
    public void ResumeHelper_HasResumeState_Absent_ReturnsFalse()
    {
        var attrs = new Dictionary<string, object>();
        Assert.False(ResumeHelper.HasResumeState(attrs));
    }

    [Fact]
    public void ResumeHelper_HasResumeState_Null_ReturnsFalse()
    {
        Assert.False(ResumeHelper.HasResumeState(null));
    }

    // =====================================================================
    // Locale strings verification
    // =====================================================================

    [Theory]
    [InlineData("en-US", "Resuming")]
    [InlineData("en-US", "FreshStart")]
    [InlineData("it-IT", "Resuming")]
    [InlineData("it-IT", "FreshStart")]
    [InlineData("de-DE", "Resuming")]
    [InlineData("fr-FR", "Resuming")]
    [InlineData("es-ES", "Resuming")]
    [InlineData("pt-BR", "Resuming")]
    [InlineData("ja-JP", "Resuming")]
    [InlineData("nl-NL", "Resuming")]
    [InlineData("ar-SA", "Resuming")]
    [InlineData("hi-IN", "Resuming")]
    public void LocaleStrings_ResumeStringsExist(string locale, string key)
    {
        string value = ResponseStrings.Get(key, locale);
        Assert.NotEqual(key, value);
        Assert.NotEmpty(value);
    }
}
