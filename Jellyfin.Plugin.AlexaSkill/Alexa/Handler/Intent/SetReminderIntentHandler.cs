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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
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
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        Logger.LogDebug("SetReminder: entered, locale={Locale}", locale);

        string? apiAccessToken = context?.System?.ApiAccessToken;
        if (string.IsNullOrEmpty(apiAccessToken))
        {
            Logger.LogWarning("SetReminderIntent: missing API access token");
            return ResponseBuilder.Tell(ResponseStrings.Get("ReminderError", locale));
        }

        string? apiEndpoint = context?.System?.ApiEndpoint ?? "https://api.amazonalexa.com";

        string? message = GetSlotValue(intentRequest, "reminder_message");
        string? durationText = GetSlotValue(intentRequest, "reminder_duration");
        string? timeText = GetSlotValue(intentRequest, "reminder_time");

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "SetReminder") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrEmpty(durationText) && string.IsNullOrEmpty(timeText))
        {
            return BuildDialogElicitResponse("DidNotCatchReminderTime", locale, "reminder_time", IntentNames.SetReminder, Util.ElicitSlots.For(IntentNames.SetReminder));
        }

        string spokenText = !string.IsNullOrEmpty(message)
            ? message
            : ResponseStrings.Get("ReminderDefaultMessage", locale);

        Reminder? reminder = null;
        TimeSpan? relativeDuration = null;
        try
        {
            // JF-622: reminder_duration is AMAZON.DURATION, so ISO 8601 carries the
            // spoken unit through the shared JF-618 parser («trenta secondi» is PT30S,
            // not 30 minutes); a bare number keeps the pre-JF-622 minutes semantic.
            // The int bound mirrors the parser's overflow stance: OffsetInSeconds is
            // a 32-bit API field, so an absurd week form must elicit, not throw at
            // the API boundary.
            TimeSpan? duration = Util.ResumeMath.ParseAlexaDuration(durationText);
            // JF-622 review: XmlConvert and int.TryParse both accept a sign, so a
            // negative value would arm a past reminder (or a generic API rejection);
            // a non-positive duration is treated as no answer and elicits.
            if (duration is { } parsed && parsed > TimeSpan.Zero && parsed.TotalSeconds <= int.MaxValue)
            {
                relativeDuration = parsed;
                reminder = BuildRelativeReminder(parsed, spokenText, locale);
            }
            else if (!string.IsNullOrEmpty(timeText))
            {
                reminder = BuildAbsoluteReminder(timeText, spokenText, locale);
            }
        }
        catch (FormatException ex)
        {
            Logger.LogDebug(ex, "SetReminderIntent: invalid time format '{Time}'", timeText);
        }

        if (reminder is null)
        {
            // JF-622 review: the elicit target follows the FAILED slot. An unparseable
            // or non-positive duration re-elicits reminder_duration with the honest
            // duration-inviting string (the old single elicit targeted reminder_time
            // while inviting "a number of minutes" - an answer that slot can never
            // accept, a dead-end loop); only a malformed TIME string re-elicts
            // reminder_time (JF-550 dead-mic sweep: one elicit for every no-time
            // shape otherwise).
            bool timeAttempted = !string.IsNullOrEmpty(timeText);
            return BuildDialogElicitResponse(
                timeAttempted ? "DidNotCatchReminderTime" : "ReminderAskDuration",
                locale,
                timeAttempted ? "reminder_time" : "reminder_duration",
                IntentNames.SetReminder,
                Util.ElicitSlots.For(IntentNames.SetReminder));
        }

        try
        {
            // JF-622 device round (2026-09-25): the ctor signature is (endpointUrl, accessToken);
            // the args were swapped, feeding the JWT to new Uri() (UriFormatException,
            // live: 'non ho potuto impostare il promemoria' on every attempt).
            var client = new RemindersClient(apiEndpoint, apiAccessToken);
            ReminderChangedResponse response = await client.Create(reminder).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response?.AlertToken))
            {
                Logger.LogInformation("Reminder created with token {Token}", response.AlertToken);

                // Argument-kind contract for translators: ReminderSetRelativeFor's {0}
                // is a fully-formed localized spoken phrase (the shared duration
                // formatter's output), while ReminderSetAbsolute's {0} is the raw
                // AMAZON.TIME slot string verbatim. Rewording one key must not assume
                // the other's contract.
                string confirmMsg = relativeDuration.HasValue
                    ? ResponseStrings.Get("ReminderSetRelativeFor", locale, Util.ResumeMath.FormatSpokenLargestUnit(relativeDuration.Value, locale))
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

    /// <summary>
    /// Internal for the InternalsVisibleTo test seam (the JF-618 sleep-deadline
    /// precedent): JF-622 tests assert the offset carries the parsed unit (PT30S
    /// arms 30 seconds, not the old minutes * 60).
    /// </summary>
    /// <param name="duration">The parsed spoken duration (unit included).</param>
    /// <param name="spokenText">The reminder's spoken message.</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The reminder with a relative trigger.</returns>
    internal static Reminder BuildRelativeReminder(TimeSpan duration, string spokenText, string locale)
    {
        var reminder = BuildReminderBase(spokenText, locale);
        reminder.Trigger = new RelativeTrigger { OffsetInSeconds = (int)duration.TotalSeconds };
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
                            Ssml = $"<speak>{SpeechBuilder.EscapeXml(spokenText)}</speak>"
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

}
