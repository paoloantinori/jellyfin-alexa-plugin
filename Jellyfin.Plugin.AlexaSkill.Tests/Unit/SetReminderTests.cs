using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class SetReminderIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SetReminderIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SetReminderIntentHandler CreateHandler()
    {
        return new SetReminderIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(
        string? durationMinutes = null,
        string? reminderTime = null,
        string? reminderMessage = null)
    {
        var intent = new Intent { Name = "SetReminderIntent" };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (durationMinutes != null)
        {
            intent.Slots["duration_minutes"] = new global::Alexa.NET.Request.Slot { Name = "duration_minutes", Value = durationMinutes };
        }

        if (reminderTime != null)
        {
            intent.Slots["reminder_time"] = new global::Alexa.NET.Request.Slot { Name = "reminder_time", Value = reminderTime };
        }

        if (reminderMessage != null)
        {
            intent.Slots["reminder_message"] = new global::Alexa.NET.Request.Slot { Name = "reminder_message", Value = reminderMessage };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
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
    public void CanHandle_MatchingIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "30");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_DifferentIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var intent = new Intent { Name = "PlayIntent" };
        var request = new IntentRequest { Intent = intent, Locale = "en-US" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { Locale = "en-US" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingApiAccessToken_ReturnsError()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "30");
        var context = CreateContext(token: null);

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.NotNull(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NoTimeSlots_ReturnsPromptForTime()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        string output = TestHelpers.GetSpeechText(response);
        Assert.Contains("When", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithDurationSlot_AttemptsReminderCreation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "30");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.NotNull(response.Response.OutputSpeech);

        string output = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("When should I remind you", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithTimeSlot_AttemptsReminderCreation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(reminderTime: "19:00");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.NotNull(response.Response.OutputSpeech);

        string output = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("When should I remind you", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithCustomMessage_ReturnsResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "15", reminderMessage: "check new episodes");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);
        Assert.NotNull(response);
    }

    [Fact]
    public async Task HandleAsync_InvalidDuration_PromptsForTime()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "abc");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        string output = TestHelpers.GetSpeechText(response);
        Assert.Contains("When", output, StringComparison.OrdinalIgnoreCase);
    }
}

public class ReminderLocaleStringsTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("it-IT")]
    public void ReminderError_ExistsForLocale(string locale)
    {
        string value = ResponseStrings.Get("ReminderError", locale);
        Assert.NotEqual("ReminderError", value);
        Assert.NotEmpty(value);
    }

    [Theory]
    [InlineData("en-US", "ReminderSetRelative")]
    [InlineData("en-US", "ReminderSetAbsolute")]
    [InlineData("en-US", "DidNotCatchReminderTime")]
    [InlineData("en-US", "ReminderPermissionRequired")]
    [InlineData("en-US", "ReminderDefaultMessage")]
    [InlineData("de-DE", "ReminderSetRelative")]
    [InlineData("it-IT", "ReminderSetRelative")]
    [InlineData("es-ES", "ReminderError")]
    [InlineData("fr-FR", "ReminderPermissionRequired")]
    public void ReminderString_HasValue(string locale, string key)
    {
        string value = ResponseStrings.Get(key, locale);
        Assert.NotEqual(key, value);
        Assert.NotEmpty(value);
    }
}
