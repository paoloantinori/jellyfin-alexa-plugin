using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for JumpToPositionIntent. Jumps to an absolute time position in the currently playing media.
/// </summary>
public class JumpToPositionIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JumpToPositionIntentHandler"/> class.
    /// </summary>
    public JumpToPositionIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.JumpToPosition, StringComparison.Ordinal);
    }

    /// <summary>
    /// Jump to a specific time position in the currently playing media.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        Logger.LogDebug("JumpToPosition: entered, locale={Locale}", locale);

        SkillResponse? disabled = IfFeatureDisabled(c => c.SeekEnabled, request);
        if (disabled != null)
        {
            return Task.FromResult(disabled);
        }

        if (session.FullNowPlayingItem == null)
        {
            Logger.LogDebug("JumpToPosition: no media playing, returning Tell");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // Parse slots
        IntentRequest intentRequest = (IntentRequest)request;
        int hours = 0, minutes = 0, seconds = 0;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("position_hours", out Slot? hoursSlot)
                && int.TryParse(hoursSlot.Value, out int parsedHours) && parsedHours > 0)
            {
                hours = parsedHours;
            }

            if (intentRequest.Intent.Slots.TryGetValue("position_minutes", out Slot? minutesSlot)
                && int.TryParse(minutesSlot.Value, out int parsedMinutes) && parsedMinutes > 0)
            {
                minutes = parsedMinutes;
            }

            if (intentRequest.Intent.Slots.TryGetValue("position_seconds", out Slot? secondsSlot)
                && int.TryParse(secondsSlot.Value, out int parsedSeconds) && parsedSeconds > 0)
            {
                seconds = parsedSeconds;
            }
        }

        long targetTicks = TimeSpan.FromHours(hours).Ticks
                         + TimeSpan.FromMinutes(minutes).Ticks
                         + TimeSpan.FromSeconds(seconds).Ticks;

        if (targetTicks <= 0)
        {
            targetTicks = 0;
        }

        long runtimeTicks = session.NowPlayingItem?.RunTimeTicks ?? 0;

        Logger.LogDebug("JumpToPosition: target={Hours}h{Minutes}m{Seconds}s ({TargetTicks} ticks), runtime={RuntimeTicks} ticks", hours, minutes, seconds, targetTicks, runtimeTicks);

        // Past-end check
        if (runtimeTicks > 0 && targetTicks >= runtimeTicks)
        {
            string runtimeStr = FormatTimeSpan(TimeSpan.FromTicks(runtimeTicks), locale);
            return Task.FromResult(ResponseBuilder.Tell(
                ResponseStrings.Get("PositionPastEnd", locale, runtimeStr)));
        }

        int offsetMs = (int)TimeSpan.FromTicks(targetTicks).TotalMilliseconds;
        string positionStr = FormatTimeSpan(TimeSpan.FromTicks(targetTicks), locale);

        var response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            GetStreamUrl(session.FullNowPlayingItem.Id.ToString(), user),
            session.FullNowPlayingItem.Id.ToString(),
            session.FullNowPlayingItem,
            user,
            context,
            offsetMs);

        response.Response.OutputSpeech = new PlainTextOutputSpeech
        {
            Text = ResponseStrings.Get("JumpedToPosition", locale, positionStr)
        };

        return Task.FromResult(response);
    }
}
