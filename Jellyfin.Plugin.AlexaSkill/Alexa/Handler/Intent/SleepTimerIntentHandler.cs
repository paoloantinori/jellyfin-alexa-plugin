using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SleepTimerIntent. Encodes a stop deadline into the current
/// AudioPlayer token so that <c>PlaybackNearlyFinishedEventHandler</c> can
/// check it and stop playback when the deadline passes.
/// </summary>
public class SleepTimerIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SleepTimerIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public SleepTimerIntentHandler(
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
            && string.Equals(intentRequest.Intent.Name, "SleepTimerIntent", StringComparison.Ordinal);
    }

    /// <summary>
    /// Set (or cancel) a sleep timer for the currently playing media.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Skill response with updated AudioPlayer directive.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.SleepTimerEnabled, request) is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        // Extract the duration_minutes slot.
        string? durationSlot = null;
        if (intentRequest.Intent.Slots != null
            && intentRequest.Intent.Slots.TryGetValue("duration_minutes", out Slot? slot))
        {
            durationSlot = slot.Value;
        }

        Logger.LogDebug("SleepTimer: entered, durationSlot={DurationSlot}", durationSlot);

        if (string.IsNullOrEmpty(durationSlot) || !int.TryParse(durationSlot, NumberStyles.Integer, CultureInfo.InvariantCulture, out int durationMinutes))
        {
            Logger.LogDebug("SleepTimer: invalid duration, returning Tell");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchSleepTimer", locale)));
        }

        // Nothing currently playing.
        if (session.FullNowPlayingItem == null)
        {
            Logger.LogDebug("SleepTimer: no media playing, returning Tell");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        string itemId = context.AudioPlayer?.Token ?? session.FullNowPlayingItem.Id.ToString();

        // The item id is CANONICALIZED through the shared StreamTokenCodec before use
        // (JF-447: the format's one owner; the event handlers parse it with the same
        // codec). During sleep playback the current token already carries a sleep
        // suffix, and using the RAW token would put a composite id into the stream URL
        // path (unmatchable) and into the replay Token (the old deadline would persist
        // and the sleep would still fire after a cancel; re-arming used to stack a
        // second suffix whose deadline parse then failed). Both the cancel replay and
        // the re-arm mint below build from the CLEAN id.
        Guid itemGuid = StreamTokenCodec.TryGetItemId(itemId, out Guid parsed)
            ? parsed
            : Guid.TryParse(itemId, out Guid bare) ? bare : Guid.Empty;

        int offsetInMilliseconds = 0;
        if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
        {
            offsetInMilliseconds = (int)context.AudioPlayer.OffsetInMilliseconds;
        }
        else if (session.PlayState?.PositionTicks != null)
        {
            offsetInMilliseconds = (int)TimeSpan.FromTicks(session.PlayState.PositionTicks.Value).TotalMilliseconds;
        }

        // Cancel mode: duration <= 0 replays without a sleep deadline.
        if (durationMinutes <= 0)
        {
            // No-token tell: an unparseable token (and no id left to fall back to)
            // must not mint a replay directive from Guid.Empty, whose stream URL
            // would point at nothing (review finding on the cancel branch).
            if (itemGuid == Guid.Empty)
            {
                Logger.LogDebug("SleepTimer: cancel mode found no parseable item id in token={Token}, returning Tell", itemId);
                return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
            }

            Logger.LogDebug("SleepTimer: cancel mode (durationMinutes={DurationMinutes}), replaying without deadline", durationMinutes);
            var cancelDirective = new AudioPlayerPlayDirective
            {
                PlayBehavior = PlayBehavior.ReplaceAll,
                AudioItem = new AudioItem
                {
                    Stream = new AudioItemStream
                    {
                        // The canonicalized GUID feeds the stream URL (a composite id
                        // in the URL path is unmatchable), and the replay Token is the
                        // CLEAN id string with NO sleep suffix: a cancel replays with
                        // no deadline, so PlaybackNearlyFinished sees nothing to enforce.
                        Url = GetStreamUrl(itemGuid.ToString(), user),
                        Token = itemGuid.ToString(),
                        OffsetInMilliseconds = offsetInMilliseconds
                    }
                }
            };

            return Task.FromResult<SkillResponse>(new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    ShouldEndSession = true,
                    OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("CancelSleepTimer", locale)),
                    Directives = new List<IDirective> { cancelDirective }
                }
            });
        }

        // Encode the sleep deadline into the token through the shared StreamTokenCodec
        // (JF-447: the format's one owner; the event handlers parse it with the same
        // codec), from the CLEAN id canonicalized above (minting from a suffixed id
        // would stack a second suffix whose deadline parse then fails).
        long deadlineTicks = DateTimeOffset.UtcNow.AddMinutes(durationMinutes).UtcTicks;

        string token = StreamTokenCodec.MintSleepTimerToken(itemGuid, deadlineTicks);

        Logger.LogDebug("SleepTimer: setting {DurationMinutes} minute timer, token={Token}", durationMinutes, token);

        var directive = new AudioPlayerPlayDirective
        {
            PlayBehavior = PlayBehavior.ReplaceAll,
            AudioItem = new AudioItem
            {
                Stream = new AudioItemStream
                {
                    // The canonicalized GUID also feeds the stream URL: during sleep
                    // playback the raw token carries the sleep suffix, which would put a
                    // composite id into the URL path (the same re-arm defect family).
                    Url = GetStreamUrl(itemGuid.ToString(), user),
                    Token = token,
                    OffsetInMilliseconds = offsetInMilliseconds
                }
            }
        };

        return Task.FromResult<SkillResponse>(new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                OutputSpeech = new PlainTextOutputSpeech(
                    ResponseStrings.Get("SleepTimerSet", locale, durationMinutes.ToString(CultureInfo.InvariantCulture))),
                Directives = new List<IDirective> { directive }
            }
        });
    }
}
