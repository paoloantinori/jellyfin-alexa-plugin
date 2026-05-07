using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Reminders;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SetReminderIntent. Creates a native Alexa reminder
/// using the Alexa Reminders API (in-session only).
/// </summary>
public class SetReminderIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SetReminderIntentHandler"/> class.
    /// </summary>
    public SetReminderIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null
            && string.Equals(intentRequest.Intent.Name, IntentNames.SetReminder, StringComparison.Ordinal);
    }

    /// <summary>
    /// Create a native Alexa reminder for the user.
    /// </summary>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? apiAccessToken = context?.System?.ApiAccessToken;
        if (string.IsNullOrEmpty(apiAccessToken))
        {
            Logger.LogWarning("SetReminderIntent: missing API access token");
            return ResponseBuilder.Tell(ResponseStrings.Get("ReminderError", locale));
        }

        string? apiEndpoint = context?.System?.ApiEndpoint ?? "https://api.amazonalexa.com";

        string? message = GetSlotValue(intentRequest, "reminder_message");
        string? durationText = GetSlotValue(intentRequest, "duration_minutes");
        string? timeText = GetSlotValue(intentRequest, "reminder_time");

        if (string.IsNullOrEmpty(durationText) && string.IsNullOrEmpty(timeText))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchReminderTime", locale));
        }

        string spokenText = !string.IsNullOrEmpty(message)
            ? message
            : ResponseStrings.Get("ReminderDefaultMessage", locale);

        int? relativeMinutes = null;
        Reminder reminder;
        try
        {
            if (!string.IsNullOrEmpty(durationText) && int.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes))
            {
                relativeMinutes = minutes;
                reminder = BuildRelativeReminder(minutes, spokenText, locale);
            }
            else if (!string.IsNullOrEmpty(timeText))
            {
                reminder = BuildAbsoluteReminder(timeText, spokenText, locale);
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchReminderTime", locale));
            }
        }
        catch (FormatException ex)
        {
            Logger.LogDebug(ex, "SetReminderIntent: invalid time format '{Time}'", timeText);
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchReminderTime", locale));
        }

        try
        {
            var client = new RemindersClient(apiAccessToken, apiEndpoint);
            ReminderChangedResponse response = await client.Create(reminder).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response?.AlertToken))
            {
                Logger.LogInformation("Reminder created with token {Token}", response.AlertToken);

                string confirmMsg = relativeMinutes.HasValue
                    ? ResponseStrings.Get("ReminderSetRelative", locale, relativeMinutes.Value.ToString(CultureInfo.InvariantCulture))
                    : ResponseStrings.Get("ReminderSetAbsolute", locale, timeText ?? string.Empty);

                return ResponseBuilder.Tell(confirmMsg);
            }

            Logger.LogWarning("Reminder creation returned empty alert token");
            return ResponseBuilder.Tell(ResponseStrings.Get("ReminderError", locale));
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.LogWarning(ex, "Reminder permission not granted by user");
            return BuildPermissionDeniedResponse(locale);
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "Reminder API error");
            return ResponseBuilder.Tell(ResponseStrings.Get("ReminderError", locale));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to create reminder");
            return ResponseBuilder.Tell(ResponseStrings.Get("ReminderError", locale));
        }
    }

    private static Reminder BuildRelativeReminder(int minutes, string spokenText, string locale)
    {
        var reminder = BuildReminderBase(spokenText, locale);
        reminder.Trigger = new RelativeTrigger { OffsetInSeconds = minutes * 60 };
        return reminder;
    }

    private static Reminder BuildAbsoluteReminder(string timeText, string spokenText, string locale)
    {
        // timeText from AMAZON.TIME slot is HH:mm or HH:mm:ss (24-hour format)
        // Convert to today's date at that time in UTC (simplified; production would use user timezone)
        TimeSpan timeOfDay = TimeSpan.Parse(timeText, CultureInfo.InvariantCulture);
        DateTime scheduled = DateTime.UtcNow.Date.Add(timeOfDay);

        if (scheduled <= DateTime.UtcNow)
        {
            scheduled = scheduled.AddDays(1);
        }

        var reminder = BuildReminderBase(spokenText, locale);
        reminder.Trigger = new AbsoluteTrigger { ScheduledTime = scheduled, TimeZoneId = "UTC" };
        return reminder;
    }

    private static Reminder BuildReminderBase(string spokenText, string locale)
    {
        return new Reminder
        {
            RequestTime = DateTime.UtcNow,
            AlertInformation = new AlertInformation
            {
                Spoken = new SpokenInformation
                {
                    Content = new List<SpokenContent>
                    {
                        new()
                        {
                            Locale = locale,
                            Ssml = $"<speak>{EscapeXml(spokenText)}</speak>"
                        }
                    }
                }
            },
            PushNotification = new PushNotification { Status = "ENABLED" }
        };
    }

    private static SkillResponse BuildPermissionDeniedResponse(string locale)
    {
        var response = new ResponseBody
        {
            OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("ReminderPermissionRequired", locale)),
            Card = new AskForPermissionsConsentCard
            {
                Permissions = new List<string>
                {
                    "alexa::alerts:reminders:skill:readwrite"
                }
            },
            ShouldEndSession = false
        };

        return new SkillResponse
        {
            Version = "1.0",
            Response = response
        };
    }

    private static string? GetSlotValue(IntentRequest intentRequest, string slotName)
    {
        if (intentRequest.Intent.Slots != null
            && intentRequest.Intent.Slots.TryGetValue(slotName, out Slot? slot)
            && !string.IsNullOrEmpty(slot.Value))
        {
            return slot.Value;
        }

        return null;
    }
}
