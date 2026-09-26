using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SkipForwardBackIntent. Skips forward or backward by a relative time amount.
/// </summary>
public class SkipForwardBackIntentHandler : BaseHandler
{
    private const int DefaultSkipSeconds = 30;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipForwardBackIntentHandler"/> class.
    /// </summary>
    public SkipForwardBackIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.SkipForwardBack, StringComparison.Ordinal);
    }

    /// <summary>
    /// Skip forward or backward in the currently playing media.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        Logger.LogDebug("SkipForwardBack: entered, locale={Locale}", locale);

        SkillResponse? disabled = IfFeatureDisabled(c => c.SeekEnabled, request);
        if (disabled != null)
        {
            return Task.FromResult(disabled);
        }

        if (session.FullNowPlayingItem == null)
        {
            Logger.LogDebug("SkipForwardBack: no media playing, returning Tell");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // Resolve current position
        long currentTicks = session.PlayState?.PositionTicks ?? 0;
        if (currentTicks == 0 && context.AudioPlayer?.OffsetInMilliseconds > 0)
        {
            // JF-636: the device offset counts the stream's OUTPUT timeline (rate-
            // adjusted on an atempo stream), so it composes through the shared
            // event-side chokepoint (launch-scope base + rate) instead of being read
            // as raw content ticks.
            currentTicks = Progress.ComposeEventPositionTicks(
                context.GetDeviceId(),
                session.FullNowPlayingItem.Id,
                context.AudioPlayer.OffsetInMilliseconds,
                "SkipForwardBack");
        }

        long runtimeTicks = session.NowPlayingItem?.RunTimeTicks ?? 0;

        // Parse slots
        IntentRequest intentRequest = (IntentRequest)request;
        bool forward = true;
        int amount = DefaultSkipSeconds;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("seek_direction", out Slot? dirSlot)
                && !string.IsNullOrEmpty(dirSlot.Value))
            {
                forward = !string.Equals(dirSlot.Value, "back", StringComparison.OrdinalIgnoreCase);
            }

            if (intentRequest.Intent.Slots.TryGetValue("seek_amount", out Slot? amountSlot)
                && int.TryParse(amountSlot.Value, out int parsedAmount)
                && parsedAmount > 0)
            {
                amount = parsedAmount;
            }

            if (intentRequest.Intent.Slots.TryGetValue("seek_unit", out Slot? unitSlot)
                && !string.IsNullOrEmpty(unitSlot.Value))
            {
                if (string.Equals(unitSlot.Value, "minutes", StringComparison.OrdinalIgnoreCase))
                {
                    amount *= 60;
                }
                // "seconds" or no match → already in seconds
            }
        }

        // Calculate target position
        long skipTicks = TimeSpan.FromSeconds(amount).Ticks;
        long targetTicks = forward ? currentTicks + skipTicks : currentTicks - skipTicks;

        Logger.LogDebug("SkipForwardBack: direction={Direction}, amount={Amount}s, currentTicks={CurrentTicks}, targetTicks={TargetTicks}", forward ? "forward" : "back", amount, currentTicks, targetTicks);

        // Clamp to bounds
        if (targetTicks <= 0)
        {
            if (currentTicks == 0)
            {
                return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("AtBeginning", locale)));
            }

            targetTicks = 0;
        }

        if (runtimeTicks > 0 && targetTicks >= runtimeTicks)
        {
            targetTicks = runtimeTicks;
            int endOffsetMs = (int)TimeSpan.FromTicks(targetTicks).TotalMilliseconds;
            var endResponse = Launch.BuildAudioPlayerResponse(
                PlayBehavior.ReplaceAll,
                SeekLaunchSource(user, context, session.FullNowPlayingItem, endOffsetMs),
                session.FullNowPlayingItem.Id.ToString(),
                session.FullNowPlayingItem,
                user,
                context);

            endResponse.Response.OutputSpeech = new PlainTextOutputSpeech
            {
                Text = ResponseStrings.Get("SkippedToEnd", locale)
            };

            return Task.FromResult(endResponse);
        }

        int offsetMs = (int)TimeSpan.FromTicks(targetTicks).TotalMilliseconds;
        string positionStr = ResumeMath.FormatTimeSpan(TimeSpan.FromTicks(targetTicks), locale);
        string announcementKey = forward ? "SkippedForward" : "SkippedBack";

        var response = Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            SeekLaunchSource(user, context, session.FullNowPlayingItem, offsetMs),
            session.FullNowPlayingItem.Id.ToString(),
            session.FullNowPlayingItem,
            user,
            context);

        response.Response.OutputSpeech = new PlainTextOutputSpeech
        {
            Text = ResponseStrings.Get(announcementKey, locale, positionStr)
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// The ONE skip re-launch source (JF-636): resolves through the shared
    /// codec-gated decision WITH the item's launch-scope rate, so a skip during
    /// atempo listening keeps the speed (the target position is CONTENT-absolute
    /// and mints straight into the speed URL's ?start=) instead of silently
    /// reverting to 1x. A rate-1000 scope keeps the pre-JF-636 static URL +
    /// directive-offset shape byte-identically.
    /// </summary>
    /// <param name="user">The user for the raw static URL.</param>
    /// <param name="context">The Alexa context (device id for the launch-scope read).</param>
    /// <param name="item">The item being seeked.</param>
    /// <param name="contentOffsetMs">The CONTENT-absolute seek target in milliseconds.</param>
    /// <returns>The resolved launch source.</returns>
    private AudioLaunchSource SeekLaunchSource(Entities.User user, Context context, MediaBrowser.Controller.Entities.BaseItem item, int contentOffsetMs)
    {
        int ratePerMille = Launch.GetActivePlaybackRate(context.GetDeviceId(), item.Id.ToString())
            ?? Util.PlaybackSpeed.NormalPerMille;
        return Launch.ResolveAudioLaunchSource(item, item.Id.ToString(), user, contentOffsetMs, ratePerMille: ratePerMille);
    }
}
