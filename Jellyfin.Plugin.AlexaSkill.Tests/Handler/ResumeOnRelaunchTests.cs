using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
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
[Collection("Plugin")]
public class ResumeOnRelaunchTests : PluginTestBase, IDisposable
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

    /// <summary>
    /// Ensure APL visuals are enabled so "WithApl" tests pass regardless of
    /// static Plugin.Instance state left by other test classes running in parallel.
    /// </summary>
    private static void EnsureVisualsEnabled()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.AplVisualsEnabled = true;
        }
    }

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
            _userManagerMock.Object,
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
    public async Task LaunchRequest_WithPriorPlayback_AplDevice_AttachesResumeOfferDirective()
    {
        EnsureVisualsEnabled();

        var itemId = Guid.NewGuid();
        var item = new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = itemId
        };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithAudio(itemId.ToString(), 45000, deviceId: "test-device");
        // Add APL support to the device
        context.System.Device.SupportedInterfaces = new Dictionary<string, object>
        {
            { "Alexa.Presentation.APL", new { } }
        };
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Should have resume offer APL directive
        Assert.NotEmpty(response.Response.Directives);
        var directive = Assert.IsType<AplRenderDocumentDirective>(response.Response.Directives[0]);
        Assert.Equal("resumeOffer", directive.Token);

        // Should still have voice prompt and session state
        Assert.False(response.Response.ShouldEndSession);
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

    [Fact]
    public async Task HandleAsync_ResumeOfferDisabled_ShowsWelcomeInstead()
    {
        // Arrange: disable the resume offer flag but AudioPlayer has a token
        _config.ResumeOfferEnabled = false;

        var itemId = Guid.NewGuid();
        var item = new Audio
        {
            Name = "Test Song",
            Id = itemId
        };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithAudio(itemId.ToString(), 45000);
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Assert: should get welcome response, NOT resume offer
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Welcome", speech);

        // Should NOT have resume_state in session attributes
        Assert.True(response.SessionAttributes == null || !response.SessionAttributes.ContainsKey("resume_state"));
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
    public async Task YesIntent_WithResumeState_AnnounceTitle_SpeechContainsTitle()
    {
        var itemId = Guid.NewGuid();
        var item = new Audio
        {
            Name = "Stairway to Heaven",
            Id = itemId
        };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);
        _config.ResumeAnnounceTitle = true;

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

        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Stairway to Heaven", speech);
    }

    [Fact]
    public async Task YesIntent_WithResumeState_BriefMode_SpeechOmitsTitle()
    {
        var itemId = Guid.NewGuid();
        var item = new Audio
        {
            Name = "Stairway to Heaven",
            Id = itemId
        };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);
        _config.ResumeAnnounceTitle = false;

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

        // Should still have AudioPlayer directive
        Assert.NotEmpty(response.Response.Directives);
        var audioDirective = Assert.IsType<AudioPlayerPlayDirective>(response.Response.Directives[0]);
        Assert.Equal(60000, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Speech should NOT contain the title in brief mode
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("Stairway to Heaven", speech);
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
    [InlineData("en-US", "ResumeBrief")]
    [InlineData("en-US", "FreshStart")]
    [InlineData("it-IT", "Resuming")]
    [InlineData("it-IT", "ResumeBrief")]
    [InlineData("it-IT", "FreshStart")]
    [InlineData("de-DE", "Resuming")]
    [InlineData("de-DE", "ResumeBrief")]
    [InlineData("fr-FR", "Resuming")]
    [InlineData("fr-FR", "ResumeBrief")]
    [InlineData("es-ES", "Resuming")]
    [InlineData("es-ES", "ResumeBrief")]
    [InlineData("pt-BR", "Resuming")]
    [InlineData("pt-BR", "ResumeBrief")]
    [InlineData("ja-JP", "Resuming")]
    [InlineData("ja-JP", "ResumeBrief")]
    [InlineData("nl-NL", "Resuming")]
    [InlineData("nl-NL", "ResumeBrief")]
    [InlineData("ar-SA", "Resuming")]
    [InlineData("ar-SA", "ResumeBrief")]
    [InlineData("hi-IN", "Resuming")]
    [InlineData("hi-IN", "ResumeBrief")]
    public void LocaleStrings_ResumeStringsExist(string locale, string key)
    {
        string value = ResponseStrings.Get(key, locale);
        Assert.NotEqual(key, value);
        Assert.NotEmpty(value);
    }

    // =====================================================================
    // APL Carousel on Welcome
    // =====================================================================

    private static Context CreateContextWithApl()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new global::Alexa.NET.Request.Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        { "Alexa.Presentation.APL", new { } }
                    }
                }
            }
        };
    }

    private static Context CreateContextWithoutApl()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new global::Alexa.NET.Request.Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                }
            }
        };
    }

    [Fact]
    public async Task LaunchRequest_AplDevice_WithHistory_AttachesCarouselDirective()
    {
        EnsureVisualsEnabled();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithApl();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Welcome", speech);

        // Should have an APL welcome splash directive (which includes the carousel when items exist)
        Assert.NotEmpty(response.Response.Directives);
        var directive = Assert.IsType<AplRenderDocumentDirective>(response.Response.Directives[0]);
        Assert.Equal("welcome", directive.Token);
    }

    [Fact]
    public async Task LaunchRequest_NonAplDevice_NoCarouselDirective()
    {
        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audioItem });

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithoutApl();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);

        // Should NOT have any directives for non-APL device
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task LaunchRequest_AplDevice_NoHistory_WelcomeSplashDirective()
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithApl();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);

        // Welcome splash screen is always shown on APL devices (even without recently played items)
        Assert.Single(response.Response.Directives);
        var directive = Assert.IsType<AplRenderDocumentDirective>(response.Response.Directives[0]);
        Assert.Equal("welcome", directive.Token);
    }

    [Fact]
    public async Task LaunchRequest_ResumeOfferTakesPriority_NoCarousel()
    {
        var itemId = Guid.NewGuid();
        var item = new Audio { Name = "Test Song", Id = itemId };
        _libraryManagerMock.Setup(l => l.GetItemById(itemId)).Returns(item);

        // Return items for recently played (should not be reached)
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item });

        var handler = CreateLaunchHandler();
        var request = new LaunchRequest { Locale = "en-US" };
        var context = CreateContextWithAudio(itemId.ToString(), 10000);
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Resume offer should be shown, not welcome+carousel
        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Test Song", speech);
        Assert.Contains("resume", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("it-IT")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void LocaleStrings_RecentlyPlayedExists(string locale)
    {
        string value = ResponseStrings.Get("RecentlyPlayed", locale);
        Assert.NotEqual("RecentlyPlayed", value);
        Assert.NotEmpty(value);
    }
}
