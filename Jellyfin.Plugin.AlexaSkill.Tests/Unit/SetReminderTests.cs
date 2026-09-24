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
        string? durationValue = null,
        string? reminderTime = null,
        string? reminderMessage = null)
    {
        var intent = new Intent { Name = "SetReminderIntent" };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (durationValue != null)
        {
            intent.Slots["reminder_duration"] = new global::Alexa.NET.Request.Slot { Name = "reminder_duration", Value = durationValue };
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
        var request = CreateIntentRequest(durationValue: "30");

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
        var request = CreateIntentRequest(durationValue: "30");
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
        var request = CreateIntentRequest(durationValue: "30");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response);
        Assert.NotNull(response.Response.OutputSpeech);

        string output = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("When should I remind you", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WithIsoDuration_AttemptsReminderCreation()
    {
        // JF-622: reminder_duration is AMAZON.DURATION, so the value arrives as
        // ISO 8601 carrying the spoken unit (the JF-618 defect class fixed here:
        // "trenta secondi" used to be swallowed into a 30-MINUTE reminder).
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue: "PT30S");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);

        string output = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("When should I remind you", output, StringComparison.OrdinalIgnoreCase);
    }

    // JF-622: the offset must carry the parsed unit. The live incident:
    // «ricordami tra trenta secondi» armed a 30-MINUTE reminder because the old
    // number-typed slot swallowed the unit and BuildRelativeReminder multiplied
    // by 60 unconditionally.
    [Theory]
    [InlineData("PT30S", 30)]         // thirty seconds: THE live incident
        [InlineData("PT1H", 3600)]        // an hour
            [InlineData("30", 1800)]          // bare number: minutes (raw-passthrough shape)
        public void BuildRelativeReminder_CarriesTheParsedUnit(string raw, int expectedSeconds)
    {
        TimeSpan? duration = Jellyfin.Plugin.AlexaSkill.Alexa.Util.ResumeMath.ParseAlexaDuration(raw);
        Assert.NotNull(duration);

        var reminder = SetReminderIntentHandler.BuildRelativeReminder(duration!.Value, "msg", "en-US");
        var trigger = Assert.IsType<global::Alexa.NET.Reminders.RelativeTrigger>(reminder.Trigger);
        Assert.Equal(expectedSeconds, trigger.OffsetInSeconds);
    }

    [Theory]
    [InlineData("PT30S", "30 seconds")]
    [InlineData("PT5M", "5 minutes")]
    [InlineData("PT1H", "one hour")]
    [InlineData("PT1H30M", "90 minutes")]
    public void SpokenConfirmation_NamesTheCorrectUnit(string raw, string expectedPhrase)
    {
        // The confirmation template (ReminderSetRelativeFor) plus the shared
        // spoken-duration formatter must name the SAME unit the trigger carries.
        TimeSpan duration = Jellyfin.Plugin.AlexaSkill.Alexa.Util.ResumeMath.ParseAlexaDuration(raw)!.Value;
        string spoken = Jellyfin.Plugin.AlexaSkill.Alexa.Util.ResumeMath.FormatSpokenLargestUnit(duration, "en-US");
        string confirmation = ResponseStrings.Get("ReminderSetRelativeFor", "en-US", spoken);
        Assert.Contains(expectedPhrase, confirmation, StringComparison.Ordinal);
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
        var request = CreateIntentRequest(durationValue: "15", reminderMessage: "check new episodes");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, CreateUser(), CreateSession(), CancellationToken.None);
        Assert.NotNull(response);
    }

    /// <summary>
    /// An unparseable duration prompts for the time. "trenta" is the dead
    /// ItalianNumber shape: AMAZON.DURATION carries the unit, so a bare number word
    /// elicits instead of arming (unitful speech arrives as ISO 8601 and never hits
    /// this path).
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("trenta")]
    public async Task HandleAsync_InvalidDuration_PromptsForTime(string durationValue)
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationValue);
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
    [InlineData("en-US", "ReminderSetRelativeFor")]
    [InlineData("en-US", "ReminderSetAbsolute")]
    [InlineData("en-US", "DidNotCatchReminderTime")]
    [InlineData("en-US", "ReminderPermissionRequired")]
    [InlineData("en-US", "ReminderDefaultMessage")]
    [InlineData("es-ES", "ReminderError")]
    [InlineData("fr-FR", "ReminderPermissionRequired")]
    public void ReminderString_HasValue(string locale, string key)
    {
        string value = ResponseStrings.Get(key, locale);
        Assert.NotEqual(key, value);
        Assert.NotEmpty(value);
    }

    /// <summary>
    /// JF-622: the unit-aware relative confirmation must resolve in EVERY locale
    /// (the {0} arg is a spoken duration phrase, so the key must keep its format
    /// arg too; the old minutes-templated ReminderSetRelative is gone).
    /// </summary>
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("en-AU")]
    [InlineData("en-CA")]
    [InlineData("en-GB")]
    [InlineData("en-IN")]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    [InlineData("es-US")]
    [InlineData("fr-CA")]
    [InlineData("fr-FR")]
    [InlineData("hi-IN")]
    [InlineData("it-IT")]
    [InlineData("ja-JP")]
    [InlineData("nl-NL")]
    [InlineData("pt-BR")]
    public void ReminderSetRelativeFor_ResolvesAllLocales(string locale)
    {
        string value = ResponseStrings.Get("ReminderSetRelativeFor", locale);
        Assert.NotEqual("ReminderSetRelativeFor", value);
        Assert.NotEmpty(value);
        Assert.Contains("{0}", value);
    }
}
